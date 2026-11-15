module NHAMBS.Cli.CircularCommand

open System
open System.IO
open System.Threading.Tasks
open NHAMBS.Circular
open NHAMBS.Mbs
open NHAMBS.Sources

/// Parse and validate one NHA MBS circular from already-extracted text.
let private parseTextResult input text =
    try
        let jsonObject = CircularPdf.parseText text
        let json = CircularPdf.toJson jsonObject

        match Schema.validate json with
        | Error errors ->
            errors
            |> List.map (fun error -> sprintf "%s: %s" error.Path error.Message)
            |> Error
        | Ok _ -> Ok(json, Circular.parse json)
    with ex ->
        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            Error [ ex.ToString() ]
        else
            Error [ ex.Message ]

let private printErrors input errors =
    eprintfn "Failed to parse %s:" input

    for error in errors do
        eprintfn "  %s" error

let private printSummary (circular: Circular.Root) =
    eprintfn
        "Parsed pool %s (%s, %s), issued %s, aggregate %M"
        circular.Mbs.PoolNumber
        circular.RateType
        circular.PoolType
        (circular.Mbs.IssueDate.ToString("yyyy-MM-dd"))
        circular.Mbs.AggregatePrincipalAmount

/// Parse one CMHC 2834 circular PDF.
let single (input: string) (output: string option) =
    try
        let text = PdfText.extractText input

        match parseTextResult input text with
        | Error errors ->
            printErrors input errors
            1
        | Ok(json, circular) ->
            match output with
            | Some path ->
                File.WriteAllText(path, json + Environment.NewLine)
                eprintfn "Wrote %s" path
            | None -> printfn "%s" json

            printSummary circular
            0
    with ex ->
        eprintfn "Failed to parse %s: %s" input ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

/// Extract, parse, and write one circular. Safe to run concurrently: each file
/// has its own output path and the PDF helpers use per-run temporary
/// directories.
let private processCircular (outputDir: string) (input: string) : BatchReport.Entry =
    let fileName = Path.GetFileName input

    let output =
        Path.Combine(outputDir, Path.GetFileNameWithoutExtension input + ".json")

    try
        // Files being downloaded are written in place; leave them for the
        // next pass rather than treating a partial PDF as a parse failure.
        let stillDownloading =
            DateTime.UtcNow - File.GetLastWriteTimeUtc input < TimeSpan.FromSeconds 10.0

        if stillDownloading then
            BatchReport.entry fileName BatchReport.Status.Pending (Some "recently modified") []
        else
            let text = PdfText.extractText input

            if text.Trim().Length < 200 then
                BatchReport.entry fileName BatchReport.Status.Skipped (Some "image-only or empty PDF") []
            else
                match parseTextResult input text with
                | Error errors ->
                    BatchReport.entry fileName BatchReport.Status.Failed (Some(String.concat " | " errors)) []
                | Ok(json, circular) ->
                    File.WriteAllText(output, json + Environment.NewLine)

                    BatchReport.entry fileName BatchReport.Status.Parsed None [ "poolNumber", circular.Mbs.PoolNumber ]
    with ex ->
        BatchReport.entry fileName BatchReport.Status.Failed (Some ex.Message) []

/// Parse every PDF directly under `inputDir`, writing one JSON file per
/// circular to `outputDir` plus a resumable `batch-report.json`. A second run
/// resumes from the report: files already parsed or skipped are not processed
/// again, while failures and deferred files are retried. PDFs are processed in
/// parallel and progress is printed as each one finishes.
let processBatch (inputDir: string) (outputDir: string) =
    if not (Directory.Exists inputDir) then
        eprintfn "Input directory does not exist: %s" inputDir
        2
    else
        Directory.CreateDirectory outputDir |> ignore

        let reportPath = Path.Combine(outputDir, "batch-report.json")

        let previous =
            BatchReport.readOrImport reportPath (Path.Combine(outputDir, "batch-report.csv")) "circular-batch"

        let previouslyCompleted = BatchReport.completedFiles previous

        let files = Directory.EnumerateFiles(inputDir, "*.pdf") |> Seq.sort |> Seq.toArray

        let pending =
            files
            |> Array.filter (fun input ->
                let fileName = Path.GetFileName input

                let output =
                    Path.Combine(outputDir, Path.GetFileNameWithoutExtension input + ".json")

                not (previouslyCompleted.Contains fileName && File.Exists output))

        let session =
            BatchReport.Session(reportPath, "circular-batch", pending.Length, previous)

        if pending.Length = 0 then
            eprintfn "Nothing to do: %d circulars already complete." (files.Length - pending.Length)
        else
            eprintfn
                "Parsing %d of %d circulars (%d already complete)."
                pending.Length
                files.Length
                (files.Length - pending.Length)

            Parallel.ForEach(
                pending,
                ParallelOptions(MaxDegreeOfParallelism = Source.parallelism),
                fun input -> session.Record(processCircular outputDir input)
            )
            |> ignore

        session.Finish()

        let parsed, skipped, deferred, failed = session.Counts

        eprintfn "Parsed %d, skipped %d, deferred %d, failed %d. Report: %s" parsed skipped deferred failed reportPath

        if failed = 0 then 0 else 1

/// Download missing pool PDFs listed in the URL manifest into `inputDir`. If
/// the manifest does not exist, scrape CMHC to create it first.
let download (inputDir: string) (urlsPath: string option) =
    let manifestPath = defaultArg urlsPath "pool_urls.txt"

    try
        if not (File.Exists manifestPath) then
            let issuers = getIssuers ()
            let pools = issuers |> List.collect (fun issuer -> getPools issuer.IssuerCode)

            Manifest.writeCsv
                manifestPath
                [ "PoolName"; "CUSIP"; "Url" ]
                [ for pool in pools -> [ pool.PoolName; pool.CUSIP; pool.Url ] ]

            eprintfn "Wrote %d pool URLs to %s" pools.Length manifestPath

        match Manifest.read manifestPath with
        | Error errors ->
            eprintfn "Failed to read %s:" manifestPath

            for error in errors do
                eprintfn "  %s" error

            1
        | Ok entries ->
            Directory.CreateDirectory inputDir |> ignore

            let results = Manifest.downloadAll ".pdf" inputDir (Some baseUrl) entries

            Manifest.printSummary results
    with ex ->
        eprintfn "Failed to download NHA MBS pools: %s" ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

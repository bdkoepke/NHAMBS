module NHAMBS.Cli.ChtCommand

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open NHAMBS.Cht
open NHAMBS.Sources

let private readPdfText (source: string) =
    if Source.isUrl source then
        Source.withTempFile ".pdf" (fun path ->
            Source.downloadFile source path
            ChtPdfText.extractLayout path)
    else
        ChtPdfText.extractLayout source

let private parseDebtIssues source html =
    let jsonObject = ChtHtml.parseDebtIssues source html
    let json = ChtHtml.toJson jsonObject

    match DebtIssuesSchema.validate json with
    | Error errors -> Error errors
    | Ok _ -> Ok(json, DebtIssues.parse json)

let private parseCircular source text =
    let jsonObject = OfferingCircularPdf.parseText source text
    let json = OfferingCircularPdf.toJson jsonObject

    match OfferingCircularSchema.validate json with
    | Error errors -> Error errors
    | Ok _ -> Ok(json, OfferingCircular.parse json)

let private printErrors label errors =
    eprintfn "Failed to parse %s:" label

    for error in errors do
        eprintfn "  %s: %s" error.Path error.Message

let private failure message : ValidationError list = [ { Path = "$"; Message = message } ]

let private describeErrors (errors: ValidationError list) =
    errors
    |> List.map (fun error -> sprintf "%s: %s" error.Path error.Message)
    |> String.concat " | "

let private printDebtIssuesSummary (issues: DebtIssues.Root) =
    let totalIssuance = issues.Issues |> Array.sumBy (fun issue -> issue.TotalIssuance)

    let outstanding = issues.Issues |> Array.sumBy (fun issue -> issue.Outstanding)

    eprintfn
        "Parsed %d CHT debt issues as of %s; total issuance %M million, outstanding %M million"
        issues.Issues.Length
        (issues.AsOfDate.ToString("yyyy-MM-dd"))
        totalIssuance
        outstanding

let private printCircularSummary (circular: OfferingCircular.Root) =
    eprintfn
        "Parsed %s offering circular dated %s; %s %M, coupon %s"
        circular.Series
        (circular.DocumentDate.ToString("yyyy-MM-dd"))
        circular.Currency
        circular.AggregatePrincipalAmount
        circular.Coupon.DisplayText

let private single source output parse summary =
    try
        let html = Source.readText source

        match parse source html with
        | Error errors ->
            printErrors source errors
            1
        | Ok(json, value) ->
            match output with
            | Some path ->
                File.WriteAllText(path, json + Environment.NewLine)
                eprintfn "Wrote %s" path
            | None -> printfn "%s" json

            summary value
            0
    with ex ->
        eprintfn "Failed to parse %s: %s" source ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

/// Parse the CMHC list of outstanding CHT debt issues.
let debtIssues (source: string) (output: string option) =
    single source output parseDebtIssues printDebtIssuesSummary

/// Parse one CHT offering circular PDF.
let circular (source: string) (output: string option) =
    try
        let text = readPdfText source

        match parseCircular source text with
        | Error errors ->
            printErrors source errors
            1
        | Ok(json, circular) ->
            match output with
            | Some path ->
                File.WriteAllText(path, json + Environment.NewLine)
                eprintfn "Wrote %s" path
            | None -> printfn "%s" json

            printCircularSummary circular
            0
    with ex ->
        eprintfn "Failed to parse %s: %s" source ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

let private slug (value: string) =
    Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-')

let private historyFileName (series: string) = slug series + "-history.json"

let private readHistoryWorkbook (source: string) =
    if Source.isUrl source then
        Source.withTempFile ".xlsx" (fun path ->
            Source.downloadFile source path
            OutstandingHistoryXlsx.parse source path)
    else
        OutstandingHistoryXlsx.parse source source

let private writeHistoryDocuments (source: string) (documents: JsonObject list) (outputDir: string) =
    Directory.CreateDirectory outputDir |> ignore

    let mutable written = 0
    let mutable cancellations = 0
    let mutable failed = 0

    for document in documents do
        let json = OutstandingHistoryXlsx.toJson document

        let label =
            if document.ContainsKey "series" && not (isNull document.["series"]) then
                document.["series"].GetValue<string>()
            else
                source

        match OutstandingHistorySchema.validate json with
        | Error errors ->
            eprintfn "Failed to validate %s:" label

            for error in errors do
                eprintfn "  %s: %s" error.Path error.Message

            failed <- failed + 1
        | Ok _ ->
            let history = OutstandingHistory.parse json

            let path = Path.Combine(outputDir, historyFileName history.Series)

            File.WriteAllText(path, json + Environment.NewLine)
            written <- written + 1
            cancellations <- cancellations + history.Cancellations.Length

    eprintfn
        "Wrote %d CHT outstanding history files to %s with %d cancellation periods, failed %d."
        written
        outputDir
        cancellations
        failed

    if failed = 0 then 0 else 1

/// Parse one history of bond purchases for cancellation workbook, writing one
/// JSON file per issue series to the output directory.
let history (source: string) (output: string option) =
    try
        let outputDir = output |> Option.defaultValue "cht_history_out"
        let documents = readHistoryWorkbook source
        writeHistoryDocuments source documents outputDir
    with ex ->
        eprintfn "Failed to parse %s: %s" source ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

let private circularFileName (series: string) (designation: string) =
    slug series + "-" + slug designation + ".pdf"

let private fileNameFromUrl (url: string) =
    let name =
        if Source.isUrl url then
            Path.GetFileName(Uri(url).AbsolutePath)
        else
            Path.GetFileName url

    if String.IsNullOrWhiteSpace name then
        "outstanding-history.xlsx"
    else
        name

/// Distinct history-of-bond-purchases workbooks linked from the outstanding
/// amounts in a debt-issues snapshot, paired with their download file names.
let outstandingHistoryFiles (issues: DebtIssues.Root) =
    issues.Issues
    |> Array.choose (fun issue ->
        issue.OutstandingHistoryUrl
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (fun url -> url, fileNameFromUrl url))
    |> Array.distinctBy fst

let private manifestRows (issues: DebtIssues.Root) =
    [
        for issue in issues.Issues do
            for circular in issue.OfferingCirculars ->
                [
                    issue.Series
                    circular.Designation
                    circularFileName issue.Series circular.Designation
                    circular.Url
                ]
    ]

let private writeManifests (pdfDir: string) (urlsPath: string option) (rows: string list list) =
    let manifestPath = Path.Combine(pdfDir, "manifest.csv")
    Manifest.writeCsv manifestPath [ "Series"; "Designation"; "File"; "Url" ] rows

    match urlsPath with
    | Some path ->
        Manifest.writeCsv path [ "Series"; "Designation"; "File"; "Url" ] rows
        eprintfn "Wrote %d offering circular URLs to %s" rows.Length path
    | None -> ()

let private circularEntries (outputDir: string) (issues: DebtIssues.Root) =
    [
        for issue in issues.Issues do
            for circular in issue.OfferingCirculars do
                let fileName = circularFileName issue.Series circular.Designation

                let outputPath =
                    Path.Combine(outputDir, Path.GetFileNameWithoutExtension fileName + ".json")

                issue, circular, fileName, outputPath
    ]

/// Download every offering circular PDF and outstanding history workbook linked
/// from a debt-issues snapshot. Existing non-empty files are reused.
let download (debtIssuesPath: string) (pdfDir: string) (urlsPath: string option) =
    try
        match DebtIssues.load debtIssuesPath with
        | Error errors ->
            printErrors debtIssuesPath errors
            1
        | Ok issues ->
            Directory.CreateDirectory pdfDir |> ignore

            let rows = manifestRows issues
            writeManifests pdfDir urlsPath rows

            let circulars =
                [
                    for issue in issues.Issues do
                        for circular in issue.OfferingCirculars -> issue.Series, circular
                ]

            let mutable downloaded = 0
            let mutable existing = 0
            let mutable failed = 0

            for series, circular in circulars do
                let fileName = circularFileName series circular.Designation
                let pdfPath = Path.Combine(pdfDir, fileName)

                if File.Exists pdfPath && FileInfo(pdfPath).Length > 0L then
                    existing <- existing + 1
                else
                    try
                        Source.downloadFile circular.Url pdfPath
                        downloaded <- downloaded + 1
                    with ex ->
                        eprintfn "Failed to download %s: %s" circular.Url ex.Message
                        failed <- failed + 1

            eprintfn "Downloaded %d offering circulars (%d already present), failed %d." downloaded existing failed

            let mutable historyDownloaded = 0
            let mutable historyExisting = 0
            let mutable historyFailed = 0

            for url, fileName in outstandingHistoryFiles issues do
                let path = Path.Combine(pdfDir, fileName)

                if File.Exists path && FileInfo(path).Length > 0L then
                    historyExisting <- historyExisting + 1
                else
                    try
                        Source.downloadFile url path
                        historyDownloaded <- historyDownloaded + 1
                    with ex ->
                        eprintfn "Failed to download %s: %s" url ex.Message
                        historyFailed <- historyFailed + 1

            eprintfn
                "Downloaded %d outstanding history workbooks (%d already present), failed %d."
                historyDownloaded
                historyExisting
                historyFailed

            if failed = 0 && historyFailed = 0 then 0 else 1
    with ex ->
        eprintfn "Failed to download CHT offering circulars: %s" ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

let private processHistoryFiles (issues: DebtIssues.Root) (pdfDir: string) (outputDir: string) =
    let mutable failed = 0

    for url, fileName in outstandingHistoryFiles issues do
        let workbookPath = Path.Combine(pdfDir, fileName)

        try
            if not (File.Exists workbookPath) || FileInfo(workbookPath).Length = 0L then
                failwithf "Workbook is missing; rerun with --download to fetch %s" url

            let source = if url <> "" then url else workbookPath
            let documents = OutstandingHistoryXlsx.parse source workbookPath

            if writeHistoryDocuments source documents outputDir <> 0 then
                failed <- failed + 1
        with ex ->
            eprintfn "Failed to parse %s: %s" workbookPath ex.Message
            failed <- failed + 1

    if failed = 0 then 0 else 1

/// Parse every offering circular linked from a debt-issues snapshot. PDFs must
/// already be present in the PDF directory; run the download phase first.
/// A second run resumes from `batch-report.json`, skipping circulars that are
/// already complete. Downloadable history workbooks are parsed into
/// `historyOutputDir`.
let processBatch
    (debtIssuesPath: string)
    (pdfDir: string)
    (outputDir: string)
    (historyOutputDir: string)
    (urlsPath: string option)
    =
    if not (Directory.Exists pdfDir) then
        Directory.CreateDirectory pdfDir |> ignore

    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore

    let reportPath = Path.Combine(outputDir, "batch-report.json")

    match DebtIssues.load debtIssuesPath with
    | Error errors ->
        printErrors debtIssuesPath errors
        1
    | Ok issues ->
        writeManifests pdfDir urlsPath (manifestRows issues)

        let circulars = circularEntries outputDir issues

        let previous =
            BatchReport.readOrImport reportPath (Path.Combine(outputDir, "circulars-report.csv")) "cht-circulars"

        let previouslyCompleted = BatchReport.completedFiles previous

        let pending =
            circulars
            |> List.filter (fun (_, _, fileName, outputPath) ->
                not (previouslyCompleted.Contains fileName && File.Exists outputPath))

        let session =
            BatchReport.Session(reportPath, "cht-circulars", pending.Length, previous)

        for issue, circular, fileName, outputPath in pending do
            let pdfPath = Path.Combine(pdfDir, fileName)

            let result =
                try
                    if not (File.Exists pdfPath) || FileInfo(pdfPath).Length = 0L then
                        failwithf "PDF is missing; rerun with --download to fetch %s" circular.Url

                    let text = ChtPdfText.extractLayout pdfPath
                    parseCircular circular.Url text
                with ex ->
                    Error(failure ex.Message)

            let metadata = [ "series", issue.Series; "designation", circular.Designation ]

            match result with
            | Ok(json, _) ->
                File.WriteAllText(outputPath, json + Environment.NewLine)

                session.Record(BatchReport.entry fileName BatchReport.Status.Parsed None metadata)
            | Error errors ->
                session.Record(
                    BatchReport.entry fileName BatchReport.Status.Failed (Some(describeErrors errors)) metadata
                )

        session.Finish()

        let parsed, skipped, deferred, failed = session.Counts

        eprintfn
            "Parsed %d offering circulars, skipped %d, deferred %d, failed %d. Report: %s"
            parsed
            skipped
            deferred
            failed
            reportPath

        let historyCode = processHistoryFiles issues pdfDir historyOutputDir

        if failed = 0 && historyCode = 0 then 0 else 1

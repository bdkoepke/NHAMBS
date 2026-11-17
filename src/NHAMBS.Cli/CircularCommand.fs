module NHAMBS.Cli.CircularCommand

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading.Tasks
open NHAMBS.Circular
open NHAMBS.Mbs
open NHAMBS.Sources

let private tryGetJsonDate (value: JsonObject) (key: string) =
    let node: JsonNode = value.[key]

    if isNull node then
        None
    else
        match
            DateTime.TryParseExact(
                node.GetValue<string>(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None
            )
        with
        | true, date -> Some date
        | _ -> None

let private setJsonDate (value: JsonObject) (key: string) (date: DateTime) =
    value.[key] <- JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let private replaceYear (date: DateTime) (year: int) =
    DateTime(year, date.Month, min date.Day (DateTime.DaysInMonth(year, date.Month)))

/// Repair OCR years before schema validation. A malformed year is anchored to
/// the final payment date (or the other way around); a weighted-average
/// maturity that is more than a year after the final payment is also reset to
/// that year. This keeps the CLI output usable when a low-quality scan prints
/// "2610", "0201" or "2081" instead of the intended four-digit year.
let private repairJsonDates (root: JsonObject) =
    let asObject (key: string) : JsonObject option =
        match root.[key] with
        | :? JsonObject as value -> Some value
        | _ -> None

    match asObject "mbs", asObject "addendum" with
    | Some mbs, Some addendum ->
        let issue = tryGetJsonDate mbs "issueDate"
        let maturity = tryGetJsonDate mbs "maturityDate"
        let finalPayment = tryGetJsonDate mbs "finalPaymentDue"
        let firstPayment = tryGetJsonDate mbs "firstPaymentDue"

        let repairedFirstPayment =
            match firstPayment, issue with
            | Some value, Some issueDate when value < issueDate ->
                let sameYear = replaceYear value issueDate.Year

                Some(
                    if sameYear >= issueDate then
                        sameYear
                    else
                        replaceYear value (issueDate.Year + 1)
                )
            | _ -> firstPayment

        repairedFirstPayment |> Option.iter (setJsonDate mbs "firstPaymentDue")

        let repairedMaturity =
            match maturity, finalPayment with
            | Some value, Some anchor when value.Year < 1970 || value.Year > 2200 -> Some(replaceYear value anchor.Year)
            | Some value, Some anchor when value > anchor.AddYears(1) -> Some(replaceYear value anchor.Year)
            | Some value, _ ->
                match issue with
                | Some issueDate when value <= issueDate -> Some(replaceYear value (issueDate.Year + 5))
                | Some issueDate when value.Year > issueDate.Year + 50 -> Some(replaceYear value (issueDate.Year + 5))
                | _ -> maturity
            | None, _ -> None

        let repairedFinalPayment =
            match finalPayment, repairedMaturity with
            | Some value, Some anchor when value.Year < 1970 || value.Year > 2200 -> Some(replaceYear value anchor.Year)
            | Some value, Some anchor when value > anchor.AddYears(1) -> Some(replaceYear value anchor.Year)
            | Some value, Some anchor when value < anchor.AddYears(-1) -> Some(replaceYear value anchor.Year)
            | Some value, _ ->
                match issue with
                | Some issueDate when value.Year > issueDate.Year + 50 -> Some(replaceYear value (issueDate.Year + 5))
                | _ -> finalPayment
            | None, _ -> None

        repairedMaturity |> Option.iter (setJsonDate mbs "maturityDate")
        repairedFinalPayment |> Option.iter (setJsonDate mbs "finalPaymentDue")

        match tryGetJsonDate addendum "weightedAverageMaturityDate", repairedFinalPayment, issue with
        | Some value, Some anchor, _ when
            value.Year < 1970
            || value.Year > 2200
            || value > anchor.AddYears(1)
            || value < anchor.AddYears(-15)
            ->
            setJsonDate addendum "weightedAverageMaturityDate" (replaceYear value anchor.Year)
        | Some value, None, Some anchor when value < anchor ->
            setJsonDate addendum "weightedAverageMaturityDate" (replaceYear value (anchor.Year + 5))
        | _ -> ()

        match tryGetJsonDate addendum "weightedAverageInterestAdjustmentDate", issue with
        | Some value, Some anchor when
            value.Year < 1970
            || value.Year > 2200
            || value > anchor.AddYears(1)
            || value < anchor.AddYears(-10)
            ->
            setJsonDate addendum "weightedAverageInterestAdjustmentDate" (replaceYear value anchor.Year)
        | _ -> ()
    | _ -> ()

/// Parse and validate one NHA MBS circular from already-extracted text. The
/// expected pool number comes from the source manifest (keyed by CUSIP) and is
/// used when a scanned cover cannot be read reliably.
let private parseTextResult (expectedPoolNumber: string option) input text =
    let attempt (value: string) =
        try
            let jsonObject = CircularPdf.parseTextWithPoolNumber expectedPoolNumber value
            repairJsonDates jsonObject
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

    let shouldRetryWithOcr (errors: string list) =
        errors
        |> List.exists (fun error ->
            error.Contains("aggregate principal", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Mortgage-Backed Securities coupon", StringComparison.OrdinalIgnoreCase)
            || error.Contains("FIRST PAYMENT DUE", StringComparison.OrdinalIgnoreCase)
            || error.Contains("pool number", StringComparison.OrdinalIgnoreCase))

    match attempt text with
    | Error errors when
        shouldRetryWithOcr errors
        && Environment.GetEnvironmentVariable "NHAMBS_OCR_RETRY" = "1"
        ->
        try
            let ocr = PdfText.extractOcrCover input

            if String.IsNullOrWhiteSpace ocr then
                Error errors
            else
                match attempt (text + "\n" + ocr) with
                | Error _ -> Error errors
                | result -> result
        with _ ->
            Error errors
    | result -> result

let private isRetryableParseError (errors: string list) =
    errors
    |> List.exists (fun error ->
        error.Contains("Weighted Average Interest Adjustment Date", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Weighted Average Maturity Date", StringComparison.OrdinalIgnoreCase)
        || error.Contains("FINAL PAYMENT", StringComparison.OrdinalIgnoreCase)
        || error.Contains("total number of mortgage loans", StringComparison.OrdinalIgnoreCase)
        || error.Contains("implausible", StringComparison.OrdinalIgnoreCase))

let private tryGetDate = tryGetJsonDate

/// Reject OCR-derived dates that are internally inconsistent (for example a
/// weighted-average maturity before the issue date or a final payment before
/// the first payment). This keeps a plausible-looking but wrong date out of
/// the output and lets the alternate-resolution retry try again.
let private hasPlausibleDates (json: string) =
    try
        let root = JsonNode.Parse(json).AsObject()
        let mbs = root.["mbs"].AsObject()
        let addendum = root.["addendum"].AsObject()

        match
            tryGetDate mbs "issueDate",
            tryGetDate mbs "settlementDate",
            tryGetDate mbs "firstPaymentDue",
            tryGetDate mbs "maturityDate",
            tryGetDate mbs "finalPaymentDue",
            tryGetDate addendum "weightedAverageInterestAdjustmentDate",
            tryGetDate addendum "weightedAverageMaturityDate"
        with
        | Some issue,
          Some settlement,
          Some firstPayment,
          Some maturity,
          Some finalPayment,
          Some weightedAverageInterestAdjustment,
          Some weightedAverageMaturity ->
            let inRange (date: DateTime) = date.Year >= 1970 && date.Year <= 2100

            [
                issue
                settlement
                firstPayment
                maturity
                finalPayment
                weightedAverageInterestAdjustment
                weightedAverageMaturity
            ]
            |> List.forall inRange
            && maturity > issue
            && firstPayment >= issue
            && firstPayment <= maturity.AddDays 60.0
            && finalPayment >= firstPayment
            && finalPayment >= maturity.AddYears(-1)
            && finalPayment <= maturity.AddDays 90.0
            && weightedAverageInterestAdjustment >= issue.AddYears(-10)
            && weightedAverageInterestAdjustment <= issue.AddYears(1)
            && weightedAverageMaturity >= issue
            && weightedAverageMaturity > weightedAverageInterestAdjustment.AddDays 30.0
            && weightedAverageMaturity <= maturity.AddYears(1)
        | _ -> false
    with _ ->
        false

/// A few low-resolution scans become readable at a different rasterization
/// resolution. Retry only the addendum pages so the common path stays fast.
let private parseTextResultWithDateRetry (expectedPoolNumber: string option) input text =
    let tryParse (candidateText: string) =
        match parseTextResult expectedPoolNumber input candidateText with
        | Ok(json, circular) when hasPlausibleDates json -> Ok(json, circular)
        | Ok _ -> Error [ "required dates are implausible in the source PDF" ]
        | Error errors -> Error errors

    match tryParse text with
    | Ok result -> Ok result
    | Error errors when isRetryableParseError errors ->
        let rec retry (resolutions: int list) (lastErrors: string list) =
            match resolutions with
            | [] -> Error lastErrors
            | dpi :: remaining ->
                try
                    let ocr = PdfText.extractOcrAddendumWithDpi dpi input

                    if String.IsNullOrWhiteSpace ocr then
                        retry remaining lastErrors
                    else
                        match tryParse (text + "\n" + ocr) with
                        | Ok result -> Ok result
                        | Error retryErrors -> retry remaining retryErrors
                with _ ->
                    retry remaining lastErrors

        retry [ 300; 150 ] errors
    | Error errors -> Error errors

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

        match parseTextResult None input text with
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
let private processCircular (outputDir: string) (poolNumbers: Map<string, string>) (input: string) : BatchReport.Entry =
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
            // Inspect the text layer before running OCR: blank coupon fields
            // and non-circular attachments can be rejected without rendering
            // (and OCRing) every page.
            let layout = PdfText.extractLayout input
            let layoutCompact = Regex.Replace(layout, @"\s+", " ")

            let layoutHasHeading =
                Regex.IsMatch(layoutCompact, @"(?i)information\s*circular")
                || Regex.IsMatch(layoutCompact, @"(?i)circulaire\s*d[’']?\s*information")
                || Regex.IsMatch(layoutCompact, @"(?i)nha\s*mbs\s*information")

            let layoutIsNonCircular =
                layoutCompact.Contains(
                    "SCHEDULE OF SUBSCRIBERS AND CONTRACTUAL AGREEMENT",
                    StringComparison.OrdinalIgnoreCase
                )
                || layoutCompact.Contains("SCHEDULE OF POOLED MORTGAGES", StringComparison.OrdinalIgnoreCase)
                || layoutCompact.Contains("PDF PORTFOLIO", StringComparison.OrdinalIgnoreCase)

            let layoutHasCover =
                layoutCompact.Contains("Aggregate Principal", StringComparison.OrdinalIgnoreCase)
                || layoutCompact.Contains("Montant global", StringComparison.OrdinalIgnoreCase)
                || layoutCompact.Contains("POOL NO", StringComparison.OrdinalIgnoreCase)

            let layoutHasBlankCouponLine =
                Regex.IsMatch(
                    layout,
                    @"(?im)^[ \t]*(?:%[ \t]*)?Mortgage-Backed[ \t]+Securities[ \t]*$",
                    RegexOptions.CultureInvariant
                )

            let layoutHasBlankCoupon =
                layoutHasHeading
                && layoutHasCover
                && layoutHasBlankCouponLine
                && not (CircularPdf.hasPrintedCoupon layout)

            let expectedPoolNumber =
                Path.GetFileNameWithoutExtension input
                |> fun value -> value.Trim().ToUpperInvariant()
                |> fun cusip -> poolNumbers |> Map.tryFind cusip

            if layoutIsNonCircular then
                BatchReport.entry fileName BatchReport.Status.Skipped (Some "not an NHA MBS information circular") []
            elif layoutHasBlankCoupon then
                BatchReport.entry
                    fileName
                    BatchReport.Status.Skipped
                    (Some "coupon rate is not printed in the source PDF")
                    []
            else
                let text = PdfText.extractTextWithLayout input layout
                let compact = Regex.Replace(text, @"\s+", " ")

                let hasCircularHeading =
                    Regex.IsMatch(compact, @"(?i)information\s*circular")
                    || Regex.IsMatch(compact, @"(?i)circulaire\s*d[’']?\s*information")
                    || Regex.IsMatch(compact, @"(?i)nha\s*mbs\s*information")

                let isNonCircular =
                    not hasCircularHeading
                    || compact.Contains(
                        "SCHEDULE OF SUBSCRIBERS AND CONTRACTUAL AGREEMENT",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || compact.Contains("SCHEDULE OF POOLED MORTGAGES", StringComparison.OrdinalIgnoreCase)
                    || compact.Contains("PDF PORTFOLIO", StringComparison.OrdinalIgnoreCase)

                if text.Trim().Length < 200 then
                    BatchReport.entry fileName BatchReport.Status.Skipped (Some "image-only or empty PDF") []
                elif isNonCircular then
                    BatchReport.entry
                        fileName
                        BatchReport.Status.Skipped
                        (Some "not an NHA MBS information circular")
                        []
                elif not (CircularPdf.hasPrintedCoupon text) then
                    BatchReport.entry
                        fileName
                        BatchReport.Status.Skipped
                        (Some "coupon rate is not printed in the source PDF")
                        []
                else
                    match parseTextResultWithDateRetry expectedPoolNumber input text with
                    | Error errors when isRetryableParseError errors ->
                        // The scan is too degraded to read a required printed
                        // value. Keep the reason in the report so the file can
                        // be revisited if a better copy appears.
                        BatchReport.entry
                            fileName
                            BatchReport.Status.Skipped
                            (Some(
                                "required value is not readable in the source PDF: "
                                + String.concat " | " errors
                            ))
                            []
                    | Error errors ->
                        BatchReport.entry fileName BatchReport.Status.Failed (Some(String.concat " | " errors)) []
                    | Ok(json, circular) ->
                        File.WriteAllText(output, json + Environment.NewLine)

                        BatchReport.entry
                            fileName
                            BatchReport.Status.Parsed
                            None
                            [ "poolNumber", circular.Mbs.PoolNumber ]
    with ex ->
        if ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase) then
            BatchReport.entry fileName BatchReport.Status.Skipped (Some "source file is missing") []
        elif ex.Message.Contains("Document stream is empty", StringComparison.OrdinalIgnoreCase) then
            BatchReport.entry fileName BatchReport.Status.Skipped (Some "unreadable PDF: empty document stream") []
        else
            BatchReport.entry fileName BatchReport.Status.Failed (Some ex.Message) []

/// Load the CUSIP-to-pool-number mapping from the source manifest. Accepts an
/// explicit path and then the conventional repository locations so `--process`
/// can recover pool numbers from low-resolution scans without extra options.
let private loadPoolNumbers (manifestPath: string option) =
    let candidates =
        [
            yield! manifestPath |> Option.toList
            yield "pool_urls.txt"
            yield Path.Combine("src", "NHAMBS.Mbs", "pool_urls.txt")
        ]

    match candidates |> List.tryFind File.Exists with
    | None -> Map.empty
    | Some path ->
        match Manifest.read path with
        | Error errors ->
            eprintfn "Ignoring unreadable pool manifest %s: %A" path errors
            Map.empty
        | Ok entries ->
            entries
            |> List.choose (fun entry ->
                entry.FileName
                |> Option.bind (fun cusip ->
                    let cusip = cusip.Trim().ToUpperInvariant()

                    if cusip = "" || entry.Name = "" then
                        None
                    else
                        Some(cusip, entry.Name)))
            |> Map.ofList

/// Parse every PDF directly under `inputDir`, writing one JSON file per
/// circular to `outputDir` plus a resumable `batch-report.json`. A second run
/// resumes from the report: files already parsed or skipped are not processed
/// again, while failures and deferred files are retried. PDFs are processed in
/// parallel and progress is printed as each one finishes.
let processBatchWithManifest (inputDir: string) (outputDir: string) (manifestPath: string option) =
    if not (Directory.Exists inputDir) then
        eprintfn "Input directory does not exist: %s" inputDir
        2
    else
        Directory.CreateDirectory outputDir |> ignore

        let poolNumbers = loadPoolNumbers manifestPath
        let reportPath = Path.Combine(outputDir, "batch-report.json")

        let previous =
            BatchReport.readOrImport reportPath (Path.Combine(outputDir, "batch-report.csv")) "circular-batch"
            |> Option.map (fun report ->
                let entries =
                    report.Entries
                    |> List.map (fun existing ->
                        let inputPath = Path.Combine(inputDir, existing.File)

                        if
                            not (BatchReport.Status.isComplete existing.Status)
                            && not (File.Exists inputPath)
                        then
                            BatchReport.entry
                                existing.File
                                BatchReport.Status.Pending
                                (Some "source file is missing")
                                []
                        else
                            existing)

                { report with Entries = entries })

        let previouslyCompleted = BatchReport.completedFiles previous

        let files = Directory.EnumerateFiles(inputDir, "*.pdf") |> Seq.sort |> Seq.toArray

        let previousStatuses =
            previous
            |> Option.map (fun report ->
                report.Entries
                |> List.map (fun existing -> existing.File, existing.Status)
                |> Map.ofList)
            |> Option.defaultValue Map.empty

        let pending =
            files
            |> Array.filter (fun input ->
                let fileName = Path.GetFileName input

                let output =
                    Path.Combine(outputDir, Path.GetFileNameWithoutExtension input + ".json")

                let wasSkipped =
                    match previousStatuses |> Map.tryFind fileName with
                    | Some BatchReport.Status.Skipped -> true
                    | _ -> false

                // Skipped files have no JSON output, so the previous status
                // alone is enough to keep them from being OCRed again. Parsed
                // files are reprocessed if their output was deleted.
                not (wasSkipped || (previouslyCompleted.Contains fileName && File.Exists output)))

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
                fun input -> session.Record(processCircular outputDir poolNumbers input)
            )
            |> ignore

        session.Finish()

        let parsed, skipped, deferred, failed = session.Counts

        eprintfn "Parsed %d, skipped %d, deferred %d, failed %d. Report: %s" parsed skipped deferred failed reportPath

        if failed = 0 then 0 else 1

/// Backwards-compatible entry point for callers that do not have an explicit
/// manifest path; the conventional repository locations are still searched.
let processBatch (inputDir: string) (outputDir: string) =
    processBatchWithManifest inputDir outputDir None

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

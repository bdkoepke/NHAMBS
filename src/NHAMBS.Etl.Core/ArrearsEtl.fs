module NHAMBS.Etl.ArrearsEtl

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Npgsql
open NHAMBS.Etl.Common
open NHAMBS.Etl.ArrearsTypes
open NHAMBS.Etl.ArrearsParser
open NHAMBS.Sources

let private cbaPage = "https://cba.ca/article/mortgages-in-arrears"

let private cmhcPage =
    "https://www.cmhc-schl.gc.ca/professionals/housing-markets-data-and-research/housing-data/data-tables/mortgage-and-debt/mortgage-delinquency-rate-canada-provinces-cmas"

let private workbookContentType =
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"

/// The CBA article links the current monthly workbook. Scraping the link keeps
/// the ETL current without hard-coding a month in the repository.
let private resolveCbaUrl () =
    let html = Source.readText cbaPage

    let matched =
        Regex.Match(
            html,
            """(?<url>(?:https://cba\.ca)?/Assets/[^"' ]*stat-mortgages-arrears-[^"' ]*\.xlsx)""",
            RegexOptions.IgnoreCase
        )

    if matched.Success then
        let url = matched.Groups.["url"].Value

        if url.StartsWith("http", StringComparison.OrdinalIgnoreCase) then
            url
        else
            "https://cba.ca" + url
    else
        failwith "Could not find the CBA mortgage-arrears workbook link."

/// CMHC exposes the current document URL through its report download API. The
/// page carries the document id; the API resolves it to the versioned asset.
let private resolveCmhcUrl () =
    let html = Source.readText cmhcPage

    let tag =
        Regex.Match(html, """<input[^>]*id="ReportDocumentId"[^>]*>""", RegexOptions.IgnoreCase)

    let value =
        if tag.Success then
            Regex.Match(tag.Value, """value="(?<id>\{[^}]+\})""", RegexOptions.IgnoreCase)
        else
            Match.Empty

    if not value.Success then
        failwith "Could not find the CMHC ReportDocumentId."

    let documentId = Uri.EscapeDataString value.Groups.["id"].Value

    let apiUrl =
        sprintf
            "https://www.cmhc-schl.gc.ca/api/Sitecore/PubsAndReports/GetReportFileUrl?documentId=%s&contextLanguage=en"
            documentId

    let response = Source.readText apiUrl

    match JsonDocument.Parse(response).RootElement with
    | value when value.ValueKind = JsonValueKind.String -> value.GetString()
    | _ -> failwith "CMHC did not return a file URL."

let private acquire (directory: string) =
    Directory.CreateDirectory directory |> ignore

    let cbaUrl = resolveCbaUrl ()
    let cmhcUrl = resolveCmhcUrl ()
    let cbaPath = Path.Combine(directory, "cba-mortgage-arrears.xlsx")
    let cmhcPath = Path.Combine(directory, "cmhc-mortgage-delinquency.xlsx")

    log (sprintf "  downloading CBA arrears workbook: %s" cbaUrl)
    Source.downloadFile cbaUrl cbaPath
    log (sprintf "  downloading CMHC delinquency workbook: %s" cmhcUrl)
    Source.downloadFile cmhcUrl cmhcPath

    cbaPath, cmhcPath, cbaUrl, cmhcUrl

let private upsertGeographies
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (geographies: ArrearsGeography[])
    =
    let distinct =
        geographies
        |> Array.distinctBy (fun geography -> geography.Code)
        |> Array.sortBy (fun geography -> geography.SortOrder)

    for geography in distinct do
        use command =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.arrears_geographies (geography_code, name, level, province_code, sort_order)
                VALUES (@code, @name, @level, @province, @sort_order)
                ON CONFLICT (geography_code) DO UPDATE
                SET level = EXCLUDED.level,
                    province_code = COALESCE(EXCLUDED.province_code, nhambs.arrears_geographies.province_code);
                """,
                connection,
                Transaction = transaction
            )

        command.Parameters.AddWithValue("code", geography.Code) |> ignore
        command.Parameters.AddWithValue("name", geography.Name) |> ignore
        command.Parameters.AddWithValue("level", geography.Level) |> ignore

        command.Parameters.AddWithValue(
            "province",
            geography.ProvinceCode
            |> Option.map box
            |> Option.defaultValue (box DBNull.Value)
        )
        |> ignore

        command.Parameters.AddWithValue("sort_order", geography.SortOrder) |> ignore
        command.ExecuteNonQuery() |> ignore

let private insertObservations
    (connection: NpgsqlConnection)
    (sourceDocumentId: int64)
    (observations: ArrearsObservation[])
    =
    use writer =
        new CopyWriter(
            connection,
            "nhambs.arrears_observations",
            [
                "source_code"
                "period_type"
                "period_start"
                "period_end"
                "geography_code"
                "lender_type"
                "insured_status"
                "product_type"
                "arrears_stage"
                "metric"
                "value"
                "is_suppressed"
                "source_document_id"
            ]
        )

    for observation in observations do
        writer.StartRow()
        writer.Write(observation.SourceCode)
        writer.Write(observation.PeriodType)
        writer.WriteDate(observation.PeriodStart)
        writer.WriteDate(observation.PeriodEnd)
        writer.Write(observation.GeographyCode)
        writer.Write(observation.LenderType)
        writer.Write(observation.InsuredStatus)
        writer.Write(observation.ProductType)
        writer.Write(observation.ArrearsStage)
        writer.Write(observation.Metric)
        writer.Write(observation.Value)
        writer.Write(observation.IsSuppressed)
        writer.Write(sourceDocumentId)

    writer.Complete() |> ignore

let private updateSource
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (sourceCode: string)
    (latestPeriodEnd: DateTime option)
    =
    use command =
        new NpgsqlCommand(
            """
            UPDATE nhambs.arrears_sources
            SET latest_period_end = @latest_period_end,
                last_loaded_at = now()
            WHERE source_code = @source_code;
            """,
            connection,
            Transaction = transaction
        )

    command.Parameters.AddWithValue(
        "latest_period_end",
        latestPeriodEnd
        |> Option.map (fun date -> box date)
        |> Option.defaultValue (box DBNull.Value)
    )
    |> ignore

    command.Parameters.AddWithValue("source_code", sourceCode) |> ignore
    command.ExecuteNonQuery() |> ignore

let private recordWarnings
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (runId: int64)
    (warnings: ArrearsWarning[])
    =
    for warning in warnings do
        use command =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.etl_anomalies
                    (run_id, dataset, record_key, field, raw_value, corrected_value, severity, message)
                VALUES
                    (@run_id, @dataset, @record_key, 'parser', @raw_value, NULL, 'warning', @message);
                """,
                connection,
                Transaction = transaction
            )

        command.Parameters.AddWithValue("run_id", runId) |> ignore

        command.Parameters.AddWithValue("dataset", "arrears-" + warning.SourceCode)
        |> ignore

        command.Parameters.AddWithValue("record_key", warning.Geography) |> ignore
        command.Parameters.AddWithValue("raw_value", warning.Period) |> ignore
        command.Parameters.AddWithValue("message", warning.Message) |> ignore
        command.ExecuteNonQuery() |> ignore

let run () =
    let connectionStringValue = connectionString ()
    let sourceRoot = resolvePath "downloads/arrears"

    use runConnection = new NpgsqlConnection(connectionStringValue)
    runConnection.Open()
    let runId = startRun runConnection "arrears" sourceRoot
    let stopwatch = Stopwatch.StartNew()

    try
        log "Arrears ETL: resolving provider workbooks..."

        let cbaPath, cmhcPath, cbaUrl, cmhcUrl = acquire sourceRoot

        log "  landing binary source documents in raw..."

        let cbaDocuments =
            writeRawDocuments runConnection runId "arrears-cba" workbookContentType [ cbaPath ]

        let cmhcDocuments =
            writeRawDocuments runConnection runId "arrears-cmhc" workbookContentType [ cmhcPath ]

        log "  parsing CBA workbook..."
        let cba = parseCba cbaPath

        log (sprintf "  CBA: %d observations, %d suppressed cells" cba.Observations.Length cba.Suppressed)

        log "  parsing CMHC workbook..."
        let cmhc = parseCmhc cmhcPath

        log (sprintf "  CMHC: %d observations, %d suppressed cells" cmhc.Observations.Length cmhc.Suppressed)

        use connection = new NpgsqlConnection(connectionStringValue)
        connection.Open()
        use transaction = connection.BeginTransaction()

        use truncate =
            new NpgsqlCommand(
                "TRUNCATE nhambs.arrears_observations RESTART IDENTITY;",
                connection,
                Transaction = transaction
            )

        truncate.ExecuteNonQuery() |> ignore

        upsertGeographies connection transaction (Array.append cba.Geographies cmhc.Geographies)

        insertObservations connection (sourceFileId cbaDocuments cbaPath) cba.Observations
        insertObservations connection (sourceFileId cmhcDocuments cmhcPath) cmhc.Observations

        updateSource connection transaction "cba" cba.LatestPeriodEnd
        updateSource connection transaction "cmhc" cmhc.LatestPeriodEnd

        recordWarnings connection transaction runId (Array.append cba.Warnings cmhc.Warnings)

        transaction.Commit()
        stopwatch.Stop()

        let rows = cba.Observations.Length + cmhc.Observations.Length

        completeRun runConnection runId 2 0 (int64 rows) None
        log (sprintf "Arrears ETL complete: %d observations in %.1fs" rows stopwatch.Elapsed.TotalSeconds)
        log (sprintf "  CBA source: %s" cbaUrl)
        log (sprintf "  CMHC source: %s" cmhcUrl)
        rows
    with ex ->
        stopwatch.Stop()
        completeRun runConnection runId 0 0 0L (Some ex.Message)
        eprintfn "Arrears ETL failed after %.1fs: %s" stopwatch.Elapsed.TotalSeconds ex.Message
        reraise ()

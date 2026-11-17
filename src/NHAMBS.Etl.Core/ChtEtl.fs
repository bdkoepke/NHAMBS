module NHAMBS.Etl.ChtEtl

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Npgsql
open NHAMBS.Etl.Common

let private designationFromFile (path: string) =
    let name = Path.GetFileNameWithoutExtension path
    let matchResult = Regex.Match(name, @"^series-\d+-(.+)$")

    if matchResult.Success then
        match matchResult.Groups.[1].Value with
        | "initial-issue" -> "Initial Issue"
        | value when value.StartsWith "re-opening-" ->
            "Re-opening " + value.Substring("re-opening-".Length).ToUpperInvariant()
        | value -> value.Replace('-', ' ')
    else
        name

let private insertIssue
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (sourceFileId: int64)
    (root: JsonElement)
    (asOf: string)
    =
    use command =
        new NpgsqlCommand(
            """
            INSERT INTO nhambs.cht_issues
                (series, settlement_date, coupon_display, coupon_kind, coupon_fixed_rate_percent,
                 coupon_index, coupon_spread_percent, maturity_date, currency, interest_payment_frequency,
                 issued_to_investors, issued_to_government_of_canada, total_issuance, outstanding,
                 identifier, outstanding_history_url, source_url, as_of_date, source_file_id, raw)
            VALUES
                (@series, @settlement_date, @coupon_display, @coupon_kind, @coupon_fixed,
                 @coupon_index, @coupon_spread, @maturity_date, @currency, @frequency,
                 @issued_investors, @issued_government, @total_issuance, @outstanding,
                 @identifier, @history_url, @source_url, @as_of_date, @source_file_id, @raw::jsonb)
            RETURNING id;
            """,
            connection,
            Transaction = transaction
        )

    let add (name: string) (value: obj) =
        command.Parameters.AddWithValue(name, value) |> ignore

    let addDate name path =
        add
            name
            (match optString path root |> Option.bind parseDate with
             | Some d -> box d
             | None -> box DBNull.Value)

    let addDecimal name path =
        add
            name
            (match optDecimal path root with
             | Some d -> box d
             | None -> box DBNull.Value)

    let addText name path = add name (stringOr path "" root)

    addText "series" [ "series" ]
    addDate "settlement_date" [ "settlementDate" ]
    addText "coupon_display" [ "coupon"; "displayText" ]
    addText "coupon_kind" [ "coupon"; "kind" ]
    addDecimal "coupon_fixed" [ "coupon"; "fixedRatePercent" ]
    addText "coupon_index" [ "coupon"; "index" ]
    addDecimal "coupon_spread" [ "coupon"; "spreadPercent" ]
    addDate "maturity_date" [ "maturityDate" ]
    addText "currency" [ "currency" ]
    addText "frequency" [ "interestPaymentFrequency" ]
    addDecimal "issued_investors" [ "issuedToInvestors" ]
    addDecimal "issued_government" [ "issuedToGovernmentOfCanada" ]
    addDecimal "total_issuance" [ "totalIssuance" ]
    addDecimal "outstanding" [ "outstanding" ]
    addText "identifier" [ "identifier" ]
    addText "history_url" [ "outstandingHistoryUrl" ]
    addText "source_url" [ "sourceUrl" ]

    add
        "as_of_date"
        (match parseDate asOf with
         | Some d -> box d
         | None -> box DBNull.Value)

    add "source_file_id" sourceFileId
    add "raw" (root.GetRawText())

    command.ExecuteScalar() :?> int

let private insertOffering
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (issueId: int)
    (root: JsonElement)
    =
    for offering in arrayItems [ "offeringCirculars" ] root do
        use command =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.cht_offering_circulars (issue_id, designation, url)
                VALUES (@issue_id, @designation, @url);
                """,
                connection,
                Transaction = transaction
            )

        command.Parameters.AddWithValue("issue_id", issueId) |> ignore

        command.Parameters.AddWithValue("designation", stringOr [ "designation" ] "" offering)
        |> ignore

        command.Parameters.AddWithValue("url", stringOr [ "url" ] "" offering) |> ignore
        command.ExecuteNonQuery() |> ignore

let private loadIssues
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (sourceFileId: int64)
    (path: string)
    =
    use document = readJson path
    let root = document.RootElement
    let asOf = stringOr [ "asOfDate" ] "" root
    let mutable count = 0

    for issue in arrayItems [ "issues" ] root do
        let id = insertIssue connection transaction sourceFileId issue asOf
        insertOffering connection transaction id issue
        count <- count + 1

    count

let private insertCircular
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (sourceFileId: int64)
    (path: string)
    =
    use document = readJson path
    let root = document.RootElement
    let series = stringOr [ "series" ] "" root
    let designation = designationFromFile path

    use command =
        new NpgsqlCommand(
            """
            INSERT INTO nhambs.cht_circulars
                (series, designation, document_date, currency, aggregate_principal_amount,
                 coupon_display, coupon_kind, coupon_fixed_rate_percent, coupon_index, coupon_spread_percent,
                 maturity_date, issue_price_percent, yield_percent, settlement_date, first_interest_payment_date,
                 interest_payment_frequency, minimum_denomination, issuer_name, guarantor_name,
                 legal_entity_identifier, listing_exchange, common_code, isin, cusip, source_url,
                 source_file_id, raw)
            VALUES
                (@series, @designation, @document_date, @currency, @aggregate_principal,
                 @coupon_display, @coupon_kind, @coupon_fixed, @coupon_index, @coupon_spread,
                 @maturity_date, @issue_price, @yield, @settlement_date, @first_interest,
                 @frequency, @minimum_denomination, @issuer_name, @guarantor_name,
                 @lei, @listing_exchange, @common_code, @isin, @cusip, @source_url,
                 @source_file_id, @raw::jsonb)
            RETURNING id;
            """,
            connection,
            Transaction = transaction
        )

    let add (name: string) (value: obj) =
        command.Parameters.AddWithValue(name, value) |> ignore

    let addDate name path =
        add
            name
            (match optString path root |> Option.bind parseDate with
             | Some d -> box d
             | None -> box DBNull.Value)

    let addDecimal name path =
        add
            name
            (match optDecimal path root with
             | Some d -> box d
             | None -> box DBNull.Value)

    let addText name path = add name (stringOr path "" root)

    add "series" series
    add "designation" designation
    addDate "document_date" [ "documentDate" ]
    addText "currency" [ "currency" ]
    addDecimal "aggregate_principal" [ "aggregatePrincipalAmount" ]
    addText "coupon_display" [ "coupon"; "displayText" ]
    addText "coupon_kind" [ "coupon"; "kind" ]
    addDecimal "coupon_fixed" [ "coupon"; "fixedRatePercent" ]
    addText "coupon_index" [ "coupon"; "index" ]
    addDecimal "coupon_spread" [ "coupon"; "spreadPercent" ]
    addDate "maturity_date" [ "maturityDate" ]
    addDecimal "issue_price" [ "issuePricePercent" ]
    addDecimal "yield" [ "yieldPercent" ]
    addDate "settlement_date" [ "settlementDate" ]
    addDate "first_interest" [ "firstInterestPaymentDate" ]
    addText "frequency" [ "interestPaymentFrequency" ]
    addDecimal "minimum_denomination" [ "minimumDenomination" ]
    addText "issuer_name" [ "issuerName" ]
    addText "guarantor_name" [ "guarantorName" ]
    addText "lei" [ "legalEntityIdentifier" ]
    addText "listing_exchange" [ "listingExchange" ]
    addText "common_code" [ "identifiers"; "commonCode" ]
    addText "isin" [ "identifiers"; "isin" ]
    addText "cusip" [ "identifiers"; "cusip" ]
    addText "source_url" [ "sourceUrl" ]
    add "source_file_id" sourceFileId
    add "raw" (root.GetRawText())

    let id = command.ExecuteScalar() :?> int

    for rating in arrayItems [ "ratings" ] root do
        use ratingCommand =
            new NpgsqlCommand(
                "INSERT INTO nhambs.cht_circular_ratings (circular_id, rating, agency) VALUES (@id, @rating, @agency);",
                connection,
                Transaction = transaction
            )

        ratingCommand.Parameters.AddWithValue("id", id) |> ignore

        ratingCommand.Parameters.AddWithValue("rating", stringOr [ "rating" ] "" rating)
        |> ignore

        ratingCommand.Parameters.AddWithValue("agency", stringOr [ "agency" ] "" rating)
        |> ignore

        ratingCommand.ExecuteNonQuery() |> ignore

    let paymentDates = arrayItems [ "interestPaymentDates" ] root

    paymentDates
    |> List.iteri (fun index value ->
        use dateCommand =
            new NpgsqlCommand(
                "INSERT INTO nhambs.cht_circular_payment_dates (circular_id, sequence, label) VALUES (@id, @seq, @label);",
                connection,
                Transaction = transaction
            )

        dateCommand.Parameters.AddWithValue("id", id) |> ignore
        dateCommand.Parameters.AddWithValue("seq", index) |> ignore
        dateCommand.Parameters.AddWithValue("label", strOrEmpty value) |> ignore
        dateCommand.ExecuteNonQuery() |> ignore)

    id

let private insertHistory
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (sourceFileId: int64)
    (path: string)
    =
    use document = readJson path
    let root = document.RootElement

    use command =
        new NpgsqlCommand(
            """
            INSERT INTO nhambs.cht_outstanding_history
                (series, title, as_of_date, coupon_display, coupon_kind, coupon_fixed_rate_percent,
                 coupon_index, coupon_spread_percent, maturity_date, identifier, source_url,
                 source_file_id, raw)
            VALUES
                (@series, @title, @as_of_date, @coupon_display, @coupon_kind, @coupon_fixed,
                 @coupon_index, @coupon_spread, @maturity_date, @identifier, @source_url,
                 @source_file_id, @raw::jsonb)
            RETURNING id;
            """,
            connection,
            Transaction = transaction
        )

    let add (name: string) (value: obj) =
        command.Parameters.AddWithValue(name, value) |> ignore

    let addDate name path =
        add
            name
            (match optString path root |> Option.bind parseDate with
             | Some d -> box d
             | None -> box DBNull.Value)

    let addDecimal name path =
        add
            name
            (match optDecimal path root with
             | Some d -> box d
             | None -> box DBNull.Value)

    let addText name path = add name (stringOr path "" root)

    addText "series" [ "series" ]
    addText "title" [ "title" ]
    addDate "as_of_date" [ "asOfDate" ]
    addText "coupon_display" [ "coupon"; "displayText" ]
    addText "coupon_kind" [ "coupon"; "kind" ]
    addDecimal "coupon_fixed" [ "coupon"; "fixedRatePercent" ]
    addText "coupon_index" [ "coupon"; "index" ]
    addDecimal "coupon_spread" [ "coupon"; "spreadPercent" ]
    addDate "maturity_date" [ "maturityDate" ]
    addText "identifier" [ "identifier" ]
    addText "source_url" [ "sourceUrl" ]
    add "source_file_id" sourceFileId
    add "raw" (root.GetRawText())

    let historyId = command.ExecuteScalar() :?> int
    let cancellations = arrayItems [ "cancellations" ] root

    cancellations
    |> List.iteri (fun index cancellation ->
        use cancellationCommand =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.cht_outstanding_history_cancellations
                    (history_id, sequence, from_date, to_date, cancelled_amount)
                VALUES (@history_id, @sequence, @from_date, @to_date, @cancelled_amount);
                """,
                connection,
                Transaction = transaction
            )

        cancellationCommand.Parameters.AddWithValue("history_id", historyId) |> ignore
        cancellationCommand.Parameters.AddWithValue("sequence", index) |> ignore

        cancellationCommand.Parameters.AddWithValue(
            "from_date",
            (match optString [ "fromDate" ] cancellation |> Option.bind parseDate with
             | Some date -> box date
             | None -> box DBNull.Value)
        )
        |> ignore

        cancellationCommand.Parameters.AddWithValue(
            "to_date",
            (match optString [ "toDate" ] cancellation |> Option.bind parseDate with
             | Some date -> box date
             | None -> box DBNull.Value)
        )
        |> ignore

        cancellationCommand.Parameters.AddWithValue(
            "cancelled_amount",
            (match optDecimal [ "cancelledAmount" ] cancellation with
             | Some amount -> box amount
             | None -> box DBNull.Value)
        )
        |> ignore

        cancellationCommand.ExecuteNonQuery() |> ignore)

    historyId, cancellations.Length

let private loadHistory
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (sourceFileId: int64)
    (path: string)
    =
    insertHistory connection transaction sourceFileId path

let run (dataDir: string) =
    let debtPath = Path.Combine(dataDir, "debt-issues.json")
    let circularsDir = Path.Combine(dataDir, "cht_circulars")
    let historyDir = Path.Combine(dataDir, "cht_history")

    if not (File.Exists debtPath) then
        failwithf "CHT debt issues file not found: %s" debtPath

    let circularFiles =
        if Directory.Exists circularsDir then
            Directory.EnumerateFiles(circularsDir, "*.json")
            |> Seq.filter (fun path ->
                let name = Path.GetFileName path
                name <> "batch-report.json" && name <> "circulars-report.json")
            |> Seq.sort
            |> Seq.toArray
        else
            [||]

    let historyFiles =
        if Directory.Exists historyDir then
            Directory.EnumerateFiles(historyDir, "*.json") |> Seq.sort |> Seq.toArray
        else
            [||]

    let connectionStringValue = connectionString ()
    use runConnection = new NpgsqlConnection(connectionStringValue)
    runConnection.Open()
    let runId = startRun runConnection "cht" dataDir
    let stopwatch = Stopwatch.StartNew()

    try
        log "  landing source files in raw..."

        let sourceIds =
            writeRawFiles
                runConnection
                runId
                "cht"
                ([ debtPath ] @ (circularFiles |> Array.toList) @ (historyFiles |> Array.toList))

        use connection = new NpgsqlConnection(connectionStringValue)
        connection.Open()

        use truncate =
            new NpgsqlCommand(
                "TRUNCATE nhambs.cht_issues, nhambs.cht_circulars, nhambs.cht_outstanding_history CASCADE;",
                connection
            )

        truncate.ExecuteNonQuery() |> ignore

        use transaction = connection.BeginTransaction()
        log "  loading CHT outstanding issues..."

        let issueCount =
            loadIssues connection transaction (sourceFileId sourceIds debtPath) debtPath

        log "  loading CHT offering circulars..."

        let mutable circularCount = 0
        let mutable skipped = 0

        for path in circularFiles do
            try
                insertCircular connection transaction (sourceFileId sourceIds path) path
                |> ignore

                circularCount <- circularCount + 1
            with ex ->
                skipped <- skipped + 1
                eprintfn "  skipped %s: %s" (Path.GetFileName path) ex.Message

        log "  loading CHT outstanding history..."

        let mutable historyCount = 0
        let mutable cancellationCount = 0

        for path in historyFiles do
            try
                let _, cancellations =
                    loadHistory connection transaction (sourceFileId sourceIds path) path

                historyCount <- historyCount + 1
                cancellationCount <- cancellationCount + cancellations
            with ex ->
                skipped <- skipped + 1
                eprintfn "  skipped %s: %s" (Path.GetFileName path) ex.Message

        transaction.Commit()
        stopwatch.Stop()
        let rows = issueCount + circularCount + historyCount + cancellationCount

        completeRun runConnection runId (1 + circularFiles.Length + historyFiles.Length) skipped (int64 rows) None

        log (
            sprintf
                "CHT ETL complete: %d issues, %d circulars, %d history series and %d cancellations in %.1fs"
                issueCount
                circularCount
                historyCount
                cancellationCount
                stopwatch.Elapsed.TotalSeconds
        )

        rows
    with ex ->
        stopwatch.Stop()
        completeRun runConnection runId 0 0 0L (Some ex.Message)
        eprintfn "CHT ETL failed after %.1fs: %s" stopwatch.Elapsed.TotalSeconds ex.Message
        reraise ()

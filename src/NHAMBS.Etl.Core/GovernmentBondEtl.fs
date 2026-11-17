module NHAMBS.Etl.GovernmentBondEtl

open System
open System.Diagnostics
open System.IO
open Npgsql
open NHAMBS.Etl.Common
open NHAMBS.Etl.GovernmentBondTypes
open NHAMBS.Etl.GovernmentBondParser
open NHAMBS.Sources

let private gocUrl =
    "https://www.bankofcanada.ca/valet/observations/group/GOC_OUTSTANDING/json"

let private pbppUrl =
    "https://www.bankofcanada.ca/valet/observations/group/PBPP_TRANSACTION_DATA/json"

let private acquire (directory: string) =
    Directory.CreateDirectory directory |> ignore
    let gocPath = Path.Combine(directory, "goc-outstanding.json")
    let pbppPath = Path.Combine(directory, "pbpp-transactions.json")

    log (sprintf "  downloading Government of Canada outstanding securities: %s" gocUrl)
    Source.downloadFile gocUrl gocPath
    log (sprintf "  downloading Provincial Bond Purchase Program transactions: %s" pbppUrl)
    Source.downloadFile pbppUrl pbppPath
    gocPath, pbppPath

let private insertIssues
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (sourceDocumentId: int64)
    (asOf: DateTime)
    (issues: GovernmentBondIssue[])
    =
    use delete =
        new NpgsqlCommand(
            """
            DELETE FROM nhambs.government_bond_issues
            WHERE program_code = 'goc' AND as_of_date = @as_of;
            """,
            connection,
            Transaction = transaction
        )

    delete.Parameters.AddWithValue("as_of", asOf) |> ignore
    delete.ExecuteNonQuery() |> ignore

    use writer =
        new CopyWriter(
            connection,
            "nhambs.government_bond_issues",
            [
                "program_code"
                "isin"
                "security_type"
                "instrument_type"
                "coupon_rate_percent"
                "issue_date"
                "maturity_date"
                "outstanding_amount"
                "inflation_adjustment"
                "outstanding_inflation_adjusted"
                "issuance_term_bucket"
                "maturity_term_bucket"
                "as_of_date"
                "source_document_id"
            ]
        )

    for issue in issues do
        writer.StartRow()
        writer.Write(issue.ProgramCode)
        writer.Write(issue.Isin)
        writer.Write(issue.SecurityType)
        writer.Write(issue.InstrumentType)
        writer.Write(issue.CouponRatePercent)
        writer.WriteDate(issue.IssueDate)
        writer.WriteDate(issue.MaturityDate)
        writer.Write(issue.OutstandingAmount)
        writeOption writer issue.InflationAdjustment (fun value -> writer.Write(value))
        writer.Write(issue.OutstandingInflationAdjusted)
        writeOption writer issue.IssuanceTermBucket (fun value -> writer.Write(value))
        writeOption writer issue.MaturityTermBucket (fun value -> writer.Write(value))
        writer.WriteDate(issue.AsOfDate)
        writer.Write(sourceDocumentId)

    writer.Complete() |> ignore

let private insertProvincialTransactions
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (sourceDocumentId: int64)
    (transactions: ProvincialBondTransaction[])
    =
    use delete =
        new NpgsqlCommand(
            "DELETE FROM nhambs.provincial_bond_transactions WHERE program_code = 'pbpp';",
            connection,
            Transaction = transaction
        )

    delete.ExecuteNonQuery() |> ignore

    use writer =
        new CopyWriter(
            connection,
            "nhambs.provincial_bond_transactions",
            [
                "program_code"
                "issuer_name"
                "province_code"
                "isin"
                "coupon_rate_percent"
                "maturity_date"
                "trade_date"
                "settlement_date"
                "amount_purchased"
                "purchase_price"
                "purchase_yield_percent"
                "source_document_id"
            ]
        )

    for item in transactions do
        writer.StartRow()
        writer.Write("pbpp")
        writer.Write(item.IssuerName)
        writeOption writer item.ProvinceCode (fun value -> writer.Write(value))
        writer.Write(item.Isin)
        writer.Write(item.CouponRatePercent)
        writer.WriteDate(item.MaturityDate)
        writer.WriteDate(item.TradeDate)
        writeOption writer item.SettlementDate (fun value -> writer.WriteDate(value))
        writer.Write(item.AmountPurchased)
        writeOption writer item.PurchasePrice (fun value -> writer.Write(value))
        writeOption writer item.PurchaseYieldPercent (fun value -> writer.Write(value))
        writer.Write(sourceDocumentId)

    writer.Complete() |> ignore

let private updateProgram
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (programCode: string)
    (asOf: DateTime)
    =
    use command =
        new NpgsqlCommand(
            """
            UPDATE nhambs.government_bond_programs
            SET latest_as_of_date = @as_of,
                last_loaded_at = now()
            WHERE program_code = @program_code;
            """,
            connection,
            Transaction = transaction
        )

    command.Parameters.AddWithValue("as_of", asOf) |> ignore
    command.Parameters.AddWithValue("program_code", programCode) |> ignore
    command.ExecuteNonQuery() |> ignore

let private recordWarnings
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (runId: int64)
    (dataset: string)
    (recordKey: string)
    (warnings: string[])
    =
    for warning in warnings do
        use command =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.etl_anomalies
                    (run_id, dataset, record_key, field, raw_value, corrected_value, severity, message)
                VALUES
                    (@run_id, @dataset, @record_key, 'parser', '', NULL, 'warning', @message);
                """,
                connection,
                Transaction = transaction
            )

        command.Parameters.AddWithValue("run_id", runId) |> ignore
        command.Parameters.AddWithValue("dataset", dataset) |> ignore
        command.Parameters.AddWithValue("record_key", recordKey) |> ignore
        command.Parameters.AddWithValue("message", warning) |> ignore
        command.ExecuteNonQuery() |> ignore

let run () =
    let connectionStringValue = connectionString ()
    let sourceRoot = resolvePath "downloads/government-bonds"

    use connection = new NpgsqlConnection(connectionStringValue)
    connection.Open()
    let runId = startRun connection "government-bonds" sourceRoot
    let stopwatch = Stopwatch.StartNew()

    try
        log "Government bond ETL: resolving Bank of Canada securities..."

        let gocPath, pbppPath = acquire sourceRoot

        let gocDocuments =
            writeRawDocuments connection runId "government-bonds-goc" "application/json" [ gocPath ]

        let pbppDocuments =
            writeRawDocuments connection runId "government-bonds-pbpp" "application/json" [ pbppPath ]

        let goc = parseGocOutstanding (File.ReadAllText gocPath)
        let pbpp = parsePbppTransactions (File.ReadAllText pbppPath)

        match goc.AsOfDate, pbpp.LatestTradeDate with
        | Some gocAsOf, Some pbppLatest ->
            if goc.Issues.Length = 0 then
                failwithf "No government bond issues found for %s." (gocAsOf.ToString("yyyy-MM-dd"))

            if pbpp.Transactions.Length = 0 then
                failwith "No provincial bond transactions were parsed."

            log (
                sprintf
                    "  parsed %d federal issues as of %s and %d provincial transactions through %s"
                    goc.Issues.Length
                    (gocAsOf.ToString("yyyy-MM-dd"))
                    pbpp.Transactions.Length
                    (pbppLatest.ToString("yyyy-MM-dd"))
            )

            use transaction = connection.BeginTransaction()
            insertIssues connection transaction (sourceFileId gocDocuments gocPath) gocAsOf goc.Issues
            insertProvincialTransactions connection transaction (sourceFileId pbppDocuments pbppPath) pbpp.Transactions
            updateProgram connection transaction "goc" gocAsOf
            updateProgram connection transaction "pbpp" pbppLatest
            recordWarnings connection transaction runId "government-bonds-goc" "goc" goc.Warnings
            recordWarnings connection transaction runId "government-bonds-pbpp" "pbpp" pbpp.Warnings
            transaction.Commit()

            stopwatch.Stop()
            let rows = goc.Issues.Length + pbpp.Transactions.Length
            completeRun connection runId 2 0 (int64 rows) None

            log (
                sprintf
                    "Government bond ETL complete: %d federal issues and %d provincial transactions in %.1fs"
                    goc.Issues.Length
                    pbpp.Transactions.Length
                    stopwatch.Elapsed.TotalSeconds
            )

            rows
        | _ -> failwith "The Bank of Canada response did not contain usable as-of dates."
    with ex ->
        stopwatch.Stop()
        completeRun connection runId 0 0 0L (Some ex.Message)
        eprintfn "Government bond ETL failed after %.1fs: %s" stopwatch.Elapsed.TotalSeconds ex.Message
        reraise ()

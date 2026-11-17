module NHAMBS.Etl.GovernmentBondEtl

open System
open System.Diagnostics
open System.IO
open Npgsql
open NHAMBS.Etl.Common
open NHAMBS.Etl.GovernmentBondTypes
open NHAMBS.Etl.GovernmentBondParser
open NHAMBS.Sources

let private sourceUrl =
    "https://www.bankofcanada.ca/valet/observations/group/GOC_OUTSTANDING/json"

let private acquire (directory: string) =
    Directory.CreateDirectory directory |> ignore
    let path = Path.Combine(directory, "goc-outstanding.json")

    log (sprintf "  downloading Government of Canada outstanding securities: %s" sourceUrl)
    Source.downloadFile sourceUrl path
    path

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

let private updateProgram (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (asOf: DateTime) =
    use command =
        new NpgsqlCommand(
            """
            UPDATE nhambs.government_bond_programs
            SET latest_as_of_date = @as_of,
                last_loaded_at = now()
            WHERE program_code = 'goc';
            """,
            connection,
            Transaction = transaction
        )

    command.Parameters.AddWithValue("as_of", asOf) |> ignore
    command.ExecuteNonQuery() |> ignore

let private recordWarnings
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (runId: int64)
    (warnings: string[])
    =
    for warning in warnings do
        use command =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.etl_anomalies
                    (run_id, dataset, record_key, field, raw_value, corrected_value, severity, message)
                VALUES
                    (@run_id, 'government-bonds-goc', 'goc', 'parser', '', NULL, 'warning', @message);
                """,
                connection,
                Transaction = transaction
            )

        command.Parameters.AddWithValue("run_id", runId) |> ignore
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
        log "Government bond ETL: resolving Bank of Canada outstanding securities..."

        let path = acquire sourceRoot

        let documents =
            writeRawDocuments connection runId "government-bonds-goc" "application/json" [ path ]

        let json = File.ReadAllText path
        let parsed = parseGocOutstanding json

        match parsed.AsOfDate with
        | None -> failwith "The Bank of Canada response did not contain an as-of date."
        | Some asOf ->
            if parsed.Issues.Length = 0 then
                failwithf "No government bond issues found for %s." (asOf.ToString("yyyy-MM-dd"))

            log (sprintf "  parsed %d issues as of %s" parsed.Issues.Length (asOf.ToString("yyyy-MM-dd")))

            use transaction = connection.BeginTransaction()
            insertIssues connection transaction (sourceFileId documents path) asOf parsed.Issues
            updateProgram connection transaction asOf
            recordWarnings connection transaction runId parsed.Warnings
            transaction.Commit()

            stopwatch.Stop()
            completeRun connection runId 1 0 (int64 parsed.Issues.Length) None

            log (
                sprintf
                    "Government bond ETL complete: %d issues in %.1fs"
                    parsed.Issues.Length
                    stopwatch.Elapsed.TotalSeconds
            )

            parsed.Issues.Length
    with ex ->
        stopwatch.Stop()
        completeRun connection runId 0 0 0L (Some ex.Message)
        eprintfn "Government bond ETL failed after %.1fs: %s" stopwatch.Elapsed.TotalSeconds ex.Message
        reraise ()

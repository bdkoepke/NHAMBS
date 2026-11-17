module NHAMBS.Etl.MbsEtl

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.Json
open FSharp.Data
open Npgsql
open NHAMBS.Etl.Common
open NHAMBS.Etl.Anomalies

type private PoolFile = { Path: string; Cusip: string }

let private poolFiles (poolsDir: string) =
    ensureDirectory poolsDir

    Directory.EnumerateFiles(poolsDir, "*.json")
    |> Seq.choose (fun path ->
        let name = Path.GetFileNameWithoutExtension path

        if name = "batch-report" || name.Length <> 9 then
            None
        else
            Some
                {
                    Path = path
                    Cusip = name.ToUpperInvariant()
                })
    |> Seq.toArray

let private sourceUrls (manifestPath: string) =
    let urls = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

    if File.Exists manifestPath then
        let csv = CsvFile.Load(manifestPath, hasHeaders = true)

        for row in csv.Rows do
            let cusip = row.GetColumn "CUSIP"
            let url = row.GetColumn "Url"

            if not (String.IsNullOrWhiteSpace cusip) && not (urls.ContainsKey(cusip.Trim())) then
                urls.[cusip.Trim()] <- url.Trim()

    urls

let private iterPools (files: PoolFile[]) (action: PoolFile -> JsonElement -> unit) =
    let mutable skipped = 0
    let mutable processed = 0

    for file in files do
        try
            use document = readJson file.Path
            let root = document.RootElement

            match prop "mbs" root with
            | Some _ ->
                action file root
                processed <- processed + 1
            | None -> skipped <- skipped + 1
        with ex ->
            skipped <- skipped + 1
            eprintfn "  skipped %s: %s" (Path.GetFileName file.Path) ex.Message

        if processed > 0 && processed % 5000 = 0 then
            log (sprintf "  processed %d pool files" processed)

    processed, skipped

let private resolveIssuerId
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (displayName: string)
    (key: string)
    =
    let canonicalKey, canonicalName =
        use lookup =
            new NpgsqlCommand(
                "SELECT canonical_key, canonical_name FROM nhambs.resolve_issuer_alias(@key);",
                connection,
                Transaction = transaction
            )

        lookup.Parameters.AddWithValue("key", key) |> ignore
        use reader = lookup.ExecuteReader()

        if reader.Read() then
            reader.GetString(0), reader.GetString(1)
        else
            key, displayName

    use upsert =
        new NpgsqlCommand(
            """
            INSERT INTO nhambs.issuers (issuer_key, canonical_name)
            VALUES (@key, @name)
            ON CONFLICT (issuer_key) DO UPDATE SET canonical_name = EXCLUDED.canonical_name
            RETURNING issuer_id;
            """,
            connection,
            Transaction = transaction
        )

    upsert.Parameters.AddWithValue("key", canonicalKey) |> ignore
    upsert.Parameters.AddWithValue("name", canonicalName) |> ignore
    upsert.ExecuteScalar() :?> int

let private resolveInsurerId (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (name: string) =
    use upsert =
        new NpgsqlCommand(
            """
            INSERT INTO nhambs.insurers (insurer_key, canonical_name)
            VALUES (@key, @name)
            ON CONFLICT (insurer_key) DO UPDATE SET canonical_name = EXCLUDED.canonical_name
            RETURNING insurer_id;
            """,
            connection,
            Transaction = transaction
        )

    upsert.Parameters.AddWithValue("key", name.ToLowerInvariant()) |> ignore
    upsert.Parameters.AddWithValue("name", name) |> ignore
    upsert.ExecuteScalar() :?> int

let private ensureRateBand (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (band: CanonicalRateBand) =
    use command =
        new NpgsqlCommand(
            """
            INSERT INTO nhambs.rate_bands (band_key, label, lower_percent, upper_percent)
            VALUES (@key, @label, @lower, @upper)
            ON CONFLICT (band_key) DO NOTHING;
            """,
            connection,
            Transaction = transaction
        )

    let nullable (value: decimal option) =
        value |> Option.map box |> Option.defaultValue (box DBNull.Value)

    command.Parameters.AddWithValue("key", band.Key) |> ignore
    command.Parameters.AddWithValue("label", band.Label) |> ignore
    command.Parameters.AddWithValue("lower", nullable band.LowerPercent) |> ignore
    command.Parameters.AddWithValue("upper", nullable band.UpperPercent) |> ignore
    command.ExecuteNonQuery() |> ignore

/// Resolve every issuer, insurer and rate band before the COPY streams start.
/// PostgreSQL does not allow another command on a connection while a binary
/// COPY is open, so the reference rows have to exist first.
let private resolveReferenceMaps (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (files: PoolFile[]) =
    let issuerIds = Dictionary<string, int>(StringComparer.Ordinal)
    let insurerIds = Dictionary<string, int>(StringComparer.Ordinal)
    let rateBandKeys = HashSet<string>(StringComparer.Ordinal)

    iterPools files (fun _ root ->
        let issuer =
            optString [ "parties"; "issuerName" ] root
            |> Option.map issuerDisplayName
            |> Option.defaultValue "Unknown issuer"

        let issuerKey = normalizeIssuer issuer

        if not (issuerIds.ContainsKey issuerKey) then
            issuerIds.[issuerKey] <- resolveIssuerId connection transaction issuer issuerKey

        for insurer in arrayItems [ "addendum"; "insurerProfile"; "insurers" ] root do
            let name = canonicalInsurerName (stringOr [ "name" ] "" insurer)

            if not (insurerIds.ContainsKey name) then
                insurerIds.[name] <- resolveInsurerId connection transaction name

        for band in arrayItems [ "addendum"; "interestAdjustmentProfile"; "rateBands" ] root do
            let canonical = canonicalRateBand (stringOr [ "rateBand" ] "" band)

            if rateBandKeys.Add canonical.Key then
                ensureRateBand connection transaction canonical)
    |> ignore

    issuerIds, insurerIds

let private writeMbsPools
    (connection: NpgsqlConnection)
    (issuerIds: Dictionary<string, int>)
    (files: PoolFile[])
    (urls: Dictionary<string, string>)
    (sourceIds: Dictionary<string, int64>)
    =
    let started = DateTime.UtcNow

    let columns =
        [
            "cusip"
            "pool_number"
            "issuer_id"
            "custodian"
            "rate_type"
            "pool_type"
            "form_version"
            "language"
            "wac_rate_type"
            "coupon_display"
            "coupon_kind"
            "coupon_index"
            "coupon_spread_percent"
            "coupon_fixed_rate_percent"
            "aggregate_principal_amount"
            "issue_date"
            "settlement_date"
            "first_payment_due"
            "maturity_date"
            "final_payment_due"
            "total_number_of_mortgage_loans"
            "weighted_average_interest_adjustment_date"
            "weighted_average_maturity_date"
            "homeowner_percent"
            "multiple_family_percent"
            "social_housing_percent"
            "ram_by_amortization"
            "ram_by_term"
            "interest_adjustment_dates"
            "interest_adjustment_totals"
            "interest_adjustment_percent"
            "interest_adjustment_wac"
            "prepayment_bona_fide_exempt"
            "prepayment_section_a"
            "prepayment_section_b"
            "prepayment_other_disclosures"
            "source_url"
            "source_file"
            "source_file_id"
            "source_modified_at"
            "ingested_at"
            "raw"
        ]

    use writer = new CopyWriter(connection, "nhambs.mbs_pools", columns)

    let stringOpt (path: string list) (root: JsonElement) =
        writeOption writer (optString path root) (fun value -> writer.Write(value))

    let decimalOpt (path: string list) (root: JsonElement) =
        writeOption writer (optDecimal path root) (fun value -> writer.Write(value))

    let intOpt (path: string list) (root: JsonElement) =
        writeOption writer (optInt path root) (fun value -> writer.Write(value))

    let dateOpt (path: string list) (root: JsonElement) =
        writeOption writer (optString path root |> Option.bind parseDate) (fun value -> writer.WriteDate(value))

    let dateValue (path: string list) (root: JsonElement) =
        optString path root |> Option.bind parseDate

    let jsonOpt (path: string list) (root: JsonElement) =
        writeOption writer (optElement path root |> Option.map (fun value -> value.GetRawText())) (fun value ->
            writer.WriteJson(value))

    let detectedAnomalies = ResizeArray<Anomaly>()
    let today = DateTime.UtcNow.Date

    let written, skipped =
        iterPools files (fun file root ->
            writer.StartRow()

            let issuer =
                optString [ "parties"; "issuerName" ] root
                |> Option.map issuerDisplayName
                |> Option.defaultValue "Unknown issuer"

            let issuerKey = normalizeIssuer issuer

            let issuerId =
                match issuerIds.TryGetValue issuerKey with
                | true, value -> value
                | _ -> failwithf "Issuer was not resolved before COPY: %s" issuer

            let sourceUrl =
                match urls.TryGetValue file.Cusip with
                | true, value -> Some value
                | _ -> None

            let modified = File.GetLastWriteTimeUtc file.Path

            let dates =
                {
                    IssueDate = dateValue [ "mbs"; "issueDate" ] root
                    SettlementDate = dateValue [ "mbs"; "settlementDate" ] root
                    FirstPaymentDue = dateValue [ "mbs"; "firstPaymentDue" ] root
                    MaturityDate = dateValue [ "mbs"; "maturityDate" ] root
                    FinalPaymentDue = dateValue [ "mbs"; "finalPaymentDue" ] root
                }

            let normalized = normalizeMbsDates "mbs" file.Cusip today dates
            detectedAnomalies.AddRange normalized.Anomalies

            let _, scheduledWamAnomaly =
                optDecimal
                    [
                        "addendum"
                        "scheduledMaturity"
                        "total"
                        "weightedAverageRemainingAmortizationMonths"
                    ]
                    root
                |> normalizeMbsAmortization "mbs" file.Cusip "scheduled_wam"

            scheduledWamAnomaly |> Option.iter detectedAnomalies.Add

            let _, scheduledIntroductoryAnomaly =
                optDecimal
                    [
                        "addendum"
                        "scheduledMaturity"
                        "total"
                        "weightedAverageIntroductoryPeriodsRemainingMonths"
                    ]
                    root
                |> normalizeMbsIntroductoryMonths "mbs" file.Cusip "scheduled_introductory_months"

            scheduledIntroductoryAnomaly |> Option.iter detectedAnomalies.Add

            writer.Write(file.Cusip)
            writer.Write(stringOr [ "mbs"; "poolNumber" ] "" root)
            writer.Write(issuerId)
            stringOpt [ "parties"; "custodian" ] root
            stringOpt [ "rateType" ] root
            stringOpt [ "poolType" ] root
            stringOpt [ "formVersion" ] root
            stringOpt [ "language" ] root
            stringOpt [ "wacRateType" ] root
            stringOpt [ "mbs"; "coupon"; "displayText" ] root
            stringOpt [ "mbs"; "coupon"; "kind" ] root
            stringOpt [ "mbs"; "coupon"; "index" ] root
            decimalOpt [ "mbs"; "coupon"; "spreadPercent" ] root
            decimalOpt [ "mbs"; "coupon"; "fixedRatePercent" ] root
            writer.Write(optDecimal [ "mbs"; "aggregatePrincipalAmount" ] root |> Option.defaultValue 0m)
            writeOption writer normalized.Dates.IssueDate (fun value -> writer.WriteDate(value))
            writeOption writer normalized.Dates.SettlementDate (fun value -> writer.WriteDate(value))
            writeOption writer normalized.Dates.FirstPaymentDue (fun value -> writer.WriteDate(value))
            writeOption writer normalized.Dates.MaturityDate (fun value -> writer.WriteDate(value))
            writeOption writer normalized.Dates.FinalPaymentDue (fun value -> writer.WriteDate(value))
            intOpt [ "addendum"; "totalNumberOfMortgageLoans" ] root
            dateOpt [ "addendum"; "weightedAverageInterestAdjustmentDate" ] root
            dateOpt [ "addendum"; "weightedAverageMaturityDate" ] root
            decimalOpt [ "addendum"; "loanTypeProfile"; "homeownerPercent" ] root
            decimalOpt [ "addendum"; "loanTypeProfile"; "multipleFamilyPercent" ] root
            decimalOpt [ "addendum"; "loanTypeProfile"; "socialHousingPercent" ] root
            jsonOpt [ "addendum"; "ramDistribution"; "byRemainingAmortizationMonths" ] root
            jsonOpt [ "addendum"; "ramDistribution"; "byRemainingTermMonths" ] root
            jsonOpt [ "addendum"; "interestAdjustmentProfile"; "interestAdjustmentDates" ] root
            jsonOpt [ "addendum"; "interestAdjustmentProfile"; "totals" ] root
            jsonOpt [ "addendum"; "interestAdjustmentProfile"; "percentOfTotal" ] root
            jsonOpt [ "addendum"; "interestAdjustmentProfile"; "wac"; "byDate" ] root

            writeOption writer (optBool [ "prepaymentProvisions"; "bonaFideSaleIndemnityExempt" ] root) (fun value ->
                writer.Write(value))

            stringOpt [ "prepaymentProvisions"; "sectionA" ] root
            stringOpt [ "prepaymentProvisions"; "sectionB" ] root
            stringOpt [ "prepaymentProvisions"; "otherDisclosures" ] root
            writeOption writer sourceUrl (fun value -> writer.Write(value))
            writer.Write(Path.GetFileName(file.Path))
            writer.Write(sourceFileId sourceIds file.Path)
            writer.WriteTimestampTz(modified)
            writer.WriteTimestampTz(started)
            writer.WriteJson(root.GetRawText()))

    writer.Complete() |> ignore
    written, skipped, List.ofSeq detectedAnomalies

let private writeInsurers (connection: NpgsqlConnection) (insurerIds: Dictionary<string, int>) (files: PoolFile[]) =
    use writer =
        new CopyWriter(
            connection,
            "nhambs.mbs_pool_insurers",
            [ "pool_cusip"; "insurer_id"; "dollar_value"; "percent_of_loans" ]
        )

    iterPools files (fun file root ->
        for insurer in arrayItems [ "addendum"; "insurerProfile"; "insurers" ] root do
            let name = canonicalInsurerName (stringOr [ "name" ] "" insurer)

            let insurerId =
                match insurerIds.TryGetValue name with
                | true, value -> value
                | _ -> failwithf "Insurer was not resolved before COPY: %s" name

            writer.StartRow()
            writer.Write(file.Cusip)
            writer.Write(insurerId)
            writeOption writer (optDecimal [ "dollarValue" ] insurer) (fun value -> writer.Write(value))
            writeOption writer (optDecimal [ "percentOfLoans" ] insurer) (fun value -> writer.Write(value)))
    |> ignore

    writer.Complete()

let private writeLocations (connection: NpgsqlConnection) (files: PoolFile[]) =
    use writer =
        new CopyWriter(
            connection,
            "nhambs.mbs_pool_locations",
            [ "pool_cusip"; "province"; "loans_percent"; "principal_amount_percent" ]
        )

    iterPools files (fun file root ->
        match optElement [ "addendum"; "locationOfMortgageLoans" ] root with
        | None -> ()
        | Some locations ->
            for province, _ in provinceNames do
                match prop province locations with
                | Some value ->
                    let loans = optDecimal [ "loansPercent" ] value |> Option.defaultValue 0m

                    let principal =
                        optDecimal [ "principalAmountPercent" ] value |> Option.defaultValue 0m

                    if loans > 0m || principal > 0m then
                        writer.StartRow()
                        writer.Write(file.Cusip)
                        writer.Write(province)
                        writer.Write(loans)
                        writer.Write(principal)
                | None -> ())
    |> ignore

    writer.Complete()

let private scheduledBuckets =
    [
        "fiveMonths"
        "fourMonths"
        "threeMonths"
        "twoMonths"
        "oneMonth"
        "maturity"
        "total"
    ]

let private writeScheduledMaturity (connection: NpgsqlConnection) (files: PoolFile[]) =
    use writer =
        new CopyWriter(
            connection,
            "nhambs.mbs_pool_scheduled_maturity",
            [
                "pool_cusip"
                "bucket"
                "remaining_face_value"
                "weighted_average_rate"
                "weighted_average_amortization"
                "index_spread_total"
                "index_spread_full_term"
                "monthly_payment"
                "introductory_months"
            ]
        )

    iterPools files (fun file root ->
        for key in scheduledBuckets do
            match optElement [ "addendum"; "scheduledMaturity"; key ] root with
            | Some bucket ->
                writer.StartRow()
                writer.Write(file.Cusip)
                writer.Write(key)
                writeOption writer (optDecimal [ "remainingFaceValue" ] bucket) (fun value -> writer.Write(value))

                writeOption writer (optDecimal [ "weightedAverageMortgageRatePercent" ] bucket) (fun value ->
                    writer.Write(value))

                writeOption
                    writer
                    (optDecimal [ "weightedAverageRemainingAmortizationMonths" ] bucket
                     |> Option.filter isPlausibleMbsAmortization)
                    (fun value -> writer.Write(value))

                writeOption writer (optDecimal [ "indexSpreadTotalPercent" ] bucket) (fun value -> writer.Write(value))

                writeOption writer (optDecimal [ "indexSpreadFullTermPercent" ] bucket) (fun value ->
                    writer.Write(value))

                writeOption writer (optDecimal [ "sumOfMonthlyPaymentEquivalents" ] bucket) (fun value ->
                    writer.Write(value))

                writeOption
                    writer
                    (optDecimal [ "weightedAverageIntroductoryPeriodsRemainingMonths" ] bucket
                     |> Option.filter isPlausibleMbsIntroductoryMonths)
                    (fun value -> writer.Write(value))
            | None -> ())
    |> ignore

    writer.Complete()

let private writeRateBands (connection: NpgsqlConnection) (files: PoolFile[]) =
    use writer =
        new CopyWriter(
            connection,
            "nhambs.mbs_pool_rate_bands",
            [
                "pool_cusip"
                "rate_band"
                "total"
                "percent_of_total"
                "remaining_face_value_by_date"
            ]
        )

    iterPools files (fun file root ->
        for band in arrayItems [ "addendum"; "interestAdjustmentProfile"; "rateBands" ] root do
            let canonical = canonicalRateBand (stringOr [ "rateBand" ] "" band)

            writer.StartRow()
            writer.Write(file.Cusip)
            writer.Write(canonical.Key)
            writeOption writer (optDecimal [ "total" ] band) (fun value -> writer.Write(value))
            writeOption writer (optDecimal [ "percentOfTotal" ] band) (fun value -> writer.Write(value))

            writeOption
                writer
                (optElement [ "remainingFaceValueByDate" ] band
                 |> Option.map (fun value -> value.GetRawText()))
                (fun value -> writer.WriteJson(value)))
    |> ignore

    writer.Complete()

let private refreshReferenceEntities (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
    use command =
        new NpgsqlCommand(
            """
            DELETE FROM nhambs.issuers i
            WHERE NOT EXISTS (SELECT 1 FROM nhambs.mbs_pools p WHERE p.issuer_id = i.issuer_id);

            DELETE FROM nhambs.insurers i
            WHERE NOT EXISTS (SELECT 1 FROM nhambs.mbs_pool_insurers x WHERE x.insurer_id = i.insurer_id);

            DELETE FROM nhambs.rate_bands b
            WHERE NOT EXISTS (SELECT 1 FROM nhambs.mbs_pool_rate_bands x WHERE x.rate_band = b.band_key);
            """,
            connection,
            Transaction = transaction
        )

    command.ExecuteNonQuery() |> ignore

let private writeAnomalies
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (runId: int64)
    (anomalies: Anomaly list)
    =
    for anomaly in anomalies do
        use command =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.etl_anomalies
                    (run_id, dataset, record_key, field, raw_value, corrected_value, severity, message)
                VALUES
                    (@run_id, @dataset, @record_key, @field, @raw_value, @corrected_value, @severity, @message);
                """,
                connection,
                Transaction = transaction
            )

        let correctedValue =
            anomaly.CorrectedValue
            |> Option.map box
            |> Option.defaultValue (box DBNull.Value)

        command.Parameters.AddWithValue("run_id", runId) |> ignore
        command.Parameters.AddWithValue("dataset", anomaly.Dataset) |> ignore
        command.Parameters.AddWithValue("record_key", anomaly.RecordKey) |> ignore
        command.Parameters.AddWithValue("field", anomaly.Field) |> ignore
        command.Parameters.AddWithValue("raw_value", anomaly.RawValue) |> ignore
        command.Parameters.AddWithValue("corrected_value", correctedValue) |> ignore
        command.Parameters.AddWithValue("severity", anomaly.Severity) |> ignore
        command.Parameters.AddWithValue("message", anomaly.Message) |> ignore
        command.ExecuteNonQuery() |> ignore

let run (poolsDir: string) (manifestPath: string) =
    let connectionStringValue = connectionString ()
    let files = poolFiles poolsDir
    let urls = sourceUrls manifestPath
    log (sprintf "MBS ETL: %d JSON pool files in %s" files.Length poolsDir)

    use runConnection = new NpgsqlConnection(connectionStringValue)
    runConnection.Open()
    let runId = startRun runConnection "mbs" poolsDir
    let stopwatch = Stopwatch.StartNew()

    try
        log "  landing source files in raw..."

        let sourceIds =
            writeRawFiles runConnection runId "mbs" (files |> Array.map (fun file -> file.Path))

        use connection = new NpgsqlConnection(connectionStringValue)
        connection.Open()

        use transaction = connection.BeginTransaction()

        use truncate =
            new NpgsqlCommand("TRUNCATE nhambs.mbs_pools CASCADE;", connection, Transaction = transaction)

        truncate.ExecuteNonQuery() |> ignore

        log "  resolving issuer, insurer and rate-band references..."

        let issuerIds, insurerIds = resolveReferenceMaps connection transaction files

        log "  loading pool headers..."

        let written, skipped, anomalies =
            writeMbsPools connection issuerIds files urls sourceIds

        log (sprintf "  recording %d data anomalies..." anomalies.Length)
        writeAnomalies connection transaction runId anomalies

        log "  loading insurer profiles..."
        writeInsurers connection insurerIds files |> ignore

        log "  loading provincial allocations..."
        writeLocations connection files |> ignore

        log "  loading scheduled maturity buckets..."
        writeScheduledMaturity connection files |> ignore

        log "  loading interest-rate bands..."
        writeRateBands connection files |> ignore

        log "  cleaning orphan reference entities..."
        refreshReferenceEntities connection transaction

        transaction.Commit()
        stopwatch.Stop()

        let corrected =
            anomalies
            |> List.filter (fun anomaly -> anomaly.CorrectedValue.IsSome)
            |> List.length

        if anomalies.IsEmpty then
            log "  data anomalies: none detected"
        else
            log (sprintf "  data anomalies: %d detected (%d corrected)" anomalies.Length corrected)

            for anomaly in anomalies |> List.truncate 25 do
                let correction =
                    anomaly.CorrectedValue
                    |> Option.map (sprintf " -> %s")
                    |> Option.defaultValue ""

                log (
                    sprintf
                        "    [%s] %s %s%s: %s"
                        anomaly.Severity
                        anomaly.RecordKey
                        anomaly.Field
                        correction
                        anomaly.Message
                )

            if anomalies.Length > 25 then
                log (sprintf "    ... and %d more" (anomalies.Length - 25))

        completeRun runConnection runId files.Length skipped (int64 written) None
        log (sprintf "MBS ETL complete: %d pools in %.1fs (%d skipped)" written stopwatch.Elapsed.TotalSeconds skipped)
        written, skipped
    with ex ->
        stopwatch.Stop()
        completeRun runConnection runId files.Length 0 0L (Some ex.Message)
        eprintfn "MBS ETL failed after %.1fs: %s" stopwatch.Elapsed.TotalSeconds ex.Message
        reraise ()

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

let private canonicalIssuers (files: PoolFile[]) =
    let variants = Dictionary<string, Dictionary<string, int>>()
    let mutable skipped = 0

    for file in files do
        try
            use document = readJson file.Path
            let root = document.RootElement

            match optString [ "parties"; "issuerName" ] root with
            | Some rawIssuer when not (String.IsNullOrWhiteSpace rawIssuer) ->
                let issuer = repairShiftedName rawIssuer
                let key = normalizeIssuer issuer

                if not (variants.ContainsKey key) then
                    variants.[key] <- Dictionary<string, int>()

                let counts = variants.[key]

                if counts.ContainsKey issuer then
                    counts.[issuer] <- counts.[issuer] + 1
                else
                    counts.[issuer] <- 1
            | _ -> ()
        with _ ->
            skipped <- skipped + 1

    let canonical = Dictionary<string, string>()

    for kv in variants do
        let best =
            kv.Value
            |> Seq.sortBy (fun pair -> -pair.Value, pair.Key)
            |> Seq.head
            |> fun pair -> pair.Key

        canonical.[kv.Key] <- best

    canonical, skipped

let private writeMbsPools
    (connection: NpgsqlConnection)
    (files: PoolFile[])
    (urls: Dictionary<string, string>)
    (issuers: Dictionary<string, string>)
    =
    let started = DateTime.UtcNow

    let columns =
        [
            "cusip"
            "pool_number"
            "issuer_name"
            "issuer_key"
            "custodian"
            "mortgage_insurer"
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
            "scheduled_remaining_face_value"
            "scheduled_wac"
            "scheduled_wam"
            "scheduled_index_spread_total"
            "scheduled_index_spread_full_term"
            "scheduled_monthly_payment"
            "scheduled_introductory_months"
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

    let jsonOpt (path: string list) (root: JsonElement) =
        writeOption writer (optElement path root |> Option.map (fun value -> value.GetRawText())) (fun value ->
            writer.WriteJson(value))

    let written, skipped =
        iterPools files (fun file root ->
            writer.StartRow()

            let issuer =
                optString [ "parties"; "issuerName" ] root
                |> Option.map repairShiftedName
                |> Option.defaultValue "Unknown issuer"

            let issuerKey = normalizeIssuer issuer

            let canonical =
                match issuers.TryGetValue issuerKey with
                | true, value -> value
                | _ -> issuer

            let sourceUrl =
                match urls.TryGetValue file.Cusip with
                | true, value -> Some value
                | _ -> None

            let modified = File.GetLastWriteTimeUtc file.Path

            writer.Write(file.Cusip)
            writer.Write(stringOr [ "mbs"; "poolNumber" ] "" root)
            writer.Write(canonical)
            writer.Write(issuerKey)
            stringOpt [ "parties"; "custodian" ] root
            stringOpt [ "parties"; "mortgageInsurer" ] root
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
            dateOpt [ "mbs"; "issueDate" ] root
            dateOpt [ "mbs"; "settlementDate" ] root
            dateOpt [ "mbs"; "firstPaymentDue" ] root
            dateOpt [ "mbs"; "maturityDate" ] root
            dateOpt [ "mbs"; "finalPaymentDue" ] root
            intOpt [ "addendum"; "totalNumberOfMortgageLoans" ] root
            dateOpt [ "addendum"; "weightedAverageInterestAdjustmentDate" ] root
            dateOpt [ "addendum"; "weightedAverageMaturityDate" ] root
            decimalOpt [ "addendum"; "scheduledMaturity"; "total"; "remainingFaceValue" ] root

            decimalOpt
                [
                    "addendum"
                    "scheduledMaturity"
                    "total"
                    "weightedAverageMortgageRatePercent"
                ]
                root

            decimalOpt
                [
                    "addendum"
                    "scheduledMaturity"
                    "total"
                    "weightedAverageRemainingAmortizationMonths"
                ]
                root

            decimalOpt [ "addendum"; "scheduledMaturity"; "total"; "indexSpreadTotalPercent" ] root
            decimalOpt [ "addendum"; "scheduledMaturity"; "total"; "indexSpreadFullTermPercent" ] root
            decimalOpt [ "addendum"; "scheduledMaturity"; "total"; "sumOfMonthlyPaymentEquivalents" ] root

            decimalOpt
                [
                    "addendum"
                    "scheduledMaturity"
                    "total"
                    "weightedAverageIntroductoryPeriodsRemainingMonths"
                ]
                root

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
            writer.WriteTimestampTz(modified)
            writer.WriteTimestampTz(started)
            writer.WriteJson(root.GetRawText()))

    writer.Complete() |> ignore
    written, skipped

let private writeInsurers (connection: NpgsqlConnection) (files: PoolFile[]) =
    use writer =
        new CopyWriter(
            connection,
            "nhambs.mbs_pool_insurers",
            [ "pool_cusip"; "name"; "dollar_value"; "percent_of_loans" ]
        )

    iterPools files (fun file root ->
        for insurer in arrayItems [ "addendum"; "insurerProfile"; "insurers" ] root do
            writer.StartRow()
            writer.Write(file.Cusip)
            writer.Write(stringOr [ "name" ] "" insurer)
            writeOption writer (optDecimal [ "dollarValue" ] insurer) (fun value -> writer.Write(value))
            writeOption writer (optDecimal [ "percentOfLoans" ] insurer) (fun value -> writer.Write(value)))
    |> ignore

    writer.Complete()

let private writeLocations (connection: NpgsqlConnection) (files: PoolFile[]) =
    use writer =
        new CopyWriter(
            connection,
            "nhambs.mbs_pool_locations",
            [
                "pool_cusip"
                "province"
                "province_name"
                "loans_percent"
                "principal_amount_percent"
            ]
        )

    iterPools files (fun file root ->
        match optElement [ "addendum"; "locationOfMortgageLoans" ] root with
        | None -> ()
        | Some locations ->
            for province, display in provinceNames do
                match prop province locations with
                | Some value ->
                    let loans = optDecimal [ "loansPercent" ] value |> Option.defaultValue 0m

                    let principal =
                        optDecimal [ "principalAmountPercent" ] value |> Option.defaultValue 0m

                    if loans > 0m || principal > 0m then
                        writer.StartRow()
                        writer.Write(file.Cusip)
                        writer.Write(province)
                        writer.Write(display)
                        writer.Write(loans)
                        writer.Write(principal)
                | None -> ())
    |> ignore

    writer.Complete()

let private scheduledBuckets =
    [
        "fiveMonths", "5 months"
        "fourMonths", "4 months"
        "threeMonths", "3 months"
        "twoMonths", "2 months"
        "oneMonth", "1 month"
        "maturity", "At maturity"
        "total", "Total"
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
        for key, label in scheduledBuckets do
            match optElement [ "addendum"; "scheduledMaturity"; key ] root with
            | Some bucket ->
                writer.StartRow()
                writer.Write(file.Cusip)
                writer.Write(label)
                writeOption writer (optDecimal [ "remainingFaceValue" ] bucket) (fun value -> writer.Write(value))

                writeOption writer (optDecimal [ "weightedAverageMortgageRatePercent" ] bucket) (fun value ->
                    writer.Write(value))

                writeOption writer (optDecimal [ "weightedAverageRemainingAmortizationMonths" ] bucket) (fun value ->
                    writer.Write(value))

                writeOption writer (optDecimal [ "indexSpreadTotalPercent" ] bucket) (fun value -> writer.Write(value))

                writeOption writer (optDecimal [ "indexSpreadFullTermPercent" ] bucket) (fun value ->
                    writer.Write(value))

                writeOption writer (optDecimal [ "sumOfMonthlyPaymentEquivalents" ] bucket) (fun value ->
                    writer.Write(value))

                writeOption
                    writer
                    (optDecimal [ "weightedAverageIntroductoryPeriodsRemainingMonths" ] bucket)
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
            writer.StartRow()
            writer.Write(file.Cusip)
            writer.Write(stringOr [ "rateBand" ] "" band)
            writeOption writer (optDecimal [ "total" ] band) (fun value -> writer.Write(value))
            writeOption writer (optDecimal [ "percentOfTotal" ] band) (fun value -> writer.Write(value))

            writeOption
                writer
                (optElement [ "remainingFaceValueByDate" ] band
                 |> Option.map (fun value -> value.GetRawText()))
                (fun value -> writer.WriteJson(value)))
    |> ignore

    writer.Complete()

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
        use connection = new NpgsqlConnection(connectionStringValue)
        connection.Open()

        use transaction = connection.BeginTransaction()

        use truncate =
            new NpgsqlCommand("TRUNCATE nhambs.mbs_pools CASCADE;", connection, Transaction = transaction)

        truncate.ExecuteNonQuery() |> ignore

        log "  resolving issuer aliases..."
        let issuers, issuerSkipped = canonicalIssuers files
        log (sprintf "  %d canonical issuers (%d unreadable files)" issuers.Count issuerSkipped)

        log "  loading pool headers..."
        let written, skipped = writeMbsPools connection files urls issuers

        log "  loading insurer profiles..."
        writeInsurers connection files |> ignore

        log "  loading provincial allocations..."
        writeLocations connection files |> ignore

        log "  loading scheduled maturity buckets..."
        writeScheduledMaturity connection files |> ignore

        log "  loading interest-rate bands..."
        writeRateBands connection files |> ignore

        transaction.Commit()
        stopwatch.Stop()

        completeRun runConnection runId files.Length skipped (int64 written) None
        log (sprintf "MBS ETL complete: %d pools in %.1fs (%d skipped)" written stopwatch.Elapsed.TotalSeconds skipped)
        written, skipped
    with ex ->
        stopwatch.Stop()
        completeRun runConnection runId files.Length 0 0L (Some ex.Message)
        eprintfn "MBS ETL failed after %.1fs: %s" stopwatch.Elapsed.TotalSeconds ex.Message
        reraise ()

module NHAMBS.Etl.CoveredBondEtl

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Npgsql
open NHAMBS.Etl.Common

let private insertProgram (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (root: JsonElement) =
    use command =
        new NpgsqlCommand(
            """
            INSERT INTO nhambs.covered_bond_programs
                (issuer_name, program_name, minimum_asset_percentage, maximum_asset_percentage, source_url, raw)
            VALUES (@issuer, @program, @minimum, @maximum, @source_url, @raw::jsonb)
            RETURNING id;
            """,
            connection,
            Transaction = transaction
        )

    command.Parameters.AddWithValue("issuer", stringOr [ "issuerName" ] "" root)
    |> ignore

    command.Parameters.AddWithValue("program", stringOr [ "programName" ] "" root)
    |> ignore

    command.Parameters.AddWithValue(
        "minimum",
        (match optDecimal [ "minimumAssetPercentage" ] root with
         | Some d -> box d
         | None -> box DBNull.Value)
    )
    |> ignore

    command.Parameters.AddWithValue(
        "maximum",
        (match optDecimal [ "maximumAssetPercentage" ] root with
         | Some d -> box d
         | None -> box DBNull.Value)
    )
    |> ignore

    command.Parameters.AddWithValue(
        "source_url",
        (match optString [ "sourceUrl" ] root with
         | Some s -> box s
         | None -> box DBNull.Value)
    )
    |> ignore

    command.Parameters.AddWithValue("raw", root.GetRawText()) |> ignore
    command.ExecuteScalar() :?> int

let private insertParties
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (programId: int)
    (root: JsonElement)
    =
    match optElement [ "parties" ] root with
    | None -> ()
    | Some parties ->
        for party in parties.EnumerateObject() do
            if party.Value.ValueKind = JsonValueKind.String then
                use command =
                    new NpgsqlCommand(
                        "INSERT INTO nhambs.covered_bond_parties (program_id, role, name) VALUES (@program_id, @role, @name);",
                        connection,
                        Transaction = transaction
                    )

                command.Parameters.AddWithValue("program_id", programId) |> ignore
                command.Parameters.AddWithValue("role", party.Name) |> ignore
                command.Parameters.AddWithValue("name", party.Value.GetString()) |> ignore
                command.ExecuteNonQuery() |> ignore

let private insertBonds
    (connection: NpgsqlConnection)
    (transaction: NpgsqlTransaction)
    (programId: int)
    (root: JsonElement)
    =
    let mutable count = 0

    for bond in arrayItems [ "bonds" ] root do
        use command =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.covered_bonds
                    (program_id, registration_date, issue_date, series_designation, issue_currency,
                     nominal_amount, isin_display, bullet_type, maturity_date, extended_maturity_date,
                     coupon_display, coupon_kind, coupon_fixed_rate_percent, coupon_index, coupon_spread_percent,
                     interest_payment_frequency, listing_authority, raw)
                VALUES
                    (@program_id, @registration_date, @issue_date, @series, @currency,
                     @nominal, @isin_display, @bullet_type, @maturity, @extended_maturity,
                     @coupon_display, @coupon_kind, @coupon_fixed, @coupon_index, @coupon_spread,
                     @frequency, @listing, @raw::jsonb)
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
                (match optString path bond |> Option.bind parseDate with
                 | Some d -> box d
                 | None -> box DBNull.Value)

        let addText name path = add name (stringOr path "" bond)

        let addDecimal name path =
            add
                name
                (match optDecimal path bond with
                 | Some d -> box d
                 | None -> box DBNull.Value)

        add "program_id" programId
        addDate "registration_date" [ "registrationDate" ]
        addDate "issue_date" [ "issueDate" ]
        addText "series" [ "seriesDesignation" ]
        addText "currency" [ "issueCurrency" ]
        addDecimal "nominal" [ "nominalAmount" ]
        addText "isin_display" [ "isin"; "displayText" ]
        addText "bullet_type" [ "bulletType" ]
        addDate "maturity" [ "maturityDate" ]
        addDate "extended_maturity" [ "extendedMaturityDate" ]
        addText "coupon_display" [ "coupon"; "displayText" ]
        addText "coupon_kind" [ "coupon"; "kind" ]
        addDecimal "coupon_fixed" [ "coupon"; "fixedRatePercent" ]
        addText "coupon_index" [ "coupon"; "index" ]
        addDecimal "coupon_spread" [ "coupon"; "spreadPercent" ]
        addText "frequency" [ "interestPaymentFrequency" ]
        addText "listing" [ "listingAuthority" ]
        add "raw" (bond.GetRawText())

        let bondId = command.ExecuteScalar() :?> int64

        for isin in arrayItems [ "isin"; "entries" ] bond do
            use isinCommand =
                new NpgsqlCommand(
                    "INSERT INTO nhambs.covered_bond_isins (bond_id, designation, value) VALUES (@bond_id, @designation, @value);",
                    connection,
                    Transaction = transaction
                )

            isinCommand.Parameters.AddWithValue("bond_id", bondId) |> ignore

            isinCommand.Parameters.AddWithValue(
                "designation",
                (match optString [ "designation" ] isin with
                 | Some s -> box s
                 | None -> box DBNull.Value)
            )
            |> ignore

            isinCommand.Parameters.AddWithValue("value", stringOr [ "value" ] "" isin)
            |> ignore

            isinCommand.ExecuteNonQuery() |> ignore

        count <- count + 1

    count

let private loadProgram (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (path: string) =
    use document = readJson path
    let root = document.RootElement
    let programId = insertProgram connection transaction root
    insertParties connection transaction programId root
    let bondCount = insertBonds connection transaction programId root
    programId, bondCount

let private loadRegistry (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (path: string) =
    use document = readJson path
    let root = document.RootElement
    let sourceUrl = stringOr [ "sourceUrl" ] "" root
    let mutable count = 0

    for issuer in arrayItems [ "issuers" ] root do
        use command =
            new NpgsqlCommand(
                """
                INSERT INTO nhambs.covered_bond_registry
                    (name, status, registration_date, business_address, website_url, program_page, source_url)
                VALUES (@name, @status, @registration_date, @address, @website, @program_page, @source_url)
                ON CONFLICT (name) DO UPDATE SET
                    status = EXCLUDED.status,
                    registration_date = EXCLUDED.registration_date,
                    business_address = EXCLUDED.business_address,
                    website_url = EXCLUDED.website_url,
                    program_page = EXCLUDED.program_page,
                    source_url = EXCLUDED.source_url;
                """,
                connection,
                Transaction = transaction
            )

        command.Parameters.AddWithValue("name", stringOr [ "name" ] "" issuer) |> ignore

        command.Parameters.AddWithValue("status", stringOr [ "status" ] "" issuer)
        |> ignore

        command.Parameters.AddWithValue(
            "registration_date",
            (match optString [ "registrationDate" ] issuer |> Option.bind parseDate with
             | Some d -> box d
             | None -> box DBNull.Value)
        )
        |> ignore

        command.Parameters.AddWithValue("address", stringOr [ "businessAddress" ] "" issuer)
        |> ignore

        command.Parameters.AddWithValue("website", stringOr [ "websiteUrl" ] "" issuer)
        |> ignore

        command.Parameters.AddWithValue("program_page", stringOr [ "programPage" ] "" issuer)
        |> ignore

        command.Parameters.AddWithValue("source_url", sourceUrl) |> ignore
        command.ExecuteNonQuery() |> ignore
        count <- count + 1

    count

let run (dataDir: string) =
    let coveredDir = Path.Combine(dataDir, "covered_bonds")

    if not (Directory.Exists coveredDir) then
        failwithf "Covered bond directory not found: %s" coveredDir

    let programFiles =
        Directory.EnumerateFiles(coveredDir, "program-*.json")
        |> Seq.sort
        |> Seq.toArray

    let registryPath = Path.Combine(coveredDir, "registry.json")
    let connectionStringValue = connectionString ()
    use runConnection = new NpgsqlConnection(connectionStringValue)
    runConnection.Open()
    let runId = startRun runConnection "covered-bonds" coveredDir
    let stopwatch = Stopwatch.StartNew()

    try
        use connection = new NpgsqlConnection(connectionStringValue)
        connection.Open()

        use truncate =
            new NpgsqlCommand(
                "TRUNCATE nhambs.covered_bond_programs, nhambs.covered_bond_registry CASCADE;",
                connection
            )

        truncate.ExecuteNonQuery() |> ignore

        use transaction = connection.BeginTransaction()
        log "  loading covered bond programs..."
        let mutable programs = 0
        let mutable bonds = 0
        let mutable skipped = 0

        for path in programFiles do
            try
                let _, bondCount = loadProgram connection transaction path
                programs <- programs + 1
                bonds <- bonds + bondCount
            with ex ->
                skipped <- skipped + 1
                eprintfn "  skipped %s: %s" (Path.GetFileName path) ex.Message

        let registryCount =
            if File.Exists registryPath then
                log "  loading covered bond registry..."
                loadRegistry connection transaction registryPath
            else
                0

        transaction.Commit()
        stopwatch.Stop()

        completeRun
            runConnection
            runId
            (programFiles.Length + (if File.Exists registryPath then 1 else 0))
            skipped
            (int64 (programs + bonds + registryCount))
            None

        log (
            sprintf
                "Covered bond ETL complete: %d programs, %d bonds, %d registry issuers in %.1fs"
                programs
                bonds
                registryCount
                stopwatch.Elapsed.TotalSeconds
        )

        programs + bonds + registryCount
    with ex ->
        stopwatch.Stop()
        completeRun runConnection runId 0 0 0L (Some ex.Message)
        eprintfn "Covered bond ETL failed after %.1fs: %s" stopwatch.Elapsed.TotalSeconds ex.Message
        reraise ()

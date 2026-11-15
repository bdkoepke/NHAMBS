module NHAMBS.Data.CoveredBondRepository

open System
open System.Data.Common
open System.Globalization
open Npgsql
open NHAMBS.Shared.Domain
open NHAMBS.Data

let private text (reader: DbDataReader) index =
    if reader.IsDBNull index then "" else reader.GetString index

let private decimalOpt (reader: DbDataReader) index =
    if reader.IsDBNull index then
        None
    else
        Some(reader.GetDecimal index)

let private decimalOrZero (reader: DbDataReader) index =
    if reader.IsDBNull index then
        0m
    else
        reader.GetDecimal index

let private intValue (reader: DbDataReader) index =
    if reader.IsDBNull index then
        0
    else
        Convert.ToInt32(reader.GetValue index, CultureInfo.InvariantCulture)

let private dateText (reader: DbDataReader) index =
    if reader.IsDBNull index then
        ""
    else
        reader.GetDateTime(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let private loadPrograms (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            """
            SELECT program_id, issuer_name, program_name, minimum_asset_percentage, maximum_asset_percentage,
                   bond_count, currencies, earliest_maturity, latest_maturity, COALESCE(source_url, '')
            FROM nhambs.v_covered_bond_program_summary
            ORDER BY issuer_name;
            """,
            connection
        )

    use reader = command.ExecuteReader()
    let rows = ResizeArray<CoveredBondProgramRow>()

    while reader.Read() do
        rows.Add(
            {
                ProgramId = intValue reader 0
                IssuerName = text reader 1
                ProgramName = text reader 2
                MinimumAssetPercentage = defaultArg (decimalOpt reader 3) 0m
                MaximumAssetPercentage = defaultArg (decimalOpt reader 4) 0m
                BondCount = intValue reader 5
                Currencies = text reader 6
                EarliestMaturity = dateText reader 7
                LatestMaturity = dateText reader 8
                SourceUrl = text reader 9
            }
        )

    rows.ToArray()

let private loadBonds (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            """
            SELECT b.id, b.program_id, pr.issuer_name, pr.program_name,
                   COALESCE(b.series_designation, ''), COALESCE(b.issue_currency, ''),
                   COALESCE(b.nominal_amount, 0), COALESCE(b.isin_display, ''),
                   COALESCE(b.coupon_display, ''), COALESCE(b.coupon_kind, ''),
                   COALESCE(b.coupon_fixed_rate_percent, b.coupon_spread_percent),
                   b.issue_date, b.registration_date, b.maturity_date, b.extended_maturity_date,
                   COALESCE(b.bullet_type, ''), COALESCE(b.interest_payment_frequency, ''),
                   COALESCE(b.listing_authority, '')
            FROM nhambs.covered_bonds b
            JOIN nhambs.covered_bond_programs pr ON pr.id = b.program_id
            ORDER BY b.issue_date DESC NULLS LAST, pr.issuer_name, b.series_designation;
            """,
            connection
        )

    use reader = command.ExecuteReader()
    let rows = ResizeArray<CoveredBondRow>()

    while reader.Read() do
        rows.Add(
            {
                BondId = intValue reader 0
                ProgramId = intValue reader 1
                IssuerName = text reader 2
                ProgramName = text reader 3
                SeriesDesignation = text reader 4
                IssueCurrency = text reader 5
                NominalAmount = decimalOrZero reader 6
                Isin = text reader 7
                CouponDisplay = text reader 8
                CouponKind = text reader 9
                CouponValue = defaultArg (decimalOpt reader 10) 0m
                IssueDate = dateText reader 11
                RegistrationDate = dateText reader 12
                MaturityDate = dateText reader 13
                ExtendedMaturityDate = dateText reader 14
                BulletType = text reader 15
                InterestPaymentFrequency = text reader 16
                ListingAuthority = text reader 17
            }
        )

    rows.ToArray()

let private loadRegistry (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            """
            SELECT name, COALESCE(status, ''), registration_date, COALESCE(business_address, ''),
                   COALESCE(website_url, ''), COALESCE(program_page, ''), COALESCE(program_name, ''),
                   minimum_asset_percentage, maximum_asset_percentage, bond_count
            FROM nhambs.v_covered_bond_registry_summary
            ORDER BY name;
            """,
            connection
        )

    use reader = command.ExecuteReader()
    let rows = ResizeArray<CoveredBondRegistryRow>()

    while reader.Read() do
        rows.Add(
            {
                Name = text reader 0
                Status = text reader 1
                RegistrationDate = dateText reader 2
                BusinessAddress = text reader 3
                WebsiteUrl = text reader 4
                ProgramPage = text reader 5
                ProgramName = text reader 6
                MinimumAssetPercentage = defaultArg (decimalOpt reader 7) 0m
                MaximumAssetPercentage = defaultArg (decimalOpt reader 8) 0m
                BondCount = intValue reader 9
            }
        )

    rows.ToArray()

let private parseDate (value: string) =
    match DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
    | true, date -> Some date
    | _ -> None

let private analytics (programs: CoveredBondProgramRow[]) (bonds: CoveredBondRow[]) =
    let currencies =
        bonds
        |> Array.map (fun bond ->
            if String.IsNullOrWhiteSpace bond.IssueCurrency then
                "?"
            else
                bond.IssueCurrency)
        |> Array.distinct

    let matureDates =
        bonds |> Array.choose (fun bond -> parseDate bond.MaturityDate) |> Array.sort

    let byCurrency =
        bonds
        |> Array.groupBy (fun bond ->
            if String.IsNullOrWhiteSpace bond.IssueCurrency then
                "?"
            else
                bond.IssueCurrency)
        |> Array.map (fun (label, items) ->
            {
                Label = label
                Value = items |> Array.sumBy (fun bond -> bond.NominalAmount)
                Count = items.Length
            })
        |> Array.sortByDescending (fun point -> point.Count)

    let byMaturityYear =
        bonds
        |> Array.choose (fun bond ->
            parseDate bond.MaturityDate
            |> Option.map (fun date ->
                {
                    Label = date.ToString("yyyy", CultureInfo.InvariantCulture)
                    Date = date.ToString("yyyy-01-01", CultureInfo.InvariantCulture)
                    Value = decimal 1
                    Secondary = 0m
                    Count = 1
                }))
        |> Array.groupBy (fun point -> point.Label)
        |> Array.map (fun (label, points) ->
            {
                Label = label
                Date = points |> Array.head |> fun point -> point.Date
                Value = decimal points.Length
                Secondary = 0m
                Count = points.Length
            })
        |> Array.sortBy (fun point -> point.Date)

    let byProgram =
        bonds
        |> Array.groupBy (fun bond -> bond.IssuerName)
        |> Array.map (fun (label, items) ->
            {
                Label = label
                Value = decimal items.Length
                Count = items.Length
            })
        |> Array.sortByDescending (fun point -> point.Count)

    {
        ProgramCount = programs.Length
        BondCount = bonds.Length
        CurrencyCount = currencies.Length
        IssuerCount =
            programs
            |> Array.map (fun program -> program.IssuerName)
            |> Array.distinct
            |> Array.length
        EarliestMaturity =
            matureDates
            |> Array.tryHead
            |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            |> Option.defaultValue ""
        LatestMaturity =
            matureDates
            |> Array.tryLast
            |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            |> Option.defaultValue ""
        ByCurrency = byCurrency
        ByMaturityYear = byMaturityYear
        ByProgram = byProgram
    }

let getData () =
    Database.withConnection (fun connection ->
        let programs = loadPrograms connection
        let bonds = loadBonds connection
        let registry = loadRegistry connection

        {
            Analytics = analytics programs bonds
            Programs = programs
            Bonds = bonds
            Registry = registry
        })

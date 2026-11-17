module NHAMBS.Data.CoveredBondRepository

open System
open System.Globalization
open NHAMBS.Shared.Domain
open NHAMBS.Data

let private dateOption (value: DateTime option) =
    value
    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let private loadPrograms (context: Provider.Sql.dataContext) =
    query {
        for row in context.Nhambs.VCoveredBondProgramSummary do
            select row
    }
    |> Seq.map (fun row ->
        {
            ProgramId = defaultArg row.ProgramId 0
            IssuerName = Option.get row.IssuerName
            ProgramName = Option.get row.ProgramName
            MinimumAssetPercentage = defaultArg row.MinimumAssetPercentage 0m
            MaximumAssetPercentage = defaultArg row.MaximumAssetPercentage 0m
            BondCount = int (defaultArg row.BondCount 0L)
            Currencies = Option.get row.Currencies
            EarliestMaturity = dateOption row.EarliestMaturity
            LatestMaturity = dateOption row.LatestMaturity
            SourceUrl = row.SourceUrl
        })
    |> Seq.sortBy (fun row -> row.IssuerName)
    |> Seq.toArray

let private loadBonds (context: Provider.Sql.dataContext) =
    query {
        for bond in context.Nhambs.CoveredBonds do
            join program in context.Nhambs.CoveredBondPrograms on (bond.ProgramId = program.Id)
            select (bond, program)
    }
    |> Seq.map (fun (bond, program) ->
        {
            BondId = int bond.Id
            ProgramId = bond.ProgramId
            IssuerName = program.IssuerName
            ProgramName = program.ProgramName
            SeriesDesignation = bond.SeriesDesignation
            IssueCurrency = bond.IssueCurrency
            NominalAmount = defaultArg bond.NominalAmount 0m
            Isin = bond.IsinDisplay
            CouponDisplay = bond.CouponDisplay
            CouponKind = bond.CouponKind
            CouponValue = defaultArg (bond.CouponFixedRatePercent |> Option.orElse bond.CouponSpreadPercent) 0m
            IssueDate = dateOption bond.IssueDate
            RegistrationDate = dateOption bond.RegistrationDate
            MaturityDate = dateOption bond.MaturityDate
            ExtendedMaturityDate = dateOption bond.ExtendedMaturityDate
            BulletType = bond.BulletType
            InterestPaymentFrequency = bond.InterestPaymentFrequency
            ListingAuthority = bond.ListingAuthority
        })
    |> Seq.sortWith (fun left right ->
        match left.IssueDate, right.IssueDate with
        | None, None -> compare (left.IssuerName, left.SeriesDesignation) (right.IssuerName, right.SeriesDesignation)
        | None, _ -> 1
        | _, None -> -1
        | Some a, Some b ->
            match compare b a with
            | 0 -> compare (left.IssuerName, left.SeriesDesignation) (right.IssuerName, right.SeriesDesignation)
            | value -> value)
    |> Seq.toArray

let private loadRegistry (context: Provider.Sql.dataContext) =
    query {
        for row in context.Nhambs.VCoveredBondRegistrySummary do
            select row
    }
    |> Seq.map (fun row ->
        {
            Name = Option.get row.Name
            Status = row.Status
            RegistrationDate = dateOption row.RegistrationDate
            BusinessAddress = row.BusinessAddress
            WebsiteUrl = row.WebsiteUrl
            ProgramPage = row.ProgramPage
            ProgramName = row.ProgramName
            MinimumAssetPercentage = defaultArg row.MinimumAssetPercentage 0m
            MaximumAssetPercentage = defaultArg row.MaximumAssetPercentage 0m
            BondCount = int (defaultArg row.BondCount 0L)
        })
    |> Seq.sortBy (fun row -> row.Name)
    |> Seq.toArray

let private parseDate (value: string) =
    match DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
    | true, date -> Some date
    | _ -> None

let private analytics (programs: CoveredBondProgramRow[]) (bonds: CoveredBondRow[]) =
    let currencies =
        bonds
        |> Array.map (fun bond ->
            match bond.IssueCurrency with
            | Some value when not (String.IsNullOrWhiteSpace value) -> value
            | _ -> "?")
        |> Array.distinct

    let matureDates =
        bonds
        |> Array.choose (fun bond -> bond.MaturityDate |> Option.bind parseDate)
        |> Array.sort

    let byCurrency =
        bonds
        |> Array.groupBy (fun bond ->
            match bond.IssueCurrency with
            | Some value when not (String.IsNullOrWhiteSpace value) -> value
            | _ -> "?")
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
            bond.MaturityDate
            |> Option.bind parseDate
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
        LatestMaturity =
            matureDates
            |> Array.tryLast
            |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        ByCurrency = byCurrency
        ByMaturityYear = byMaturityYear
        ByProgram = byProgram
    }

let getData (context: Provider.Sql.dataContext) =
    let programs = loadPrograms context
    let bonds = loadBonds context
    let registry = loadRegistry context

    {
        Analytics = analytics programs bonds
        Programs = programs
        Bonds = bonds
        Registry = registry
    }

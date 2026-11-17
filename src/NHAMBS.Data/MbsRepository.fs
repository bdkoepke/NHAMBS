module NHAMBS.Data.MbsRepository

open System
open System.Globalization
open System.Text.Json
open NHAMBS.Shared.Domain
open NHAMBS.Data

let private dateOption (value: DateTime option) =
    value
    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let private parseDate (value: string) =
    if String.IsNullOrWhiteSpace value then
        None
    else
        match DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
        | true, date -> Some(date.Date)
        | _ -> None

let private parseDecimalArray (json: string option) =
    match json with
    | Some value when not (String.IsNullOrWhiteSpace value) ->
        try
            JsonSerializer.Deserialize<decimal[]>(value)
        with _ ->
            [||]
    | _ -> [||]

let private parseStringArray (json: string option) =
    match json with
    | Some value when not (String.IsNullOrWhiteSpace value) ->
        try
            JsonSerializer.Deserialize<string[]>(value)
        with _ ->
            [||]
    | _ -> [||]

let private parseByDate (json: string option) (label: string) =
    match json with
    | Some value when not (String.IsNullOrWhiteSpace value) ->
        try
            use document = JsonDocument.Parse value
            let mutable byDate = [||]

            match document.RootElement.TryGetProperty "byDate" with
            | true, element ->
                byDate <-
                    element.EnumerateArray()
                    |> Seq.map (fun item ->
                        if item.ValueKind = JsonValueKind.Number then
                            item.GetDecimal()
                        else
                            0m)
                    |> Seq.toArray
            | _ -> ()

            { Label = label; Values = byDate }
        with _ ->
            { Label = label; Values = [||] }
    | _ -> { Label = label; Values = [||] }

type private InsurerFact =
    {
        PoolCusip: string
        Name: string
        DollarValue: decimal option
        PercentOfLoans: decimal option
    }

type private LocationFact =
    {
        PoolCusip: string
        ProvinceName: string
        PrincipalAmountPercent: decimal option
    }

type private PoolFact =
    {
        Row: MbsPoolRow
        IssueDate: DateTime option
        SettlementDate: DateTime option
        MaturityDate: DateTime option
        CouponValue: decimal option
        Wam: decimal option
    }

let private loadPoolFacts (context: Provider.Sql.dataContext) =
    query {
        for row in context.Nhambs.VMbsPoolGrid do
            select row
    }
    |> Seq.map (fun row ->
        let text = Option.get

        {
            Row =
                {
                    PoolId = text row.PoolId
                    Cusip = text row.Cusip
                    PoolNumber = text row.PoolNumber
                    IssuerName = text row.IssuerName
                    RateType = text row.RateType
                    PoolType = text row.PoolType
                    PoolTypeDescription = text row.PoolTypeDescription
                    CouponDisplay = text row.CouponDisplay
                    CouponValue = defaultArg row.CouponValue 0m
                    Principal = defaultArg row.Principal 0m
                    IssueDate = dateOption row.IssueDate
                    SettlementDate = dateOption row.SettlementDate
                    MaturityDate = dateOption row.MaturityDate
                    Loans = defaultArg row.Loans 0
                    Wac = defaultArg row.Wac 0m
                    Wam = defaultArg row.Wam 0m
                    Provinces = text row.Provinces
                    Insurers = text row.Insurers
                    SourceUrl = text row.SourceUrl
                }
            IssueDate = row.IssueDate
            SettlementDate = row.SettlementDate
            MaturityDate = row.MaturityDate
            CouponValue = row.CouponValue
            Wam = row.Wam
        })
    |> Seq.toArray

let private loadInsurers (context: Provider.Sql.dataContext) =
    query {
        for row in context.Nhambs.MbsPoolInsurers do
            select row
    }
    |> Seq.map (fun row ->
        {
            PoolCusip = row.PoolCusip
            Name = row.Name
            DollarValue = row.DollarValue
            PercentOfLoans = row.PercentOfLoans
        })
    |> Seq.toArray

let private loadLocations (context: Provider.Sql.dataContext) =
    query {
        for row in context.Nhambs.MbsPoolLocations do
            select row
    }
    |> Seq.map (fun row ->
        {
            PoolCusip = row.PoolCusip
            ProvinceName = row.ProvinceName
            PrincipalAmountPercent = row.PrincipalAmountPercent
        })
    |> Seq.toArray

let private matchesFilter
    (filter: MbsPoolFilter)
    (insurerPools: Set<string>)
    (provincePools: Set<string>)
    (row: MbsPoolRow)
    =
    let search = filter.Search.Trim()

    let matchesSearch =
        String.IsNullOrWhiteSpace search
        || row.PoolNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.Cusip.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.IssuerName.Contains(search, StringComparison.OrdinalIgnoreCase)

    let matchesIssuer =
        String.IsNullOrWhiteSpace filter.Issuer || row.IssuerName = filter.Issuer

    let matchesRateType =
        String.IsNullOrWhiteSpace filter.RateType || row.RateType = filter.RateType

    let matchesPoolType =
        String.IsNullOrWhiteSpace filter.PoolType || row.PoolType = filter.PoolType

    let matchesInsurer =
        String.IsNullOrWhiteSpace filter.Insurer || insurerPools.Contains row.PoolId

    let matchesProvince =
        String.IsNullOrWhiteSpace filter.Province || provincePools.Contains row.PoolId

    let matchesDate =
        match parseDate filter.DateFrom, parseDate filter.DateTo with
        | Some fromDate, Some toDate ->
            match row.IssueDate |> Option.bind parseDate with
            | Some date -> date >= fromDate && date <= toDate
            | None -> false
        | Some fromDate, None ->
            match row.IssueDate |> Option.bind parseDate with
            | Some date -> date >= fromDate
            | None -> false
        | None, Some toDate ->
            match row.IssueDate |> Option.bind parseDate with
            | Some date -> date <= toDate
            | None -> false
        | None, None -> true

    let matchesMaturity =
        match parseDate filter.MaturityFrom, parseDate filter.MaturityTo with
        | Some fromDate, Some toDate ->
            match row.MaturityDate |> Option.bind parseDate with
            | Some date -> date >= fromDate && date <= toDate
            | None -> false
        | Some fromDate, None ->
            match row.MaturityDate |> Option.bind parseDate with
            | Some date -> date >= fromDate
            | None -> false
        | None, Some toDate ->
            match row.MaturityDate |> Option.bind parseDate with
            | Some date -> date <= toDate
            | None -> false
        | None, None -> true

    let matchesStatus =
        match filter.Status.ToLowerInvariant() with
        | "active" ->
            match row.MaturityDate |> Option.bind parseDate with
            | Some date -> date >= DateTime.UtcNow.Date
            | None -> false
        | "inactive" ->
            match row.MaturityDate |> Option.bind parseDate with
            | Some date -> date < DateTime.UtcNow.Date
            | None -> false
        | _ -> true

    matchesSearch
    && matchesIssuer
    && matchesRateType
    && matchesPoolType
    && matchesInsurer
    && matchesProvince
    && matchesDate
    && matchesMaturity
    && matchesStatus

let private filterFacts (context: Provider.Sql.dataContext) (filter: MbsPoolFilter) =
    let facts = loadPoolFacts context

    let insurerPools =
        if String.IsNullOrWhiteSpace filter.Insurer then
            Set.empty
        else
            loadInsurers context
            |> Array.filter (fun insurer -> insurer.Name = filter.Insurer)
            |> Array.map (fun insurer -> insurer.PoolCusip)
            |> Set.ofArray

    let provincePools =
        if String.IsNullOrWhiteSpace filter.Province then
            Set.empty
        else
            loadLocations context
            |> Array.filter (fun location ->
                location.ProvinceName = filter.Province
                && (defaultArg location.PrincipalAmountPercent 0m) > 0m)
            |> Array.map (fun location -> location.PoolCusip)
            |> Set.ofArray

    facts
    |> Array.filter (fun fact -> matchesFilter filter insurerPools provincePools fact.Row)

let private sortRows (sortBy: string) (sortDir: string) (rows: PoolFact[]) =
    let ascending = sortDir.ToLowerInvariant() <> "desc"

    let compareBy =
        match sortBy with
        | "pool_number" -> fun left right -> compare left.Row.PoolNumber right.Row.PoolNumber
        | "cusip" -> fun left right -> compare left.Row.Cusip right.Row.Cusip
        | "issuer_name" -> fun left right -> compare left.Row.IssuerName right.Row.IssuerName
        | "rate_type" -> fun left right -> compare left.Row.RateType right.Row.RateType
        | "pool_type" -> fun left right -> compare left.Row.PoolType right.Row.PoolType
        | "coupon_value" -> fun left right -> compare left.Row.CouponValue right.Row.CouponValue
        | "principal" -> fun left right -> compare left.Row.Principal right.Row.Principal
        | "settlement_date" -> fun left right -> compare left.Row.SettlementDate right.Row.SettlementDate
        | "maturity_date" -> fun left right -> compare left.Row.MaturityDate right.Row.MaturityDate
        | "loans" -> fun left right -> compare left.Row.Loans right.Row.Loans
        | "wac" -> fun left right -> compare left.Row.Wac right.Row.Wac
        | "wam" -> fun left right -> compare left.Row.Wam right.Row.Wam
        | _ -> fun left right -> compare left.Row.IssueDate right.Row.IssueDate

    rows
    |> Array.sortWith (fun left right ->
        let result = compareBy left right

        if result = 0 then compare left.Row.PoolId right.Row.PoolId
        elif ascending then result
        else -result)

let search (context: Provider.Sql.dataContext) (query: MbsPoolQuery) =
    let facts = filterFacts context query.Filter
    let sorted = sortRows query.Filter.SortBy query.Filter.SortDir facts

    {
        Rows = sorted |> Array.map (fun fact -> fact.Row)
        Total = facts.Length
    }

let private sumBy (projection: PoolFact -> decimal) (rows: PoolFact[]) = rows |> Array.sumBy projection

let private weightedAverage (value: PoolFact -> decimal option) (principal: PoolFact -> decimal) (rows: PoolFact[]) =
    let numerator =
        rows
        |> Array.sumBy (fun row ->
            match value row with
            | Some amount -> amount * principal row
            | None -> 0m)

    let denominator =
        rows |> Array.filter (fun row -> (value row).IsSome) |> Array.sumBy principal

    if denominator = 0m then 0m else numerator / denominator

let private monthStart (date: DateTime) = DateTime(date.Year, date.Month, 1)

let private seriesPoint (label: string) (date: string) (value: decimal) (count: int) =
    {
        Label = label
        Date = date
        Value = value
        Secondary = 0m
        Count = count
    }

let private analyticsFor (context: Provider.Sql.dataContext) (filter: MbsPoolFilter) =
    let facts = filterFacts context filter
    let insurers = loadInsurers context
    let locations = loadLocations context

    let totalPrincipal = facts |> sumBy (fun row -> row.Row.Principal)
    let poolCount = facts.Length

    let issuerCount =
        facts
        |> Array.map (fun row -> row.Row.IssuerName)
        |> Array.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Array.distinct
        |> Array.length

    let loanCount = facts |> Array.sumBy (fun row -> row.Row.Loans)

    let averageCoupon =
        weightedAverage (fun row -> row.CouponValue) (fun row -> row.Row.Principal) facts

    let averageWam =
        weightedAverage (fun row -> row.Wam) (fun row -> row.Row.Principal) facts

    let issueDates = facts |> Array.choose (fun row -> row.IssueDate) |> Array.sort

    let newestIssueDate =
        issueDates
        |> Array.tryLast
        |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

    let monthly =
        facts
        |> Array.choose (fun row ->
            row.IssueDate
            |> Option.map (fun date ->
                let month = monthStart date

                month,
                {
                    Label = month.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                    Date = month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    Value = row.Row.Principal
                    Secondary = 0m
                    Count = 1
                }))
        |> Array.groupBy fst
        |> Array.map (fun (month, values) ->
            let points = values |> Array.map snd

            seriesPoint
                points.[0].Label
                points.[0].Date
                (points |> Array.sumBy (fun point -> point.Value))
                points.Length)
        |> Array.sortBy (fun point -> point.Date)

    let byProvince =
        let rowsById = facts |> Array.map (fun row -> row.Row.PoolId, row) |> dict

        locations
        |> Array.filter (fun location ->
            (defaultArg location.PrincipalAmountPercent 0m) > 0m
            && rowsById.ContainsKey location.PoolCusip)
        |> Array.groupBy (fun location -> location.ProvinceName)
        |> Array.map (fun (province, values) ->
            let poolIds =
                values |> Array.map (fun location -> location.PoolCusip) |> Array.distinct

            let principal =
                values
                |> Array.sumBy (fun location ->
                    let fact = rowsById.[location.PoolCusip]

                    fact.Row.Principal * (defaultArg location.PrincipalAmountPercent 0m) / 100m)

            {
                Province = province
                Pools = poolIds.Length
                Principal = principal
                Share =
                    if totalPrincipal = 0m then
                        0m
                    else
                        principal / totalPrincipal * 100m
            })
        |> Array.sortByDescending (fun row -> row.Principal)

    let byRateType =
        facts
        |> Array.groupBy (fun row ->
            if String.IsNullOrWhiteSpace row.Row.RateType then
                "unknown"
            else
                row.Row.RateType)
        |> Array.map (fun (label, values) ->
            {
                Label = label
                Value = values |> Array.sumBy (fun row -> row.Row.Principal)
                Count = values.Length
            })
        |> Array.sortByDescending (fun point -> point.Value)

    let byPoolType =
        facts
        |> Array.groupBy (fun row -> row.Row.PoolType + " · " + row.Row.PoolTypeDescription)
        |> Array.map (fun (label, values) ->
            {
                Label = label
                Value = values |> Array.sumBy (fun row -> row.Row.Principal)
                Count = values.Length
            })
        |> Array.sortByDescending (fun point -> point.Value)

    let byInsurer =
        let rowsById = facts |> Array.map (fun row -> row.Row.PoolId, row) |> dict

        insurers
        |> Array.filter (fun insurer -> rowsById.ContainsKey insurer.PoolCusip)
        |> Array.groupBy (fun insurer -> insurer.Name)
        |> Array.map (fun (name, values) ->
            let pools = values |> Array.map (fun insurer -> insurer.PoolCusip) |> Array.distinct

            let principal =
                values
                |> Array.sumBy (fun insurer ->
                    let fact = rowsById.[insurer.PoolCusip]

                    match insurer.DollarValue with
                    | Some value -> value
                    | None -> fact.Row.Principal * (defaultArg insurer.PercentOfLoans 0m) / 100m)

            name, principal, pools.Length)
        |> Array.sortByDescending (fun (_, principal, _) -> principal)
        |> Array.truncate 12
        |> Array.map (fun (label, principal, count) ->
            {
                Label = label
                Value = principal
                Count = count
            })

    let byMaturityYear =
        facts
        |> Array.choose (fun row ->
            row.MaturityDate
            |> Option.map (fun date ->
                let yearStart = DateTime(date.Year, 1, 1)

                yearStart,
                {
                    Label = date.Year.ToString(CultureInfo.InvariantCulture)
                    Date = yearStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    Value = row.Row.Principal
                    Secondary = 0m
                    Count = 1
                }))
        |> Array.groupBy fst
        |> Array.map (fun (_, values) ->
            let points = values |> Array.map snd

            seriesPoint
                points.[0].Label
                points.[0].Date
                (points |> Array.sumBy (fun point -> point.Value))
                points.Length)
        |> Array.sortBy (fun point -> point.Date)

    let trendMonths =
        monthly
        |> Array.map (fun point -> point.Label)
        |> Array.rev
        |> Array.truncate 12
        |> Array.rev

    let latestMonth =
        match issueDates |> Array.tryLast with
        | Some date -> Some(monthStart date)
        | None -> monthly |> Array.tryLast |> Option.bind (fun point -> parseDate point.Date)

    let periods =
        match latestMonth with
        | None -> [||]
        | Some current ->
            [|
                "Current", current
                "Previous Month", current.AddMonths(-1)
                "Last Year", current.AddYears(-1)
            |]
            |> Array.map (fun (label, month) ->
                let monthEnd = month.AddMonths(1).AddDays(-1.0)

                let upToMonth =
                    facts
                    |> Array.filter (fun row ->
                        match row.IssueDate with
                        | Some date -> date <= monthEnd
                        | None -> false)

                let newPrincipal =
                    upToMonth
                    |> Array.filter (fun row ->
                        match row.IssueDate with
                        | Some date -> date >= month && date <= monthEnd
                        | None -> false)
                    |> Array.sumBy (fun row -> row.Row.Principal)

                {
                    Label = label
                    Month = month.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                    Issuers =
                        upToMonth
                        |> Array.map (fun row -> row.Row.IssuerName)
                        |> Array.distinct
                        |> Array.length
                    Pools = upToMonth.Length
                    NewPrincipal = newPrincipal
                    PoolBalance = upToMonth |> Array.sumBy (fun row -> row.Row.Principal)
                    AverageCoupon =
                        weightedAverage (fun row -> row.CouponValue) (fun row -> row.Row.Principal) upToMonth
                    Loans = upToMonth |> Array.sumBy (fun row -> row.Row.Loans)
                })

    let topIssuers =
        facts
        |> Array.groupBy (fun row -> row.Row.IssuerName)
        |> Array.map (fun (issuer, values) ->
            let principal = values |> Array.sumBy (fun row -> row.Row.Principal)

            {
                Issuer = issuer
                Pools = values.Length
                Principal = principal
                AverageCoupon = weightedAverage (fun row -> row.CouponValue) (fun row -> row.Row.Principal) values
                Loans = values |> Array.sumBy (fun row -> row.Row.Loans)
                Share =
                    if totalPrincipal = 0m then
                        0m
                    else
                        principal / totalPrincipal * 100m
                Trend = [||]
            })
        |> Array.sortByDescending (fun issuer -> issuer.Principal)
        |> Array.truncate 15

    let topIssuerNames = topIssuers |> Array.map (fun issuer -> issuer.Issuer)

    let topIssuersWithTrend =
        topIssuers
        |> Array.map (fun issuer ->
            let trend =
                trendMonths
                |> Array.map (fun label ->
                    facts
                    |> Array.filter (fun row ->
                        row.Row.IssuerName = issuer.Issuer
                        && match row.IssueDate with
                           | Some date -> date.Year.ToString("yyyy-MM", CultureInfo.InvariantCulture) = label
                           | None -> false)
                    |> Array.sumBy (fun row -> row.Row.Principal))

            { issuer with Trend = trend })

    {
        TotalPrincipal = totalPrincipal
        PoolCount = poolCount
        IssuerCount = issuerCount
        LoanCount = loanCount
        AverageCoupon = averageCoupon
        AverageWam = averageWam
        NewestIssueDate = newestIssueDate
        Monthly = monthly
        ByProvince = byProvince
        ByRateType = byRateType
        ByPoolType = byPoolType
        ByInsurer = byInsurer
        ByMaturityYear = byMaturityYear
        TopIssuers = topIssuersWithTrend
        Periods = periods
        TrendMonths = trendMonths
    }

let analytics (context: Provider.Sql.dataContext) (filter: MbsPoolFilter) = analyticsFor context filter

let filterOptions (context: Provider.Sql.dataContext) =
    let issuers =
        query {
            for row in context.Nhambs.VMbsPoolGrid do
                select row.IssuerName
        }
        |> Seq.choose id
        |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray

    let rateTypes =
        query {
            for row in context.Nhambs.VMbsPoolGrid do
                select row.RateType
        }
        |> Seq.choose id
        |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray

    let poolTypes =
        query {
            for row in context.Nhambs.VMbsPoolGrid do
                select (row.PoolType, row.PoolTypeDescription)
        }
        |> Seq.distinct
        |> Seq.sortBy fst
        |> Seq.map (fun (code, label) ->
            {
                Code = Option.get code
                Label = Option.get label
            })
        |> Seq.toArray

    let insurers =
        query {
            for row in context.Nhambs.MbsPoolInsurers do
                select row.Name
        }
        |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray

    let provinces =
        query {
            for row in context.Nhambs.MbsPoolLocations do
                select row.ProvinceName
        }
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray

    let issueDates =
        query {
            for row in context.Nhambs.VMbsPoolGrid do
                select row.IssueDate
        }
        |> Seq.choose id
        |> Seq.sort
        |> Seq.toArray

    {
        Issuers = issuers
        RateTypes = rateTypes
        PoolTypes = poolTypes
        Insurers = insurers
        Provinces = provinces
        MinIssueDate =
            issueDates
            |> Array.tryHead
            |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        MaxIssueDate =
            issueDates
            |> Array.tryLast
            |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
    }

let getDetail (context: Provider.Sql.dataContext) (cusip: string) =

    let viewRow =
        query {
            for row in context.Nhambs.VMbsPoolGrid do
                where (row.Cusip = Some cusip)
                select row
        }
        |> Seq.tryHead

    match viewRow with
    | None -> None
    | Some view ->
        let text = Option.get

        let poolRow =
            {
                PoolId = text view.PoolId
                Cusip = text view.Cusip
                PoolNumber = text view.PoolNumber
                IssuerName = text view.IssuerName
                RateType = text view.RateType
                PoolType = text view.PoolType
                PoolTypeDescription = text view.PoolTypeDescription
                CouponDisplay = text view.CouponDisplay
                CouponValue = defaultArg view.CouponValue 0m
                Principal = defaultArg view.Principal 0m
                IssueDate = dateOption view.IssueDate
                SettlementDate = dateOption view.SettlementDate
                MaturityDate = dateOption view.MaturityDate
                Loans = defaultArg view.Loans 0
                Wac = defaultArg view.Wac 0m
                Wam = defaultArg view.Wam 0m
                Provinces = text view.Provinces
                Insurers = text view.Insurers
                SourceUrl = text view.SourceUrl
            }

        let pool =
            query {
                for row in context.Nhambs.MbsPools do
                    where (row.Cusip = cusip)
                    select row
            }
            |> Seq.tryHead

        match pool with
        | None -> None
        | Some pool ->
            let insurers =
                query {
                    for row in context.Nhambs.MbsPoolInsurers do
                        where (row.PoolCusip = cusip)
                        select row
                }
                |> Seq.sortByDescending (fun row -> row.DollarValue |> Option.defaultValue 0m)
                |> Seq.map (fun row ->
                    {
                        Name = row.Name
                        DollarValue = defaultArg row.DollarValue 0m
                        PercentOfLoans = defaultArg row.PercentOfLoans 0m
                    })
                |> Seq.toArray

            let locations =
                query {
                    for row in context.Nhambs.MbsPoolLocations do
                        where (row.PoolCusip = cusip)
                        select row
                }
                |> Seq.sortByDescending (fun row -> row.PrincipalAmountPercent |> Option.defaultValue 0m)
                |> Seq.map (fun row ->
                    {
                        Province = row.ProvinceName
                        LoansPercent = defaultArg row.LoansPercent 0m
                        PrincipalAmountPercent = defaultArg row.PrincipalAmountPercent 0m
                    })
                |> Seq.toArray

            let bucketOrder =
                dict
                    [
                        "5 months", 0
                        "4 months", 1
                        "3 months", 2
                        "2 months", 3
                        "1 month", 4
                        "At maturity", 5
                        "Total", 6
                    ]

            let buckets =
                query {
                    for row in context.Nhambs.MbsPoolScheduledMaturity do
                        where (row.PoolCusip = cusip)
                        select row
                }
                |> Seq.sortBy (fun row ->
                    match bucketOrder.TryGetValue row.Bucket with
                    | true, value -> value
                    | _ -> 99)
                |> Seq.map (fun row ->
                    {
                        Bucket = row.Bucket
                        RemainingFaceValue = defaultArg row.RemainingFaceValue 0m
                        WeightedAverageRate = defaultArg row.WeightedAverageRate 0m
                        WeightedAverageAmortization = defaultArg row.WeightedAverageAmortization 0m
                        IndexSpreadTotal = defaultArg row.IndexSpreadTotal 0m
                        IndexSpreadFullTerm = defaultArg row.IndexSpreadFullTerm 0m
                        MonthlyPayment = defaultArg row.MonthlyPayment 0m
                    })
                |> Seq.toArray

            let bands =
                query {
                    for row in context.Nhambs.MbsPoolRateBands do
                        where (row.PoolCusip = cusip)
                        select row
                }
                |> Seq.sortBy (fun row -> row.Id)
                |> Seq.map (fun row ->
                    {
                        RateBand = row.RateBand
                        Total = defaultArg row.Total 0m
                        PercentOfTotal = defaultArg row.PercentOfTotal 0m
                        RemainingFaceValueByDate = parseDecimalArray row.RemainingFaceValueByDate
                    })
                |> Seq.toArray

            Some
                {
                    Row = poolRow
                    Custodian = pool.Custodian
                    MortgageInsurer = pool.MortgageInsurer
                    FormVersion = pool.FormVersion
                    CouponKind = pool.CouponKind
                    CouponIndex = pool.CouponIndex
                    CouponSpread = defaultArg pool.CouponSpreadPercent 0m
                    CouponFixedRate = defaultArg pool.CouponFixedRatePercent 0m
                    FirstPaymentDue = dateOption pool.FirstPaymentDue
                    FinalPaymentDue = dateOption pool.FinalPaymentDue
                    WeightedAverageInterestAdjustmentDate = dateOption pool.WeightedAverageInterestAdjustmentDate
                    WeightedAverageMaturityDate = dateOption pool.WeightedAverageMaturityDate
                    ScheduledRemainingFaceValue = defaultArg pool.ScheduledRemainingFaceValue 0m
                    ScheduledWac = defaultArg pool.ScheduledWac 0m
                    ScheduledWam = defaultArg pool.ScheduledWam 0m
                    ScheduledIndexSpreadTotal = defaultArg pool.ScheduledIndexSpreadTotal 0m
                    ScheduledMonthlyPayment = defaultArg pool.ScheduledMonthlyPayment 0m
                    HomeownerPercent = defaultArg pool.HomeownerPercent 0m
                    MultipleFamilyPercent = defaultArg pool.MultipleFamilyPercent 0m
                    SocialHousingPercent = defaultArg pool.SocialHousingPercent 0m
                    RamByAmortization = parseDecimalArray pool.RamByAmortization
                    RamByTerm = parseDecimalArray pool.RamByTerm
                    InterestAdjustmentDates = parseStringArray pool.InterestAdjustmentDates
                    InterestTotals = [| parseByDate pool.InterestAdjustmentTotals "Remaining face value" |]
                    InterestPercent = [| parseByDate pool.InterestAdjustmentPercent "Percent of total" |]
                    InterestWac = parseDecimalArray pool.InterestAdjustmentWac
                    MaturityBuckets = buckets
                    Insurers = insurers
                    Locations = locations
                    RateBands = bands
                    PrepaymentBonaFideExempt = pool.PrepaymentBonaFideExempt |> Option.defaultValue false
                    PrepaymentSectionA = pool.PrepaymentSectionA
                    PrepaymentSectionB = pool.PrepaymentSectionB
                    PrepaymentOther = pool.PrepaymentOtherDisclosures
                    SourceFile = pool.SourceFile
                    IngestedAt = pool.IngestedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                }

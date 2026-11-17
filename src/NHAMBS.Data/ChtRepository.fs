module NHAMBS.Data.ChtRepository

open System
open System.Globalization
open NHAMBS.Shared.Domain
open NHAMBS.Data

let private dateOption (value: DateTime option) =
    value
    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let private loadIssues (context: Provider.Sql.dataContext) =
    let circularCounts =
        query {
            for offering in context.Nhambs.ChtOfferingCirculars do
                select offering.IssueId
        }
        |> Seq.countBy id
        |> dict

    let history =
        query {
            for row in context.Nhambs.VChtHistorySummary do
                select row
        }
        |> Seq.map (fun row -> Option.get row.Series, row)
        |> dict

    query {
        for issue in context.Nhambs.ChtIssues do
            select issue
    }
    |> Seq.map (fun issue ->
        let historyRow =
            match history.TryGetValue issue.Series with
            | true, value -> Some value
            | _ -> None

        let circularCount =
            match circularCounts.TryGetValue issue.Id with
            | true, value -> value
            | _ -> 0

        {
            IssueId = issue.Id
            Series = issue.Series
            SettlementDate = dateOption issue.SettlementDate
            CouponDisplay = issue.CouponDisplay
            CouponKind = issue.CouponKind
            CouponValue = defaultArg (issue.CouponFixedRatePercent |> Option.orElse issue.CouponSpreadPercent) 0m
            MaturityDate = dateOption issue.MaturityDate
            Currency = issue.Currency
            InterestPaymentFrequency = issue.InterestPaymentFrequency
            IssuedToInvestors = defaultArg issue.IssuedToInvestors 0m
            IssuedToGovernment = defaultArg issue.IssuedToGovernmentOfCanada 0m
            TotalIssuance = defaultArg issue.TotalIssuance 0m
            Outstanding = defaultArg issue.Outstanding 0m
            Identifier = issue.Identifier
            CircularCount = circularCount
            HistoryAvailable = historyRow.IsSome
            CancellationCount =
                historyRow
                |> Option.map (fun row -> int (defaultArg row.CancellationCount 0L))
                |> Option.defaultValue 0
            CancelledMillions =
                historyRow
                |> Option.map (fun row -> defaultArg row.CancelledMillions 0m)
                |> Option.defaultValue 0m
            SourceUrl = issue.SourceUrl
        })
    |> Seq.sortWith (fun left right ->
        match left.SettlementDate, right.SettlementDate with
        | None, None -> compare left.Series right.Series
        | None, _ -> 1
        | _, None -> -1
        | Some a, Some b ->
            match compare b a with
            | 0 -> compare left.Series right.Series
            | value -> value)
    |> Seq.toArray

let private loadCirculars (context: Provider.Sql.dataContext) =
    let ratings =
        query {
            for rating in context.Nhambs.ChtCircularRatings do
                select rating
        }
        |> Seq.groupBy (fun rating -> rating.CircularId)
        |> Seq.map (fun (circularId, items) ->
            circularId,
            items
            |> Seq.sortBy (fun item -> item.Id)
            |> Seq.map (fun item -> [ item.Rating; item.Agency ] |> List.choose id |> String.concat " · ")
            |> String.concat ", ")
        |> dict

    query {
        for circular in context.Nhambs.ChtCirculars do
            select circular
    }
    |> Seq.map (fun circular ->
        let ratingText =
            match ratings.TryGetValue circular.Id with
            | true, value -> value
            | _ -> ""

        {
            CircularId = circular.Id
            Series = circular.Series
            Designation = circular.Designation
            DocumentDate = dateOption circular.DocumentDate
            Currency = circular.Currency
            AggregatePrincipalAmount = defaultArg circular.AggregatePrincipalAmount 0m
            CouponDisplay = circular.CouponDisplay
            CouponKind = circular.CouponKind
            CouponValue = defaultArg (circular.CouponFixedRatePercent |> Option.orElse circular.CouponSpreadPercent) 0m
            MaturityDate = dateOption circular.MaturityDate
            IssuePricePercent = defaultArg circular.IssuePricePercent 0m
            SettlementDate = dateOption circular.SettlementDate
            FirstInterestPaymentDate = dateOption circular.FirstInterestPaymentDate
            InterestPaymentFrequency = circular.InterestPaymentFrequency
            MinimumDenomination = defaultArg circular.MinimumDenomination 0m
            IssuerName = circular.IssuerName
            Isin = circular.Isin
            Cusip = circular.Cusip
            CommonCode = circular.CommonCode
            LegalEntityIdentifier = circular.LegalEntityIdentifier
            ListingExchange = circular.ListingExchange
            Ratings = ratingText
            YieldPercent = defaultArg circular.YieldPercent 0m
            SourceUrl = circular.SourceUrl
        })
    |> Seq.sortWith (fun left right ->
        match left.SettlementDate, right.SettlementDate with
        | None, None -> compare (left.Series, left.Designation) (right.Series, right.Designation)
        | None, _ -> 1
        | _, None -> -1
        | Some a, Some b ->
            match compare b a with
            | 0 -> compare (left.Series, left.Designation) (right.Series, right.Designation)
            | value -> value)
    |> Seq.toArray

let private loadHistoryRows (context: Provider.Sql.dataContext) =
    query {
        for row in context.Nhambs.VChtHistorySummary do
            select row
    }
    |> Seq.map (fun row ->
        {
            HistoryId = Option.get row.Id
            Series = Option.get row.Series
            AsOfDate = dateOption row.AsOfDate
            Title = row.Title
            CouponDisplay = row.CouponDisplay
            CouponKind = row.CouponKind
            CouponValue = defaultArg row.CouponValue 0m
            MaturityDate = dateOption row.MaturityDate
            Identifier = row.Identifier
            CancellationCount = int (defaultArg row.CancellationCount 0L)
            CancelledMillions = defaultArg row.CancelledMillions 0m
            FirstCancellationDate = dateOption row.FirstCancellationDate
            LastCancellationDate = dateOption row.LastCancellationDate
            Cancellations = [||]
            SourceUrl = row.SourceUrl
        })
    |> Seq.sortBy (fun row -> row.Series)
    |> Seq.toArray

let private loadHistoryCancellations (context: Provider.Sql.dataContext) =
    query {
        for cancellation in context.Nhambs.ChtOutstandingHistoryCancellations do
            select cancellation
    }
    |> Seq.sortBy (fun cancellation -> cancellation.HistoryId, cancellation.Sequence)
    |> Seq.map (fun cancellation ->
        cancellation.HistoryId,
        {
            FromDate = dateOption cancellation.FromDate
            ToDate = dateOption cancellation.ToDate
            CancelledAmount = defaultArg cancellation.CancelledAmount 0m
        })
    |> Seq.toArray

let private loadHistory (context: Provider.Sql.dataContext) =
    let rows = loadHistoryRows context
    let cancellations = loadHistoryCancellations context

    let byHistory = cancellations |> Array.groupBy fst |> dict

    rows
    |> Array.map (fun row ->
        let periods =
            match byHistory.TryGetValue row.HistoryId with
            | true, values -> values |> Array.map snd
            | _ -> [||]

        { row with Cancellations = periods })

let private parseDate (value: string) =
    match DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
    | true, date -> Some date
    | _ -> None

let private couponBucket (rate: decimal) =
    if rate = 0m then "Floating"
    elif rate < 1m then "< 1.00%"
    elif rate < 2m then "1.00 – 1.99%"
    elif rate < 3m then "2.00 – 2.99%"
    elif rate < 4m then "3.00 – 3.99%"
    elif rate < 5m then "4.00 – 4.99%"
    else "5.00% +"

let private analytics (issues: ChtIssueRow[]) (circulars: ChtCircularRow[]) (history: ChtHistoryRow[]) =
    let outstanding = issues |> Array.sumBy (fun issue -> issue.Outstanding)
    let totalIssuance = issues |> Array.sumBy (fun issue -> issue.TotalIssuance)

    let averageCoupon =
        let weighted =
            issues
            |> Array.sumBy (fun issue ->
                if issue.CouponValue = 0m then
                    0m
                else
                    issue.CouponValue * issue.Outstanding)

        let baseAmount =
            issues
            |> Array.filter (fun issue -> issue.CouponValue <> 0m)
            |> Array.sumBy (fun issue -> issue.Outstanding)

        if baseAmount = 0m then 0m else weighted / baseAmount

    let nextMaturity =
        issues
        |> Array.choose (fun issue -> issue.MaturityDate |> Option.bind parseDate)
        |> Array.sort
        |> Array.tryHead
        |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

    let byMaturityYear =
        issues
        |> Array.choose (fun issue ->
            issue.MaturityDate
            |> Option.bind parseDate
            |> Option.map (fun date ->
                {
                    Label = date.ToString("yyyy", CultureInfo.InvariantCulture)
                    Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    Value = issue.Outstanding
                    Secondary = 0m
                    Count = 1
                }))
        |> Array.groupBy (fun point -> point.Label)
        |> Array.map (fun (label, points) ->
            {
                Label = label
                Date = points |> Array.minBy (fun point -> point.Date) |> fun point -> point.Date
                Value = points |> Array.sumBy (fun point -> point.Value)
                Secondary = 0m
                Count = points |> Array.sumBy (fun point -> point.Count)
            })
        |> Array.sortBy (fun point -> point.Date)

    let byCoupon =
        issues
        |> Array.groupBy (fun issue -> couponBucket issue.CouponValue)
        |> Array.map (fun (label, items) ->
            {
                Label = label
                Value = items |> Array.sumBy (fun issue -> issue.Outstanding)
                Count = items.Length
            })
        |> Array.sortBy (fun point -> point.Label)

    let byCurrency =
        issues
        |> Array.groupBy (fun issue ->
            match issue.Currency with
            | Some value when not (String.IsNullOrWhiteSpace value) -> value
            | _ -> "CAD")
        |> Array.map (fun (label, items) ->
            {
                Label = label
                Value = items |> Array.sumBy (fun issue -> issue.Outstanding)
                Count = items.Length
            })
        |> Array.sortByDescending (fun point -> point.Value)

    let historyAsOfDate =
        history
        |> Array.choose (fun row -> row.AsOfDate |> Option.bind parseDate)
        |> Array.sortDescending
        |> Array.tryHead
        |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

    {
        OutstandingMillions = outstanding
        TotalIssuanceMillions = totalIssuance
        IssueCount = issues.Length
        CircularCount = circulars.Length
        AverageCoupon = averageCoupon
        NextMaturityDate = nextMaturity
        HistoryCount = history.Length
        CancellationCount = history |> Array.sumBy (fun row -> row.CancellationCount)
        CancelledMillions = history |> Array.sumBy (fun row -> row.CancelledMillions)
        HistoryAsOfDate = historyAsOfDate
        ByMaturityYear = byMaturityYear
        ByCouponBucket = byCoupon
        ByCurrency = byCurrency
    }

let getData (context: Provider.Sql.dataContext) =
    let issues = loadIssues context
    let circulars = loadCirculars context
    let history = loadHistory context

    {
        Analytics = analytics issues circulars history
        Issues = issues
        Circulars = circulars
        History = history
    }

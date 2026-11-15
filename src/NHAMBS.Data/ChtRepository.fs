module NHAMBS.Data.ChtRepository

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

let private boolValue (reader: DbDataReader) index =
    if reader.IsDBNull index then
        false
    else
        reader.GetBoolean index

let private dateText (reader: DbDataReader) index =
    if reader.IsDBNull index then
        ""
    else
        reader.GetDateTime(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let private loadIssues (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            """
            SELECT i.id,
                   i.series,
                   i.settlement_date,
                   COALESCE(i.coupon_display, ''),
                   COALESCE(i.coupon_kind, ''),
                   COALESCE(i.coupon_fixed_rate_percent, i.coupon_spread_percent) AS coupon_value,
                   i.maturity_date,
                   COALESCE(i.currency, ''),
                   COALESCE(i.interest_payment_frequency, ''),
                   COALESCE(i.issued_to_investors, 0),
                   COALESCE(i.issued_to_government_of_canada, 0),
                   COALESCE(i.total_issuance, 0),
                   COALESCE(i.outstanding, 0),
                   COALESCE(i.identifier, ''),
                   (SELECT COUNT(*) FROM nhambs.cht_offering_circulars o WHERE o.issue_id = i.id) AS circular_count,
                   (h.series IS NOT NULL) AS history_available,
                   COALESCE(h.cancellation_count, 0),
                   COALESCE(h.cancelled_millions, 0),
                   COALESCE(i.source_url, '')
            FROM nhambs.cht_issues i
            LEFT JOIN nhambs.v_cht_history_summary h ON h.series = i.series
            ORDER BY i.settlement_date DESC NULLS LAST, i.series;
            """,
            connection
        )

    use reader = command.ExecuteReader()
    let rows = ResizeArray<ChtIssueRow>()

    while reader.Read() do
        rows.Add(
            {
                IssueId = intValue reader 0
                Series = text reader 1
                SettlementDate = dateText reader 2
                CouponDisplay = text reader 3
                CouponKind = text reader 4
                CouponValue = defaultArg (decimalOpt reader 5) 0m
                MaturityDate = dateText reader 6
                Currency = text reader 7
                InterestPaymentFrequency = text reader 8
                IssuedToInvestors = decimalOrZero reader 9
                IssuedToGovernment = decimalOrZero reader 10
                TotalIssuance = decimalOrZero reader 11
                Outstanding = decimalOrZero reader 12
                Identifier = text reader 13
                CircularCount = intValue reader 14
                HistoryAvailable = boolValue reader 15
                CancellationCount = intValue reader 16
                CancelledMillions = decimalOrZero reader 17
                SourceUrl = text reader 18
            }
        )

    rows.ToArray()

let private loadCirculars (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            """
            SELECT c.id,
                   c.series,
                   c.designation,
                   c.document_date,
                   COALESCE(c.currency, ''),
                   COALESCE(c.aggregate_principal_amount, 0),
                   COALESCE(c.coupon_display, ''),
                   COALESCE(c.coupon_kind, ''),
                   COALESCE(c.coupon_fixed_rate_percent, c.coupon_spread_percent) AS coupon_value,
                   c.maturity_date,
                   c.issue_price_percent,
                   c.settlement_date,
                   c.first_interest_payment_date,
                   COALESCE(c.interest_payment_frequency, ''),
                   c.minimum_denomination,
                   COALESCE(c.issuer_name, ''),
                   COALESCE(c.isin, ''),
                   COALESCE(c.cusip, ''),
                   COALESCE(c.common_code, ''),
                   COALESCE(c.legal_entity_identifier, ''),
                   COALESCE(c.listing_exchange, ''),
                   COALESCE((
                       SELECT string_agg(r.rating || ' · ' || r.agency, ', ' ORDER BY r.id)
                       FROM nhambs.cht_circular_ratings r
                       WHERE r.circular_id = c.id
                   ), '') AS ratings,
                   c.yield_percent,
                   COALESCE(c.source_url, '')
            FROM nhambs.cht_circulars c
            ORDER BY c.settlement_date DESC NULLS LAST, c.series, c.designation;
            """,
            connection
        )

    use reader = command.ExecuteReader()
    let rows = ResizeArray<ChtCircularRow>()

    while reader.Read() do
        rows.Add(
            {
                CircularId = intValue reader 0
                Series = text reader 1
                Designation = text reader 2
                DocumentDate = dateText reader 3
                Currency = text reader 4
                AggregatePrincipalAmount = decimalOrZero reader 5
                CouponDisplay = text reader 6
                CouponKind = text reader 7
                CouponValue = defaultArg (decimalOpt reader 8) 0m
                MaturityDate = dateText reader 9
                IssuePricePercent = defaultArg (decimalOpt reader 10) 0m
                SettlementDate = dateText reader 11
                FirstInterestPaymentDate = dateText reader 12
                InterestPaymentFrequency = text reader 13
                MinimumDenomination = defaultArg (decimalOpt reader 14) 0m
                IssuerName = text reader 15
                Isin = text reader 16
                Cusip = text reader 17
                CommonCode = text reader 18
                LegalEntityIdentifier = text reader 19
                ListingExchange = text reader 20
                Ratings = text reader 21
                YieldPercent = defaultArg (decimalOpt reader 22) 0m
                SourceUrl = text reader 23
            }
        )

    rows.ToArray()

let private loadHistoryRows (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            """
            SELECT h.id,
                   h.series,
                   h.as_of_date,
                   h.title,
                   h.coupon_display,
                   h.coupon_kind,
                   COALESCE(h.coupon_value, 0),
                   h.maturity_date,
                   h.identifier,
                   h.cancellation_count,
                   h.cancelled_millions,
                   h.first_cancellation_date,
                   h.last_cancellation_date,
                   h.source_url
            FROM nhambs.v_cht_history_summary h
            ORDER BY h.series;
            """,
            connection
        )

    use reader = command.ExecuteReader()
    let rows = ResizeArray<ChtHistoryRow>()

    while reader.Read() do
        rows.Add(
            {
                HistoryId = intValue reader 0
                Series = text reader 1
                AsOfDate = dateText reader 2
                Title = text reader 3
                CouponDisplay = text reader 4
                CouponKind = text reader 5
                CouponValue = defaultArg (decimalOpt reader 6) 0m
                MaturityDate = dateText reader 7
                Identifier = text reader 8
                CancellationCount = intValue reader 9
                CancelledMillions = decimalOrZero reader 10
                FirstCancellationDate = dateText reader 11
                LastCancellationDate = dateText reader 12
                Cancellations = [||]
                SourceUrl = text reader 13
            }
        )

    rows.ToArray()

let private loadHistoryCancellations (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            """
            SELECT c.history_id,
                   c.from_date,
                   c.to_date,
                   COALESCE(c.cancelled_amount, 0)
            FROM nhambs.cht_outstanding_history_cancellations c
            ORDER BY c.history_id, c.sequence;
            """,
            connection
        )

    use reader = command.ExecuteReader()
    let rows = ResizeArray<int * ChtCancellationRow>()

    while reader.Read() do
        rows.Add(
            intValue reader 0,
            {
                FromDate = dateText reader 1
                ToDate = dateText reader 2
                CancelledAmount = decimalOrZero reader 3
            }
        )

    rows.ToArray()

let private loadHistory (connection: NpgsqlConnection) =
    let rows = loadHistoryRows connection
    let cancellations = loadHistoryCancellations connection

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
        |> Array.choose (fun issue -> parseDate issue.MaturityDate)
        |> Array.sort
        |> Array.tryHead
        |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        |> Option.defaultValue ""

    let byMaturityYear =
        issues
        |> Array.choose (fun issue ->
            parseDate issue.MaturityDate
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
            if String.IsNullOrWhiteSpace issue.Currency then
                "CAD"
            else
                issue.Currency)
        |> Array.map (fun (label, items) ->
            {
                Label = label
                Value = items |> Array.sumBy (fun issue -> issue.Outstanding)
                Count = items.Length
            })
        |> Array.sortByDescending (fun point -> point.Value)

    let historyAsOfDate =
        history
        |> Array.choose (fun row -> parseDate row.AsOfDate)
        |> Array.sortDescending
        |> Array.tryHead
        |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        |> Option.defaultValue ""

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

let getData () =
    Database.withConnection (fun connection ->
        let issues = loadIssues connection
        let circulars = loadCirculars connection
        let history = loadHistory connection

        {
            Analytics = analytics issues circulars history
            Issues = issues
            Circulars = circulars
            History = history
        })

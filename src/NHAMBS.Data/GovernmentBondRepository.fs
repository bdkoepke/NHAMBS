module NHAMBS.Data.GovernmentBondRepository

open System
open System.Globalization
open NHAMBS.Shared.Domain

let private dateText (value: DateTime option) =
    value
    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let private timestampText (value: DateTime option) =
    value
    |> Option.map (fun date -> date.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))

let private couponBucket (coupon: decimal) =
    if coupon < 1m then "< 1.00%"
    elif coupon < 2m then "1.00 – 1.99%"
    elif coupon < 3m then "2.00 – 2.99%"
    elif coupon < 4m then "3.00 – 3.99%"
    elif coupon < 5m then "4.00 – 4.99%"
    else "5.00% +"

let private securityLabel (securityType: string) =
    if securityType = "BOND" then "Bonds" else "Treasury bills"

let get (context: Provider.Sql.dataContext) =
    let freshness =
        query {
            for row in context.Nhambs.VGovernmentBondFreshness do
                select row
        }
        |> Seq.map (fun row -> defaultArg row.ProgramCode "", row)
        |> dict

    let programs =
        query {
            for row in context.Nhambs.VGovernmentBondProgramSummary do
                select row
        }
        |> Seq.map (fun row ->
            let fresh = freshness.[defaultArg row.ProgramCode ""]

            ({
                ProgramCode = Option.get row.ProgramCode
                Name = Option.get row.Name
                IssuerName = Option.get row.IssuerName
                GovernmentLevel = Option.get row.GovernmentLevel
                SourceName = Option.get row.SourceName
                SourceUrl = Option.get row.SourceUrl
                Cadence = Option.get row.Cadence
                Notes = Option.get row.Notes
                LatestAsOfDate = dateText row.LatestAsOfDate
                IsStale = defaultArg fresh.IsStale false
                IssueCount = defaultArg row.IssueCount 0L |> int
                BondOutstanding = defaultArg row.BondOutstanding 0m
                BillOutstanding = defaultArg row.BillOutstanding 0m
                TotalOutstanding = defaultArg row.TotalOutstanding 0m
                WeightedAverageCoupon = row.WeightedAverageCoupon
                LastLoadedAt = timestampText fresh.LastLoadedAt
            }
            : GovernmentBondProgramInfo))
        |> Seq.sortBy (fun program -> program.ProgramCode)
        |> Seq.toArray

    let programNames =
        programs |> Array.map (fun program -> program.ProgramCode, program.Name) |> dict

    let issues =
        query {
            for row in context.Nhambs.VGovernmentBondLatest do
                select row
        }
        |> Seq.map (fun row ->
            let programCode = Option.get row.ProgramCode

            ({
                ProgramCode = programCode
                ProgramName = programNames.[programCode]
                Isin = Option.get row.Isin
                SecurityType = Option.get row.SecurityType
                InstrumentType = Option.get row.InstrumentType
                CouponRatePercent = defaultArg row.CouponRatePercent 0m
                IssueDate =
                    row.IssueDate
                    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    |> Option.defaultValue ""
                MaturityDate =
                    row.MaturityDate
                    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    |> Option.defaultValue ""
                OutstandingAmount = defaultArg row.OutstandingAmount 0m
                InflationAdjustment = row.InflationAdjustment
                OutstandingInflationAdjusted = defaultArg row.OutstandingInflationAdjusted 0m
                AsOfDate =
                    row.AsOfDate
                    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    |> Option.defaultValue ""
            }
            : GovernmentBondIssueRow))
        |> Seq.sortBy (fun issue -> (if issue.SecurityType = "BOND" then 0 else 1), issue.MaturityDate, issue.Isin)
        |> Seq.toArray

    let bonds = issues |> Array.filter (fun issue -> issue.SecurityType = "BOND")
    let bills = issues |> Array.filter (fun issue -> issue.SecurityType <> "BOND")
    let bondOutstanding = bonds |> Array.sumBy (fun issue -> issue.OutstandingAmount)
    let billOutstanding = bills |> Array.sumBy (fun issue -> issue.OutstandingAmount)

    let weightedAverageCoupon =
        if bondOutstanding = 0m then
            0m
        else
            bonds
            |> Array.sumBy (fun issue -> issue.CouponRatePercent * issue.OutstandingAmount)
            |> fun total -> total / bondOutstanding

    let byMaturityYear =
        bonds
        |> Array.groupBy (fun issue ->
            match DateTime.TryParse(issue.MaturityDate, CultureInfo.InvariantCulture) with
            | true, date -> date.Year
            | _ -> 0)
        |> Array.filter (fun (year, _) -> year > 0)
        |> Array.map (fun (year, rows) ->
            {
                Label = string year
                Date = sprintf "%d-01-01" year
                Value = rows |> Array.sumBy (fun issue -> issue.OutstandingAmount)
                Secondary = 0m
                Count = rows.Length
            })
        |> Array.sortBy (fun point -> point.Date)

    let couponOrder =
        [
            "< 1.00%"
            "1.00 – 1.99%"
            "2.00 – 2.99%"
            "3.00 – 3.99%"
            "4.00 – 4.99%"
            "5.00% +"
        ]

    let byCouponBucket =
        bonds
        |> Array.groupBy (fun issue -> couponBucket issue.CouponRatePercent)
        |> Array.map (fun (label, rows) ->
            {
                Label = label
                Value = rows |> Array.sumBy (fun issue -> issue.OutstandingAmount)
                Count = rows.Length
            })
        |> Array.sortBy (fun point ->
            couponOrder
            |> List.tryFindIndex ((=) point.Label)
            |> Option.defaultValue couponOrder.Length)

    let bySecurityType =
        issues
        |> Array.groupBy (fun issue -> securityLabel issue.SecurityType)
        |> Array.map (fun (label, rows) ->
            {
                Label = label
                Value = rows |> Array.sumBy (fun issue -> issue.OutstandingAmount)
                Count = rows.Length
            })
        |> Array.sortByDescending (fun point -> point.Value)

    let asOfDate =
        issues
        |> Array.map (fun issue -> issue.AsOfDate)
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.sortDescending
        |> Array.tryHead

    let provincialSummary =
        query {
            for row in context.Nhambs.VProvincialBondSummary do
                select row
        }
        |> Seq.map (fun row ->
            ({
                ProvinceCode = row.ProvinceCode
                ProvinceName = Option.get row.ProvinceName
                TransactionCount = defaultArg row.TransactionCount 0L |> int
                IssueCount = defaultArg row.IssueCount 0L |> int
                IssuerCount = defaultArg row.IssuerCount 0L |> int
                AmountPurchased = defaultArg row.AmountPurchased 0m
                WeightedAverageYield = row.WeightedAverageYield
                FirstTradeDate = dateText row.FirstTradeDate
                LastTradeDate = dateText row.LastTradeDate
            }
            : ProvincialBondSummaryRow))
        |> Seq.sortByDescending (fun row -> row.AmountPurchased)
        |> Seq.toArray

    let provincialIssues =
        query {
            for row in context.Nhambs.VProvincialBondIssueSummary do
                select row
        }
        |> Seq.map (fun row ->
            ({
                IssuerName = Option.get row.IssuerName
                ProvinceCode = row.ProvinceCode
                ProvinceName = Option.get row.ProvinceName
                Isin = Option.get row.Isin
                CouponRatePercent = defaultArg row.CouponRatePercent 0m
                MaturityDate =
                    row.MaturityDate
                    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    |> Option.defaultValue ""
                TransactionCount = defaultArg row.TransactionCount 0L |> int
                AmountPurchased = defaultArg row.AmountPurchased 0m
                WeightedAverageYield = row.WeightedAverageYield
                FirstTradeDate = dateText row.FirstTradeDate
                LastTradeDate = dateText row.LastTradeDate
            }
            : ProvincialBondIssueRow))
        |> Seq.sortByDescending (fun row -> row.AmountPurchased)
        |> Seq.toArray

    {
        Analytics =
            {
                AsOfDate = asOfDate
                BondOutstanding = bondOutstanding
                BillOutstanding = billOutstanding
                TotalOutstanding = bondOutstanding + billOutstanding
                IssueCount = issues.Length
                BondIssueCount = bonds.Length
                WeightedAverageCoupon = weightedAverageCoupon
                ByMaturityYear = byMaturityYear
                ByCouponBucket = byCouponBucket
                BySecurityType = bySecurityType
            }
        Issues = issues
        Programs = programs
        ProvincialSummary = provincialSummary
        ProvincialIssues = provincialIssues
    }

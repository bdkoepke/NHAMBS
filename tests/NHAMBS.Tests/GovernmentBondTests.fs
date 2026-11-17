module NHAMBS.GovernmentBondTests

open System
open System.IO
open Xunit
open NHAMBS.Etl.GovernmentBondParser

let private fixture path =
    Path.Combine(AppContext.BaseDirectory, "fixtures", path)

[<Fact>]
let ``Government bond parser selects the latest as-of snapshot`` () =
    let result = parseGocOutstanding (File.ReadAllText(fixture "goc-outstanding.json"))

    Assert.Equal(Some(DateTime(2026, 11, 13)), result.AsOfDate)
    Assert.Equal(2, result.Issues.Length)
    Assert.Empty(result.Warnings)

[<Fact>]
let ``Government bond parser maps coupons, dates and outstanding amounts`` () =
    let result = parseGocOutstanding (File.ReadAllText(fixture "goc-outstanding.json"))

    let issue = result.Issues |> Array.find (fun item -> item.Isin = "CA135087VS05")

    Assert.Equal("BOND", issue.SecurityType)
    Assert.Equal("BD-REAL", issue.InstrumentType)
    Assert.Equal(4.25m, issue.CouponRatePercent)
    Assert.Equal(DateTime(1995, 12, 7), issue.IssueDate)
    Assert.Equal(DateTime(2026, 12, 1), issue.MaturityDate)
    Assert.Equal(5_250_000_000m, issue.OutstandingAmount)
    Assert.Equal(Some 4_905_022_500m, issue.InflationAdjustment)
    Assert.Equal(10_155_022_500m, issue.OutstandingInflationAdjusted)

[<Fact>]
let ``Government bond parser leaves fixed-rate inflation adjustment empty`` () =
    let result = parseGocOutstanding (File.ReadAllText(fixture "goc-outstanding.json"))

    let issue = result.Issues |> Array.find (fun item -> item.Isin = "CA135087S547")

    Assert.Equal(None, issue.InflationAdjustment)

[<Fact>]
let ``PBPP parser maps provincial issuers and transaction fields`` () =
    let result = parsePbppTransactions (File.ReadAllText(fixture "pbpp.json"))

    Assert.Equal(3, result.Transactions.Length)
    Assert.Equal(Some(DateTime(2021, 5, 6)), result.LatestTradeDate)

    let ontario =
        result.Transactions |> Array.find (fun item -> item.Isin = "CA68323AEA84")

    Assert.Equal(Some "ontario", ontario.ProvinceCode)
    Assert.Equal(1.35m, ontario.CouponRatePercent)
    Assert.Equal(DateTime(2022, 3, 8), ontario.MaturityDate)
    Assert.Equal(DateTime(2020, 5, 22), ontario.SettlementDate |> Option.defaultValue DateTime.MinValue)
    Assert.Equal(1_000_000m, ontario.AmountPurchased)
    Assert.Equal(Some 101.25m, ontario.PurchasePrice)
    Assert.Equal(Some 0.648m, ontario.PurchaseYieldPercent)

[<Fact>]
let ``PBPP parser flags unmapped issuers`` () =
    let result = parsePbppTransactions (File.ReadAllText(fixture "pbpp.json"))

    let unknown =
        result.Transactions |> Array.find (fun item -> item.Isin = "CA0000000001")

    Assert.Equal(None, unknown.ProvinceCode)
    Assert.Single(result.Warnings) |> ignore

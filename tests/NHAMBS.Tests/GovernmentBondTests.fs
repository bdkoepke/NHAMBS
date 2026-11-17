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

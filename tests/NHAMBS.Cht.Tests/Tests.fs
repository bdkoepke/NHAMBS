module NHAMBS.Cht.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open NHAMBS.Cht
open NHAMBS.Cli

let private fixture name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

let private generated name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "Generated", name)

let private generatedCircular name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "Circulars", name)

let private html = File.ReadAllText(fixture "cht.html")

let private at (path: string list) (node: JsonNode) =
    path
    |> List.fold
        (fun (current: JsonNode) key ->
            match current with
            | :? JsonArray as array -> array.[Int32.Parse key]
            | _ -> current.[key])
        node

let private stringAt path node = (at path node).GetValue<string>()

let private decimalAt path node = (at path node).GetValue<decimal>()

let private issueWithSeries (series: string) (root: JsonNode) =
    root.["issues"].AsArray()
    |> Seq.find (fun issue -> issue.["series"].GetValue<string>() = series)

[<Fact>]
let ``parseDebtIssues reads the page date and every issue row`` () =
    let root = ChtHtml.parseDebtIssues "" html

    Assert.Equal("2026-10-15", stringAt [ "asOfDate" ] root)

    let issues = root.["issues"].AsArray()
    Assert.Equal(44, issues.Count)

    let first = issues.[0]
    Assert.Equal("Series 77", stringAt [ "series" ] first)
    Assert.Equal("2017-02-23", stringAt [ "settlementDate" ] first)
    Assert.Equal("2027-06-15", stringAt [ "maturityDate" ] first)
    Assert.Equal("CAD", stringAt [ "currency" ] first)
    Assert.Equal("semi-annually", stringAt [ "interestPaymentFrequency" ] first)
    Assert.Equal(12250M, decimalAt [ "issuedToInvestors" ] first)
    Assert.Equal(0M, decimalAt [ "issuedToGovernmentOfCanada" ] first)
    Assert.Equal(12250M, decimalAt [ "totalIssuance" ] first)
    Assert.Equal(12250M, decimalAt [ "outstanding" ] first)
    Assert.Equal("13509PFX6", stringAt [ "identifier" ] first)
    Assert.Equal(4, first.["offeringCirculars"].AsArray().Count)

[<Fact>]
let ``parseDebtIssues normalizes fixed and floating coupons`` () =
    let root = ChtHtml.parseDebtIssues "" html

    let fixedIssue = issueWithSeries "Series 77" root
    Assert.Equal("fixed", stringAt [ "coupon"; "kind" ] fixedIssue)
    Assert.Equal(2.35M, decimalAt [ "coupon"; "fixedRatePercent" ] fixedIssue)

    let negative = issueWithSeries "Series 103" root
    Assert.Equal("floating", stringAt [ "coupon"; "kind" ] negative)
    Assert.Equal("3m CDOR", stringAt [ "coupon"; "index" ] negative)
    Assert.Equal(-0.18M, decimalAt [ "coupon"; "spreadPercent" ] negative)

    let positive = issueWithSeries "Series 134" root
    Assert.Equal("floating", stringAt [ "coupon"; "kind" ] positive)
    Assert.Equal("CORRA", stringAt [ "coupon"; "index" ] positive)
    Assert.Equal(0.275M, decimalAt [ "coupon"; "spreadPercent" ] positive)

[<Fact>]
let ``parseDebtIssues reads Government of Canada issuance and circular links`` () =
    let root = ChtHtml.parseDebtIssues "" html

    let government = issueWithSeries "Series 118" root
    Assert.Equal(15000M, decimalAt [ "issuedToInvestors" ] government)
    Assert.Equal(7000M, decimalAt [ "issuedToGovernmentOfCanada" ] government)
    Assert.Equal(22000M, decimalAt [ "totalIssuance" ] government)

    let circulars = government.["offeringCirculars"].AsArray()
    Assert.Equal(3, circulars.Count)
    Assert.Equal("Initial Issue", circulars.[0].["designation"].GetValue<string>())
    Assert.StartsWith("https://assets.cmhc-schl.gc.ca/", circulars.[0].["url"].GetValue<string>())

[<Fact>]
let ``parseDebtIssues output passes full schema validation`` () =
    let json = ChtHtml.parseDebtIssues "" html |> ChtHtml.toJson

    match DebtIssuesSchema.validate json with
    | Ok _ -> ()
    | Error errors -> failwithf "Expected the parsed CHT debt issues to validate, but got %A" errors

[<Fact>]
let ``DebtIssues.parse returns typed values from generated JSON`` () =
    let json = File.ReadAllText(generated "debt-issues.json")

    match DebtIssues.parseValidated json with
    | Ok issues ->
        Assert.Equal(DateTime(2026, 10, 15), issues.AsOfDate)
        Assert.Equal(44, issues.Issues.Length)
        Assert.Equal("Series 77", issues.Issues.[0].Series)
        Assert.Equal(12250M, issues.Issues.[0].TotalIssuance)
        Assert.Equal(12250M, issues.Issues.[0].Outstanding)
    | Error errors -> failwithf "Expected the generated CHT debt issues to validate, but got %A" errors

[<Fact>]
let ``every generated CHT JSON file validates against the schema`` () =
    let directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Generated")

    let files = Directory.GetFiles(directory, "*.json") |> Array.sort

    Assert.NotEmpty files

    for file in files do
        match DebtIssuesSchema.validate (File.ReadAllText file) with
        | Ok _ -> ()
        | Error errors -> failwithf "Expected %s to validate, but got %A" (Path.GetFileName file) errors

[<Fact>]
let ``DebtIssuesSchema rejects an invalid CUSIP`` () =
    let json =
        File
            .ReadAllText(generated "debt-issues.json")
            .Replace("\"identifier\": \"13509PFX6\"", "\"identifier\": \"123\"")

    match DebtIssuesSchema.validate json with
    | Ok _ -> failwith "Expected the invalid CUSIP to be rejected."
    | Error errors -> Assert.NotEmpty errors

[<Fact>]
let ``DebtIssuesSchema rejects a coupon kind that does not match its fields`` () =
    let json =
        File
            .ReadAllText(generated "debt-issues.json")
            .Replace("\"kind\": \"fixed\",\n        \"fixedRatePercent\": 2.35", "\"kind\": \"floating\"")

    match DebtIssuesSchema.validate json with
    | Ok _ -> failwith "Expected the inconsistent coupon to be rejected."
    | Error errors -> Assert.NotEmpty errors

[<Fact>]
let ``parseText reads a fixed-rate offering circular`` () =
    let text = File.ReadAllText(fixture "series-77-initial-issue.txt")
    let root = OfferingCircularPdf.parseText "" text

    Assert.Equal("Series 77", stringAt [ "series" ] root)
    Assert.Equal("2017-02-15", stringAt [ "documentDate" ] root)
    Assert.Equal("CAD", stringAt [ "currency" ] root)
    Assert.Equal(2500000000M, decimalAt [ "aggregatePrincipalAmount" ] root)
    Assert.Equal("fixed", stringAt [ "coupon"; "kind" ] root)
    Assert.Equal(2.35M, decimalAt [ "coupon"; "fixedRatePercent" ] root)
    Assert.Equal("2027-06-15", stringAt [ "maturityDate" ] root)
    Assert.Equal(99.702M, decimalAt [ "issuePricePercent" ] root)
    Assert.Equal(2.383M, decimalAt [ "yieldPercent" ] root)
    Assert.Equal("2017-02-23", stringAt [ "settlementDate" ] root)
    Assert.Equal("2017-06-15", stringAt [ "firstInterestPaymentDate" ] root)
    Assert.Equal("semi-annually", stringAt [ "interestPaymentFrequency" ] root)
    Assert.Equal(2, root.["interestPaymentDates"].AsArray().Count)
    Assert.Equal("June 15", root.["interestPaymentDates"].[0].GetValue<string>())
    Assert.Equal("December 15", root.["interestPaymentDates"].[1].GetValue<string>())
    Assert.Equal(5000M, decimalAt [ "minimumDenomination" ] root)
    Assert.Equal("CA13509PFX65", stringAt [ "identifiers"; "isin" ] root)
    Assert.Equal("13509PFX6", stringAt [ "identifiers"; "cusip" ] root)

[<Fact>]
let ``parseText reads a floating-rate offering circular`` () =
    let text = File.ReadAllText(fixture "series-103-initial-issue.txt")
    let root = OfferingCircularPdf.parseText "" text

    Assert.Equal("Series 103", stringAt [ "series" ] root)
    Assert.Equal("floating", stringAt [ "coupon"; "kind" ] root)
    Assert.Equal("3 Month CDOR", stringAt [ "coupon"; "index" ] root)
    Assert.Equal(-0.18M, decimalAt [ "coupon"; "spreadPercent" ] root)
    Assert.Equal("quarterly", stringAt [ "interestPaymentFrequency" ] root)
    Assert.Equal(4, root.["interestPaymentDates"].AsArray().Count)
    Assert.Equal("549300M44WH89JG50067", stringAt [ "legalEntityIdentifier" ] root)
    Assert.Null(root.["yieldPercent"])

[<Fact>]
let ``parseText output passes full offering circular schema validation`` () =
    for name in [ "series-77-initial-issue.txt"; "series-103-initial-issue.txt" ] do
        let json =
            File.ReadAllText(fixture name)
            |> OfferingCircularPdf.parseText ""
            |> OfferingCircularPdf.toJson

        match OfferingCircularSchema.validate json with
        | Ok _ -> ()
        | Error errors -> failwithf "Expected %s to validate, but got %A" name errors

[<Fact>]
let ``OfferingCircular.parse returns typed values from generated JSON`` () =
    let json = File.ReadAllText(generatedCircular "series-77-initial-issue.json")

    match OfferingCircular.parseValidated json with
    | Ok circular ->
        Assert.Equal("Series 77", circular.Series)
        Assert.Equal(DateTime(2017, 2, 15), circular.DocumentDate)
        Assert.Equal(2500000000M, circular.AggregatePrincipalAmount)
        Assert.Equal("fixed", circular.Coupon.Kind)
        Assert.Equal(2.35M, circular.Coupon.FixedRatePercent.Value)
    | Error errors -> failwithf "Expected the generated offering circular to validate, but got %A" errors

[<Fact>]
let ``every generated offering circular JSON validates against the schema`` () =
    let directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Circulars")

    let files = Directory.GetFiles(directory, "*.json") |> Array.sort

    Assert.Equal(77, files.Length)

    for file in files do
        match OfferingCircularSchema.validate (File.ReadAllText file) with
        | Ok _ -> ()
        | Error errors -> failwithf "Expected %s to validate, but got %A" (Path.GetFileName file) errors

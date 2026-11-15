module NHAMBS.CoveredBonds.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open NHAMBS.CoveredBonds
open NHAMBS.Cli

let private fixture name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

let private generated name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "Generated", name)

let private registryHtml = File.ReadAllText(fixture "registry.html")

let private programHtml =
    File.ReadAllText(fixture "program-bank-of-nova-scotia.html")

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

let private bondWithSeries (series: string) (root: JsonNode) =
    root.["bonds"].AsArray()
    |> Seq.find (fun bond -> bond.["seriesDesignation"].GetValue<string>() = series)

[<Fact>]
let ``parseRegistry reads every issuer row`` () =
    let root = CoveredBondHtml.parseRegistry "" registryHtml
    let issuers = root.["issuers"].AsArray()

    Assert.Equal(10, issuers.Count)
    Assert.Equal("Bank of Montreal", stringAt [ "issuers"; "0"; "name" ] root)
    Assert.Equal("2014-04-11", stringAt [ "issuers"; "0"; "registrationDate" ] root)
    Assert.Equal("active", stringAt [ "issuers"; "0"; "status" ] root)
    Assert.Equal("Bank of Nova Scotia", stringAt [ "issuers"; "1"; "name" ] root)
    Assert.Equal("2013-07-22", stringAt [ "issuers"; "1"; "registrationDate" ] root)

    Assert.EndsWith(
        "/canadian-covered-bond-program-bank-of-nova-scotia",
        stringAt [ "issuers"; "1"; "programPage" ] root
    )

[<Fact>]
let ``parseRegistry output passes full schema validation`` () =
    let json = CoveredBondHtml.parseRegistry "" registryHtml |> CoveredBondHtml.toJson

    match RegistrySchema.validate json with
    | Ok _ -> ()
    | Error errors -> failwithf "Expected the parsed registry to validate, but got %A" errors

[<Fact>]
let ``parseProgram reads the program profile and every bond table`` () =
    let root = CoveredBondHtml.parseProgram "" programHtml

    Assert.Equal("Bank of Nova Scotia", stringAt [ "issuerName" ] root)
    Assert.Equal("Global Registered Covered Bond Program", stringAt [ "programName" ] root)
    Assert.Equal(39, root.["bonds"].AsArray().Count)
    Assert.Equal("Bank of Nova Scotia", stringAt [ "parties"; "cashManager" ] root)
    Assert.Equal("Computershare Trust Company of Canada", stringAt [ "parties"; "custodian" ] root)
    Assert.Equal("KPMG LLP", stringAt [ "parties"; "coverPoolMonitor" ] root)
    Assert.Equal(80.0M, decimalAt [ "minimumAssetPercentage" ] root)
    Assert.Equal(95.0M, decimalAt [ "maximumAssetPercentage" ] root)

    let first = root.["bonds"].[0]
    Assert.Equal("CBL10", stringAt [ "seriesDesignation" ] first)
    Assert.Equal("2015-09-28", stringAt [ "registrationDate" ] first)
    Assert.Equal("2035-09-28", stringAt [ "maturityDate" ] first)
    Assert.Equal(188000000M, decimalAt [ "nominalAmount" ] first)
    Assert.Equal("fixed", stringAt [ "coupon"; "kind" ] first)
    Assert.Equal(1.637M, decimalAt [ "coupon"; "fixedRatePercent" ] first)

[<Fact>]
let ``parseProgram normalizes floating coupons and multi-designation ISINs`` () =
    let root = CoveredBondHtml.parseProgram "" programHtml

    let floating = bondWithSeries "CBL52" root
    Assert.Equal("floating", stringAt [ "coupon"; "kind" ] floating)
    Assert.Equal("SOFR", stringAt [ "coupon"; "index" ] floating)
    Assert.Equal(0.9M, decimalAt [ "coupon"; "spreadPercent" ] floating)

    let registered = bondWithSeries "CBL41" root
    let entries = registered.["isin"].["entries"].AsArray()
    Assert.Equal(2, entries.Count)
    Assert.Equal("Reg S", entries.[0].["designation"].GetValue<string>())
    Assert.Equal("USC0574BAB48", entries.[0].["value"].GetValue<string>())
    Assert.Equal("Rule 144A", entries.[1].["designation"].GetValue<string>())
    Assert.Equal("US06418BAB45", entries.[1].["value"].GetValue<string>())

[<Fact>]
let ``parseProgram output passes full schema validation`` () =
    let json = CoveredBondHtml.parseProgram "" programHtml |> CoveredBondHtml.toJson

    match ProgramSchema.validate json with
    | Ok _ -> ()
    | Error errors -> failwithf "Expected the parsed program to validate, but got %A" errors

/// The Fairstone page uses a combined bond-trustee/custodian/titleholder row and
/// carries an Originators row that the other pages omit.
let private fairstoneLikeHtml =
    """
    <html>
      <body>
        <main>
          <table>
            <tr><th>Issuer Name</th><td>Fairstone Bank of Canada</td></tr>
            <tr><th>Program Name</th><td>Fairstone Bank of Canada Legislative Covered Bond Programme</td></tr>
            <tr><th>Guarantor Entity’s Name [and the name of its general partner(s) or trustees(s)]</th><td>Example Guarantor LP</td></tr>
            <tr><th>Counterparty(s) to the Covered Bond Collateral Hedge</th><td>Royal Bank of Canada</td></tr>
            <tr><th>Eligible Loan Servicer(s)</th><td>Fairstone Bank of Canada</td></tr>
            <tr><th>Cash Manager</th><td>Fairstone Bank of Canada</td></tr>
            <tr><th>Originators</th><td>Home Trust Company and Home Bank</td></tr>
            <tr><th>Cover Pool Monitor</th><td>Ernst &amp; Young LLP</td></tr>
            <tr><th>Bond Trustee, Custodian and Titleholder</th><td>Computershare Trust Company of Canada</td></tr>
            <tr><th>Paying Agent</th><td>The Bank of New York Mellon</td></tr>
            <tr><th>Portfolio Manager</th><td>None</td></tr>
            <tr><th>Account Bank/Guaranteed Investment Contract Provider</th><td>Bank of Montreal</td></tr>
            <tr><th>Standby Account Bank/Standby Guaranteed Investment Contract Provider</th><td>Royal Bank of Canada</td></tr>
            <tr><th>Minimum Asset Percentage</th><td>80.0</td></tr>
            <tr><th>Maximum Asset Percentage</th><td>95.0</td></tr>
          </table>
          <table>
            <tr><th>Registration Date</th><td>September 8, 2026</td></tr>
            <tr><th>Date of Issue</th><td>September 8, 2026</td></tr>
            <tr><th>Series Designation</th><td>CBL1</td></tr>
            <tr><th>Issue Currency</th><td>EUR</td></tr>
            <tr><th>Nominal Amount</th><td>500,000,000</td></tr>
            <tr><th>ISIN</th><td>XS1234567890</td></tr>
            <tr><th>Hard Bullet/Soft Bullet</th><td>Soft Bullet</td></tr>
            <tr><th>Maturity Date</th><td>September 8, 2031</td></tr>
            <tr><th>Extended Maturity Date (if Soft Bullet)</th><td>September 8, 2032</td></tr>
            <tr><th>Coupon Rate (%)</th><td>SOFR + 75 bps</td></tr>
            <tr><th>Interest Payment Date(s)/Frequency</th><td>Quarterly</td></tr>
            <tr><th>Listing Authority (if applicable)</th><td>NA</td></tr>
            <tr><th>Other Material Characteristics as Applicable</th><td>NA</td></tr>
          </table>
        </main>
      </body>
    </html>
    """

[<Fact>]
let ``parseProgram accepts combined trustee rows and basis-point coupons`` () =
    let root = CoveredBondHtml.parseProgram "" fairstoneLikeHtml

    Assert.Equal(
        "Computershare Trust Company of Canada",
        stringAt [ "parties"; "bondTrusteeCustodianAndTitleholder" ] root
    )

    Assert.Equal("Home Trust Company and Home Bank", stringAt [ "parties"; "originators" ] root)
    Assert.Null(root.["parties"].["custodian"])

    let coupon = root.["bonds"].[0].["coupon"]
    Assert.Equal("floating", stringAt [ "kind" ] coupon)
    Assert.Equal(0.75M, decimalAt [ "spreadPercent" ] coupon)
    Assert.Null(root.["bonds"].[0].["listingAuthority"])

    let json = CoveredBondHtml.toJson root

    match ProgramSchema.validate json with
    | Ok _ -> ()
    | Error errors -> failwithf "Expected the parsed program to validate, but got %A" errors

[<Fact>]
let ``parseProgram accepts a registration date printed without a comma`` () =
    let html = fairstoneLikeHtml.Replace("September 8, 2026", "March 11 2024")
    let root = CoveredBondHtml.parseProgram "" html

    Assert.Equal("2024-03-11", stringAt [ "bonds"; "0"; "registrationDate" ] root)
    Assert.Equal("2024-03-11", stringAt [ "bonds"; "0"; "issueDate" ] root)

[<Fact>]
let ``Registry.parse returns typed values from generated JSON`` () =
    let json = File.ReadAllText(generated "registry.json")

    match Registry.parseValidated json with
    | Ok registry ->
        Assert.Equal(10, registry.Issuers.Length)
        Assert.Equal("Bank of Nova Scotia", registry.Issuers.[1].Name)
        Assert.Equal("active", registry.Issuers.[1].Status)
        Assert.Equal(DateTime(2013, 7, 22), registry.Issuers.[1].RegistrationDate)
    | Error errors -> failwithf "Expected the generated registry to validate, but got %A" errors

[<Fact>]
let ``CoveredBondProgram.parse returns typed values from generated JSON`` () =
    let json = File.ReadAllText(generated "program-bank-of-nova-scotia.json")

    match CoveredBondProgram.parseValidated json with
    | Ok program ->
        Assert.Equal("Bank of Nova Scotia", program.IssuerName)
        Assert.Equal("Global Registered Covered Bond Program", program.ProgramName)
        Assert.Equal(39, program.Bonds.Length)
        Assert.Equal(80.0M, program.MinimumAssetPercentage)
        Assert.Equal(95.0M, program.MaximumAssetPercentage)
    | Error errors -> failwithf "Expected the generated program to validate, but got %A" errors

[<Fact>]
let ``every generated covered bond JSON file validates against its schema`` () =
    let directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Generated")

    let files = Directory.GetFiles(directory, "*.json") |> Array.sort

    Assert.NotEmpty files

    for file in files do
        let json = File.ReadAllText file
        let name = Path.GetFileName file

        let result =
            if name = "registry.json" then
                RegistrySchema.validate json
            else
                ProgramSchema.validate json

        match result with
        | Ok _ -> ()
        | Error errors -> failwithf "Expected %s to validate, but got %A" name errors

[<Fact>]
let ``RegistrySchema rejects an unknown status`` () =
    let json =
        File.ReadAllText(generated "registry.json").Replace("\"status\": \"active\"", "\"status\": \"retired\"")

    match RegistrySchema.validate json with
    | Ok _ -> failwith "Expected the unknown registry status to be rejected."
    | Error errors -> Assert.NotEmpty errors

[<Fact>]
let ``ProgramSchema rejects a bond whose coupon kind does not match its fields`` () =
    let json =
        File
            .ReadAllText(generated "program-bank-of-nova-scotia.json")
            .Replace("\"kind\": \"fixed\",\n        \"fixedRatePercent\": 1.637", "\"kind\": \"floating\"")

    match ProgramSchema.validate json with
    | Ok _ -> failwith "Expected the inconsistent coupon to be rejected."
    | Error errors -> Assert.NotEmpty errors

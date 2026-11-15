module NHAMBS.Cli.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open NHAMBS.Circular
open NHAMBS.Cli
open NHAMBS.Sources
open NHAMBS.Sources.Manifest

let private fixture name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

let private text = File.ReadAllText(fixture "97507987.txt")

let private at (path: string list) (node: JsonNode) =
    path |> List.fold (fun (current: JsonNode) key -> current.[key]) node

let private stringAt path node = (at path node).GetValue<string>()

let private decimalAt path node = (at path node).GetValue<decimal>()

[<Fact>]
let ``parseText reads the cover page fields`` () =
    let root = CircularPdf.parseText text

    Assert.Equal("fixed", stringAt [ "rateType" ] root)
    Assert.Equal("975", stringAt [ "poolType" ] root)
    Assert.Equal("97507987", stringAt [ "mbs"; "poolNumber" ] root)
    Assert.Equal("1st Choice Savings and Credit Union Ltd.", stringAt [ "parties"; "issuerName" ] root)
    Assert.Equal("Canada Mortgage Housing Corporation (CMHC)", stringAt [ "parties"; "mortgageInsurer" ] root)
    Assert.Equal("Computershare Trust Company of Canada", stringAt [ "parties"; "custodian" ] root)
    Assert.Equal(7509942.67M, decimalAt [ "mbs"; "aggregatePrincipalAmount" ] root)
    Assert.Equal("2016-11-01", stringAt [ "mbs"; "issueDate" ] root)
    Assert.Equal("2021-07-15", stringAt [ "mbs"; "finalPaymentDue" ] root)

[<Fact>]
let ``parseText reads the fixed coupon`` () =
    let root = CircularPdf.parseText text

    Assert.Equal("fixed", stringAt [ "mbs"; "coupon"; "kind" ] root)
    Assert.Equal("1.76000%", stringAt [ "mbs"; "coupon"; "displayText" ] root)
    Assert.Equal(1.76M, decimalAt [ "mbs"; "coupon"; "fixedRatePercent" ] root)

[<Fact>]
let ``parseText reads the addendum tables`` () =
    let root = CircularPdf.parseText text
    let addendum = at [ "addendum" ] root

    let insurers = (at [ "insurerProfile"; "insurers" ] addendum).AsArray()

    Assert.Equal(32, (at [ "totalNumberOfMortgageLoans" ] addendum).GetValue<int>())
    Assert.Equal(7509942.67M, decimalAt [ "scheduledMaturity"; "total"; "remainingFaceValue" ] addendum)
    Assert.NotEmpty insurers
    Assert.Equal("Canada Mortgage Housing Corporation (CMHC)", insurers.[0].["name"].GetValue<string>())
    Assert.Equal(100M, decimalAt [ "loanTypeProfile"; "homeownerPercent" ] addendum)
    Assert.Equal(100M, decimalAt [ "locationOfMortgageLoans"; "total"; "loansPercent" ] addendum)
    Assert.Equal(6, (at [ "interestAdjustmentProfile"; "interestAdjustmentDates" ] addendum).AsArray().Count)
    Assert.NotEmpty((at [ "interestAdjustmentProfile"; "rateBands" ] addendum).AsArray())

[<Fact>]
let ``parseText output passes full schema validation`` () =
    let json = CircularPdf.parseText text |> CircularPdf.toJson

    match Schema.validate json with
    | Ok _ -> ()
    | Error errors -> failwithf "Expected the parsed circular to validate, but got %A" errors

[<Fact>]
let ``toJson produces indented JSON that can be parsed again`` () =
    let json = CircularPdf.parseText text |> CircularPdf.toJson

    Assert.Contains(Environment.NewLine, json)
    Assert.NotNull(JsonNode.Parse json)

[<Fact>]
let ``parseText fails with a useful error when required cover text is missing`` () =
    let error =
        Assert.Throws<Exception>(fun () -> CircularPdf.parseText "not a circular" |> ignore)

    Assert.Contains("pool number", error.Message, StringComparison.OrdinalIgnoreCase)

let private withTempText (contents: string) (action: string -> unit) =
    let path =
        Path.Combine(Path.GetTempPath(), "nhambs-test-" + Guid.NewGuid().ToString("N") + ".txt")

    try
        File.WriteAllText(path, contents)
        action path
    finally
        File.Delete path

[<Fact>]
let ``Manifest.read parses a pool_urls manifest`` () =
    withTempText
        "PoolName,CUSIP,Url\n97500001,62945ZR69,/pools/97500001.pdf\n\"975,00002\",62945ZR77,https://example.test/p.pdf\n"
        (fun path ->
            match Manifest.read path with
            | Ok entries ->
                Assert.Equal(2, entries.Length)
                Assert.Equal("97500001", entries.[0].Name)
                Assert.Equal(Some "62945ZR69", entries.[0].FileName)
                Assert.Equal("975,00002", entries.[1].Name)
                Assert.Equal(Some "62945ZR77", entries.[1].FileName)
            | Error errors -> failwithf "Expected a manifest, got %A" errors)

[<Fact>]
let ``Manifest.read accepts a plain list of URLs`` () =
    withTempText "https://example.test/a.pdf\n# comment\nhttps://example.test/b.pdf\n" (fun path ->
        match Manifest.read path with
        | Ok entries ->
            Assert.Equal(2, entries.Length)
            Assert.Equal("a", entries.[0].Name)
            Assert.Equal("https://example.test/b.pdf", entries.[1].Url)
        | Error errors -> failwithf "Expected a manifest, got %A" errors)

[<Fact>]
let ``Manifest.fileName prefers explicit names and appends extensions`` () =
    let explicitName =
        {
            Name = "97500001"
            Url = "https://example.test/pools/97500001.pdf"
            FileName = Some "62945ZR69"
        }

    Assert.Equal("62945ZR69.pdf", Manifest.fileName ".pdf" explicitName)

    let explicitExtension =
        {
            Name = "Series 77"
            Url = "https://example.test/series-77.pdf"
            FileName = Some "series-77-initial-issue.pdf"
        }

    Assert.Equal("series-77-initial-issue.pdf", Manifest.fileName ".pdf" explicitExtension)

    let derived =
        {
            Name = ""
            Url = "https://example.test/series-77.pdf"
            FileName = None
        }

    Assert.Equal("series-77.pdf", Manifest.fileName ".pdf" derived)

[<Fact>]
let ``Manifest CSV writing round-trips through read`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "nhambs-test-" + Guid.NewGuid().ToString("N") + ".csv")

    try
        Manifest.writeCsv
            path
            [ "PoolName"; "CUSIP"; "Url" ]
            [ [ "97500001"; "62945ZR69"; "https://example.test/a.pdf" ] ]

        match Manifest.read path with
        | Ok entries ->
            Assert.Equal(1, entries.Length)
            Assert.Equal("97500001", entries.[0].Name)
            Assert.Equal(Some "62945ZR69", entries.[0].FileName)
            Assert.Equal("https://example.test/a.pdf", entries.[0].Url)
        | Error errors -> failwithf "Expected a manifest, got %A" errors
    finally
        File.Delete path

let private withTempDirectory (action: string -> unit) =
    let path =
        Path.Combine(Path.GetTempPath(), "nhambs-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory path |> ignore

    try
        action path
    finally
        Directory.Delete(path, true)

let private sampleReport (entries: BatchReport.Entry list) : BatchReport.Report =
    {
        BatchReport.Command = "circular-batch"
        StartedAt = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
        UpdatedAt = DateTimeOffset(2026, 1, 2, 3, 5, 6, TimeSpan.Zero)
        Total = entries.Length
        Completed = entries.Length
        Entries = entries
    }

[<Fact>]
let ``BatchReport round-trips entries and metadata`` () =
    withTempDirectory (fun directory ->
        let path = Path.Combine(directory, "batch-report.json")

        let report =
            sampleReport
                [
                    BatchReport.entry "a.pdf" BatchReport.Status.Parsed None [ "poolNumber", "97500001" ]

                    BatchReport.entry "b.pdf" BatchReport.Status.Failed (Some "boom") []
                ]

        BatchReport.write path report

        match BatchReport.read path with
        | Some actual ->
            Assert.Equal(report.Command, actual.Command)
            Assert.Equal(report.StartedAt, actual.StartedAt)
            Assert.Equal(report.UpdatedAt, actual.UpdatedAt)
            Assert.Equal(report.Total, actual.Total)
            Assert.Equal(report.Completed, actual.Completed)
            Assert.Equal(2, actual.Entries.Length)
            Assert.Equal(BatchReport.Status.Parsed, actual.Entries.[0].Status)

            Assert.Equal<(string * string) list>([ "poolNumber", "97500001" ], actual.Entries.[0].Metadata)

            Assert.Equal(BatchReport.Status.Failed, actual.Entries.[1].Status)
            Assert.Equal(Some "boom", actual.Entries.[1].Detail)
        | None -> failwith "Expected a batch report")

[<Fact>]
let ``BatchReport only treats parsed and skipped entries as complete`` () =
    let report =
        sampleReport
            [
                BatchReport.entry "parsed.pdf" BatchReport.Status.Parsed None []
                BatchReport.entry "skipped.pdf" BatchReport.Status.Skipped (Some "empty") []
                BatchReport.entry "pending.pdf" BatchReport.Status.Pending (Some "downloading") []
                BatchReport.entry "failed.pdf" BatchReport.Status.Failed (Some "boom") []
            ]

    let completed = BatchReport.completedFiles (Some report)

    Assert.Contains("parsed.pdf", completed)
    Assert.Contains("skipped.pdf", completed)
    Assert.DoesNotContain("pending.pdf", completed)
    Assert.DoesNotContain("failed.pdf", completed)

[<Fact>]
let ``BatchReport session resumes and replaces retried entries`` () =
    withTempDirectory (fun directory ->
        let path = Path.Combine(directory, "batch-report.json")

        let previous =
            sampleReport
                [
                    BatchReport.entry "parsed.pdf" BatchReport.Status.Parsed None []
                    BatchReport.entry "failed.pdf" BatchReport.Status.Failed (Some "boom") []
                ]

        let session = BatchReport.Session(path, "circular-batch", 1, Some previous)

        Assert.True(session.IsComplete "parsed.pdf")
        Assert.False(session.IsComplete "failed.pdf")

        session.Record(BatchReport.entry "failed.pdf" BatchReport.Status.Parsed None [])
        session.Finish()

        match BatchReport.read path with
        | Some report ->
            Assert.Equal(2, report.Entries.Length)
            Assert.Equal(1, report.Completed)

            let retried = report.Entries |> List.find (fun entry -> entry.File = "failed.pdf")

            Assert.Equal(BatchReport.Status.Parsed, retried.Status)
        | None -> failwith "Expected a batch report")

[<Fact>]
let ``BatchReport imports a legacy CSV report when JSON is missing`` () =
    withTempDirectory (fun directory ->
        let jsonPath = Path.Combine(directory, "batch-report.json")
        let csvPath = Path.Combine(directory, "batch-report.csv")

        File.WriteAllText(
            csvPath,
            "File,Status,PoolNumber,Detail\n"
            + "\"a.pdf\",parsed,\"97500001\",\n"
            + "\"b.pdf\",skipped,,\"recently modified\"\n"
            + "\"c.pdf\",failed,,\"boom\"\n"
        )

        match BatchReport.readOrImport jsonPath csvPath "circular-batch" with
        | Some report ->
            Assert.Equal(3, report.Entries.Length)

            let find name =
                report.Entries |> List.find (fun entry -> entry.File = name)

            Assert.Equal(BatchReport.Status.Parsed, (find "a.pdf").Status)

            Assert.Equal<(string * string) list>([ "poolNumber", "97500001" ], (find "a.pdf").Metadata)

            Assert.Equal(BatchReport.Status.Pending, (find "b.pdf").Status)
            Assert.Equal(BatchReport.Status.Failed, (find "c.pdf").Status)
            Assert.Equal(Some "boom", (find "c.pdf").Detail)
        | None -> failwith "Expected an imported batch report")

[<Fact>]
let ``BatchReport ignores malformed and missing reports`` () =
    withTempDirectory (fun directory ->
        let path = Path.Combine(directory, "batch-report.json")

        Assert.True((BatchReport.read path).IsNone)

        File.WriteAllText(path, "not json")

        Assert.True((BatchReport.read path).IsNone))

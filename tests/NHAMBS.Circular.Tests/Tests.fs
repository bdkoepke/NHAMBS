module NHAMBS.Circular.Tests

open System
open System.IO
open Xunit
open NHAMBS.Circular

let private fixture name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

let private validJson = File.ReadAllText(fixture "valid-circular.json")

let private withTempDirectory (action: string -> unit) =
    let directory =
        Path.Combine(Path.GetTempPath(), "nhambs-circular-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore

    try
        action directory
    finally
        try
            Directory.Delete(directory, true)
        with _ ->
            ()

[<Fact>]
let ``Schema.validate accepts a valid circular`` () =
    match Schema.validate validJson with
    | Ok _ -> ()
    | Error errors -> failwithf "Expected a valid circular, but got %A" errors

[<Fact>]
let ``Schema.validate rejects a pool type that does not match the rate type`` () =
    let json = validJson.Replace("\"poolType\": \"975\"", "\"poolType\": \"985\"")

    match Schema.validate json with
    | Ok _ -> failwith "Expected pool type 985 to be rejected for a fixed-rate circular."
    | Error errors ->
        Assert.NotEmpty errors
        Assert.Contains(errors, fun error -> error.Path.Contains("poolType", StringComparison.Ordinal))

[<Fact>]
let ``Schema.validate rejects an unexpected property`` () =
    let json =
        validJson.Replace("\"language\": \"en\",", "\"language\": \"en\",\n  \"unexpected\": true,")

    match Schema.validate json with
    | Ok _ -> failwith "Expected the additional property to be rejected."
    | Error errors -> Assert.NotEmpty errors

[<Fact>]
let ``Schema.validate reports malformed JSON at the document root`` () =
    match Schema.validate "{" with
    | Ok _ -> failwith "Expected malformed JSON to be rejected."
    | Error errors -> Assert.Contains(errors, fun error -> error.Path = "$")

[<Fact>]
let ``Circular.parse returns typed values from the fixture`` () =
    let circular = Circular.parse validJson

    Assert.Equal("fixed", circular.RateType)
    Assert.Equal("975", circular.PoolType)
    Assert.Equal("97500001", circular.Mbs.PoolNumber)
    Assert.Equal("Example Trust", circular.Parties.IssuerName)
    Assert.Equal("CMHC", circular.Parties.MortgageInsurer)
    Assert.Equal(1000000M, circular.Mbs.AggregatePrincipalAmount)
    Assert.Equal(10, circular.Addendum.TotalNumberOfMortgageLoans)
    Assert.Equal(1000000M, circular.Addendum.ScheduledMaturity.Total.RemainingFaceValue)

[<Fact>]
let ``Circular.parseValidated rejects an invalid circular`` () =
    let json =
        validJson.Replace("\"poolNumber\": \"97500001\"", "\"poolNumber\": \"123\"")

    match Circular.parseValidated json with
    | Ok _ -> failwith "Expected the invalid pool number to be rejected."
    | Error errors -> Assert.NotEmpty errors

[<Fact>]
let ``Circular.load reads and validates a file`` () =
    withTempDirectory (fun directory ->
        let path = Path.Combine(directory, "circular.json")
        File.WriteAllText(path, validJson)

        match Circular.load path with
        | Ok circular -> Assert.Equal("97500001", circular.Mbs.PoolNumber)
        | Error errors -> failwithf "Expected the fixture to load, but got %A" errors)

[<Fact>]
let ``Circular.loadMany keeps the source path for each result`` () =
    withTempDirectory (fun directory ->
        let validPath = Path.Combine(directory, "valid.json")
        let invalidPath = Path.Combine(directory, "invalid.json")
        File.WriteAllText(validPath, validJson)
        File.WriteAllText(invalidPath, "{}")

        let results = Circular.loadMany [ validPath; invalidPath ]

        Assert.Equal(2, results.Length)

        match results.[0] with
        | path, Ok _ -> Assert.Equal(validPath, path)
        | path, Error errors -> failwithf "Expected %s to load, but got %A" path errors

        match results.[1] with
        | path, Ok _ -> failwithf "Expected %s to fail validation." path
        | path, Error errors ->
            Assert.Equal(invalidPath, path)
            Assert.NotEmpty errors)

[<Fact>]
let ``Circular.loadDirectory reads every JSON file in a directory`` () =
    withTempDirectory (fun directory ->
        File.WriteAllText(Path.Combine(directory, "first.json"), validJson)
        File.WriteAllText(Path.Combine(directory, "second.json"), validJson)
        File.WriteAllText(Path.Combine(directory, "ignored.txt"), validJson)

        let results = Circular.loadDirectory directory

        Assert.Equal(2, results.Length)
        Assert.All(results, fun (_, result) -> Assert.True(Result.isOk result)))

[<Fact>]
let ``Circular.summary projects the indexing fields`` () =
    let summary = Circular.parse validJson |> Circular.summary

    Assert.Equal("fixed", summary.RateType)
    Assert.Equal("975", summary.PoolType)
    Assert.Equal("97500001", summary.PoolNumber)
    Assert.Equal("Example Trust", summary.Issuer)
    Assert.Equal(1000000M, summary.AggregatePrincipalAmount)

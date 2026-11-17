module NHAMBS.ArrearsTests

open System
open System.IO
open Xunit
open NHAMBS.Etl.ArrearsParser

let private fixture name =
    Path.Combine(AppContext.BaseDirectory, "fixtures", name)

let private hasObservation
    (result: NHAMBS.Etl.ArrearsTypes.ArrearsParseResult)
    (source: string)
    (geography: string)
    (period: DateTime)
    (metric: string)
    (value: decimal)
    =
    result.Observations
    |> Array.exists (fun observation ->
        observation.SourceCode = source
        && observation.GeographyCode = geography
        && observation.PeriodEnd.Date = period
        && observation.Metric = metric
        && abs (observation.Value - value) < 0.000001m)

[<Fact>]
let ``CBA parser reads both date blocks and normalises month ends`` () =
    let result = parseCba (fixture "cba-arrears.xlsx")

    Assert.Equal(3, result.Geographies.Length)

    Assert.True(hasObservation result "cba" "canada" (DateTime(2010, 12, 31)) "arrears_rate_percent" 0.433885m)

    Assert.True(hasObservation result "cba" "canada" (DateTime(2011, 1, 31)) "arrears_rate_percent" 0.4461028m)

    Assert.True(hasObservation result "cba" "canada" (DateTime(2010, 12, 31)) "arrears_count" 18177m)
    Assert.True(hasObservation result "cba" "canada" (DateTime(2010, 12, 31)) "mortgage_count" 4189355m)
    Assert.True(result.Suppressed > 0)
    Assert.Equal(Some(DateTime(2011, 2, 28)), result.LatestPeriodEnd)

[<Fact>]
let ``CBA parser maps regional aggregates to stable geography keys`` () =
    let result = parseCba (fixture "cba-arrears.xlsx")

    let codes =
        result.Geographies |> Array.map (fun geography -> geography.Code) |> Set.ofArray

    let expected: Set<string> = Set.ofList [ "canada"; "ontario"; "territories" ]
    Assert.Equal<Set<string>>(expected, codes)

[<Fact>]
let ``CMHC parser reads national, provincial and CMA rows`` () =
    let result = parseCmhc (fixture "cmhc-delinquency.xlsx")

    Assert.True(hasObservation result "cmhc" "canada" (DateTime(2012, 9, 30)) "arrears_rate_percent" 0.38m)

    Assert.True(hasObservation result "cmhc" "ontario" (DateTime(2012, 12, 31)) "arrears_rate_percent" 0.30m)

    Assert.True(hasObservation result "cmhc" "cma_toronto" (DateTime(2012, 9, 30)) "arrears_rate_percent" 0.24m)

    Assert.True(hasObservation result "cmhc" "cma_montreal" (DateTime(2012, 12, 31)) "arrears_rate_percent" 0.32m)

    Assert.Equal(Some(DateTime(2012, 12, 31)), result.LatestPeriodEnd)

[<Fact>]
let ``CMHC parser records geography levels`` () =
    let result = parseCmhc (fixture "cmhc-delinquency.xlsx")

    let level code =
        result.Geographies
        |> Array.find (fun geography -> geography.Code = code)
        |> fun geography -> geography.Level

    Assert.Equal("country", level "canada")
    Assert.Equal("province", level "quebec")
    Assert.Equal("cma", level "cma_toronto")

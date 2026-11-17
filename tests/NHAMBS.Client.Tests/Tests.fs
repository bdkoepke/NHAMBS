module NHAMBS.ClientTests

open Xunit
open NHAMBS.Shared.Domain
open NHAMBS.Client.Components
open NHAMBS.Client.Arrears

let private pagesIn section =
    navigationSections
    |> Array.find (fun (candidate, _) -> candidate = section)
    |> snd
    |> Array.map fst

let private geography code name level sortOrder sources =
    {
        Code = code
        Name = name
        Level = level
        ProvinceCode = None
        SortOrder = sortOrder
        Sources = sources
    }

let private cba = geography "ontario" "Ontario" "province" 15 [| "cba"; "cmhc" |]

let private cmhcOnly =
    geography "newBrunswick" "New Brunswick" "province" 12 [| "cmhc" |]

let private cbaOnly = geography "atlantic" "Atlantic" "region" 50 [| "cba" |]
let private cma = geography "cma_toronto" "Toronto" "cma" 114 [| "cmhc" |]
let private noData = geography "yukon" "Yukon" "province" 21 [||]

[<Fact>]
let ``market data and securities pages are classified correctly`` () =
    Assert.Equal(MarketData, pageSection Overview)
    Assert.Equal(MarketData, pageSection ForwardCurves)
    Assert.Equal(MarketData, pageSection Arrears)
    Assert.Equal(Securities, pageSection Pools)
    Assert.Equal(Securities, pageSection Cht)
    Assert.Equal(Securities, pageSection CoveredBonds)
    Assert.Equal(Securities, pageSection GovernmentBonds)

[<Fact>]
let ``navigation sections keep the expected order for desktop and mobile`` () =
    Assert.Equal<PageSection[]>([| MarketData; Securities |], navigationSections |> Array.map fst)

    Assert.Equal<Page[]>([| Overview; ForwardCurves; Arrears |], pagesIn MarketData)

    Assert.Equal<Page[]>([| Pools; Cht; CoveredBonds; GovernmentBonds |], pagesIn Securities)

[<Fact>]
let ``navigation section titles use market data and securities labels`` () =
    Assert.Equal("Market data", pageSectionTitle MarketData)
    Assert.Equal("Securities", pageSectionTitle Securities)

[<Fact>]
let ``geography grouping preserves every loaded geography and orders levels`` () =
    let groups = groupedGeographies [| cma; noData; cbaOnly; cmhcOnly; cba |]

    Assert.Equal<string[]>([| "province"; "region"; "cma" |], groups |> Array.map fst)

    let groupedCodes =
        groups
        |> Array.collect (fun (_, items) -> items |> Array.map (fun item -> item.Code))
        |> Set.ofArray

    let expected =
        [| cma; noData; cbaOnly; cmhcOnly; cba |]
        |> Array.map (fun item -> item.Code)
        |> Set.ofArray

    Assert.Equal<Set<string>>(expected, groupedCodes)

[<Fact>]
let ``coverage notes explain missing providers`` () =
    Assert.Contains("does not publish a separate New Brunswick", coverageNote cmhcOnly)
    Assert.Contains("CMHC does not publish the Atlantic aggregate", coverageNote cbaOnly)
    Assert.Contains("CBA does not publish CMA-level", coverageNote cma)
    Assert.Contains("both shown", coverageNote cba)
    Assert.Contains("No provider observations", coverageNote noData)

[<Fact>]
let ``coverage value labels provider availability`` () =
    Assert.Equal("CBA + CMHC", coverageValue cba)
    Assert.Equal("CMHC only", coverageValue cmhcOnly)
    Assert.Equal("CBA only", coverageValue cbaOnly)
    Assert.Equal("No data", coverageValue noData)

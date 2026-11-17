module NHAMBS.Client.Arrears

open System
open Browser.Types
open Fable.Core.JsInterop
open Feliz
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting
open NHAMBS.Client.Charts

type ArrearsProps =
    {
        Data: Loadable<ArrearsData>
        SelectedGeography: string
        OnGeographyChange: string -> unit
        OnRefresh: unit -> unit
    }

let private eventValue (event: Event) = string event.target?value

let private geographyLevels = [| "country"; "province"; "region"; "cma" |]

let private geographyLevelLabel =
    function
    | "country" -> "Country"
    | "province" -> "Province or territory"
    | "region" -> "Region"
    | "cma" -> "Census metropolitan area"
    | level -> titleCase level

let groupedGeographies (geographies: ArrearsGeographyInfo[]) =
    let known =
        geographyLevels
        |> Array.choose (fun level ->
            let items =
                geographies
                |> Array.filter (fun geography -> geography.Level = level)
                |> Array.sortBy (fun geography -> geography.SortOrder, geography.Name)

            if items.Length = 0 then None else Some(level, items))

    let unknown =
        geographies
        |> Array.filter (fun geography -> not (Array.contains geography.Level geographyLevels))
        |> Array.sortBy (fun geography -> geography.SortOrder, geography.Name)

    if unknown.Length = 0 then known
    elif known.Length = 0 then [| "", unknown |]
    else Array.append known [| "", unknown |]

let private sourceLabel (sourceCode: string) =
    if sourceCode = "cba" then "CBA"
    elif sourceCode = "cmhc" then "CMHC"
    else sourceCode

let private hasSource (geography: ArrearsGeographyInfo) sourceCode =
    geography.Sources |> Array.contains sourceCode

let coverageValue (geography: ArrearsGeographyInfo) =
    match geography.Sources with
    | [||] -> "No data"
    | [| "cba" |] -> "CBA only"
    | [| "cmhc" |] -> "CMHC only"
    | [| "cba"; "cmhc" |] -> "CBA + CMHC"
    | sources -> sources |> Array.map sourceLabel |> String.concat " + "

let coverageNote (geography: ArrearsGeographyInfo) =
    let cba = hasSource geography "cba"
    let cmhc = hasSource geography "cmhc"

    match geography.Level, cba, cmhc with
    | "cma", _, true ->
        "CMHC publishes selected CMAs; the CBA does not publish CMA-level arrears, so this chart uses the CMHC/Equifax series only."
    | "region", true, false ->
        sprintf
            "CMHC does not publish the %s aggregate; this chart uses the CBA chartered-bank series only."
            geography.Name
    | "province", false, true ->
        sprintf
            "The CBA does not publish a separate %s series; this chart uses the CMHC/Equifax all-lender series only."
            geography.Name
    | _, true, true ->
        "CBA chartered-bank and CMHC/Equifax all-lender series are both shown. Their lender populations and publication cadences differ."
    | _, false, true -> "CMHC/Equifax publishes this geography; no CBA series is available."
    | _, true, false -> "The CBA publishes this geography; no CMHC series is available."
    | _ -> "No provider observations are loaded for this geography."

let private latestMetric (data: ArrearsData) (sourceCode: string) (metric: string) (geographyCode: string) =
    data.Latest
    |> Array.tryFind (fun row ->
        row.SourceCode = sourceCode
        && row.Metric = metric
        && row.GeographyCode = geographyCode)

let private sourceInfo (data: ArrearsData) (sourceCode: string) =
    data.Sources |> Array.tryFind (fun source -> source.SourceCode = sourceCode)

let private staleBadge (isStale: bool) =
    Html.span
        [
            prop.className (
                if isStale then
                    "badge badge-warning"
                else
                    "badge badge-success"
            )
            prop.text (if isStale then "Stale" else "Current")
        ]

let private sourceCard (source: ArrearsSourceInfo) =
    Card
        source.Provider
        source.DatasetName
        []
        [
            Html.div
                [
                    prop.className "arrears-source"
                    prop.children
                        [
                            Html.div
                                [
                                    prop.className "arrears-source-meta"
                                    prop.children
                                        [
                                            Html.span [ prop.className "arrears-source-label"; prop.text "Cadence" ]
                                            Html.span
                                                [
                                                    prop.className "arrears-source-value"
                                                    prop.text (titleCase source.Cadence)
                                                ]
                                            Html.span
                                                [ prop.className "arrears-source-label"; prop.text "Latest period" ]
                                            Html.span
                                                [
                                                    prop.className "arrears-source-value"
                                                    prop.text (optionText source.LatestPeriodEnd)
                                                ]
                                            staleBadge source.IsStale
                                        ]
                                ]
                            Html.p [ prop.className "arrears-definition"; prop.text source.Definition ]
                            Html.p [ prop.className "arrears-attribution"; prop.text source.Attribution ]
                        ]
                ]
        ]

let private latestPeriod (row: ArrearsObservationRow option) =
    row
    |> Option.map (fun value -> sprintf "As of %s" (dateShort value.PeriodEnd))
    |> Option.defaultValue "No observation"

let private rateTable (selectedGeography: string) (onGeographyChange: string -> unit) (rows: ArrearsObservationRow[]) =
    if rows.Length = 0 then
        EmptyState "No arrears observations" "Load the CBA and CMHC data tables to populate this view."
    else
        Html.div
            [
                prop.className "table-scroll"
                prop.children
                    [
                        Html.table
                            [
                                prop.className "data-table"
                                prop.children
                                    [
                                        Html.thead
                                            [
                                                prop.children
                                                    [
                                                        Html.tr
                                                            [
                                                                prop.children
                                                                    [
                                                                        Html.th [ prop.text "Source" ]
                                                                        Html.th [ prop.text "Geography" ]
                                                                        Html.th [ prop.text "Level" ]
                                                                        Html.th [ prop.text "Period" ]
                                                                        Html.th [ prop.text "90+ day rate" ]
                                                                        Html.th [ prop.text "" ]
                                                                    ]
                                                            ]
                                                    ]
                                            ]
                                        Html.tbody
                                            [
                                                prop.children
                                                    [
                                                        for row in rows do
                                                            let isSelected = row.GeographyCode = selectedGeography

                                                            Html.tr
                                                                [
                                                                    prop.className (
                                                                        if isSelected then "selected" else ""
                                                                    )
                                                                    prop.children
                                                                        [
                                                                            Html.td
                                                                                [
                                                                                    prop.text (
                                                                                        if row.SourceCode = "cba" then
                                                                                            "CBA"
                                                                                        else
                                                                                            "CMHC"
                                                                                    )
                                                                                ]
                                                                            Html.td [ prop.text row.GeographyName ]
                                                                            Html.td
                                                                                [
                                                                                    prop.text (
                                                                                        geographyLevelLabel
                                                                                            row.GeographyLevel
                                                                                    )
                                                                                ]
                                                                            Html.td [ prop.text row.PeriodEnd ]
                                                                            Html.td [ prop.text (percent row.Value) ]
                                                                            Html.td
                                                                                [
                                                                                    Html.button
                                                                                        [
                                                                                            prop.className
                                                                                                "btn btn-ghost btn-small"
                                                                                            prop.disabled isSelected
                                                                                            prop.custom (
                                                                                                "aria-label",
                                                                                                sprintf
                                                                                                    "View %s arrears"
                                                                                                    row.GeographyName
                                                                                            )
                                                                                            prop.onClick (fun _ ->
                                                                                                onGeographyChange
                                                                                                    row.GeographyCode)
                                                                                            prop.text (
                                                                                                if isSelected then
                                                                                                    "Selected"
                                                                                                else
                                                                                                    "View"
                                                                                            )
                                                                                        ]
                                                                                ]
                                                                        ]
                                                                ]
                                                    ]
                                            ]
                                    ]
                            ]
                    ]
            ]

let private geographySelector
    (geographies: ArrearsGeographyInfo[])
    (selectedGeography: string)
    (onGeographyChange: string -> unit)
    =
    Html.div
        [
            prop.className "arrears-selector"
            prop.children
                [
                    Html.label
                        [
                            prop.className "arrears-selector-label"
                            prop.htmlFor "arrears-geography"
                            prop.text "Geography"
                        ]
                    Html.select
                        [
                            prop.id "arrears-geography"
                            prop.className "field-input arrears-selector-input"
                            prop.value selectedGeography
                            prop.onChange (fun (event: Event) -> onGeographyChange (eventValue event))
                            prop.children
                                [
                                    for level, items in groupedGeographies geographies do
                                        Html.optgroup
                                            [
                                                if String.IsNullOrEmpty level then
                                                    prop.custom ("label", "Other geographies")
                                                else
                                                    prop.custom ("label", geographyLevelLabel level)
                                                prop.children
                                                    [
                                                        for geography in items do
                                                            Html.option
                                                                [ prop.value geography.Code; prop.text geography.Name ]
                                                    ]
                                            ]
                                ]
                        ]
                ]
        ]

let private content (props: ArrearsProps) (data: ArrearsData) =
    let selected =
        data.Geographies
        |> Array.tryFind (fun geography -> geography.Code = props.SelectedGeography)
        |> Option.orElse (data.Geographies |> Array.tryFind (fun geography -> geography.Code = "canada"))
        |> Option.defaultValue
            {
                Code = "canada"
                Name = "Canada"
                Level = "country"
                ProvinceCode = None
                SortOrder = 0
                Sources = [||]
            }

    let selectedSeries =
        data.Series |> Array.filter (fun point -> point.GeographyCode = selected.Code)

    let cbaRate = latestMetric data "cba" "arrears_rate_percent" selected.Code
    let cmhcRate = latestMetric data "cmhc" "arrears_rate_percent" selected.Code
    let cbaCount = latestMetric data "cba" "arrears_count" selected.Code

    let cbaInfo = sourceInfo data "cba"
    let cmhcInfo = sourceInfo data "cmhc"

    let rates =
        data.Latest
        |> Array.filter (fun row -> row.Metric = "arrears_rate_percent")
        |> Array.sortBy (fun row ->
            let levelOrder =
                match row.GeographyLevel with
                | "country" -> 0
                | "province" -> 1
                | "region" -> 2
                | "cma" -> 3
                | _ -> 4

            levelOrder, row.GeographyName, row.SourceCode)

    Html.div
        [
            prop.className "page"
            prop.children
                [
                    PageHeader
                        "HOUSING CREDIT"
                        "Mortgage arrears"
                        "90+ day arrears rates from the Canadian Bankers Association and CMHC, with source definitions and freshness."
                        [
                            geographySelector data.Geographies selected.Code props.OnGeographyChange
                            Html.button
                                [
                                    prop.className "btn btn-ghost"
                                    prop.onClick (fun _ -> props.OnRefresh())
                                    prop.text "↻ Refresh"
                                ]
                        ]
                    Html.div
                        [
                            prop.className "kpi-grid kpi-grid-5"
                            prop.children
                                [
                                    KpiCard
                                        "CBA arrears rate"
                                        (cbaRate
                                         |> Option.map (fun row -> row.Value)
                                         |> Option.map percent
                                         |> Option.defaultValue "—")
                                        (sprintf "%s · %s" selected.Name (latestPeriod cbaRate))
                                        "#2e5771"
                                    KpiCard
                                        "CMHC arrears rate"
                                        (cmhcRate
                                         |> Option.map (fun row -> row.Value)
                                         |> Option.map percent
                                         |> Option.defaultValue "—")
                                        (sprintf "%s · %s" selected.Name (latestPeriod cmhcRate))
                                        "#f47c3c"
                                    KpiCard
                                        "CBA mortgages in arrears"
                                        (cbaCount
                                         |> Option.map (fun row -> row.Value)
                                         |> Option.map number
                                         |> Option.defaultValue "—")
                                        (sprintf "%s · Chartered-bank mortgages" selected.Name)
                                        "#93c54b"
                                    KpiCard
                                        "Selected geography"
                                        selected.Name
                                        (geographyLevelLabel selected.Level)
                                        "#29abe0"
                                    KpiCard
                                        "Provider coverage"
                                        (coverageValue selected)
                                        "CBA chartered banks · CMHC/Equifax all lenders"
                                        "#8e8c84"
                                ]
                        ]

                    Card
                        (sprintf "%s 90+ day arrears rate" selected.Name)
                        (coverageNote selected)
                        []
                        [ arrearsComparisonChart selected.Name selectedSeries ]

                    Card
                        "Latest rates by geography"
                        "Every loaded geography is listed. Select View to update the KPIs and chart for that country, province, territory, region or CMA."
                        []
                        [ rateTable selected.Code props.OnGeographyChange rates ]

                    Html.div
                        [
                            prop.className "overview-grid-2"
                            prop.children
                                [
                                    match cbaInfo with
                                    | Some source -> sourceCard source
                                    | None -> Html.div []
                                    match cmhcInfo with
                                    | Some source -> sourceCard source
                                    | None -> Html.div []
                                ]
                        ]
                ]
        ]

let ArrearsPage (props: ArrearsProps) =
    LoadableBlock "Loading mortgage arrears" props.Data (content props)

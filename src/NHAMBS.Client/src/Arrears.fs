module NHAMBS.Client.Arrears

open System
open Browser.Types
open Feliz
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting
open NHAMBS.Client.Charts

type ArrearsProps =
    {
        Data: Loadable<ArrearsData>
        SelectedGeographies: string list
        OnGeographiesChange: string list -> unit
        OnRefresh: unit -> unit
    }

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

let private coverageCounts (selected: ArrearsGeographyInfo[]) =
    let both =
        selected
        |> Array.filter (fun geography -> hasSource geography "cba" && hasSource geography "cmhc")
        |> Array.length

    let cbaOnly =
        selected
        |> Array.filter (fun geography -> hasSource geography "cba" && not (hasSource geography "cmhc"))
        |> Array.length

    let cmhcOnly =
        selected
        |> Array.filter (fun geography -> hasSource geography "cmhc" && not (hasSource geography "cba"))
        |> Array.length

    let noData =
        selected
        |> Array.filter (fun geography -> geography.Sources.Length = 0)
        |> Array.length

    both, cbaOnly, cmhcOnly, noData

let private coverageSummary (selected: ArrearsGeographyInfo[]) =
    match selected with
    | [||] -> "None"
    | [| geography |] -> coverageValue geography
    | _ ->
        let both, _, _, _ = coverageCounts selected
        sprintf "%d / %d" both selected.Length

let private coverageSummarySub (selected: ArrearsGeographyInfo[]) =
    match selected with
    | [||] -> "No geographies selected"
    | [| _ |] -> "CBA chartered banks · CMHC/Equifax all lenders"
    | _ ->
        let both, cbaOnly, cmhcOnly, noData = coverageCounts selected

        sprintf "%d both · %d CBA-only · %d CMHC-only · %d no data" both cbaOnly cmhcOnly noData

let private selectionCoverageNote (selected: ArrearsGeographyInfo[]) =
    match selected with
    | [||] -> "No geographies are selected. Choose at least one geography to display arrears rates."
    | [| geography |] -> coverageNote geography
    | _ ->
        let both, cbaOnly, cmhcOnly, noData = coverageCounts selected

        sprintf
            "Comparing %d selected geographies: %d publish both CBA and CMHC series, %d are CBA-only, %d are CMHC-only and %d have no observations. Provider series are charted only where available."
            selected.Length
            both
            cbaOnly
            cmhcOnly
            noData

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

let private latestRows (data: ArrearsData) (sourceCode: string) (metric: string) (selectedCodes: Set<string>) =
    data.Latest
    |> Array.filter (fun row ->
        row.SourceCode = sourceCode
        && row.Metric = metric
        && selectedCodes.Contains row.GeographyCode)

let private rateRange (rows: ArrearsObservationRow[]) =
    if rows.Length = 0 then
        "—"
    else
        let minimum = rows |> Array.map (fun row -> row.Value) |> Array.min
        let maximum = rows |> Array.map (fun row -> row.Value) |> Array.max

        if minimum = maximum then
            percent minimum
        else
            sprintf "%s–%s" (percent minimum) (percent maximum)

let private rateRangeSub (rows: ArrearsObservationRow[]) =
    match rows with
    | [||] -> "No observations for the selected geographies"
    | [| row |] -> sprintf "%s · As of %s" row.GeographyName (dateShort row.PeriodEnd)
    | _ -> sprintf "%d geographies · latest periods may differ" rows.Length

let private selectedSubtitle (selected: ArrearsGeographyInfo[]) =
    if selected.Length = 0 then
        "None selected"
    elif selected.Length <= 3 then
        selected |> Array.map (fun geography -> geography.Name) |> String.concat ", "
    else
        sprintf
            "%s +%d more"
            (selected
             |> Array.take 3
             |> Array.map (fun geography -> geography.Name)
             |> String.concat ", ")
            (selected.Length - 3)

let private geographyPanel
    (geographies: ArrearsGeographyInfo[])
    (selectedCodes: Set<string>)
    (selected: string list)
    (onChange: string list -> unit)
    =
    let toggle code =
        let next =
            if selectedCodes.Contains code then
                selected |> List.filter ((<>) code)
            else
                selected @ [ code ]

        onChange next

    let resetCode =
        geographies
        |> Array.tryFind (fun geography -> geography.Code = "canada")
        |> Option.orElse (Array.tryHead geographies)
        |> Option.map (fun geography -> geography.Code)
        |> Option.toList

    Html.section
        [
            prop.className "arrears-geography-panel"
            prop.children
                [
                    Html.div
                        [
                            prop.className "arrears-geography-head"
                            prop.children
                                [
                                    Html.div
                                        [
                                            prop.children
                                                [
                                                    Html.h2
                                                        [
                                                            prop.className "arrears-geography-title"
                                                            prop.text "Geographies"
                                                        ]
                                                    Html.p
                                                        [
                                                            prop.className "arrears-geography-subtitle"
                                                            prop.text (
                                                                sprintf
                                                                    "%d selected. Choose one or more countries, provinces or territories, regions and CMAs to compare."
                                                                    selected.Length
                                                            )
                                                        ]
                                                ]
                                        ]
                                    Html.div
                                        [
                                            prop.className "arrears-geography-actions"
                                            prop.children
                                                [
                                                    Html.button
                                                        [
                                                            prop.className "btn btn-ghost btn-small"
                                                            prop.onClick (fun _ ->
                                                                onChange (
                                                                    geographies
                                                                    |> Array.map (fun geography -> geography.Code)
                                                                    |> Array.toList
                                                                ))
                                                            prop.text "Select all"
                                                        ]
                                                    Html.button
                                                        [
                                                            prop.className "btn btn-ghost btn-small"
                                                            prop.onClick (fun _ -> onChange resetCode)
                                                            prop.text "Reset to Canada"
                                                        ]
                                                    Html.button
                                                        [
                                                            prop.className "btn btn-ghost btn-small"
                                                            prop.onClick (fun _ -> onChange [])
                                                            prop.text "Clear"
                                                        ]
                                                ]
                                        ]
                                ]
                        ]
                    Html.div
                        [
                            prop.className "arrears-geography-groups"
                            prop.children
                                [
                                    for level, items in groupedGeographies geographies do
                                        Html.fieldSet
                                            [
                                                prop.className "arrears-geography-group"
                                                prop.children
                                                    [
                                                        Html.legend
                                                            [
                                                                prop.className "arrears-geography-legend"
                                                                prop.text (
                                                                    if String.IsNullOrEmpty level then
                                                                        "Other geographies"
                                                                    else
                                                                        geographyLevelLabel level
                                                                )
                                                            ]
                                                        for geography in items do
                                                            let isSelected = selectedCodes.Contains geography.Code

                                                            Html.label
                                                                [
                                                                    prop.className "arrears-geography-option"
                                                                    prop.children
                                                                        [
                                                                            Html.input
                                                                                [
                                                                                    prop.type' "checkbox"
                                                                                    prop.isChecked isSelected
                                                                                    prop.onChange (fun (_: Event) ->
                                                                                        toggle geography.Code)
                                                                                ]
                                                                            Html.span
                                                                                [
                                                                                    prop.className
                                                                                        "arrears-geography-name"
                                                                                    prop.text geography.Name
                                                                                ]
                                                                            Html.span
                                                                                [
                                                                                    prop.className
                                                                                        "arrears-geography-coverage"
                                                                                    prop.text (coverageValue geography)
                                                                                ]
                                                                        ]
                                                                ]
                                                    ]
                                            ]
                                ]
                        ]
                ]
        ]

let private rateTable (selectedCodes: Set<string>) (onToggle: string -> unit) (rows: ArrearsObservationRow[]) =
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
                                                            let isSelected = selectedCodes.Contains row.GeographyCode

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
                                                                                            prop.custom (
                                                                                                "aria-label",
                                                                                                sprintf
                                                                                                    "%s %s arrears"
                                                                                                    (if isSelected then
                                                                                                         "Remove"
                                                                                                     else
                                                                                                         "Add")
                                                                                                    row.GeographyName
                                                                                            )
                                                                                            prop.onClick (fun _ ->
                                                                                                onToggle
                                                                                                    row.GeographyCode)
                                                                                            prop.text (
                                                                                                if isSelected then
                                                                                                    "Remove"
                                                                                                else
                                                                                                    "Add"
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

let private content (props: ArrearsProps) (data: ArrearsData) =
    let selectedCodes = props.SelectedGeographies |> Set.ofList

    let selected =
        data.Geographies
        |> Array.filter (fun geography -> selectedCodes.Contains geography.Code)

    let selectedSeries =
        data.Series
        |> Array.filter (fun point -> selectedCodes.Contains point.GeographyCode)

    let cbaRates = latestRows data "cba" "arrears_rate_percent" selectedCodes
    let cmhcRates = latestRows data "cmhc" "arrears_rate_percent" selectedCodes
    let cbaCounts = latestRows data "cba" "arrears_count" selectedCodes

    let cbaCount =
        match selected with
        | [| _ |] -> cbaCounts |> Array.tryHead
        | _ -> None

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

    let toggle code =
        let next =
            if selectedCodes.Contains code then
                props.SelectedGeographies |> List.filter ((<>) code)
            else
                props.SelectedGeographies @ [ code ]

        props.OnGeographiesChange next

    let chartTitle =
        match selected with
        | [| geography |] -> sprintf "%s 90+ day arrears rate" geography.Name
        | [||] -> "90+ day arrears rate"
        | many -> sprintf "90+ day arrears rate · %d selected geographies" many.Length

    let cbaCountSub =
        match selected with
        | [| geography |] -> sprintf "%s · Chartered-bank mortgages" geography.Name
        | _ -> "Select a single geography to show counts"

    Html.div
        [
            prop.className "page"
            prop.children
                [
                    PageHeader
                        "HOUSING CREDIT"
                        "Mortgage arrears"
                        "Compare 90+ day arrears rates across every loaded country, province or territory, region and CMA, with source definitions and freshness."
                        [
                            Html.button
                                [
                                    prop.className "btn btn-ghost"
                                    prop.onClick (fun _ -> props.OnRefresh())
                                    prop.text "↻ Refresh"
                                ]
                        ]
                    geographyPanel data.Geographies selectedCodes props.SelectedGeographies props.OnGeographiesChange
                    Html.div
                        [
                            prop.className "kpi-grid kpi-grid-5"
                            prop.children
                                [
                                    KpiCard "CBA latest rate" (rateRange cbaRates) (rateRangeSub cbaRates) "#2e5771"
                                    KpiCard "CMHC latest rate" (rateRange cmhcRates) (rateRangeSub cmhcRates) "#f47c3c"
                                    KpiCard
                                        "CBA mortgages in arrears"
                                        (cbaCount |> Option.map (fun row -> number row.Value) |> Option.defaultValue "—")
                                        cbaCountSub
                                        "#93c54b"
                                    KpiCard
                                        "Selected geographies"
                                        (string selected.Length)
                                        (selectedSubtitle selected)
                                        "#29abe0"
                                    KpiCard
                                        "Provider coverage"
                                        (coverageSummary selected)
                                        (coverageSummarySub selected)
                                        "#8e8c84"
                                ]
                        ]

                    Card chartTitle (selectionCoverageNote selected) [] [ arrearsComparisonChart selectedSeries ]

                    Card
                        "Latest rates by geography"
                        "Every loaded geography is listed. Use Add or Remove to build the comparison; selected rows are highlighted."
                        []
                        [ rateTable selectedCodes toggle rates ]

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

module NHAMBS.Client.Arrears

open Feliz
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting
open NHAMBS.Client.Charts

type ArrearsProps =
    {
        Data: Loadable<ArrearsData>
        OnRefresh: unit -> unit
    }

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

let private rateTable (rows: ArrearsObservationRow[]) =
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
                                prop.className "arrears-table"
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
                                                                    ]
                                                            ]
                                                    ]
                                            ]
                                        Html.tbody
                                            [
                                                prop.children
                                                    [
                                                        for row in rows do
                                                            Html.tr
                                                                [
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
                                                                                        titleCase row.GeographyLevel
                                                                                    )
                                                                                ]
                                                                            Html.td [ prop.text row.PeriodEnd ]
                                                                            Html.td [ prop.text (percent row.Value) ]
                                                                        ]
                                                                ]
                                                    ]
                                            ]
                                    ]
                            ]
                    ]
            ]

let private content (onRefresh: unit -> unit) (data: ArrearsData) =
    let cbaRate =
        latestMetric data "cba" "arrears_rate_percent" "canada"
        |> Option.map (fun row -> row.Value)

    let cmhcRate =
        latestMetric data "cmhc" "arrears_rate_percent" "canada"
        |> Option.map (fun row -> row.Value)

    let cbaCount =
        latestMetric data "cba" "arrears_count" "canada"
        |> Option.map (fun row -> row.Value)

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
                | _ -> 3

            levelOrder, row.SourceCode, row.GeographyName)

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
                            Html.button
                                [
                                    prop.className "btn btn-ghost"
                                    prop.onClick (fun _ -> onRefresh ())
                                    prop.text "↻ Refresh"
                                ]
                        ]
                    Html.div
                        [
                            prop.className "kpi-grid kpi-grid-5"
                            prop.children
                                [
                                    KpiCard
                                        "CBA national rate"
                                        (cbaRate |> Option.map percent |> Option.defaultValue "—")
                                        (cbaInfo
                                         |> Option.bind (fun info -> info.LatestPeriodEnd)
                                         |> Option.map (fun value -> sprintf "As of %s" (dateShort value))
                                         |> Option.defaultValue "No observation")
                                        "#2e5771"
                                    KpiCard
                                        "CMHC national rate"
                                        (cmhcRate |> Option.map percent |> Option.defaultValue "—")
                                        (cmhcInfo
                                         |> Option.bind (fun info -> info.LatestPeriodEnd)
                                         |> Option.map (fun value -> sprintf "As of %s" (dateShort value))
                                         |> Option.defaultValue "No observation")
                                        "#f47c3c"
                                    KpiCard
                                        "CBA mortgages in arrears"
                                        (cbaCount |> Option.map number |> Option.defaultValue "—")
                                        "Chartered-bank residential mortgages"
                                        "#93c54b"
                                    KpiCard "CBA coverage" "Monthly" "1995 to present" "#29abe0"
                                    KpiCard "CMHC coverage" "Quarterly" "2012 Q3 to 2025 Q4" "#8e8c84"
                                ]
                        ]

                    Card
                        "National 90+ day arrears rate"
                        "CBA chartered-bank series (monthly) versus CMHC/Equifax all-lender series (quarterly). The definitions are similar but the lender populations differ."
                        []
                        [ arrearsComparisonChart data.Comparison ]

                    Card
                        "Latest rates by geography"
                        "CBA publishes Canada, Atlantic and the territories as aggregates; CMHC publishes provinces and selected CMAs."
                        []
                        [ rateTable rates ]

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
    LoadableBlock "Loading mortgage arrears" props.Data (content props.OnRefresh)

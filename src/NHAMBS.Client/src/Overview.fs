module NHAMBS.Client.Overview

open Feliz
open Fable.Core.JsInterop
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting
open NHAMBS.Client.Charts

type OverviewProps =
    {
        Data: OverviewData
        OnNavigate: Page -> unit
        OnRefresh: unit -> unit
    }

let private sparklineSvg (months: string[]) (values: decimal[]) =
    if values.Length = 0 then
        ""
    else
        let width = 136.0
        let height = 28.0
        let maxValue = values |> Array.max |> max 1m

        let x index =
            if values.Length = 1 then
                width / 2.0
            else
                float index / float (values.Length - 1) * width

        let y value =
            height - 3.0 - float value / float maxValue * (height - 6.0)

        let points =
            values
            |> Array.mapi (fun index value -> sprintf "%.2f,%.2f" (x index) (y value))
            |> String.concat " "

        let areaPoints = sprintf "0.00,%.2f %s %.2f,%.2f" height points width height

        let title =
            values
            |> Array.mapi (fun index value ->
                let month =
                    if index < months.Length then
                        monthLabel months.[index]
                    else
                        ""

                sprintf "%s %s" month (moneyCompact value))
            |> String.concat "&#10;"

        let lastX = x (values.Length - 1)
        let lastY = y values.[values.Length - 1]

        sprintf
            "<svg class=\"sparkline-svg\" viewBox=\"0 0 %.0f %.0f\" preserveAspectRatio=\"none\" role=\"img\"><title>%s</title><polygon points=\"%s\" fill=\"rgba(46,87,113,0.14)\" /><polyline points=\"%s\" fill=\"none\" stroke=\"#2e5771\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" /><circle cx=\"%.2f\" cy=\"%.2f\" r=\"2.2\" fill=\"#ffffff\" stroke=\"#2e5771\" stroke-width=\"1.4\" /></svg>"
            width
            height
            title
            areaPoints
            points
            lastX
            lastY

let private issuerRows (issuers: IssuerSummary[]) (trendMonths: string[]) =
    issuers
    |> Array.map (fun issuer ->
        createObj
            [
                "issuer" ==> issuer.Issuer
                "pools" ==> issuer.Pools
                "principal" ==> issuer.Principal
                "share" ==> issuer.Share
                "trend" ==> sparklineSvg trendMonths issuer.Trend
                "averageCoupon" ==> issuer.AverageCoupon
                "loans" ==> issuer.Loans
            ])

let private issuerColumns =
    [|
        AgGrid.textColumn "issuer" "Issuer" 210
        AgGrid.numberColumn "pools" "Pools" 80
        AgGrid.moneyColumn "principal" "Principal" 135
        AgGrid.shareBarColumn "share" "Share" 145
        AgGrid.htmlColumn "trend" "12-mo issuance" 150
        AgGrid.rateColumn "averageCoupon" "Avg coupon" 105
        AgGrid.numberColumn "loans" "Loans" 90
    |]

[<ReactComponent>]
let OverviewPage (props: OverviewProps) =
    let data = props.Data
    let mbs = data.Mbs
    let cht = data.Cht
    let covered = data.CoveredBonds

    let periodPrincipal =
        mbs.Periods
        |> Array.map (fun period ->
            {
                Label = period.Label
                Date = period.Month
                Value = period.NewPrincipal
                Secondary = period.PoolBalance
                Count = period.Pools
            })

    let periodPools =
        mbs.Periods
        |> Array.map (fun period ->
            {
                Label = period.Label
                Date = period.Month
                Value = decimal period.Pools
                Secondary = decimal period.Issuers
                Count = period.Pools
            })

    Html.div
        [
            prop.className "page"
            prop.children
                [
                    PageHeader
                        "Securitization analytics"
                        "Market overview"
                        "A consolidated view of NHA mortgage-backed securities, Canada Mortgage Bonds and registered covered bonds."
                        [
                            Html.button
                                [
                                    prop.className "btn btn-ghost"
                                    prop.onClick (fun _ -> props.OnRefresh())
                                    prop.children [ Html.span [ prop.text "↻" ]; Html.span [ prop.text "Refresh" ] ]
                                ]
                            Html.button
                                [
                                    prop.className "btn btn-primary"
                                    prop.onClick (fun _ -> props.OnNavigate Pools)
                                    prop.children
                                        [ Html.span [ prop.text "Explore pools" ]; Html.span [ prop.text "→" ] ]
                                ]
                        ]

                    Html.div
                        [
                            prop.className "kpi-grid"
                            prop.children
                                [
                                    KpiCard
                                        "NHA MBS principal"
                                        (moneyCompact mbs.TotalPrincipal)
                                        (sprintf
                                            "%s pools · since %s"
                                            (number (decimal mbs.PoolCount))
                                            (dateShort mbs.NewestIssueDate))
                                        "#2e5771"
                                    KpiCard
                                        "Mortgage loans"
                                        (number (decimal mbs.LoanCount))
                                        (sprintf "%s issuers" (number (decimal mbs.IssuerCount)))
                                        "#f47c3c"
                                    KpiCard
                                        "Average coupon"
                                        (rate mbs.AverageCoupon)
                                        (sprintf "Weighted · %s WAM" (months mbs.AverageWam))
                                        "#93c54b"
                                    KpiCard
                                        "CHT outstanding"
                                        (moneyFromMillions cht.OutstandingMillions)
                                        (sprintf "%d outstanding series" cht.IssueCount)
                                        "#29abe0"
                                    KpiCard
                                        "Covered bonds"
                                        (number (decimal covered.BondCount))
                                        (sprintf
                                            "%d programs · %d currencies"
                                            covered.ProgramCount
                                            covered.CurrencyCount)
                                        "#d9534f"
                                ]
                        ]

                    Html.div
                        [
                            prop.className "overview-grid"
                            prop.children
                                [
                                    Card
                                        "Monthly NHA MBS issuance"
                                        "Aggregate principal amount by issue month"
                                        []
                                        [ areaChart mbs.Monthly ]
                                    Card
                                        "Rate type mix"
                                        "Principal outstanding by coupon structure"
                                        []
                                        [ donut (mbs.ByRateType |> Array.map (fun point -> point.Label, point.Value)) ]
                                ]
                        ]

                    Html.div
                        [
                            prop.className "overview-grid overview-grid-2"
                            prop.children
                                [
                                    Card
                                        "Principal by province"
                                        "Allocated using each pool's provincial principal percentage"
                                        []
                                        [
                                            horizontalBars (
                                                mbs.ByProvince
                                                |> Array.truncate 10
                                                |> Array.map (fun province ->
                                                    province.Province, province.Principal, percent1 province.Share)
                                            )
                                        ]
                                    Card
                                        "Maturity profile"
                                        "NHA MBS principal scheduled to mature by year"
                                        []
                                        [ verticalBars mbs.ByMaturityYear ]
                                ]
                        ]

                    Card
                        "Period pivot"
                        "Cumulative market snapshot for the latest reporting month, the prior month and the same month last year"
                        []
                        [
                            if mbs.Periods.Length = 0 then
                                EmptyState "No period data" "Issue dates are not available for the current selection."
                            else
                                Html.div
                                    [
                                        prop.className "pivot-layout"
                                        prop.children
                                            [
                                                Html.div
                                                    [
                                                        prop.className "pivot-charts"
                                                        prop.children
                                                            [
                                                                Html.div
                                                                    [
                                                                        prop.className "pivot-chart"
                                                                        prop.children
                                                                            [
                                                                                Html.h3 [ prop.text "New principal" ]
                                                                                verticalBars periodPrincipal
                                                                            ]
                                                                    ]
                                                                Html.div
                                                                    [
                                                                        prop.className "pivot-chart"
                                                                        prop.children
                                                                            [
                                                                                Html.h3 [ prop.text "Pool records" ]
                                                                                verticalBarsCount periodPools
                                                                            ]
                                                                    ]
                                                            ]
                                                    ]
                                                Html.div
                                                    [
                                                        prop.className "pivot-table-wrap"
                                                        prop.children
                                                            [
                                                                Html.table
                                                                    [
                                                                        prop.className "data-table pivot-table"
                                                                        prop.children
                                                                            [
                                                                                Html.thead
                                                                                    [
                                                                                        Html.tr
                                                                                            [
                                                                                                for header in
                                                                                                    [|
                                                                                                        "Period"
                                                                                                        "Issuers"
                                                                                                        "Pool records"
                                                                                                        "New pool"
                                                                                                        "Pool balance"
                                                                                                        "Avg coupon"
                                                                                                        "Loans"
                                                                                                    |] do
                                                                                                    Html.th
                                                                                                        [
                                                                                                            prop.text
                                                                                                                header
                                                                                                        ]
                                                                                            ]
                                                                                    ]
                                                                                Html.tbody
                                                                                    [
                                                                                        for period in mbs.Periods do
                                                                                            Html.tr
                                                                                                [
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop
                                                                                                                .children
                                                                                                                [
                                                                                                                    Html
                                                                                                                        .strong
                                                                                                                        [
                                                                                                                            prop
                                                                                                                                .text
                                                                                                                                period
                                                                                                                                    .Label
                                                                                                                        ]
                                                                                                                    Html
                                                                                                                        .span
                                                                                                                        [
                                                                                                                            prop
                                                                                                                                .className
                                                                                                                                "pivot-month"
                                                                                                                            prop.text (
                                                                                                                                monthLabel
                                                                                                                                    period
                                                                                                                                        .Month
                                                                                                                            )
                                                                                                                        ]
                                                                                                                ]
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                number (
                                                                                                                    decimal
                                                                                                                        period
                                                                                                                            .Issuers
                                                                                                                )
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                number (
                                                                                                                    decimal
                                                                                                                        period
                                                                                                                            .Pools
                                                                                                                )
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                moneyCompact
                                                                                                                    period
                                                                                                                        .NewPrincipal
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                moneyCompact
                                                                                                                    period
                                                                                                                        .PoolBalance
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                rate
                                                                                                                    period
                                                                                                                        .AverageCoupon
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                number (
                                                                                                                    decimal
                                                                                                                        period
                                                                                                                            .Loans
                                                                                                                )
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

                    Card
                        "Top issuers"
                        "Ranked by aggregate principal across all loaded NHA MBS pools"
                        [
                            Html.button
                                [
                                    prop.className "btn btn-small btn-ghost"
                                    prop.onClick (fun _ -> props.OnNavigate Pools)
                                    prop.text "View all pools"
                                ]
                        ]
                        [
                            AgGrid.AgGrid
                                {
                                    Columns = issuerColumns
                                    Rows = issuerRows mbs.TopIssuers mbs.TrendMonths
                                    Height = "390px"
                                    Pagination = true
                                    InitialSortModel = null
                                    InitialFilterModel = null
                                    ApplyQuickFilterInternally = true
                                    HeaderGroupFields = [||]
                                    OnSortModelChanged = ignore
                                    OnFilterModelChanged = ignore
                                    OnQuickFilterChanged = ignore
                                    OnRowClicked = ignore
                                }
                        ]

                    Html.div
                        [
                            prop.className "overview-grid overview-grid-2"
                            prop.children
                                [
                                    Card
                                        "Canada Mortgage Bonds"
                                        "Outstanding CHT issuance in CAD millions"
                                        []
                                        [
                                            Html.div
                                                [
                                                    prop.className "split-metrics"
                                                    prop.children
                                                        [
                                                            MiniLabel
                                                                "Outstanding"
                                                                (moneyFromMillions cht.OutstandingMillions)
                                                            MiniLabel
                                                                "Total issuance"
                                                                (moneyFromMillions cht.TotalIssuanceMillions)
                                                            MiniLabel "Average coupon" (rate cht.AverageCoupon)
                                                            MiniLabel "Next maturity" (dateShort cht.NextMaturityDate)
                                                            MiniLabel
                                                                "Circulars parsed"
                                                                (number (decimal cht.CircularCount))
                                                        ]
                                                ]
                                            verticalBars cht.ByMaturityYear
                                        ]
                                    Card
                                        "Covered bond programmes"
                                        "Registered programmes and outstanding series"
                                        []
                                        [
                                            Html.div
                                                [
                                                    prop.className "split-metrics"
                                                    prop.children
                                                        [
                                                            MiniLabel
                                                                "Programmes"
                                                                (number (decimal covered.ProgramCount))
                                                            MiniLabel "Bonds" (number (decimal covered.BondCount))
                                                            MiniLabel
                                                                "Currencies"
                                                                (number (decimal covered.CurrencyCount))
                                                            MiniLabel
                                                                "Earliest maturity"
                                                                (dateShort covered.EarliestMaturity)
                                                            MiniLabel
                                                                "Latest maturity"
                                                                (dateShort covered.LatestMaturity)
                                                        ]
                                                ]
                                            miniBarsWith numberCompact covered.ByCurrency
                                        ]
                                ]
                        ]

                    Card
                        "Data freshness"
                        "Latest successful ETL run for each source dataset"
                        []
                        [
                            Html.div
                                [
                                    prop.className "freshness-grid"
                                    prop.children
                                        [
                                            for dataset in data.Freshness do
                                                Html.div
                                                    [
                                                        prop.className "freshness-card"
                                                        prop.children
                                                            [
                                                                Html.div
                                                                    [
                                                                        prop.className "freshness-head"
                                                                        prop.children
                                                                            [
                                                                                Html.strong
                                                                                    [ prop.text dataset.DisplayName ]
                                                                                StatusBadge dataset.Status
                                                                            ]
                                                                    ]
                                                                Html.div
                                                                    [
                                                                        prop.className "freshness-time"
                                                                        prop.text dataset.LastLoaded
                                                                    ]
                                                                Html.div
                                                                    [
                                                                        prop.className "freshness-meta"
                                                                        prop.children
                                                                            [
                                                                                Html.span
                                                                                    [
                                                                                        prop.text (
                                                                                            sprintf
                                                                                                "%s rows"
                                                                                                (number (
                                                                                                    decimal
                                                                                                        dataset
                                                                                                            .RowsLoaded
                                                                                                ))
                                                                                        )
                                                                                    ]
                                                                                Html.span
                                                                                    [
                                                                                        prop.text (
                                                                                            sprintf
                                                                                                "%d files"
                                                                                                dataset.FilesProcessed
                                                                                        )
                                                                                    ]
                                                                                Html.span
                                                                                    [
                                                                                        prop.text (
                                                                                            sprintf
                                                                                                "%ss"
                                                                                                (dataset.DurationSeconds
                                                                                                    .ToString("N1"))
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

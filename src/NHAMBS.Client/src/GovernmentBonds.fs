module NHAMBS.Client.GovernmentBonds

open System
open Feliz
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting
open NHAMBS.Client.Charts

type GovernmentBondsProps =
    {
        Data: Loadable<GovernmentBondData>
        OnRefresh: unit -> unit
    }

let private programCard (program: GovernmentBondProgramInfo) =
    Card
        program.Name
        program.IssuerName
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
                                            Html.span [ prop.className "arrears-source-label"; prop.text "As of" ]
                                            Html.span
                                                [
                                                    prop.className "arrears-source-value"
                                                    prop.text (optionText program.LatestAsOfDate)
                                                ]
                                            Html.span [ prop.className "arrears-source-label"; prop.text "Cadence" ]
                                            Html.span
                                                [
                                                    prop.className "arrears-source-value"
                                                    prop.text (titleCase program.Cadence)
                                                ]
                                            Html.span
                                                [
                                                    prop.className (
                                                        if program.IsStale then
                                                            "badge badge-warning"
                                                        else
                                                            "badge badge-success"
                                                    )
                                                    prop.text (if program.IsStale then "Stale" else "Current")
                                                ]
                                        ]
                                ]
                            Html.p [ prop.className "arrears-definition"; prop.text program.Notes ]
                            Html.p
                                [
                                    prop.className "arrears-attribution"
                                    prop.text (sprintf "Source: %s" program.SourceName)
                                ]
                        ]
                ]
        ]

let private issueTable (issues: GovernmentBondIssueRow[]) =
    if issues.Length = 0 then
        EmptyState "No government bond issues" "Run the government-bonds ETL to populate this view."
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
                                                                        Html.th [ prop.text "ISIN" ]
                                                                        Html.th [ prop.text "Type" ]
                                                                        Html.th [ prop.text "Coupon" ]
                                                                        Html.th [ prop.text "Issued" ]
                                                                        Html.th [ prop.text "Maturity" ]
                                                                        Html.th [ prop.text "Outstanding" ]
                                                                        Html.th [ prop.text "Inflation adj." ]
                                                                    ]
                                                            ]
                                                    ]
                                            ]
                                        Html.tbody
                                            [
                                                prop.children
                                                    [
                                                        for issue in issues do
                                                            Html.tr
                                                                [
                                                                    prop.children
                                                                        [
                                                                            Html.td [ prop.text issue.Isin ]
                                                                            Html.td
                                                                                [
                                                                                    prop.text (
                                                                                        if
                                                                                            issue.SecurityType = "BOND"
                                                                                        then
                                                                                            "Bond"
                                                                                        else
                                                                                            "T-bill"
                                                                                    )
                                                                                ]
                                                                            Html.td
                                                                                [
                                                                                    prop.text (
                                                                                        percent issue.CouponRatePercent
                                                                                    )
                                                                                ]
                                                                            Html.td [ prop.text issue.IssueDate ]
                                                                            Html.td [ prop.text issue.MaturityDate ]
                                                                            Html.td
                                                                                [
                                                                                    prop.text (
                                                                                        moneyCompact
                                                                                            issue.OutstandingAmount
                                                                                    )
                                                                                ]
                                                                            Html.td
                                                                                [
                                                                                    prop.text (
                                                                                        issue.InflationAdjustment
                                                                                        |> Option.map moneyCompact
                                                                                        |> Option.defaultValue "—"
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

let private content (onRefresh: unit -> unit) (data: GovernmentBondData) =
    let analytics = data.Analytics

    let program =
        data.Programs |> Array.tryFind (fun program -> program.ProgramCode = "goc")

    Html.div
        [
            prop.className "page"
            prop.children
                [
                    PageHeader
                        "SOVEREIGN AND AGENCY DEBT"
                        "Government bonds"
                        "Issue-level Government of Canada domestic marketable bonds and treasury bills, with maturity, coupon and outstanding profiles."
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
                                        "Bonds outstanding"
                                        (moneyCompact analytics.BondOutstanding)
                                        (sprintf "%d issues" analytics.BondIssueCount)
                                        "#2e5771"
                                    KpiCard
                                        "Treasury bills outstanding"
                                        (moneyCompact analytics.BillOutstanding)
                                        "Money-market securities"
                                        "#f47c3c"
                                    KpiCard
                                        "Total Government of Canada debt"
                                        (moneyCompact analytics.TotalOutstanding)
                                        (sprintf "%d securities" analytics.IssueCount)
                                        "#93c54b"
                                    KpiCard
                                        "Weighted average coupon"
                                        (percent analytics.WeightedAverageCoupon)
                                        "Bonds, weighted by outstanding"
                                        "#29abe0"
                                    KpiCard
                                        "As of"
                                        (dateShortOption analytics.AsOfDate)
                                        "Bank of Canada Valet"
                                        "#8e8c84"
                                ]
                        ]

                    Card
                        "Outstanding bonds by maturity year"
                        "Par value outstanding, grouped by calendar maturity year."
                        []
                        [ verticalBars analytics.ByMaturityYear ]

                    Card
                        "Coupon profile"
                        "Outstanding bonds grouped by coupon bucket. Inflation-adjusted real-return bonds are included at their nominal coupon."
                        []
                        [ miniBars analytics.ByCouponBucket ]

                    Card
                        "Issues"
                        "Current Government of Canada domestic marketable bonds and treasury bills. Inflation adjustment applies to real-return bonds."
                        []
                        [ issueTable data.Issues ]

                    match program with
                    | Some value -> programCard value
                    | None -> Html.div []
                ]
        ]

let GovernmentBondsPage (props: GovernmentBondsProps) =
    LoadableBlock "Loading government bonds" props.Data (content props.OnRefresh)

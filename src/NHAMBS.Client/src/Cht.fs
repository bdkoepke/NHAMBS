module NHAMBS.Client.Cht

open System
open Feliz
open Fable.Core.JsInterop
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting
open NHAMBS.Client.Charts

type ChtProps =
    {
        Data: Loadable<ChtData>
        OnRefresh: unit -> unit
    }

let private issueRows (issues: ChtIssueRow[]) =
    issues
    |> Array.map (fun issue ->
        createObj
            [
                "series" ==> issue.Series
                "settlementDate" ==> issue.SettlementDate
                "coupon" ==> issue.CouponDisplay
                "maturityDate" ==> issue.MaturityDate
                "currency" ==> issue.Currency
                "frequency" ==> titleCase issue.InterestPaymentFrequency
                "investors" ==> issue.IssuedToInvestors * 1_000_000m
                "government" ==> issue.IssuedToGovernment * 1_000_000m
                "totalIssuance" ==> issue.TotalIssuance * 1_000_000m
                "outstanding" ==> issue.Outstanding * 1_000_000m
                "identifier" ==> issue.Identifier
                "circulars" ==> issue.CircularCount
                "history" ==> (if issue.HistoryAvailable then "Yes" else "No")
                "cancellations" ==> issue.CancellationCount
                "cancelled" ==> issue.CancelledMillions * 1_000_000m
                "sourceUrl" ==> issue.SourceUrl
            ])

let private issueColumns =
    [|
        AgGrid.textColumn "series" "Series" 100
        AgGrid.dateColumn "settlementDate" "Settled" 120
        AgGrid.textColumn "coupon" "Coupon" 110
        AgGrid.dateColumn "maturityDate" "Matures" 120
        AgGrid.textColumn "currency" "Currency" 90
        AgGrid.textColumn "frequency" "Frequency" 110
        AgGrid.moneyColumn "investors" "Issued to investors" 150
        AgGrid.moneyColumn "government" "Issued to GoC" 130
        AgGrid.moneyColumn "totalIssuance" "Total issuance" 140
        AgGrid.moneyColumn "outstanding" "Outstanding" 140
        AgGrid.textColumn "identifier" "Identifier" 120
        AgGrid.numberColumn "circulars" "Circulars" 95
        AgGrid.textColumn "history" "History" 90
        AgGrid.numberColumn "cancellations" "Cancellations" 120
        AgGrid.moneyColumn "cancelled" "Cancelled" 130
        AgGrid.linkColumn "sourceUrl" "Source" 100
    |]

let private circularRows (circulars: ChtCircularRow[]) =
    circulars
    |> Array.map (fun circular ->
        createObj
            [
                "series" ==> circular.Series
                "designation" ==> circular.Designation
                "documentDate" ==> circular.DocumentDate
                "currency" ==> circular.Currency
                "principal" ==> circular.AggregatePrincipalAmount
                "coupon" ==> circular.CouponDisplay
                "couponValue" ==> circular.CouponValue
                "maturityDate" ==> circular.MaturityDate
                "settlementDate" ==> circular.SettlementDate
                "frequency" ==> titleCase circular.InterestPaymentFrequency
                "isin" ==> circular.Isin
                "cusip" ==> circular.Cusip
                "ratings" ==> circular.Ratings
                "yield" ==> circular.YieldPercent
                "sourceUrl" ==> circular.SourceUrl
            ])

let private circularColumns =
    [|
        AgGrid.textColumn "series" "Series" 95
        AgGrid.textColumn "designation" "Designation" 130
        AgGrid.dateColumn "documentDate" "Document date" 130
        AgGrid.textColumn "currency" "Currency" 90
        AgGrid.moneyColumn "principal" "Principal" 145
        AgGrid.textColumn "coupon" "Coupon" 110
        AgGrid.rateColumn "couponValue" "Coupon rate" 110
        AgGrid.dateColumn "maturityDate" "Matures" 120
        AgGrid.dateColumn "settlementDate" "Settled" 120
        AgGrid.textColumn "frequency" "Frequency" 105
        AgGrid.textColumn "isin" "ISIN" 150
        AgGrid.textColumn "cusip" "CUSIP" 110
        AgGrid.textColumn "ratings" "Ratings" 240
        AgGrid.linkColumn "sourceUrl" "Circular" 100
    |]

let private historyRows (history: ChtHistoryRow[]) =
    history
    |> Array.map (fun row ->
        let periods =
            row.Cancellations
            |> Array.map (fun period ->
                sprintf
                    "%s to %s (%s)"
                    (dateShort period.FromDate)
                    (dateShort period.ToDate)
                    (moneyCompact (period.CancelledAmount * 1_000_000m)))
            |> String.concat "; "

        createObj
            [
                "series" ==> row.Series
                "asOfDate" ==> row.AsOfDate
                "coupon" ==> row.CouponDisplay
                "couponValue" ==> row.CouponValue
                "maturityDate" ==> row.MaturityDate
                "identifier" ==> row.Identifier
                "cancellations" ==> row.CancellationCount
                "cancelled" ==> row.CancelledMillions * 1_000_000m
                "firstCancellation" ==> row.FirstCancellationDate
                "lastCancellation" ==> row.LastCancellationDate
                "periods" ==> periods
                "sourceUrl" ==> row.SourceUrl
            ])

let private historyColumns =
    [|
        AgGrid.textColumn "series" "Series" 95
        AgGrid.dateColumn "asOfDate" "As of" 115
        AgGrid.textColumn "coupon" "Coupon" 110
        AgGrid.rateColumn "couponValue" "Coupon rate" 110
        AgGrid.dateColumn "maturityDate" "Matures" 120
        AgGrid.textColumn "identifier" "Identifier" 120
        AgGrid.numberColumn "cancellations" "Cancellations" 120
        AgGrid.moneyColumn "cancelled" "Cancelled" 130
        AgGrid.dateColumn "firstCancellation" "First period" 120
        AgGrid.dateColumn "lastCancellation" "Last period" 120
        AgGrid.textColumn "periods" "Cancellation periods" 320
        AgGrid.linkColumnWithLabel "sourceUrl" "Workbook" 110 "Open workbook"
    |]

[<ReactComponent>]
let ChtPage (props: ChtProps) =
    let tab, setTab = React.useState "issues"

    Html.div
        [
            prop.className "page"
            prop.children
                [
                    PageHeader
                        "Canada Housing Trust"
                        "Canada Mortgage Bonds"
                        "Outstanding CHT issuance and the full library of parsed offering circulars."
                        [
                            Html.button
                                [
                                    prop.className "btn btn-ghost"
                                    prop.onClick (fun _ -> props.OnRefresh())
                                    prop.children [ Html.span [ prop.text "↻" ]; Html.span [ prop.text "Refresh" ] ]
                                ]
                        ]

                    LoadableBlock "Loading Canada Mortgage Bond data" props.Data (fun data ->
                        let analytics = data.Analytics

                        let byCurrency =
                            analytics.ByCurrency
                            |> Array.map (fun point -> point.Label, point.Value * 1_000_000m)

                        let byCoupon =
                            analytics.ByCouponBucket
                            |> Array.map (fun point ->
                                { point with
                                    Value = point.Value * 1_000_000m
                                })

                        let byMaturity =
                            analytics.ByMaturityYear
                            |> Array.map (fun point ->
                                { point with
                                    Value = point.Value * 1_000_000m
                                })

                        Html.div
                            [
                                prop.children
                                    [
                                        Html.div
                                            [
                                                prop.className "kpi-grid kpi-grid-5"
                                                prop.children
                                                    [
                                                        KpiCard
                                                            "Outstanding"
                                                            (moneyFromMillions analytics.OutstandingMillions)
                                                            (sprintf "%d series" analytics.IssueCount)
                                                            "#18a999"
                                                        KpiCard
                                                            "Total issuance"
                                                            (moneyFromMillions analytics.TotalIssuanceMillions)
                                                            (sprintf "%d circulars parsed" analytics.CircularCount)
                                                            "#4f8ef7"
                                                        KpiCard
                                                            "Average coupon"
                                                            (rate analytics.AverageCoupon)
                                                            "Weighted by outstanding amount"
                                                            "#d8a84e"
                                                        KpiCard
                                                            "Next maturity"
                                                            (dateShort analytics.NextMaturityDate)
                                                            "Nearest outstanding maturity"
                                                            "#7c6ee6"
                                                        KpiCard
                                                            "Currencies"
                                                            (number (decimal byCurrency.Length))
                                                            "Issuance currencies"
                                                            "#e2725b"
                                                    ]
                                            ]

                                        Html.div
                                            [
                                                prop.className "pool-chart-grid"
                                                prop.children
                                                    [
                                                        Card
                                                            "Outstanding by maturity year"
                                                            "CHT outstanding amount by year"
                                                            []
                                                            [ verticalBars byMaturity ]
                                                        Card
                                                            "Coupon profile"
                                                            "Outstanding amount by coupon bucket"
                                                            []
                                                            [ miniBars byCoupon ]
                                                        Card
                                                            "Issuance currency"
                                                            "Outstanding by currency"
                                                            []
                                                            [
                                                                horizontalBars (
                                                                    analytics.ByCurrency
                                                                    |> Array.map (fun point ->
                                                                        point.Label,
                                                                        point.Value * 1_000_000m,
                                                                        sprintf "%d series" point.Count)
                                                                )
                                                            ]
                                                        Card
                                                            "Circular and history coverage"
                                                            "Parsed offering circulars and cancellation history by series"
                                                            []
                                                            [
                                                                Html.div
                                                                    [
                                                                        prop.className "split-metrics"
                                                                        prop.children
                                                                            [
                                                                                MiniLabel
                                                                                    "Issues"
                                                                                    (number (
                                                                                        decimal analytics.IssueCount
                                                                                    ))
                                                                                MiniLabel
                                                                                    "Circulars"
                                                                                    (number (
                                                                                        decimal analytics.CircularCount
                                                                                    ))
                                                                                MiniLabel
                                                                                    "History series"
                                                                                    (number (
                                                                                        decimal analytics.HistoryCount
                                                                                    ))
                                                                                MiniLabel
                                                                                    "Cancelled"
                                                                                    (moneyFromMillions
                                                                                        analytics.CancelledMillions)
                                                                                MiniLabel
                                                                                    "History as of"
                                                                                    (dateShort
                                                                                        analytics.HistoryAsOfDate)
                                                                                MiniLabel
                                                                                    "Average coupon"
                                                                                    (rate analytics.AverageCoupon)
                                                                            ]
                                                                    ]
                                                                Html.p
                                                                    [
                                                                        prop.className "muted"
                                                                        prop.text
                                                                            "Each offering circular row includes identifiers, ratings and a source PDF. The outstanding-history tab carries every series block from the CMHC bond-purchase cancellation workbook."
                                                                    ]
                                                            ]
                                                    ]
                                            ]

                                        Card
                                            ""
                                            ""
                                            []
                                            [
                                                Tabs
                                                    [|
                                                        "issues", sprintf "Outstanding issues (%d)" data.Issues.Length
                                                        "circulars",
                                                        sprintf "Offering circulars (%d)" data.Circulars.Length
                                                        "history",
                                                        sprintf "Outstanding history (%d)" data.History.Length
                                                    |]
                                                    tab
                                                    setTab

                                                if tab = "issues" then
                                                    AgGrid.AgGrid
                                                        {
                                                            Columns = issueColumns
                                                            Rows = issueRows data.Issues
                                                            Height = "560px"
                                                            OnRowClicked = ignore
                                                        }
                                                elif tab = "circulars" then
                                                    AgGrid.AgGrid
                                                        {
                                                            Columns = circularColumns
                                                            Rows = circularRows data.Circulars
                                                            Height = "640px"
                                                            OnRowClicked = ignore
                                                        }
                                                else
                                                    AgGrid.AgGrid
                                                        {
                                                            Columns = historyColumns
                                                            Rows = historyRows data.History
                                                            Height = "640px"
                                                            OnRowClicked = ignore
                                                        }
                                            ]
                                    ]
                            ])
                ]
        ]

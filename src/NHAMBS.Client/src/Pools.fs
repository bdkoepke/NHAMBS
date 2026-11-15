module NHAMBS.Client.Pools

open System
open Feliz
open Fable.Core.JsInterop
open Browser.Types
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting
open NHAMBS.Client.Charts

type PoolsProps =
    {
        Analytics: Loadable<MbsAnalytics>
        Page: Loadable<MbsPoolPage>
        Filter: MbsPoolFilter
        Filters: Loadable<MbsFilterOptions>
        SelectedPool: string option
        Detail: Loadable<MbsPoolDetail[]>
        OnFilterChange: MbsPoolFilter -> unit
        OnApply: unit -> unit
        OnReset: unit -> unit
        OnSelectPool: string -> unit
        OnCloseDetail: unit -> unit
        OnRefresh: unit -> unit
    }

let private eventValue (event: Event) = string event.target?value

let private field (label: string) (control: ReactElement) =
    Html.label
        [
            prop.className "field"
            prop.children [ Html.span [ prop.className "field-label"; prop.text label ]; control ]
        ]

let private selectField
    (label: string)
    (value: string)
    (placeholder: string)
    (options: (string * string)[])
    (onChange: string -> unit)
    =
    field
        label
        (Html.select
            [
                prop.className "field-input"
                prop.value value
                prop.onChange (fun event -> onChange (eventValue event))
                prop.children
                    [
                        Html.option [ prop.value ""; prop.text placeholder ]
                        for optionValue, optionLabel in options do
                            Html.option [ prop.value optionValue; prop.text optionLabel ]
                    ]
            ])

let private textField (label: string) (value: string) (placeholder: string) (onChange: string -> unit) =
    field
        label
        (Html.input
            [
                prop.className "field-input"
                prop.value value
                prop.placeholder placeholder
                prop.onChange (fun event -> onChange (eventValue event))
            ])

let private dateField (label: string) (value: string) (onChange: string -> unit) =
    field
        label
        (Html.input
            [
                prop.className "field-input"
                prop.custom ("type", "date")
                prop.value value
                prop.onChange (fun event -> onChange (eventValue event))
            ])

let private filterPanel (props: PoolsProps) (options: MbsFilterOptions) =
    let filter = props.Filter

    Html.div
        [
            prop.className "filter-panel"
            prop.children
                [
                    Html.div
                        [
                            prop.className "filter-grid"
                            prop.children
                                [
                                    textField "Search" filter.Search "Pool, CUSIP or issuer" (fun value ->
                                        props.OnFilterChange { filter with Search = value })
                                    selectField
                                        "Issuer"
                                        filter.Issuer
                                        "All issuers"
                                        (options.Issuers |> Array.map (fun value -> value, value))
                                        (fun value -> props.OnFilterChange { filter with Issuer = value })
                                    selectField
                                        "Rate type"
                                        filter.RateType
                                        "All rate types"
                                        (options.RateTypes |> Array.map (fun value -> value, titleCase value))
                                        (fun value -> props.OnFilterChange { filter with RateType = value })
                                    selectField
                                        "Pool type"
                                        filter.PoolType
                                        "All pool types"
                                        (options.PoolTypes
                                         |> Array.map (fun value -> value.Code, poolTypeLabel value.Code value.Label))
                                        (fun value -> props.OnFilterChange { filter with PoolType = value })
                                    selectField
                                        "Province"
                                        filter.Province
                                        "All provinces"
                                        (options.Provinces |> Array.map (fun value -> value, value))
                                        (fun value -> props.OnFilterChange { filter with Province = value })
                                    selectField
                                        "Insurer"
                                        filter.Insurer
                                        "All insurers"
                                        (options.Insurers |> Array.map (fun value -> value, truncate 34 value))
                                        (fun value -> props.OnFilterChange { filter with Insurer = value })
                                    dateField "Issued from" filter.DateFrom (fun value ->
                                        props.OnFilterChange { filter with DateFrom = value })
                                    dateField "Issued to" filter.DateTo (fun value ->
                                        props.OnFilterChange { filter with DateTo = value })
                                    dateField "Matures from" filter.MaturityFrom (fun value ->
                                        props.OnFilterChange { filter with MaturityFrom = value })
                                    dateField "Matures to" filter.MaturityTo (fun value ->
                                        props.OnFilterChange { filter with MaturityTo = value })
                                ]
                        ]
                    Html.div
                        [
                            prop.className "filter-footer"
                            prop.children
                                [
                                    Html.span
                                        [
                                            prop.className "filter-hint"
                                            prop.text (
                                                sprintf
                                                    "Data available from %s to %s"
                                                    (dateShort options.MinIssueDate)
                                                    (dateShort options.MaxIssueDate)
                                            )
                                        ]
                                    Html.div
                                        [
                                            prop.className "filter-buttons"
                                            prop.children
                                                [
                                                    Html.button
                                                        [
                                                            prop.className "btn btn-ghost"
                                                            prop.onClick (fun _ -> props.OnReset())
                                                            prop.text "Reset"
                                                        ]
                                                    Html.button
                                                        [
                                                            prop.className "btn btn-primary"
                                                            prop.onClick (fun _ -> props.OnApply())
                                                            prop.text "Apply filters"
                                                        ]
                                                ]
                                        ]
                                ]
                        ]
                ]
        ]

let private poolRows (rows: MbsPoolRow[]) =
    rows
    |> Array.map (fun row ->
        createObj
            [
                "poolId" ==> row.PoolId
                "poolNumber" ==> row.PoolNumber
                "cusip" ==> row.Cusip
                "issuer" ==> row.IssuerName
                "rateType" ==> titleCase row.RateType
                "poolType" ==> poolTypeLabel row.PoolType row.PoolTypeDescription
                "poolTypeTooltip" ==> PoolTypes.tooltipFor row.PoolType
                "coupon" ==> row.CouponDisplay
                "principal" ==> row.Principal
                "issueDate" ==> row.IssueDate
                "maturityDate" ==> row.MaturityDate
                "loans" ==> row.Loans
                "wac" ==> row.Wac
                "wam" ==> row.Wam
                "provinces" ==> row.Provinces
                "insurers" ==> row.Insurers
                "sourceUrl" ==> row.SourceUrl
            ])

let private poolColumns =
    [|
        AgGrid.textColumn "poolNumber" "Pool" 120
        AgGrid.textColumn "cusip" "CUSIP" 110
        AgGrid.textColumn "issuer" "Issuer" 190
        AgGrid.textColumn "rateType" "Rate" 90
        AgGrid.textColumnWithTooltip
            "poolType"
            "Pool type"
            190
            "Three-digit CMHC NHA MBS pool type. Hover a cell for a definition, or expand the pool type guide below."
            "poolTypeTooltip"
        AgGrid.textColumn "coupon" "Coupon" 110
        AgGrid.moneyColumn "principal" "Principal" 145
        AgGrid.dateColumn "issueDate" "Issued" 120
        AgGrid.dateColumn "maturityDate" "Matures" 120
        AgGrid.numberColumn "loans" "Loans" 85
        AgGrid.rateColumn "wac" "WAC" 90
        AgGrid.monthsColumn "wam" "WAM" 85
        AgGrid.textColumn "provinces" "Provinces" 240
        AgGrid.textColumn "insurers" "Insurers" 230
        AgGrid.linkColumn "sourceUrl" "Circular" 100
    |]

let private percentageBars (items: (string * decimal)[]) =
    if items.Length = 0 then
        EmptyState "No allocation" "This circular does not report a breakdown."
    else
        let maxValue = items |> Array.map snd |> Array.max |> max 1m

        Html.div
            [
                prop.className "hbar-list"
                prop.children
                    [
                        for label, value in items do
                            Html.div
                                [
                                    prop.className "hbar-row"
                                    prop.children
                                        [
                                            Html.div
                                                [
                                                    prop.className "hbar-head"
                                                    prop.children
                                                        [
                                                            Html.span [ prop.className "hbar-label"; prop.text label ]
                                                            Html.span
                                                                [
                                                                    prop.className "hbar-value"
                                                                    prop.text (percent1 value)
                                                                ]
                                                        ]
                                                ]
                                            Html.div
                                                [
                                                    prop.className "hbar-track"
                                                    prop.children
                                                        [
                                                            Html.div
                                                                [
                                                                    prop.className "hbar-fill hbar-fill-alt"
                                                                    prop.style
                                                                        [
                                                                            style.width (
                                                                                length.percent (
                                                                                    float value / float maxValue * 100.0
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

let private poolTypeChartTooltip (label: string) =
    let separator = label.IndexOf(" · ", StringComparison.Ordinal)

    let code =
        if separator < 0 then
            label
        else
            label.Substring(0, separator)

    match PoolTypes.tryFind code with
    | Some info -> Some(PoolTypes.tooltip info)
    | None -> None

let private issuerChartItems (rows: MbsPoolRow[]) =
    rows
    |> Array.groupBy (fun row -> row.IssuerName)
    |> Array.map (fun (issuer, issuerRows) ->
        issuer, issuerRows |> Array.sumBy (fun row -> row.Principal), sprintf "%d pools" issuerRows.Length)
    |> Array.sortByDescending (fun (_, principal, _) -> principal)
    |> Array.truncate 8

let private couponBand (coupon: decimal) =
    if coupon <= 0m then
        "Floating / other"
    else
        let lower = decimal (int (Math.Floor(float coupon * 4.0))) / 4m
        sprintf "%.2f–%.2f%%" lower (lower + 0.25m)

let private couponChartPoints (rows: MbsPoolRow[]) =
    rows
    |> Array.groupBy (fun row -> couponBand row.CouponValue)
    |> Array.map (fun (label, bandRows) ->
        {
            Label = label
            Date = ""
            Value = bandRows |> Array.sumBy (fun row -> row.Principal)
            Secondary = 0m
            Count = bandRows.Length
        })
    |> Array.sortBy (fun point -> point.Label)

let private poolTypeGuide () =
    Card
        "Pool type guide"
        "CMHC assigns a three-digit code to each NHA MBS pool type. For example, 975 is a fixed-rate homeowner pool that passes prepayment indemnities to investors for the first 60 months. Hover a pool type in the table or chart for the same definition."
        []
        [
            Html.details
                [
                    prop.className "pool-guide"
                    prop.children
                        [
                            Html.summary
                                [
                                    prop.className "pool-guide-summary"
                                    prop.children
                                        [
                                            Html.span [ prop.text "What do the pool type codes mean?" ]
                                            Html.span
                                                [
                                                    prop.className "pool-guide-count"
                                                    prop.text (sprintf "%d codes" PoolTypes.all.Length)
                                                ]
                                        ]
                                ]
                            Html.div
                                [
                                    prop.className "pool-guide-grid"
                                    prop.children
                                        [
                                            for info in PoolTypes.all do
                                                Html.div
                                                    [
                                                        prop.className "pool-guide-item"
                                                        prop.children
                                                            [
                                                                Html.div
                                                                    [
                                                                        prop.className "pool-guide-head"
                                                                        prop.children
                                                                            [
                                                                                Html.span
                                                                                    [
                                                                                        prop.className "pool-guide-code"
                                                                                        prop.text info.Code
                                                                                    ]
                                                                                Html.span
                                                                                    [
                                                                                        prop.className "pool-guide-name"
                                                                                        prop.text info.Name
                                                                                    ]
                                                                                if info.Legacy then
                                                                                    Html.span
                                                                                        [
                                                                                            prop.className "badge"
                                                                                            prop.text "No new issues"
                                                                                        ]
                                                                            ]
                                                                    ]
                                                                Html.p
                                                                    [
                                                                        prop.className "pool-guide-detail"
                                                                        prop.text info.Detail
                                                                    ]
                                                            ]
                                                    ]
                                        ]
                                ]
                        ]
                ]
        ]

let private detailDrawer (props: PoolsProps) =
    let close () = props.OnCloseDetail()

    let body =
        match props.Detail with
        | NotLoaded
        | Loading -> LoadingBlock "Loading pool detail"
        | Failed message -> ErrorBlock message
        | Loaded details ->
            match details with
            | [||] -> EmptyState "Pool not found" "The selected CUSIP is no longer available in the database."
            | _ ->
                let detail = details.[0]
                let row = detail.Row

                let interestPoints =
                    match detail.InterestTotals with
                    | [| series |] when series.Values.Length = detail.InterestAdjustmentDates.Length ->
                        Array.init series.Values.Length (fun index ->
                            {
                                Label = detail.InterestAdjustmentDates.[index]
                                Date = detail.InterestAdjustmentDates.[index]
                                Value = series.Values.[index]
                                Secondary = 0m
                                Count = 0
                            })
                    | _ -> [||]

                Html.div
                    [
                        prop.className "drawer-content"
                        prop.children
                            [
                                Html.div
                                    [
                                        prop.className "drawer-hero"
                                        prop.children
                                            [
                                                Html.div
                                                    [
                                                        prop.children
                                                            [
                                                                Html.div
                                                                    [
                                                                        prop.className "eyebrow"
                                                                        prop.text (sprintf "NHA MBS · %s" row.Cusip)
                                                                    ]
                                                                Html.h2
                                                                    [
                                                                        prop.className "drawer-title"
                                                                        prop.text row.PoolNumber
                                                                    ]
                                                                Html.p
                                                                    [
                                                                        prop.className "drawer-subtitle"
                                                                        prop.text row.IssuerName
                                                                    ]
                                                            ]
                                                    ]
                                                Html.a
                                                    [
                                                        prop.className "btn btn-small btn-primary"
                                                        prop.href row.SourceUrl
                                                        prop.target.blank
                                                        prop.rel "noopener"
                                                        prop.text "Open circular PDF"
                                                    ]
                                            ]
                                    ]

                                Html.div
                                    [
                                        prop.className "detail-metrics"
                                        prop.children
                                            [
                                                MiniLabel "Principal" (moneyFull row.Principal)
                                                MiniLabel
                                                    "Coupon"
                                                    (if String.IsNullOrWhiteSpace row.CouponDisplay then
                                                         rate row.CouponValue
                                                     else
                                                         row.CouponDisplay)
                                                MiniLabel "Loans" (number (decimal row.Loans))
                                                MiniLabel "WAC" (rate row.Wac)
                                                MiniLabel "WAM" (months row.Wam)
                                                MiniLabel "Maturity" (dateShort row.MaturityDate)
                                            ]
                                    ]

                                Html.div
                                    [
                                        prop.className "detail-grid"
                                        prop.children
                                            [
                                                Html.div
                                                    [
                                                        prop.className "detail-panel"
                                                        prop.children
                                                            [
                                                                Html.h3 [ prop.text "Pool profile" ]
                                                                Html.dl
                                                                    [
                                                                        prop.className "detail-list"
                                                                        prop.children
                                                                            [
                                                                                Html.dt [ prop.text "Rate type" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            titleCase row.RateType
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Pool type" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            poolTypeLabel
                                                                                                row.PoolType
                                                                                                row.PoolTypeDescription
                                                                                        )
                                                                                        prop.title (
                                                                                            PoolTypes.tooltipFor
                                                                                                row.PoolType
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Issue date" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            dateShort row.IssueDate
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Settlement date" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            dateShort row.SettlementDate
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "First payment" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            dateShort
                                                                                                detail.FirstPaymentDue
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Final payment" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            dateShort
                                                                                                detail.FinalPaymentDue
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Mortgage insurer" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            if
                                                                                                String
                                                                                                    .IsNullOrWhiteSpace
                                                                                                    detail
                                                                                                        .MortgageInsurer
                                                                                            then
                                                                                                "—"
                                                                                            else
                                                                                                detail.MortgageInsurer
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Custodian" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            if
                                                                                                String
                                                                                                    .IsNullOrWhiteSpace
                                                                                                    detail.Custodian
                                                                                            then
                                                                                                "—"
                                                                                            else
                                                                                                detail.Custodian
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Form version" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            if
                                                                                                String
                                                                                                    .IsNullOrWhiteSpace
                                                                                                    detail.FormVersion
                                                                                            then
                                                                                                "—"
                                                                                            else
                                                                                                detail.FormVersion
                                                                                        )
                                                                                    ]
                                                                            ]
                                                                    ]
                                                            ]
                                                    ]
                                                Html.div
                                                    [
                                                        prop.className "detail-panel"
                                                        prop.children
                                                            [
                                                                Html.h3 [ prop.text "Loan type profile" ]
                                                                percentageBars (
                                                                    [|
                                                                        "Homeowner", detail.HomeownerPercent
                                                                        "Multiple-family", detail.MultipleFamilyPercent
                                                                        "Social housing", detail.SocialHousingPercent
                                                                    |]
                                                                )
                                                                Html.h3
                                                                    [
                                                                        prop.className "detail-subhead"
                                                                        prop.text "Provincial allocation"
                                                                    ]
                                                                percentageBars (
                                                                    detail.Locations
                                                                    |> Array.map (fun location ->
                                                                        location.Province,
                                                                        location.PrincipalAmountPercent)
                                                                )
                                                            ]
                                                    ]
                                            ]
                                    ]

                                Html.div
                                    [
                                        prop.className "detail-grid"
                                        prop.children
                                            [
                                                Html.div
                                                    [
                                                        prop.className "detail-panel"
                                                        prop.children
                                                            [
                                                                Html.h3 [ prop.text "Insurer profile" ]
                                                                miniBars (
                                                                    detail.Insurers
                                                                    |> Array.map (fun insurer ->
                                                                        {
                                                                            Label = insurer.Name
                                                                            Value = insurer.DollarValue
                                                                            Count = 1
                                                                        })
                                                                )
                                                            ]
                                                    ]
                                                Html.div
                                                    [
                                                        prop.className "detail-panel"
                                                        prop.children
                                                            [
                                                                Html.h3 [ prop.text "Scheduled maturity" ]
                                                                Html.table
                                                                    [
                                                                        prop.className "data-table"
                                                                        prop.children
                                                                            [
                                                                                Html.thead
                                                                                    [
                                                                                        Html.tr
                                                                                            [
                                                                                                Html.th
                                                                                                    [
                                                                                                        prop.text
                                                                                                            "Bucket"
                                                                                                    ]
                                                                                                Html.th
                                                                                                    [
                                                                                                        prop.text
                                                                                                            "Face value"
                                                                                                    ]
                                                                                                Html.th
                                                                                                    [ prop.text "Rate" ]
                                                                                                Html.th
                                                                                                    [
                                                                                                        prop.text
                                                                                                            "Amortization"
                                                                                                    ]
                                                                                            ]
                                                                                    ]
                                                                                Html.tbody
                                                                                    [
                                                                                        for bucket in
                                                                                            detail.MaturityBuckets do
                                                                                            Html.tr
                                                                                                [
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text
                                                                                                                bucket
                                                                                                                    .Bucket
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                (if
                                                                                                                     bucket
                                                                                                                         .RemainingFaceValue
                                                                                                                         =
                                                                                                                         0m
                                                                                                                 then
                                                                                                                     "—"
                                                                                                                 else
                                                                                                                     moneyCompact
                                                                                                                         bucket
                                                                                                                             .RemainingFaceValue)
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                rate
                                                                                                                    bucket
                                                                                                                        .WeightedAverageRate
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                months
                                                                                                                    bucket
                                                                                                                        .WeightedAverageAmortization
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

                                if interestPoints.Length > 0 then
                                    Card
                                        "Interest adjustment profile"
                                        "Remaining face value by interest adjustment date"
                                        []
                                        [ areaChart interestPoints ]

                                Html.div
                                    [
                                        prop.className "detail-grid"
                                        prop.children
                                            [
                                                Html.div
                                                    [
                                                        prop.className "detail-panel"
                                                        prop.children
                                                            [
                                                                Html.h3 [ prop.text "Rate bands" ]
                                                                Html.table
                                                                    [
                                                                        prop.className "data-table"
                                                                        prop.children
                                                                            [
                                                                                Html.thead
                                                                                    [
                                                                                        Html.tr
                                                                                            [
                                                                                                Html.th
                                                                                                    [ prop.text "Band" ]
                                                                                                Html.th
                                                                                                    [
                                                                                                        prop.text
                                                                                                            "Total"
                                                                                                    ]
                                                                                                Html.th
                                                                                                    [
                                                                                                        prop.text
                                                                                                            "Share"
                                                                                                    ]
                                                                                            ]
                                                                                    ]
                                                                                Html.tbody
                                                                                    [
                                                                                        for band in detail.RateBands do
                                                                                            Html.tr
                                                                                                [
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text
                                                                                                                band
                                                                                                                    .RateBand
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                (if
                                                                                                                     band
                                                                                                                         .Total
                                                                                                                         =
                                                                                                                         0m
                                                                                                                 then
                                                                                                                     "—"
                                                                                                                 else
                                                                                                                     moneyCompact
                                                                                                                         band
                                                                                                                             .Total)
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                (if
                                                                                                                     band
                                                                                                                         .PercentOfTotal
                                                                                                                         =
                                                                                                                         0m
                                                                                                                 then
                                                                                                                     "—"
                                                                                                                 else
                                                                                                                     percent1
                                                                                                                         band
                                                                                                                             .PercentOfTotal)
                                                                                                            )
                                                                                                        ]
                                                                                                ]
                                                                                    ]
                                                                            ]
                                                                    ]
                                                            ]
                                                    ]
                                                Html.div
                                                    [
                                                        prop.className "detail-panel"
                                                        prop.children
                                                            [
                                                                Html.h3 [ prop.text "Prepayment provisions" ]
                                                                Html.dl
                                                                    [
                                                                        prop.className "detail-list"
                                                                        prop.children
                                                                            [
                                                                                Html.dt
                                                                                    [
                                                                                        prop.text
                                                                                            "Bona fide sale exemption"
                                                                                    ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            if
                                                                                                detail
                                                                                                    .PrepaymentBonaFideExempt
                                                                                            then
                                                                                                "Yes"
                                                                                            else
                                                                                                "No"
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Section A" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            if
                                                                                                String
                                                                                                    .IsNullOrWhiteSpace
                                                                                                    detail
                                                                                                        .PrepaymentSectionA
                                                                                            then
                                                                                                "—"
                                                                                            else
                                                                                                detail
                                                                                                    .PrepaymentSectionA
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Section B" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            if
                                                                                                String
                                                                                                    .IsNullOrWhiteSpace
                                                                                                    detail
                                                                                                        .PrepaymentSectionB
                                                                                            then
                                                                                                "—"
                                                                                            else
                                                                                                detail
                                                                                                    .PrepaymentSectionB
                                                                                        )
                                                                                    ]
                                                                                Html.dt
                                                                                    [ prop.text "Other disclosures" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            if
                                                                                                String
                                                                                                    .IsNullOrWhiteSpace
                                                                                                    detail
                                                                                                        .PrepaymentOther
                                                                                            then
                                                                                                "—"
                                                                                            else
                                                                                                detail.PrepaymentOther
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Source file" ]
                                                                                Html.dd [ prop.text detail.SourceFile ]
                                                                                Html.dt [ prop.text "Ingested" ]
                                                                                Html.dd [ prop.text detail.IngestedAt ]
                                                                            ]
                                                                    ]
                                                            ]
                                                    ]
                                            ]
                                    ]
                            ]
                    ]

    Html.div
        [
            prop.className "drawer-backdrop"
            prop.onClick (fun _ -> close ())
            prop.children
                [
                    Html.aside
                        [
                            prop.className "drawer"
                            prop.onClick (fun event -> event.stopPropagation ())
                            prop.children
                                [
                                    Html.button
                                        [
                                            prop.className "drawer-close"
                                            prop.onClick (fun _ -> close ())
                                            prop.text "×"
                                        ]
                                    body
                                ]
                        ]
                ]
        ]

[<ReactComponent>]
let PoolsPage (props: PoolsProps) =
    let analyticsView =
        LoadableBlock "Loading pool analytics" props.Analytics (fun analytics ->
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
                                                "Filtered principal"
                                                (moneyCompact analytics.TotalPrincipal)
                                                (sprintf "%s matching pools" (number (decimal analytics.PoolCount)))
                                                "#18a999"
                                            KpiCard
                                                "Mortgage loans"
                                                (number (decimal analytics.LoanCount))
                                                (sprintf "%s issuers" (number (decimal analytics.IssuerCount)))
                                                "#4f8ef7"
                                            KpiCard
                                                "Average coupon"
                                                (rate analytics.AverageCoupon)
                                                (sprintf "%s WAM" (months analytics.AverageWam))
                                                "#d8a84e"
                                            KpiCard
                                                "Newest issue"
                                                (dateShort analytics.NewestIssueDate)
                                                "Latest issue date in selection"
                                                "#7c6ee6"
                                            KpiCard
                                                "Provinces"
                                                (string analytics.ByProvince.Length)
                                                "Represented in selection"
                                                "#e2725b"
                                        ]
                                ]

                            Html.div
                                [
                                    prop.className "pool-chart-grid"
                                    prop.children
                                        [
                                            Card
                                                "Monthly issuance"
                                                "Filtered aggregate principal by issue month"
                                                []
                                                [ areaChart analytics.Monthly ]
                                            Card
                                                "Rate type mix"
                                                "Principal by coupon structure"
                                                []
                                                [
                                                    donut (
                                                        analytics.ByRateType
                                                        |> Array.map (fun point -> point.Label, point.Value)
                                                    )
                                                ]
                                            Card
                                                "Provincial allocation"
                                                "Top provinces by allocated principal"
                                                []
                                                [
                                                    horizontalBars (
                                                        analytics.ByProvince
                                                        |> Array.truncate 9
                                                        |> Array.map (fun province ->
                                                            province.Province,
                                                            province.Principal,
                                                            percent1 province.Share)
                                                    )
                                                ]
                                            Card
                                                "Pool type mix"
                                                "Principal by CMHC pool type · hover a bar for a definition"
                                                []
                                                [ miniBarsWithTooltip poolTypeChartTooltip analytics.ByPoolType ]
                                        ]
                                ]
                        ]
                ])

    let pageView =
        LoadableBlock "Loading NHA MBS pools" props.Page (fun page ->
            let exportRows () =
                let headers =
                    [|
                        "Pool"
                        "CUSIP"
                        "Issuer"
                        "Rate type"
                        "Pool type"
                        "Coupon"
                        "Principal"
                        "Issued"
                        "Matures"
                        "Loans"
                        "WAC"
                        "WAM"
                        "Provinces"
                        "Insurers"
                    |]

                let rows =
                    page.Rows
                    |> Array.map (fun row ->
                        [|
                            row.PoolNumber
                            row.Cusip
                            row.IssuerName
                            row.RateType
                            row.PoolTypeDescription
                            row.CouponDisplay
                            row.Principal.ToString("N2")
                            row.IssueDate
                            row.MaturityDate
                            string row.Loans
                            (if row.Wac = 0m then "" else row.Wac.ToString("N3"))
                            (if row.Wam = 0m then "" else row.Wam.ToString("N1"))
                            row.Provinces
                            row.Insurers
                        |])

                AgGrid.exportCsv "nha-mbs-pools.csv" headers rows

            Html.div
                [
                    prop.children
                        [
                            if page.Rows.Length > 0 then
                                Html.div
                                    [
                                        prop.className "pool-chart-grid"
                                        prop.children
                                            [
                                                Card
                                                    "Loaded rows · issuer mix"
                                                    "Principal by issuer across the rows currently loaded in the grid"
                                                    []
                                                    [ horizontalBars (issuerChartItems page.Rows) ]
                                                Card
                                                    "Loaded rows · coupon bands"
                                                    "Principal by 25 basis point coupon band across the loaded rows"
                                                    []
                                                    [ verticalBarsWith moneyCompact (couponChartPoints page.Rows) ]
                                            ]
                                    ]

                            Html.div
                                [
                                    prop.className "grid-toolbar"
                                    prop.children
                                        [
                                            Html.div
                                                [
                                                    prop.children
                                                        [
                                                            Html.strong
                                                                [
                                                                    prop.text (
                                                                        sprintf
                                                                            "%s pools"
                                                                            (number (decimal page.Total))
                                                                    )
                                                                ]
                                                            Html.span
                                                                [
                                                                    prop.className "muted"
                                                                    prop.text (
                                                                        sprintf
                                                                            " · showing %s loaded rows"
                                                                            (number (decimal page.Rows.Length))
                                                                    )
                                                                ]
                                                            if page.Capped then
                                                                Html.span
                                                                    [
                                                                        prop.className "cap-note"
                                                                        prop.text
                                                                            " · results capped at 20,000; narrow the filters for a complete export"
                                                                    ]
                                                        ]
                                                ]
                                            Html.div
                                                [
                                                    prop.className "toolbar-actions"
                                                    prop.children
                                                        [
                                                            Html.button
                                                                [
                                                                    prop.className "btn btn-small btn-ghost"
                                                                    prop.onClick (fun _ -> props.OnRefresh())
                                                                    prop.text "↻ Refresh"
                                                                ]
                                                            Html.button
                                                                [
                                                                    prop.className "btn btn-small btn-secondary"
                                                                    prop.onClick (fun _ -> exportRows ())
                                                                    prop.text "Export CSV"
                                                                ]
                                                        ]
                                                ]
                                        ]
                                ]

                            AgGrid.AgGrid
                                {
                                    Columns = poolColumns
                                    Rows = poolRows page.Rows
                                    Height = "640px"
                                    OnRowClicked = (fun data -> data?poolId |> string |> props.OnSelectPool)
                                }

                            Html.p
                                [
                                    prop.className "grid-hint"
                                    prop.text
                                        "Click any row to open the pool detail drawer. Use the column headers to sort or filter within the loaded result set."
                                ]
                        ]
                ])

    Html.div
        [
            prop.className "page"
            prop.children
                [
                    PageHeader
                        "NHA mortgage-backed securities"
                        "Pool explorer"
                        "Filter 50,000+ CMHC information circulars by issuer, rate structure, province, insurer and maturity."
                        [
                            Html.button
                                [
                                    prop.className "btn btn-ghost"
                                    prop.onClick (fun _ -> props.OnRefresh())
                                    prop.children [ Html.span [ prop.text "↻" ]; Html.span [ prop.text "Refresh" ] ]
                                ]
                        ]

                    LoadableBlock "Loading filter options" props.Filters (fun options -> filterPanel props options)

                    analyticsView
                    poolTypeGuide ()

                    pageView

                    if props.SelectedPool.IsSome then
                        detailDrawer props
                ]
        ]

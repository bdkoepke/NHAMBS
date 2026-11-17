module NHAMBS.Client.Pools

open System
open System.Globalization
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
        OnStatusChange: string -> unit
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
                                                    (dateShortOption options.MinIssueDate)
                                                    (dateShortOption options.MaxIssueDate)
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

let private poolRow (row: MbsPoolRow) =
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
        ]

let private poolRows (rows: MbsPoolRow[]) = rows |> Array.map poolRow

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

type private PoolSortValue =
    | PoolText of string
    | PoolNumber of decimal

let private poolSortValue (field: string) (row: MbsPoolRow) =
    match field with
    | "poolNumber" -> PoolText row.PoolNumber
    | "cusip" -> PoolText row.Cusip
    | "issuer" -> PoolText row.IssuerName
    | "rateType" -> PoolText(titleCase row.RateType)
    | "poolType" -> PoolText(poolTypeLabel row.PoolType row.PoolTypeDescription)
    | "coupon" -> PoolText row.CouponDisplay
    | "principal" -> PoolNumber row.Principal
    | "issueDate" -> PoolText(textOrEmpty row.IssueDate)
    | "maturityDate" -> PoolText(textOrEmpty row.MaturityDate)
    | "loans" -> PoolNumber(decimal row.Loans)
    | "wac" -> PoolNumber row.Wac
    | "wam" -> PoolNumber row.Wam
    | "provinces" -> PoolText row.Provinces
    | "insurers" -> PoolText row.Insurers
    | _ -> PoolText ""

let private comparePoolSortValues left right =
    match left, right with
    | PoolNumber x, PoolNumber y -> compare x y
    | PoolText x, PoolText y -> String.Compare(x, y, StringComparison.OrdinalIgnoreCase)
    | PoolText x, PoolNumber y -> String.Compare(x, string y, StringComparison.OrdinalIgnoreCase)
    | PoolNumber x, PoolText y -> String.Compare(string x, y, StringComparison.OrdinalIgnoreCase)

let private sortModelEntries (sortModel: obj) =
    if isNull sortModel then [||] else unbox<obj[]> sortModel

let private comparePoolRows (sortModel: obj) (left: MbsPoolRow) (right: MbsPoolRow) =
    let entries = sortModelEntries sortModel

    let rec loop index =
        if index >= entries.Length then
            0
        else
            let entry = entries.[index]
            let field = string entry?colId
            let direction = string entry?sort

            let comparison =
                comparePoolSortValues (poolSortValue field left) (poolSortValue field right)

            if comparison = 0 then loop (index + 1)
            elif direction = "desc" then -comparison
            else comparison

    loop 0

let private sortPoolRows (sortModel: obj) (rows: MbsPoolRow[]) =
    if (sortModelEntries sortModel).Length = 0 then
        rows
    else
        rows |> Array.sortWith (comparePoolRows sortModel)

let private groupSortDirection (sortModel: obj) (field: string) =
    sortModelEntries sortModel
    |> Array.tryFind (fun entry -> string entry?colId = field)
    |> Option.map (fun entry -> string entry?sort)

let private tryDecimalValue (value: obj) =
    if isNull value then
        None
    elif value :? string then
        match Decimal.TryParse(unbox<string> value, NumberStyles.Any, CultureInfo.InvariantCulture) with
        | true, parsed -> Some parsed
        | _ -> None
    else
        Some(unbox<decimal> value)

let private tryDateValue (value: obj) =
    if isNull value then
        None
    else
        match DateTime.TryParse(string value, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, parsed -> Some parsed.Date
        | _ -> None

let private blankText (value: obj) =
    isNull value || String.IsNullOrWhiteSpace(string value)

let private matchesTextFilter (cellValue: obj) (filter: obj) =
    let cell = if isNull cellValue then "" else string cellValue
    let cellLower = cell.ToLowerInvariant()
    let filterValue = filter?filter

    let filterLower =
        if isNull filterValue then
            ""
        else
            (string filterValue).ToLowerInvariant()

    let operator = string (filter?``type``)

    match operator with
    | "equals" -> cellLower = filterLower
    | "notEqual" -> cellLower <> filterLower
    | "contains" -> cellLower.Contains filterLower
    | "notContains" -> not (cellLower.Contains filterLower)
    | "startsWith" -> cellLower.StartsWith filterLower
    | "endsWith" -> cellLower.EndsWith filterLower
    | "blank" -> blankText cellValue
    | "notBlank" -> not (blankText cellValue)
    | _ -> true

let private matchesNumberFilter (cellValue: obj) (filter: obj) =
    let cell = tryDecimalValue cellValue
    let first = tryDecimalValue (filter?filter)
    let second = tryDecimalValue (filter?filterTo)
    let operator = string (filter?``type``)

    match operator with
    | "blank" -> cell.IsNone
    | "notBlank" -> cell.IsSome
    | _ ->
        match cell, first with
        | Some value, Some target ->
            match operator with
            | "equals" -> value = target
            | "notEqual" -> value <> target
            | "lessThan" -> value < target
            | "lessThanOrEqual" -> value <= target
            | "greaterThan" -> value > target
            | "greaterThanOrEqual" -> value >= target

            | "inRange" -> second |> Option.exists (fun upper -> value >= target && value <= upper)
            | _ -> true
        | _ -> false

let private matchesDateFilter (cellValue: obj) (filter: obj) =
    let cell = tryDateValue cellValue
    let first = tryDateValue (filter?dateFrom)
    let second = tryDateValue (filter?dateTo)
    let operator = string (filter?``type``)

    match operator with
    | "blank" -> cell.IsNone
    | "notBlank" -> cell.IsSome
    | _ ->
        match cell, first with
        | Some value, Some from ->
            match operator with
            | "equals" -> value = from
            | "notEqual" -> value <> from
            | "lessThan" -> value < from
            | "lessThanOrEqual" -> value <= from
            | "greaterThan" -> value > from
            | "greaterThanOrEqual" -> value >= from
            | "inRange" -> second |> Option.exists (fun upper -> value >= from && value <= upper)
            | _ -> true
        | _ -> false

let rec private matchesFilterCell (filterType: string) (cellValue: obj) (filter: obj) =
    let conditions = filter?conditions

    if isNull conditions then
        match filterType with
        | "number" -> matchesNumberFilter cellValue filter
        | "date" -> matchesDateFilter cellValue filter
        | _ -> matchesTextFilter cellValue filter
    else
        let results =
            unbox<obj[]> conditions
            |> Array.map (fun condition ->
                let conditionType = string condition?filterType

                let actualType =
                    if String.IsNullOrWhiteSpace conditionType then
                        filterType
                    else
                        conditionType

                matchesFilterCell actualType cellValue condition)

        if string filter?operator = "OR" then
            Array.exists id results
        else
            Array.forall id results

let private filterCellValue (field: string) (row: MbsPoolRow) =
    match field with
    | "poolNumber" -> Some(box row.PoolNumber)
    | "cusip" -> Some(box row.Cusip)
    | "issuer" -> Some(box row.IssuerName)
    | "rateType" -> Some(box (titleCase row.RateType))
    | "poolType" -> Some(box (poolTypeLabel row.PoolType row.PoolTypeDescription))
    | "coupon" -> Some(box row.CouponDisplay)
    | "principal" -> Some(box row.Principal)
    | "issueDate" -> Some(box row.IssueDate)
    | "maturityDate" -> Some(box row.MaturityDate)
    | "loans" -> Some(box (decimal row.Loans))
    | "wac" -> Some(box row.Wac)
    | "wam" -> Some(box row.Wam)
    | "provinces" -> Some(box row.Provinces)
    | "insurers" -> Some(box row.Insurers)
    | "sourceUrl" -> Some(box row.SourceUrl)
    | _ -> None

let private matchesFilterModel (filterModel: obj) (row: MbsPoolRow) =
    if isNull filterModel then
        true
    else
        Interop.filterEntries filterModel
        |> Array.forall (fun entry ->
            let field = string entry?colId

            match filterCellValue field row with
            | None -> true
            | Some cell ->
                let filter = entry?filter
                let filterType = string filter?filterType
                matchesFilterCell filterType cell filter)

let private matchesQuickFilter (text: string) (row: MbsPoolRow) =
    let needle = text.Trim().ToLowerInvariant()

    if needle = "" then
        true
    else
        [|
            row.PoolNumber
            row.Cusip
            row.IssuerName
            titleCase row.RateType
            row.PoolTypeDescription
            row.CouponDisplay
            textOrEmpty row.IssueDate
            textOrEmpty row.MaturityDate
            row.Provinces
            row.Insurers
            string row.Principal
            string row.Loans
            string row.Wac
            string row.Wam
        |]
        |> Array.exists (fun value -> value.ToLowerInvariant().Contains needle)

let private poolGroupFields =
    [|
        "issuer", "Issuer"
        "rateType", "Rate type"
        "poolType", "Pool type"
        "maturityYear", "Maturity year"
        "provinces", "Province"
        "insurers", "Insurer"
    |]

let private poolPivotFields =
    [|
        "rateType", "Rate type"
        "poolType", "Pool type"
        "maturityYear", "Maturity year"
    |]

let private poolPivotFieldLabel (field: string) =
    poolPivotFields
    |> Array.tryFind (fun (key, _) -> key = field)
    |> Option.map snd
    |> Option.defaultValue field

let private poolGroupFieldLabel (field: string) =
    poolGroupFields
    |> Array.tryFind (fun (key, _) -> key = field)
    |> Option.map snd
    |> Option.defaultValue field

let private poolGroupValue (field: string) (row: MbsPoolRow) =
    match field with
    | "issuer" -> row.IssuerName
    | "rateType" -> titleCase row.RateType
    | "poolType" -> poolTypeLabel row.PoolType row.PoolTypeDescription
    | "maturityYear" ->
        match row.MaturityDate with
        | Some value when value.Length >= 4 -> value.Substring(0, 4)
        | _ -> "Unknown"
    | "provinces" -> row.Provinces
    | "insurers" -> row.Insurers
    | _ -> ""

let private collectGroupKeys (prefix: string) (fields: string list) (rows: MbsPoolRow[]) =
    let rec collect level prefix remaining rows =
        match remaining with
        | [] -> Set.empty
        | field :: rest ->
            let groups =
                System.Collections.Generic.Dictionary<string, ResizeArray<MbsPoolRow>>()

            for row in rows do
                let value = poolGroupValue field row

                match groups.TryGetValue value with
                | true, items -> items.Add row
                | _ ->
                    let items = ResizeArray<MbsPoolRow>()
                    items.Add row
                    groups.[value] <- items

            let keys = ResizeArray<string>()

            for pair in groups do
                let key = sprintf "%s|%d|%s=%s" prefix level field pair.Key
                keys.Add key

                for child in collect (level + 1) key rest (pair.Value.ToArray()) do
                    keys.Add child

            Set.ofSeq keys

    collect 0 prefix fields rows

let private groupPoolRow (level: int) (key: string) (label: string) (expanded: bool) (rows: MbsPoolRow[]) =
    let totalPrincipal = rows |> Array.sumBy (fun row -> row.Principal)

    let weighted (value: MbsPoolRow -> decimal) =
        if totalPrincipal = 0m then
            0m
        else
            (rows |> Array.sumBy (fun row -> value row * row.Principal)) / totalPrincipal

    let averageCoupon = weighted (fun row -> row.CouponValue)

    createObj
        [
            "poolId" ==> ""
            "poolNumber" ==> ""
            "cusip" ==> ""
            "issuer" ==> ""
            "rateType" ==> ""
            "poolType" ==> ""
            "poolTypeTooltip" ==> ""
            "coupon"
            ==> (if averageCoupon = 0m then
                     ""
                 else
                     sprintf "%.3f%% avg" averageCoupon)
            "principal" ==> totalPrincipal
            "issueDate" ==> ""
            "maturityDate" ==> ""
            "loans" ==> (rows |> Array.sumBy (fun row -> row.Loans))
            "wac" ==> weighted (fun row -> row.Wac)
            "wam" ==> weighted (fun row -> row.Wam)
            "provinces" ==> ""
            "insurers" ==> ""
            "sourceUrl" ==> ""
            "groupLabel" ==> label
            "groupLevel" ==> level
            "groupCount" ==> rows.Length
            "groupExpanded" ==> expanded
            "groupExpandable" ==> true
            "groupKey" ==> key
            "isGroup" ==> true
            "rowClass" ==> "grid-group-row"
            "__filterValues" ==> poolRow rows.[0]
        ]

let private groupedPoolRows (fields: string list) (expanded: Set<string>) (sortModel: obj) (rows: MbsPoolRow[]) =
    let rec emit (level: int) (prefix: string) (remaining: string list) (rows: MbsPoolRow[]) : obj[] =
        match remaining with
        | [] -> poolRows rows
        | field :: rest ->
            let groups =
                rows
                |> Array.groupBy (fun row -> poolGroupValue field row)
                |> fun groups ->
                    match groupSortDirection sortModel field with
                    | Some "desc" -> groups |> Array.sortByDescending fst
                    | _ -> groups |> Array.sortBy fst

            groups
            |> Array.collect (fun (value, groupRows) ->
                let label = if String.IsNullOrWhiteSpace value then "(Blank)" else value

                let key = sprintf "%s|%d|%s=%s" prefix level field value
                let isExpanded = expanded.Contains key
                let header = groupPoolRow level key label isExpanded groupRows

                if isExpanded then
                    Array.append [| header |] (emit (level + 1) key rest groupRows)
                else
                    [| header |])

    emit 0 "group" fields rows

let private poolPivotValues (pivotField: string) (rows: MbsPoolRow[]) =
    rows |> Array.map (poolGroupValue pivotField) |> Array.distinct |> Array.sort

let private pivotGroupRow
    (level: int)
    (key: string)
    (label: string)
    (expanded: bool)
    (expandable: bool)
    (pivotField: string)
    (pivotValues: string[])
    (rows: MbsPoolRow[])
    =
    let totalPrincipal = rows |> Array.sumBy (fun row -> row.Principal)

    let weighted (value: MbsPoolRow -> decimal) =
        if totalPrincipal = 0m then
            0m
        else
            (rows |> Array.sumBy (fun row -> value row * row.Principal)) / totalPrincipal

    let averageCoupon = weighted (fun row -> row.CouponValue)

    let pivotTotals = Array.zeroCreate<decimal> pivotValues.Length

    let pivotIndex =
        dict [ for index, value in Array.indexed pivotValues -> value, index ]

    for row in rows do
        match pivotIndex.TryGetValue(poolGroupValue pivotField row) with
        | true, index -> pivotTotals.[index] <- pivotTotals.[index] + row.Principal
        | _ -> ()

    let fields =
        ResizeArray<string * obj>(
            [
                "poolId" ==> ""
                "poolNumber" ==> ""
                "cusip" ==> ""
                "issuer" ==> ""
                "rateType" ==> ""
                "poolType" ==> ""
                "poolTypeTooltip" ==> ""
                "coupon"
                ==> (if averageCoupon = 0m then
                         ""
                     else
                         sprintf "%.3f%% avg" averageCoupon)
                "principal" ==> totalPrincipal
                "issueDate" ==> ""
                "maturityDate" ==> ""
                "loans" ==> (rows |> Array.sumBy (fun row -> row.Loans))
                "wac" ==> weighted (fun row -> row.Wac)
                "wam" ==> weighted (fun row -> row.Wam)
                "provinces" ==> ""
                "insurers" ==> ""
                "sourceUrl" ==> ""
                "groupLabel" ==> label
                "groupLevel" ==> level
                "groupCount" ==> rows.Length
                "groupExpanded" ==> expanded
                "groupExpandable" ==> expandable
                "groupKey" ==> key
                "isGroup" ==> true
                "rowClass"
                ==> (if expandable then
                         "grid-group-row"
                     else
                         "grid-group-row grid-group-row-terminal")
                "pivotTotal" ==> totalPrincipal
                "__filterValues" ==> poolRow rows.[0]
            ]
        )

    for index, total in Array.indexed pivotTotals do
        fields.Add(sprintf "pivot_%d" index ==> total)

    createObj fields

let private pivotedPoolRows
    (fields: string list)
    (pivotField: string)
    (expanded: Set<string>)
    (sortModel: obj)
    (rows: MbsPoolRow[])
    =
    let pivotValues = poolPivotValues pivotField rows

    let rec emit (level: int) (prefix: string) (remaining: string list) (rows: MbsPoolRow[]) : obj[] =
        match remaining with
        | [] -> [||]
        | field :: rest ->
            let groups =
                rows
                |> Array.groupBy (fun row -> poolGroupValue field row)
                |> fun groups ->
                    match groupSortDirection sortModel field with
                    | Some "desc" -> groups |> Array.sortByDescending fst
                    | _ -> groups |> Array.sortBy fst

            groups
            |> Array.collect (fun (value, groupRows) ->
                let label = if String.IsNullOrWhiteSpace value then "(Blank)" else value

                let key = sprintf "%s|%d|%s=%s" prefix level field value
                let isExpanded = expanded.Contains key
                let expandable = not rest.IsEmpty

                let header =
                    pivotGroupRow level key label isExpanded expandable pivotField pivotValues groupRows

                if isExpanded && expandable then
                    Array.append [| header |] (emit (level + 1) key rest groupRows)
                else
                    [| header |])

    emit 0 "pivot" fields rows

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
                                                MiniLabel "Maturity" (dateShortOption row.MaturityDate)
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
                                                                                            dateShortOption
                                                                                                row.IssueDate
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Settlement date" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            dateShortOption
                                                                                                row.SettlementDate
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "First payment" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            dateShortOption
                                                                                                detail.FirstPaymentDue
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Final payment" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            dateShortOption
                                                                                                detail.FinalPaymentDue
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Mortgage insurer" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            optionText
                                                                                                detail.MortgageInsurer
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Custodian" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            optionText detail.Custodian
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Form version" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            optionText
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
                                                                                            optionText
                                                                                                detail
                                                                                                    .PrepaymentSectionA
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Section B" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            optionText
                                                                                                detail
                                                                                                    .PrepaymentSectionB
                                                                                        )
                                                                                    ]
                                                                                Html.dt
                                                                                    [ prop.text "Other disclosures" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            optionText
                                                                                                detail.PrepaymentOther
                                                                                        )
                                                                                    ]
                                                                                Html.dt [ prop.text "Source file" ]
                                                                                Html.dd
                                                                                    [
                                                                                        prop.text (
                                                                                            optionText detail.SourceFile
                                                                                        )
                                                                                    ]
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

type private PoolGridProps =
    {
        Rows: MbsPoolRow[]
        GroupFields: string list
        ExpandedGroups: Set<string>
        PivotField: string option
        SortModel: obj
        FilterModel: obj
        QuickFilter: string
        OnGroupFieldsChange: string list -> unit
        OnToggleGroup: string -> unit
        OnPivotFieldChange: string option -> unit
        OnExpandedGroupsChange: Set<string> -> unit
        OnSortModelChanged: obj -> unit
        OnFilterModelChanged: obj -> unit
        OnQuickFilterChanged: string -> unit
        OnSelectPool: string -> unit
    }

[<ReactComponent>]
let private PoolDataGrid (props: PoolGridProps) =
    let groupFields = props.GroupFields
    let expandedGroups = props.ExpandedGroups
    let pivotField = props.PivotField
    let (dragOver, setDragOver) = React.useState false

    let processedRows =
        React.useMemo (
            (fun () ->
                if groupFields.IsEmpty then
                    props.Rows
                else
                    props.Rows
                    |> Array.filter (fun row ->
                        matchesQuickFilter props.QuickFilter row
                        && matchesFilterModel props.FilterModel row)
                    |> sortPoolRows props.SortModel),
            [|
                box props.Rows
                box groupFields
                box props.SortModel
                box props.FilterModel
                box props.QuickFilter
            |]
        )

    let pivotValues =
        React.useMemo (
            (fun () ->
                match pivotField with
                | Some field -> poolPivotValues field processedRows
                | None -> [||]),
            [| box processedRows; box pivotField |]
        )

    let gridRows =
        React.useMemo (
            (fun () ->
                if groupFields.IsEmpty then
                    poolRows processedRows
                elif pivotField.IsSome then
                    pivotedPoolRows groupFields pivotField.Value expandedGroups props.SortModel processedRows
                else
                    groupedPoolRows groupFields expandedGroups props.SortModel processedRows),
            [|
                box processedRows
                box groupFields
                box expandedGroups
                box pivotField
                box props.SortModel
            |]
        )

    let activeColumns =
        React.useMemo (
            (fun () ->
                if groupFields.IsEmpty then
                    poolColumns
                elif pivotField.IsSome then
                    let pivotColumns =
                        pivotValues
                        |> Array.mapi (fun index value ->
                            AgGrid.moneyColumn (sprintf "pivot_%d" index) value 140 |> AgGrid.plainColumn)

                    let totalColumn = AgGrid.moneyColumn "pivotTotal" "Total" 150 |> AgGrid.plainColumn

                    Array.concat [ [| AgGrid.groupColumn |]; pivotColumns; [| totalColumn |] ]
                else
                    let groupedColumns = poolColumns |> Array.map AgGrid.groupedColumn

                    Array.append [| AgGrid.groupColumn |] groupedColumns),
            [| box groupFields; box pivotField; box pivotValues |]
        )

    let availableFields =
        if groupFields.Length >= 3 then
            [||]
        else
            poolGroupFields
            |> Array.filter (fun (field, _) -> not (List.contains field groupFields) && pivotField <> Some field)

    let availablePivotFields =
        poolPivotFields
        |> Array.filter (fun (field, _) -> not (List.contains field groupFields))

    let addGroupField (field: string) =
        if groupFields.Length < 3 && not (List.contains field groupFields) then
            props.OnGroupFieldsChange(groupFields @ [ field ])

    let removeGroupField (field: string) =
        props.OnGroupFieldsChange(groupFields |> List.filter (fun item -> item <> field))

    let expandAllGroups () =
        let prefix = if pivotField.IsSome then "pivot" else "group"
        props.OnExpandedGroupsChange(collectGroupKeys prefix groupFields processedRows)

    let collapseAllGroups () = props.OnExpandedGroupsChange Set.empty

    let onRowClicked (data: obj) =
        if isNull data then
            ()
        elif not (isNull data?isGroup) && unbox<bool> data?isGroup then
            let expandable =
                let value = data?groupExpandable
                if isNull value then true else unbox<bool> value

            if expandable then
                props.OnToggleGroup(string data?groupKey)
        else
            string data?poolId |> props.OnSelectPool

    let handleDragOver (event: DragEvent) =
        event.preventDefault ()
        setDragOver true

    let handleDragLeave (_: DragEvent) = setDragOver false

    let handleDrop (event: DragEvent) =
        event.preventDefault ()
        setDragOver false
        let data = event.dataTransfer

        if not (isNull data) then
            let field = data.getData ("text/plain")

            if poolGroupFields |> Array.exists (fun (key, _) -> key = field) then
                addGroupField field

    Html.div
        [
            prop.children
                [
                    Html.div
                        [
                            prop.className (
                                if dragOver then
                                    "grid-group-bar drag-over"
                                else
                                    "grid-group-bar"
                            )
                            prop.onDragOver handleDragOver
                            prop.onDragLeave handleDragLeave
                            prop.onDrop handleDrop
                            prop.children
                                [
                                    Html.span [ prop.className "grid-group-bar-label"; prop.text "Group by" ]
                                    for field in groupFields do
                                        Html.span
                                            [
                                                prop.className "grid-group-chip"
                                                prop.children
                                                    [
                                                        Html.span [ prop.text (poolGroupFieldLabel field) ]
                                                        Html.button
                                                            [
                                                                prop.className "grid-group-chip-remove"
                                                                prop.title "Remove grouping"
                                                                prop.onClick (fun _ -> removeGroupField field)
                                                                prop.text "×"
                                                            ]
                                                    ]
                                            ]
                                    if availableFields.Length > 0 then
                                        Html.select
                                            [
                                                prop.className "grid-group-select"
                                                prop.value ""
                                                prop.onChange (fun (event: Event) ->
                                                    let field = string event.target?value

                                                    if not (String.IsNullOrWhiteSpace field) then
                                                        addGroupField field)
                                                prop.children
                                                    [
                                                        Html.option
                                                            [
                                                                prop.value ""
                                                                prop.text (
                                                                    if groupFields.IsEmpty then
                                                                        "Add grouping…"
                                                                    else
                                                                        "Add level…"
                                                                )
                                                            ]
                                                        for field, label in availableFields do
                                                            Html.option [ prop.value field; prop.text label ]
                                                    ]
                                            ]
                                    if groupFields.IsEmpty then
                                        Html.span
                                            [
                                                prop.className "grid-group-drop-hint"
                                                prop.text "or drag a column header here"
                                            ]
                                    if not groupFields.IsEmpty && availablePivotFields.Length > 0 then
                                        Html.span [ prop.className "grid-group-bar-label"; prop.text "Pivot by" ]

                                        Html.select
                                            [
                                                prop.className "grid-group-select"
                                                prop.value (defaultArg pivotField "")
                                                prop.onChange (fun (event: Event) ->
                                                    let field = string event.target?value

                                                    props.OnPivotFieldChange(
                                                        if String.IsNullOrWhiteSpace field then None else Some field
                                                    ))
                                                prop.children
                                                    [
                                                        Html.option [ prop.value ""; prop.text "No pivot" ]
                                                        for field, label in availablePivotFields do
                                                            Html.option [ prop.value field; prop.text label ]
                                                    ]
                                            ]
                                    if not groupFields.IsEmpty then
                                        Html.div
                                            [
                                                prop.className "grid-group-actions"
                                                prop.children
                                                    [
                                                        Html.button
                                                            [
                                                                prop.className "btn btn-small btn-ghost"
                                                                prop.onClick (fun _ -> expandAllGroups ())
                                                                prop.text "Expand all"
                                                            ]
                                                        Html.button
                                                            [
                                                                prop.className "btn btn-small btn-ghost"
                                                                prop.onClick (fun _ -> collapseAllGroups ())
                                                                prop.text "Collapse all"
                                                            ]
                                                    ]
                                            ]
                                    if not groupFields.IsEmpty then
                                        Html.button
                                            [
                                                prop.className "btn btn-small btn-ghost"
                                                prop.onClick (fun _ -> props.OnGroupFieldsChange [])
                                                prop.text "Clear grouping"
                                            ]
                                    if not groupFields.IsEmpty then
                                        Html.span
                                            [
                                                prop.className "grid-group-hint"
                                                prop.text (
                                                    match pivotField with
                                                    | Some field ->
                                                        sprintf
                                                            "Pivot mode: principal grouped by %s. Click a group row to expand deeper levels."
                                                            (poolPivotFieldLabel field)
                                                    | None ->
                                                        "Click a group row to expand or collapse it. Drag column headers here to add another level."
                                                )
                                            ]
                                ]
                        ]
                    AgGrid.AgGrid
                        {
                            Columns = activeColumns
                            Rows = gridRows
                            Height = "640px"
                            Pagination = groupFields.IsEmpty
                            InitialSortModel = props.SortModel
                            InitialFilterModel = props.FilterModel
                            ApplyQuickFilterInternally = groupFields.IsEmpty
                            HeaderGroupFields = poolGroupFields |> Array.map fst
                            OnSortModelChanged = props.OnSortModelChanged
                            OnFilterModelChanged = props.OnFilterModelChanged
                            OnQuickFilterChanged = props.OnQuickFilterChanged
                            OnRowClicked = onRowClicked
                        }
                ]
        ]

let private statusToggle (props: PoolsProps) =
    let optionButton (value: string) (label: string) =
        Html.button
            [
                prop.className (
                    if props.Filter.Status = value then
                        "status-filter-option active"
                    else
                        "status-filter-option"
                )
                prop.onClick (fun _ -> props.OnStatusChange value)
                prop.text label
            ]

    Html.div
        [
            prop.className "status-filter"
            prop.children
                [
                    Html.span [ prop.className "status-filter-label"; prop.text "Status" ]
                    optionButton "" "All"
                    optionButton "active" "Active"
                    optionButton "inactive" "Inactive"
                ]
        ]

[<ReactComponent>]
let PoolsPage (props: PoolsProps) =
    let (groupFields, setGroupFields) = React.useStateWithUpdater<string list> []

    let (expandedGroups, setExpandedGroups) =
        React.useStateWithUpdater<Set<string>> Set.empty

    let (pivotField, setPivotField) = React.useStateWithUpdater<string option> None

    let (sortModel, setSortModel) = React.useStateWithUpdater<obj>(box [||])
    let (filterModel, setFilterModel) = React.useStateWithUpdater<obj>(null: obj)
    let (quickFilter, setQuickFilter) = React.useState ""

    let updateGroupFields (fields: string list) =
        setGroupFields (fun _ -> fields)
        setExpandedGroups (fun _ -> Set.empty)

        setPivotField (fun pivot ->
            match pivot with
            | Some field when fields.IsEmpty || List.contains field fields -> None
            | other -> other)

    let toggleGroup (key: string) =
        setExpandedGroups (fun expanded ->
            if expanded.Contains key then
                Set.remove key expanded
            else
                Set.add key expanded)

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
                                                "#2e5771"
                                            KpiCard
                                                "Mortgage loans"
                                                (number (decimal analytics.LoanCount))
                                                (sprintf "%s issuers" (number (decimal analytics.IssuerCount)))
                                                "#f47c3c"
                                            KpiCard
                                                "Average coupon"
                                                (rate analytics.AverageCoupon)
                                                (sprintf "%s WAM" (months analytics.AverageWam))
                                                "#93c54b"
                                            KpiCard
                                                "Newest issue"
                                                (dateShortOption analytics.NewestIssueDate)
                                                "Latest issue date in selection"
                                                "#29abe0"
                                            KpiCard
                                                "Provinces"
                                                (string analytics.ByProvince.Length)
                                                "Represented in selection"
                                                "#d9534f"
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
                            textOrEmpty row.IssueDate
                            textOrEmpty row.MaturityDate
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

                            PoolDataGrid
                                {
                                    Rows = page.Rows
                                    GroupFields = groupFields
                                    ExpandedGroups = expandedGroups
                                    PivotField = pivotField
                                    SortModel = sortModel
                                    FilterModel = filterModel
                                    QuickFilter = quickFilter
                                    OnGroupFieldsChange = updateGroupFields
                                    OnToggleGroup = toggleGroup
                                    OnPivotFieldChange = fun field -> setPivotField (fun _ -> field)
                                    OnExpandedGroupsChange = fun expanded -> setExpandedGroups (fun _ -> expanded)
                                    OnSortModelChanged = fun model -> setSortModel (fun _ -> model)
                                    OnFilterModelChanged = fun model -> setFilterModel (fun _ -> model)
                                    OnQuickFilterChanged = fun text -> setQuickFilter text
                                    OnSelectPool = props.OnSelectPool
                                }

                            Html.p
                                [
                                    prop.className "grid-hint"
                                    prop.text
                                        "Click any pool row to open the detail drawer. When grouping is active, click a group row to expand or collapse it."
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
                            statusToggle props
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

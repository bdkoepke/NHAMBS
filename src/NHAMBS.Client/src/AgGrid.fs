module NHAMBS.Client.AgGrid

open System
open Fable.Core
open Fable.Core.JsInterop
open Feliz
open Browser.Types
open NHAMBS.Client
open NHAMBS.Client.Formatting

type GridOptions =
    {
        Columns: obj[]
        Rows: obj[]
        Height: string
        Pagination: bool
        InitialSortModel: obj
        InitialFilterModel: obj
        ApplyQuickFilterInternally: bool
        HeaderGroupFields: string[]
        OnSortModelChanged: obj -> unit
        OnFilterModelChanged: obj -> unit
        OnQuickFilterChanged: string -> unit
        OnRowClicked: obj -> unit
    }

let private defaultValue (value: obj) =
    if isNull value then 0m else unbox<decimal> value

let private cellFormatter (formatter: obj -> string) =
    box (fun (parameters: obj) -> formatter parameters?value)

let private columnProps (field: string) (header: string) (minWidth: int) =
    [
        "field" ==> field
        "headerName" ==> header
        "minWidth" ==> minWidth
        "flex" ==> 1
        "sortable" ==> true
        "filter" ==> true
        "resizable" ==> true
        "suppressHeaderMenuButton" ==> true
    ]

let column field header minWidth =
    createObj (columnProps field header minWidth)

let textColumn field header minWidth = column field header minWidth

/// A text column that shows the value of tooltipField when a cell is hovered,
/// with optional explanatory text on the column header.
let textColumnWithTooltip field header minWidth headerTooltip tooltipField =
    createObj (
        columnProps field header minWidth
        @ [ "headerTooltip" ==> headerTooltip; "tooltipField" ==> tooltipField ]
    )

let numberColumn field header minWidth =
    createObj
        [
            "field" ==> field
            "headerName" ==> header
            "minWidth" ==> minWidth
            "flex" ==> 1
            "sortable" ==> true
            "filter" ==> "agNumberColumnFilter"
            "resizable" ==> true
            "type" ==> "numericColumn"
            "valueFormatter" ==> cellFormatter (fun value -> number (defaultValue value))
        ]

let moneyColumn field header minWidth =
    createObj
        [
            "field" ==> field
            "headerName" ==> header
            "minWidth" ==> minWidth
            "flex" ==> 1
            "sortable" ==> true
            "filter" ==> "agNumberColumnFilter"
            "resizable" ==> true
            "type" ==> "numericColumn"
            "valueFormatter"
            ==> cellFormatter (fun value -> moneyCompact (defaultValue value))
        ]

let rateColumn field header minWidth =
    createObj
        [
            "field" ==> field
            "headerName" ==> header
            "minWidth" ==> minWidth
            "flex" ==> 1
            "sortable" ==> true
            "filter" ==> "agNumberColumnFilter"
            "resizable" ==> true
            "type" ==> "numericColumn"
            "valueFormatter"
            ==> cellFormatter (fun value ->
                if isNull value || defaultValue value = 0m then
                    "—"
                else
                    sprintf "%s%%" ((defaultValue value).ToString("N3")))
        ]

let dateColumn field header minWidth =
    createObj
        [
            "field" ==> field
            "headerName" ==> header
            "minWidth" ==> minWidth
            "flex" ==> 1
            "sortable" ==> true
            "filter" ==> true
            "resizable" ==> true
            "valueFormatter" ==> cellFormatter (fun value -> dateShort (string value))
        ]

let monthsColumn field header minWidth =
    createObj
        [
            "field" ==> field
            "headerName" ==> header
            "minWidth" ==> minWidth
            "flex" ==> 1
            "sortable" ==> true
            "filter" ==> "agNumberColumnFilter"
            "resizable" ==> true
            "type" ==> "numericColumn"
            "valueFormatter" ==> cellFormatter (fun value -> months (defaultValue value))
        ]

/// Numeric percentage column that also renders a proportional bar in the cell.
let shareBarColumn field header minWidth =
    createObj
        [
            "field" ==> field
            "headerName" ==> header
            "minWidth" ==> minWidth
            "flex" ==> 1
            "sortable" ==> true
            "filter" ==> "agNumberColumnFilter"
            "resizable" ==> true
            "type" ==> "numericColumn"
            "valueFormatter" ==> cellFormatter (fun value -> percent1 (defaultValue value))
            "cellRenderer"
            ==> box (fun (parameters: obj) ->
                let value = defaultValue parameters?value
                let wrapper = Browser.Dom.document.createElement "div"
                wrapper.setAttribute ("class", "cell-share")
                let track = Browser.Dom.document.createElement "div"
                track.setAttribute ("class", "cell-share-track")
                let fill = Browser.Dom.document.createElement "div"
                fill.setAttribute ("class", "cell-share-fill")
                fill.setAttribute ("style", sprintf "width:%.1f%%" (min 100.0 (max 0.0 (float value))))
                track.appendChild fill |> ignore
                let label = Browser.Dom.document.createElement "span"
                label.setAttribute ("class", "cell-share-value")
                label.textContent <- percent1 value
                wrapper.appendChild track |> ignore
                wrapper.appendChild label |> ignore
                wrapper :> obj)
        ]

/// Renders a pre-built HTML string (for example an inline SVG sparkline).
let htmlColumn field header minWidth =
    createObj
        [
            "field" ==> field
            "headerName" ==> header
            "minWidth" ==> minWidth
            "flex" ==> 1
            "sortable" ==> false
            "filter" ==> false
            "resizable" ==> true
            "cellRenderer"
            ==> box (fun (parameters: obj) ->
                let element = Browser.Dom.document.createElement "div"
                element.setAttribute ("class", "cell-chart")

                element.innerHTML <-
                    (if isNull parameters?value then
                         ""
                     else
                         string parameters?value)

                element :> obj)
        ]

let linkColumnWithLabel field header minWidth linkText =
    createObj
        [
            "field" ==> field
            "headerName" ==> header
            "minWidth" ==> minWidth
            "flex" ==> 1
            "sortable" ==> false
            "filter" ==> false
            "resizable" ==> true
            "cellRenderer"
            ==> box (fun (parameters: obj) ->
                let url = string parameters?value

                if String.IsNullOrWhiteSpace url then
                    null
                elif url.StartsWith("http", StringComparison.OrdinalIgnoreCase) then
                    let link = Browser.Dom.document.createElement "a"
                    link.setAttribute ("href", url)
                    link.setAttribute ("target", "_blank")
                    link.setAttribute ("rel", "noopener")
                    link.textContent <- linkText
                    link :> obj
                else
                    let text = Browser.Dom.document.createElement "span"
                    text.textContent <- url
                    text :> obj)
        ]

let linkColumn field header minWidth =
    linkColumnWithLabel field header minWidth "Open PDF"

/// First column used while rows are grouped. Group-row objects provide
/// groupLabel, groupLevel, groupCount and groupExpanded fields.
let groupColumn =
    createObj
        [
            "field" ==> "groupLabel"
            "headerName" ==> "Group"
            "minWidth" ==> 250
            "flex" ==> 2
            "sortable" ==> false
            "filter" ==> false
            "resizable" ==> true
            "cellRenderer"
            ==> box (fun (parameters: obj) ->
                let data = parameters?data

                if isNull data || isNull data?isGroup then
                    null
                else
                    let wrapper = Browser.Dom.document.createElement "div"
                    wrapper.setAttribute ("class", "cell-group")
                    wrapper.setAttribute ("style", sprintf "padding-left:%dpx" (unbox<int> data?groupLevel * 18))

                    let chevron = Browser.Dom.document.createElement "span"
                    chevron.setAttribute ("class", "cell-group-chevron")

                    let expandable =
                        let value = data?groupExpandable
                        if isNull value then true else unbox<bool> value

                    chevron.textContent <-
                        if not expandable then ""
                        elif unbox<bool> data?groupExpanded then "▾"
                        else "▸"

                    let label = Browser.Dom.document.createElement "span"
                    label.setAttribute ("class", "cell-group-label")
                    label.textContent <- string data?groupLabel

                    let count = Browser.Dom.document.createElement "span"
                    count.setAttribute ("class", "cell-group-count")
                    count.textContent <- sprintf "(%d)" (unbox<int> data?groupCount)

                    wrapper.appendChild chevron |> ignore
                    wrapper.appendChild label |> ignore
                    wrapper.appendChild count |> ignore
                    wrapper :> obj)
        ]

/// Removes sorting and filtering from a column definition while grouping,
/// so the group hierarchy cannot be reordered by column interactions.
let plainColumn (column: obj) : obj = Interop.withoutColumnInteraction column

/// Keeps the sort/filter UI enabled while preventing the grid from reordering
/// custom group rows; filtering uses the group's representative child values.
let groupedColumn (column: obj) : obj = Interop.groupedColumn column

[<ReactComponent>]
let AgGrid (options: GridOptions) =
    let containerRef = React.useRef<Element option>(None)
    let apiRef = React.useRef<obj option>(None)
    let (pageSizeChoice, setPageSizeChoice) = React.useState ("25")
    let (showColumnsPanel, setShowColumnsPanel) = React.useState false

    let (hiddenColumns, setHiddenColumns) =
        React.useStateWithUpdater<Set<string>> Set.empty

    let (quickFilterText, setQuickFilterText) = React.useState ""

    let pageSize =
        if pageSizeChoice = "all" then
            max 1 options.Rows.Length
        else
            int pageSizeChoice

    let columnChoices =
        options.Columns
        |> Array.choose (fun column ->
            let field = column?field
            let header = column?headerName

            if isNull field then
                None
            else
                Some(string field, if isNull header then string field else string header))

    let columnVisible (field: string) = not (hiddenColumns.Contains field)

    let setColumnVisible (field: string) (visible: bool) =
        setHiddenColumns (fun hidden ->
            if visible then
                Set.remove field hidden
            else
                Set.add field hidden)

        match apiRef.current with
        | Some api -> Interop.setColumnsVisible api [| field |] visible
        | None -> ()

    let showAllColumns () =
        setHiddenColumns (fun _ -> Set.empty)

        match apiRef.current with
        | Some api ->
            let fields = columnChoices |> Array.map fst
            Interop.setColumnsVisible api fields true
        | None -> ()

    let updateQuickFilter (value: string) =
        setQuickFilterText value
        options.OnQuickFilterChanged value

        if options.ApplyQuickFilterInternally then
            match apiRef.current with
            | Some api -> Interop.setQuickFilter api value
            | None -> ()

    let clearGridFilters () =
        setQuickFilterText ""
        options.OnQuickFilterChanged ""
        options.OnFilterModelChanged(box (createObj []))

        match apiRef.current with
        | Some api ->
            Interop.clearFilters api

            if options.ApplyQuickFilterInternally then
                Interop.setQuickFilter api ""
        | None -> ()

    React.useEffect (
        (fun () ->
            match containerRef.current with
            | Some element ->
                let gridOptions =
                    createObj
                        [
                            "columnDefs" ==> options.Columns
                            "rowData" ==> options.Rows
                            "defaultColDef"
                            ==> createObj
                                    [
                                        "sortable" ==> true
                                        "resizable" ==> true
                                        "filter" ==> true
                                        "minWidth" ==> 96
                                    ]
                            "pagination" ==> options.Pagination
                            "paginationPageSize" ==> pageSize
                            "paginationPageSizeSelector" ==> false
                            "animateRows" ==> false
                            "rowHeight" ==> 40
                            "headerHeight" ==> 42
                            "suppressCellFocus" ==> true
                            "tooltipShowDelay" ==> 200
                            "getRowClass"
                            ==> (fun (parameters: obj) ->
                                let value = parameters?data?rowClass
                                if isNull value then "" else string value)
                            "onRowClicked" ==> (fun (event: obj) -> options.OnRowClicked event?data)
                            "onSortChanged"
                            ==> (fun (_: obj) ->
                                Interop.defer (fun () ->
                                    match apiRef.current with
                                    | Some api -> options.OnSortModelChanged(Interop.getSortModel api)
                                    | None -> ()))
                            "onFilterChanged"
                            ==> (fun (_: obj) ->
                                Interop.defer (fun () ->
                                    match apiRef.current with
                                    | Some api -> options.OnFilterModelChanged(Interop.getFilterModel api)
                                    | None -> ()))
                            "localeText"
                            ==> createObj
                                    [
                                        "page" ==> "Page"
                                        "of" ==> "of"
                                        "to" ==> "to"
                                        "more" ==> "more"
                                        "noRowsToShow" ==> "No records match the current filters."
                                    ]
                        ]

                let api = Interop.createGrid element gridOptions
                apiRef.current <- Some api

                let detachHeaderGrips =
                    if options.HeaderGroupFields.Length > 0 then
                        Interop.attachHeaderGrips api element options.HeaderGroupFields
                    else
                        fun () -> ()

                if not (isNull options.InitialSortModel) then
                    Interop.setSortModel api options.InitialSortModel

                if not (isNull options.InitialFilterModel) then
                    Interop.setFilterModel api options.InitialFilterModel

                for field in hiddenColumns do
                    Interop.setColumnsVisible api [| field |] false

                if
                    options.ApplyQuickFilterInternally
                    && not (String.IsNullOrWhiteSpace quickFilterText)
                then
                    Interop.setQuickFilter api quickFilterText

                fun () ->
                    apiRef.current <- None
                    detachHeaderGrips ()
                    Interop.destroyGrid api
            | None -> fun () -> ()),
        [| box options.Columns; box options.Pagination |]
    )

    React.useEffect (
        (fun () ->
            match apiRef.current with
            | Some api -> Interop.setGridRowData api (box options.Rows)
            | None -> ()),
        [| box options.Rows |]
    )

    let changePageSize (value: string) =
        setPageSizeChoice value

        let size =
            if value = "all" then
                max 1 options.Rows.Length
            else
                match Int32.TryParse value with
                | true, parsed when parsed > 0 -> parsed
                | _ -> 25

        match apiRef.current with
        | Some api -> Interop.setPaginationPageSize api size
        | None -> ()

    Html.div
        [
            prop.className "grid-wrap"
            prop.children
                [
                    Html.div
                        [
                            prop.className "grid-tools"
                            prop.children
                                [
                                    Html.div
                                        [
                                            prop.className "grid-tools-left"
                                            prop.children
                                                [
                                                    Html.div
                                                        [
                                                            prop.className "grid-columns"
                                                            prop.children
                                                                [
                                                                    Html.button
                                                                        [
                                                                            prop.className (
                                                                                if showColumnsPanel then
                                                                                    "btn btn-small btn-secondary"
                                                                                else
                                                                                    "btn btn-small btn-ghost"
                                                                            )
                                                                            prop.onClick (fun _ ->
                                                                                setShowColumnsPanel (
                                                                                    not showColumnsPanel
                                                                                ))
                                                                            prop.text "▦ Columns"
                                                                        ]
                                                                    if showColumnsPanel then
                                                                        Html.div
                                                                            [
                                                                                prop.className "grid-columns-panel"
                                                                                prop.children
                                                                                    [
                                                                                        Html.div
                                                                                            [
                                                                                                prop.className
                                                                                                    "grid-columns-head"
                                                                                                prop.children
                                                                                                    [
                                                                                                        Html.strong
                                                                                                            [
                                                                                                                prop
                                                                                                                    .text
                                                                                                                    "Columns"
                                                                                                            ]
                                                                                                        Html.div
                                                                                                            [
                                                                                                                prop
                                                                                                                    .className
                                                                                                                    "grid-columns-actions"
                                                                                                                prop
                                                                                                                    .children
                                                                                                                    [
                                                                                                                        Html
                                                                                                                            .button
                                                                                                                            [
                                                                                                                                prop
                                                                                                                                    .className
                                                                                                                                    "grid-columns-link"
                                                                                                                                prop.onClick
                                                                                                                                    (fun
                                                                                                                                        _ ->
                                                                                                                                        showAllColumns ())
                                                                                                                                prop
                                                                                                                                    .text
                                                                                                                                    "Show all"
                                                                                                                            ]
                                                                                                                        Html
                                                                                                                            .button
                                                                                                                            [
                                                                                                                                prop
                                                                                                                                    .className
                                                                                                                                    "grid-columns-link"
                                                                                                                                prop.onClick
                                                                                                                                    (fun
                                                                                                                                        _ ->
                                                                                                                                        setShowColumnsPanel
                                                                                                                                            false)
                                                                                                                                prop
                                                                                                                                    .text
                                                                                                                                    "Close"
                                                                                                                            ]
                                                                                                                    ]
                                                                                                            ]
                                                                                                    ]
                                                                                            ]
                                                                                        for field, label in
                                                                                            columnChoices do
                                                                                            Html.label
                                                                                                [
                                                                                                    prop.className
                                                                                                        "grid-column-option"
                                                                                                    prop.children
                                                                                                        [
                                                                                                            Html.input
                                                                                                                [
                                                                                                                    prop.custom (
                                                                                                                        "type",
                                                                                                                        "checkbox"
                                                                                                                    )
                                                                                                                    prop.isChecked (
                                                                                                                        columnVisible
                                                                                                                            field
                                                                                                                    )
                                                                                                                    prop.onChange
                                                                                                                        (fun
                                                                                                                            (event:
                                                                                                                                Event) ->
                                                                                                                            setColumnVisible
                                                                                                                                field
                                                                                                                                (unbox<
                                                                                                                                    bool
                                                                                                                                  >
                                                                                                                                    event
                                                                                                                                        .target?``checked``))
                                                                                                                ]
                                                                                                            Html.span
                                                                                                                [
                                                                                                                    prop
                                                                                                                        .text
                                                                                                                        label
                                                                                                                ]
                                                                                                        ]
                                                                                                ]
                                                                                    ]
                                                                            ]
                                                                ]
                                                        ]
                                                    Html.input
                                                        [
                                                            prop.className "grid-quick-filter"
                                                            prop.custom ("aria-label", "Quick filter")
                                                            prop.placeholder "Quick filter…"
                                                            prop.value quickFilterText
                                                            prop.onInput (fun (event: Event) ->
                                                                updateQuickFilter (string event.target?value))
                                                        ]
                                                    Html.button
                                                        [
                                                            prop.className "btn btn-small btn-ghost"
                                                            prop.onClick (fun _ -> clearGridFilters ())
                                                            prop.text "Clear grid filters"
                                                        ]
                                                ]
                                        ]
                                    if options.Pagination then
                                        Html.div
                                            [
                                                prop.className "grid-page-size"
                                                prop.children
                                                    [
                                                        Html.label
                                                            [
                                                                prop.children
                                                                    [
                                                                        Html.span [ prop.text "Rows per page" ]
                                                                        Html.select
                                                                            [
                                                                                prop.className "grid-page-size-select"
                                                                                prop.value pageSizeChoice
                                                                                prop.onChange (fun (event: Event) ->
                                                                                    changePageSize (
                                                                                        string event.target?value
                                                                                    ))
                                                                                prop.children
                                                                                    [
                                                                                        Html.option
                                                                                            [
                                                                                                prop.value "25"
                                                                                                prop.text "25"
                                                                                            ]
                                                                                        Html.option
                                                                                            [
                                                                                                prop.value "50"
                                                                                                prop.text "50"
                                                                                            ]
                                                                                        Html.option
                                                                                            [
                                                                                                prop.value "100"
                                                                                                prop.text "100"
                                                                                            ]
                                                                                        Html.option
                                                                                            [
                                                                                                prop.value "250"
                                                                                                prop.text "250"
                                                                                            ]
                                                                                        Html.option
                                                                                            [
                                                                                                prop.value "500"
                                                                                                prop.text "500"
                                                                                            ]
                                                                                        Html.option
                                                                                            [
                                                                                                prop.value "1000"
                                                                                                prop.text "1000"
                                                                                            ]
                                                                                        Html.option
                                                                                            [
                                                                                                prop.value "all"
                                                                                                prop.text "All"
                                                                                            ]
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
                            prop.className "ag-theme-quartz grid-shell"
                            prop.style [ style.custom ("height", options.Height) ]
                            prop.ref (fun element -> containerRef.current <- Some element)
                        ]
                ]
        ]

let exportCsv (fileName: string) (headers: string[]) (rows: string[][]) =
    let escape (value: string) =
        if value.Contains "\"" || value.Contains "," || value.Contains "\n" then
            "\"" + value.Replace("\"", "\"\"") + "\""
        else
            value

    let lines =
        Array.append
            [| headers |> Array.map escape |> String.concat "," |]
            (rows |> Array.map (fun row -> row |> Array.map escape |> String.concat ","))
        |> String.concat "\n"

    Interop.createCsvBlob lines |> fun blob -> Interop.downloadBlob blob fileName

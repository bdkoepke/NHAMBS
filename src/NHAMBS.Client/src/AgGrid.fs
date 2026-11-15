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

[<ReactComponent>]
let AgGrid (options: GridOptions) =
    let containerRef = React.useRef<Element option>(None)

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
                            "pagination" ==> true
                            "paginationPageSize" ==> 25
                            "paginationPageSizeSelector" ==> [| 25; 50; 100 |]
                            "animateRows" ==> false
                            "rowHeight" ==> 40
                            "headerHeight" ==> 42
                            "suppressCellFocus" ==> true
                            "tooltipShowDelay" ==> 200
                            "onRowClicked" ==> (fun (event: obj) -> options.OnRowClicked event?data)
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
                fun () -> Interop.destroyGrid api
            | None -> fun () -> ()),
        [| box options.Rows; box options.Columns |]
    )

    Html.div
        [
            prop.className "ag-theme-quartz grid-shell"
            prop.style [ style.custom ("height", options.Height) ]
            prop.ref (fun element -> containerRef.current <- Some element)
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

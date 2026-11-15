module NHAMBS.Client.ForwardCurves

open System
open Feliz
open Fable.Core.JsInterop
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting

type ForwardCurvesProps =
    {
        Data: Loadable<ForwardCurveSet>
        OnRefresh: unit -> unit
    }

let private tryDate (value: string) =
    if String.IsNullOrWhiteSpace value || value.Length < 10 then
        None
    else
        match
            Int32.TryParse(value.Substring(0, 4)),
            Int32.TryParse(value.Substring(5, 2)),
            Int32.TryParse(value.Substring(8, 2))
        with
        | (true, year), (true, month), (true, day) when month >= 1 && month <= 12 && day >= 1 && day <= 31 ->
            Some(DateTime(year, month, day))
        | _ -> None

let private tenorMonths (asOfDate: string) (date: string) =
    match tryDate asOfDate, tryDate date with
    | Some asOf, Some pointDate when pointDate >= asOf ->
        let months = ((pointDate.Year - asOf.Year) * 12) + (pointDate.Month - asOf.Month)

        if pointDate.Day < asOf.Day && months > 0 then
            max 0 (months - 1)
        else
            months
    | _ -> -1

let private tenorLabel (months: int) =
    if months < 0 then
        "—"
    elif months = 0 then
        "Spot"
    elif months % 12 = 0 then
        sprintf "%dy" (months / 12)
    elif months >= 12 then
        sprintf "%dy %dm" (months / 12) (months % 12)
    else
        sprintf "%dm" months

let private curveRate (value: decimal) =
    sprintf "%s%%" ((value * 100m).ToString("N3"))

let private axisRate (value: decimal) =
    sprintf "%s%%" ((value * 100m).ToString("N2"))

let private maxPointDate (curves: ForwardCurve[]) =
    curves
    |> Array.collect (fun curve -> curve.Points |> Array.map (fun point -> point.Date))
    |> Array.sort
    |> Array.tryLast
    |> Option.defaultValue ""

let private observationRows (data: ForwardCurveSet) =
    data.Curves
    |> Array.collect (fun curve ->
        curve.Points
        |> Array.map (fun point ->
            let months = tenorMonths data.AsOfDate point.Date

            createObj
                [
                    "curve" ==> curve.ShortName
                    "date" ==> point.Date
                    "tenor" ==> tenorLabel months
                    "rate" ==> point.Value * 100m
                ]))
    |> Array.sortBy (fun row -> string row?curve, string row?date)

let private observationColumns =
    [|
        AgGrid.textColumn "curve" "Curve" 110
        AgGrid.dateColumn "date" "Date" 140
        AgGrid.textColumn "tenor" "Tenor" 90
        AgGrid.rateColumn "rate" "Forward rate" 130
    |]

let private lineChart (curves: ForwardCurve[]) =
    let series =
        curves
        |> Array.choose (fun curve ->
            let points =
                curve.Points
                |> Array.choose (fun point ->
                    tryDate point.Date |> Option.map (fun date -> date, point.Date, point.Value))

            if points.Length = 0 then None else Some(curve, points))

    if series.Length = 0 then
        Charts.emptyChart "No forward curve points"
    else
        let chartWidth = 960.0
        let chartHeight = 360.0
        let padLeft = 64.0
        let padRight = 18.0
        let padTop = 16.0
        let padBottom = 34.0
        let plotWidth = chartWidth - padLeft - padRight
        let plotHeight = chartHeight - padTop - padBottom

        let allDates =
            series
            |> Array.collect (fun (_, points) -> points |> Array.map (fun (date, _, _) -> date))
            |> Array.sort

        let minDate = Array.head allDates
        let maxDate = Array.last allDates

        let allValues =
            series
            |> Array.collect (fun (_, points) -> points |> Array.map (fun (_, _, value) -> value))

        let rawMin = Array.min allValues
        let rawMax = Array.max allValues
        let span = rawMax - rawMin
        let padding = max 0.0025m (span * 0.12m)
        let yMin = rawMin - padding
        let yMax = rawMax + padding
        let yRange = max 0.0001m (yMax - yMin)

        let xOf (date: DateTime) =
            if maxDate = minDate then
                padLeft + plotWidth / 2.0
            else
                padLeft
                + (float (date.Ticks - minDate.Ticks) / float (maxDate.Ticks - minDate.Ticks))
                  * plotWidth

        let yOf (value: decimal) =
            padTop + plotHeight - (float ((value - yMin) / yRange) * plotHeight)

        let labelPoints =
            let points = snd series.[0]
            let step = max 1 (int (Math.Ceiling(float points.Length / 6.0)))

            let labels =
                points
                |> Array.mapi (fun index point -> index, point)
                |> Array.filter (fun (index, _) -> index % step = 0)
                |> Array.map snd
                |> Array.toList

            match List.tryLast labels with
            | Some(_, date, _) when date <> (points |> Array.last |> fun (_, date, _) -> date) ->
                labels @ [ points |> Array.last ]
            | _ -> labels

        Svg.svg
            [
                svg.viewBox (0, 0, int chartWidth, int chartHeight)
                svg.className "chart-svg"
                svg.children
                    [
                        for tick in 0..4 do
                            let value = yMax - ((yMax - yMin) * decimal tick / 4m)
                            let yPos = yOf value

                            Svg.line
                                [
                                    svg.x1 padLeft
                                    svg.x2 (chartWidth - padRight)
                                    svg.y1 yPos
                                    svg.y2 yPos
                                    svg.stroke "#e6dfd3"
                                    svg.strokeWidth 1.0
                                ]

                            Svg.text
                                [
                                    svg.x (padLeft - 9.0)
                                    svg.y (yPos + 4.0)
                                    svg.custom ("text-anchor", "end")
                                    svg.fill "#8e8c84"
                                    svg.fontSize 11.0
                                    svg.children [ Html.text (axisRate value) ]
                                ]

                        for _, label, _ in labelPoints do
                            let labelDate = tryDate label |> Option.defaultValue minDate

                            let anchor =
                                if labelDate = minDate then "start"
                                elif labelDate = maxDate then "end"
                                else "middle"

                            Svg.text
                                [
                                    svg.x (xOf labelDate)
                                    svg.y (chartHeight - 10.0)
                                    svg.custom ("text-anchor", anchor)
                                    svg.fill "#8e8c84"
                                    svg.fontSize 11.0
                                    svg.children [ Html.text (dateShort label) ]
                                ]

                        for index, (curve, points) in Array.indexed series do
                            let color = Charts.palette.[index % Charts.palette.Length]

                            let path =
                                points
                                |> Array.mapi (fun pointIndex (date, _, value) ->
                                    sprintf "%s %.2f %.2f" (if pointIndex = 0 then "M" else "L") (xOf date) (yOf value))
                                |> String.concat " "

                            Svg.path
                                [
                                    svg.d path
                                    svg.fill "none"
                                    svg.stroke color
                                    svg.strokeWidth 2.2
                                    svg.custom ("stroke-linecap", "round")
                                    svg.custom ("stroke-linejoin", "round")
                                    svg.children [ Svg.title curve.Name ]
                                ]

                            let (lastDate, _, lastValue) = Array.last points

                            Svg.circle
                                [
                                    svg.cx (xOf lastDate)
                                    svg.cy (yOf lastValue)
                                    svg.r 3.2
                                    svg.fill "#ffffff"
                                    svg.stroke color
                                    svg.strokeWidth 1.6
                                    svg.children
                                        [
                                            Svg.title (
                                                sprintf
                                                    "%s · %s · %s"
                                                    curve.ShortName
                                                    (dateShort (lastDate.ToString("yyyy-MM-dd")))
                                                    (curveRate lastValue)
                                            )
                                        ]
                                ]
                    ]
            ]

let private legend (curves: ForwardCurve[]) =
    Html.div
        [
            prop.className "forward-legend"
            prop.children
                [
                    for index, curve in Array.indexed curves do
                        let color = Charts.palette.[index % Charts.palette.Length]

                        let latest =
                            curve.Points
                            |> Array.tryLast
                            |> Option.map (fun point -> curveRate point.Value)
                            |> Option.defaultValue "—"

                        Html.div
                            [
                                prop.className "forward-legend-item"
                                prop.children
                                    [
                                        Html.span
                                            [ prop.className "legend-dot"; prop.style [ style.backgroundColor color ] ]
                                        Html.div
                                            [
                                                prop.children
                                                    [
                                                        Html.div
                                                            [
                                                                prop.className "forward-legend-head"
                                                                prop.children
                                                                    [
                                                                        Html.strong [ prop.text curve.ShortName ]
                                                                        Html.span
                                                                            [
                                                                                prop.className "forward-legend-value"
                                                                                prop.text latest
                                                                            ]
                                                                    ]
                                                            ]
                                                        Html.div
                                                            [
                                                                prop.className "forward-legend-name"
                                                                prop.text curve.Name
                                                            ]
                                                    ]
                                            ]
                                    ]
                            ]
                ]
        ]

let private emptyState (data: ForwardCurveSet) =
    let hasSeries = data.Curves.Length > 0

    Card
        "Forward curves"
        (if hasSeries then
             "forward_curves.json contains no observations"
         else
             "forward_curves.json was not found")
        []
        [
            Html.div
                [
                    prop.className "empty-state"
                    prop.children
                        [
                            Html.span [ prop.className "empty-icon"; prop.text "∿" ]
                            Html.strong
                                [
                                    prop.text (
                                        if hasSeries then
                                            "No forward curve observations found"
                                        else
                                            "No forward curve data found"
                                    )
                                ]
                            Html.span
                                [
                                    prop.text (
                                        if hasSeries then
                                            sprintf "Inspect %s for empty series data." data.SourceFile
                                        else
                                            sprintf
                                                "Place the pulled JSON at %s or set NHAMBS_FORWARD_CURVES."
                                                data.SourceFile
                                    )
                                ]
                        ]
                ]
        ]

let private forwardCurveView (props: ForwardCurvesProps) (data: ForwardCurveSet) =
    let curves = data.Curves |> Array.filter (fun curve -> curve.Points.Length > 0)

    if curves.Length = 0 then
        emptyState data
    else
        let totalPoints = curves |> Array.sumBy (fun curve -> curve.Points.Length)
        let horizon = maxPointDate curves

        let latestUpdate =
            curves
            |> Array.map (fun curve -> curve.UpdatedOn)
            |> Array.sortDescending
            |> Array.tryHead
            |> Option.defaultValue ""

        let staleCount = curves |> Array.filter (fun curve -> curve.Stale) |> Array.length

        Html.div
            [
                prop.className "page"
                prop.children
                    [
                        PageHeader
                            "MARKET FORWARDS"
                            "Forward curves"
                            data.Name
                            [
                                Html.button
                                    [
                                        prop.className "btn btn-ghost"
                                        prop.onClick (fun _ -> props.OnRefresh())
                                        prop.children [ Html.span [ prop.text "↻" ]; Html.span [ prop.text "Refresh" ] ]
                                    ]
                            ]

                        Html.div
                            [
                                prop.className "kpi-grid"
                                prop.children
                                    [
                                        KpiCard
                                            "Curves"
                                            (number (decimal curves.Length))
                                            (sprintf "%d stale" staleCount)
                                            Charts.palette.[0]
                                        KpiCard
                                            "As of"
                                            (dateShort data.AsOfDate)
                                            "Curve snapshot date"
                                            Charts.palette.[1]
                                        KpiCard "Horizon" (dateShort horizon) "Final forecast date" Charts.palette.[2]
                                        KpiCard
                                            "Observations"
                                            (number (decimal totalPoints))
                                            (sprintf "%d series" curves.Length)
                                            Charts.palette.[3]
                                    ]
                            ]

                        Card
                            "Forward curves"
                            "Projected rates by date; values are shown as percentages"
                            []
                            [ lineChart curves; legend curves ]

                        Card
                            "Curve observations"
                            "All points loaded from forward_curves.json"
                            []
                            [
                                AgGrid.AgGrid
                                    {
                                        Columns = observationColumns
                                        Rows = observationRows data
                                        Height = "440px"
                                        OnRowClicked = ignore
                                    }
                            ]

                        Html.div
                            [
                                prop.className "overview-grid overview-grid-2"
                                prop.children
                                    [
                                        Card
                                            "Source"
                                            "Forward curve input file"
                                            []
                                            [
                                                Html.dl
                                                    [
                                                        prop.className "detail-list"
                                                        prop.children
                                                            [
                                                                Html.dt [ prop.text "Dataset" ]
                                                                Html.dd
                                                                    [
                                                                        prop.text (
                                                                            if String.IsNullOrWhiteSpace data.Name then
                                                                                "—"
                                                                            else
                                                                                data.Name
                                                                        )
                                                                    ]
                                                                Html.dt [ prop.text "As of" ]
                                                                Html.dd [ prop.text (dateShort data.AsOfDate) ]
                                                                Html.dt [ prop.text "Last updated" ]
                                                                Html.dd [ prop.text (dateShort latestUpdate) ]
                                                                Html.dt [ prop.text "Series" ]
                                                                Html.dd [ prop.text (string curves.Length) ]
                                                                Html.dt [ prop.text "Observations" ]
                                                                Html.dd [ prop.text (number (decimal totalPoints)) ]
                                                                Html.dt [ prop.text "File" ]
                                                                Html.dd
                                                                    [
                                                                        Html.code
                                                                            [
                                                                                prop.className "source-path"
                                                                                prop.text data.SourceFile
                                                                            ]
                                                                    ]
                                                            ]
                                                    ]
                                            ]
                                        Card
                                            "Series"
                                            "Curves carried in the current snapshot"
                                            []
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
                                                                                                Html.th
                                                                                                    [
                                                                                                        prop.text
                                                                                                            "Curve"
                                                                                                    ]
                                                                                                Html.th
                                                                                                    [ prop.text "Name" ]
                                                                                                Html.th
                                                                                                    [ prop.text "Type" ]
                                                                                                Html.th
                                                                                                    [
                                                                                                        prop.text
                                                                                                            "Points"
                                                                                                    ]
                                                                                                Html.th
                                                                                                    [
                                                                                                        prop.text
                                                                                                            "Updated"
                                                                                                    ]
                                                                                            ]
                                                                                    ]
                                                                            ]
                                                                    ]
                                                                Html.tbody
                                                                    [
                                                                        prop.children
                                                                            [
                                                                                for curve in curves do
                                                                                    Html.tr
                                                                                        [
                                                                                            prop.children
                                                                                                [
                                                                                                    Html.td
                                                                                                        [
                                                                                                            Html.strong
                                                                                                                [
                                                                                                                    prop
                                                                                                                        .text
                                                                                                                        curve
                                                                                                                            .ShortName
                                                                                                                ]
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text
                                                                                                                curve
                                                                                                                    .Name
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text
                                                                                                                curve
                                                                                                                    .DataType
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                number (
                                                                                                                    decimal
                                                                                                                        curve
                                                                                                                            .Points
                                                                                                                            .Length
                                                                                                                )
                                                                                                            )
                                                                                                        ]
                                                                                                    Html.td
                                                                                                        [
                                                                                                            prop.text (
                                                                                                                dateShort
                                                                                                                    curve
                                                                                                                        .UpdatedOn
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
                    ]
            ]

let private forwardCurvesPage (props: ForwardCurvesProps) =
    LoadableBlock "Loading forward curves" props.Data (fun data -> forwardCurveView props data)

[<ReactComponent>]
let ForwardCurvesPage (props: ForwardCurvesProps) = forwardCurvesPage props

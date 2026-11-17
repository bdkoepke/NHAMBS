module NHAMBS.Client.Charts

open System
open System.Globalization
open Feliz
open NHAMBS.Shared.Domain
open NHAMBS.Client.Formatting

let palette =
    [|
        "#2e5771" // CMB steel blue
        "#f47c3c" // Sandstone warning
        "#93c54b" // Sandstone success
        "#29abe0" // Sandstone info
        "#d9534f" // Sandstone danger
        "#8e8c84" // Sandstone secondary
        "#5c8aa6"
        "#c2a878"
        "#6b7a8f"
    |]

let private chartWidth = 900.0
let private chartHeight = 280.0
let private padLeft = 62.0
let private padRight = 18.0
let private padTop = 16.0
let private padBottom = 34.0

let emptyChart (message: string) =
    Html.div [ prop.className "chart-empty"; prop.text message ]

/// Multi-series date chart used for the CBA/CMHC arrears comparison. The two
/// providers publish monthly and quarterly points, so the x-axis is calendar
/// time rather than a shared array index.
let arrearsComparisonChart (geographyName: string) (points: ArrearsSeriesPoint[]) =
    if points.Length = 0 then
        emptyChart "No arrears observations"
    else
        let parsed =
            points
            |> Array.choose (fun point ->
                match DateTime.TryParse(point.Date, CultureInfo.InvariantCulture) with
                | true, date -> Some(date, point)
                | _ -> None)

        if parsed.Length = 0 then
            emptyChart "No dated arrears observations"
        else
            let dates = parsed |> Array.map fst
            let minDate = dates |> Array.min
            let maxDate = dates |> Array.max
            let spanDays = max 1.0 (maxDate - minDate).TotalDays

            let maxValue =
                parsed |> Array.map (fun (_, point) -> point.Value) |> Array.max |> max 0.1m

            let plotWidth = chartWidth - padLeft - padRight
            let plotHeight = chartHeight - padTop - padBottom

            let x (date: DateTime) =
                padLeft + ((date - minDate).TotalDays / spanDays) * plotWidth

            let y (value: decimal) =
                padTop + plotHeight - (float value / float maxValue) * plotHeight

            let series =
                parsed
                |> Array.groupBy (fun (_, point) -> point.Source)
                |> Array.sortBy fst
                |> Array.mapi (fun index (source, values) ->
                    let ordered = values |> Array.sortBy fst

                    let path =
                        ordered
                        |> Array.mapi (fun pointIndex (date, point) ->
                            sprintf "%s %.2f %.2f" (if pointIndex = 0 then "M" else "L") (x date) (y point.Value))
                        |> String.concat " "

                    source, path, palette.[index % palette.Length])

            let xLabels =
                [ 0.0; 0.25; 0.5; 0.75; 1.0 ]
                |> List.map (fun fraction -> minDate.AddDays(spanDays * fraction))

            let yLabels = [ 0.0; 0.25; 0.5; 0.75; 1.0 ]

            Html.div
                [
                    prop.className "arrears-chart"
                    prop.children
                        [
                            Svg.svg
                                [
                                    svg.viewBox (0, 0, int chartWidth, int chartHeight)
                                    svg.className "chart-svg"
                                    svg.children
                                        [
                                            for fraction in yLabels do
                                                let value = decimal fraction * maxValue

                                                Svg.line
                                                    [
                                                        svg.x1 padLeft
                                                        svg.y1 (y value)
                                                        svg.x2 (chartWidth - padRight)
                                                        svg.y2 (y value)
                                                        svg.stroke "#e7e0d5"
                                                        svg.strokeWidth 1.0
                                                    ]

                                                Svg.text
                                                    [
                                                        svg.x (padLeft - 8.0)
                                                        svg.y (y value + 4.0)
                                                        svg.custom ("text-anchor", "end")
                                                        svg.fill "#8a8175"
                                                        svg.fontSize 10.0
                                                        svg.text (sprintf "%.2f%%" (float value))
                                                    ]

                                            for date in xLabels do
                                                Svg.text
                                                    [
                                                        svg.x (x date)
                                                        svg.y (chartHeight - 10.0)
                                                        svg.custom ("text-anchor", "middle")
                                                        svg.fill "#8a8175"
                                                        svg.fontSize 10.0
                                                        svg.text (
                                                            date.ToString("MMM yyyy", CultureInfo.InvariantCulture)
                                                        )
                                                    ]

                                            for source, path, colour in series do
                                                Svg.path
                                                    [
                                                        svg.d path
                                                        svg.fill "none"
                                                        svg.stroke colour
                                                        svg.strokeWidth 2.4
                                                        svg.custom ("stroke-linejoin", "round")
                                                        svg.custom ("stroke-linecap", "round")
                                                        svg.custom (
                                                            "aria-label",
                                                            sprintf "%s %s 90+ day arrears rate" geographyName source
                                                        )
                                                    ]
                                        ]
                                ]
                            Html.div
                                [
                                    prop.className "chart-legend"
                                    prop.children
                                        [
                                            for source, _, colour in series do
                                                Html.span
                                                    [
                                                        prop.className "chart-legend-item"
                                                        prop.children
                                                            [
                                                                Html.span
                                                                    [
                                                                        prop.className "chart-legend-dot"
                                                                        prop.style [ style.backgroundColor colour ]
                                                                    ]
                                                                Html.span [ prop.text source ]
                                                            ]
                                                    ]
                                        ]
                                ]
                        ]
                ]

let areaChart (points: SeriesPoint[]) =
    if points.Length = 0 then
        emptyChart "No issuance data for this selection"
    else
        let maxValue = points |> Array.map (fun point -> point.Value) |> Array.max |> max 1m

        let plotWidth = chartWidth - padLeft - padRight
        let plotHeight = chartHeight - padTop - padBottom

        let x index =
            padLeft + (float index / float (max 1 (points.Length - 1))) * plotWidth

        let y value =
            padTop + plotHeight - (float value / float maxValue) * plotHeight

        let linePath =
            points
            |> Array.mapi (fun index point ->
                sprintf "%s %.2f %.2f" (if index = 0 then "M" else "L") (x index) (y point.Value))
            |> String.concat " "

        let areaPath =
            linePath
            + sprintf
                " L %.2f %.2f L %.2f %.2f Z"
                (x (points.Length - 1))
                (padTop + plotHeight)
                (x 0)
                (padTop + plotHeight)

        let labelIndices =
            if points.Length <= 7 then
                [| 0 .. points.Length - 1 |]
            else
                [|
                    0
                    points.Length / 4
                    points.Length / 2
                    (points.Length * 3) / 4
                    points.Length - 1
                |]
                |> Array.distinct

        Svg.svg
            [
                svg.viewBox (0, 0, int chartWidth, int chartHeight)
                svg.className "chart-svg"
                svg.children
                    [
                        Svg.defs
                            [
                                Svg.linearGradient
                                    [
                                        svg.id "issuanceFill"
                                        svg.x1 0.0
                                        svg.y1 0.0
                                        svg.x2 0.0
                                        svg.y2 1.0
                                        svg.children
                                            [
                                                Svg.stop
                                                    [ svg.offset 0.0; svg.stopColor "#2e5771"; svg.stopOpacity 0.34 ]
                                                Svg.stop
                                                    [ svg.offset 1.0; svg.stopColor "#2e5771"; svg.stopOpacity 0.02 ]
                                            ]
                                    ]
                            ]

                        for tick in 0..4 do
                            let value = maxValue * decimal tick / 4m
                            let yPos = y value

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
                                    svg.children [ Html.text (moneyCompact value) ]
                                ]

                        Svg.path [ svg.d areaPath; svg.fill "url(#issuanceFill)" ]
                        Svg.path
                            [
                                svg.d linePath
                                svg.fill "none"
                                svg.stroke "#2e5771"
                                svg.strokeWidth 2.6
                                svg.custom ("stroke-linecap", "round")
                                svg.custom ("stroke-linejoin", "round")
                            ]

                        for index in labelIndices do
                            if index >= 0 && index < points.Length then
                                let point = points.[index]

                                Svg.text
                                    [
                                        svg.x (x index)
                                        svg.y (chartHeight - 10.0)
                                        svg.custom ("text-anchor", "middle")
                                        svg.fill "#8e8c84"
                                        svg.fontSize 11.0
                                        svg.children [ Html.text (monthLabel point.Label) ]
                                    ]

                        for index in 0 .. points.Length - 1 do
                            let point = points.[index]

                            Svg.circle
                                [
                                    svg.cx (x index)
                                    svg.cy (y point.Value)
                                    svg.r (if points.Length > 120 then 0.0 else 3.0)
                                    svg.fill "#ffffff"
                                    svg.stroke "#2e5771"
                                    svg.strokeWidth 1.4
                                    svg.children
                                        [
                                            Svg.title (
                                                sprintf
                                                    "%s · %s · %d pools"
                                                    (monthLabel point.Label)
                                                    (moneyCompact point.Value)
                                                    point.Count
                                            )
                                        ]
                                ]
                    ]
            ]

let donut (items: (string * decimal)[]) =
    let total = items |> Array.sumBy snd

    if total <= 0m then
        emptyChart "No distribution data"
    else
        let radius = 62.0
        let diameter = radius * 2.0 * Math.PI
        let mutable offset = 0.0

        let segments =
            items
            |> Array.mapi (fun index (label, value) ->
                let share = float value / float total
                let length = share * diameter

                let segment =
                    Svg.circle
                        [
                            svg.cx 90.0
                            svg.cy 90.0
                            svg.r radius
                            svg.fill "none"
                            svg.stroke palette.[index % palette.Length]
                            svg.strokeWidth 26.0
                            svg.custom ("stroke-dasharray", sprintf "%.2f %.2f" length (diameter - length))
                            svg.strokeDashoffset (-offset)
                            svg.custom ("transform", "rotate(-90 90 90)")
                            svg.children [ Svg.title (sprintf "%s · %s" label (percent (share * 100.0 |> decimal))) ]
                        ]

                offset <- offset + length
                segment)

        Html.div
            [
                prop.className "donut-layout"
                prop.children
                    [
                        Svg.svg
                            [
                                svg.viewBox (0, 0, 180, 180)
                                svg.className "donut-svg"
                                svg.children
                                    [
                                        Svg.circle
                                            [
                                                svg.cx 90.0
                                                svg.cy 90.0
                                                svg.r radius
                                                svg.fill "none"
                                                svg.stroke "#f1ece3"
                                                svg.strokeWidth 26.0
                                            ]
                                        yield! segments
                                        Svg.text
                                            [
                                                svg.x 90.0
                                                svg.y 86.0
                                                svg.custom ("text-anchor", "middle")
                                                svg.fill "#3e3f3a"
                                                svg.fontSize 21.0
                                                svg.custom ("font-weight", "700")
                                                svg.children [ Html.text (moneyCompact total) ]
                                            ]
                                        Svg.text
                                            [
                                                svg.x 90.0
                                                svg.y 104.0
                                                svg.custom ("text-anchor", "middle")
                                                svg.fill "#8e8c84"
                                                svg.fontSize 10.0
                                                svg.children [ Html.text "TOTAL PRINCIPAL" ]
                                            ]
                                    ]
                            ]
                        Html.div
                            [
                                prop.className "donut-legend"
                                prop.children
                                    [
                                        for index, (label, value) in Array.indexed items do
                                            let share = if total = 0m then 0m else value / total * 100m

                                            Html.div
                                                [
                                                    prop.className "legend-row"
                                                    prop.children
                                                        [
                                                            Html.span
                                                                [
                                                                    prop.className "legend-dot"
                                                                    prop.style
                                                                        [
                                                                            style.backgroundColor
                                                                                palette.[index % palette.Length]
                                                                        ]
                                                                ]
                                                            Html.span [ prop.className "legend-label"; prop.text label ]
                                                            Html.span
                                                                [
                                                                    prop.className "legend-value"
                                                                    prop.text (percent1 share)
                                                                ]
                                                        ]
                                                ]
                                    ]
                            ]
                    ]
            ]

let verticalBarsWith (formatValue: decimal -> string) (points: SeriesPoint[]) =
    if points.Length = 0 then
        emptyChart "No maturity data"
    else
        let maxValue = points |> Array.map (fun point -> point.Value) |> Array.max |> max 1m
        let plotWidth = chartWidth - padLeft - padRight
        let plotHeight = chartHeight - padTop - padBottom
        let slot = plotWidth / float points.Length
        let barWidth = min 42.0 (slot * 0.62)

        let labelEvery = max 1 (int (Math.Ceiling(float points.Length / 12.0)))

        Svg.svg
            [
                svg.viewBox (0, 0, int chartWidth, int chartHeight)
                svg.className "chart-svg"
                svg.children
                    [
                        for tick in 0..3 do
                            let value = maxValue * decimal tick / 3m
                            let yPos = padTop + plotHeight - (float value / float maxValue) * plotHeight

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
                                    svg.children [ Html.text (formatValue value) ]
                                ]

                        for index in 0 .. points.Length - 1 do
                            let point = points.[index]
                            let barHeight = (float point.Value / float maxValue) * plotHeight
                            let xPos = padLeft + slot * float index + (slot - barWidth) / 2.0

                            Svg.rect
                                [
                                    svg.x xPos
                                    svg.y (padTop + plotHeight - barHeight)
                                    svg.width barWidth
                                    svg.height (max 1.0 barHeight)
                                    svg.rx 4.0
                                    svg.fill palette.[0]
                                    svg.children
                                        [
                                            Svg.title (
                                                sprintf
                                                    "%s · %s · %d pools"
                                                    point.Label
                                                    (formatValue point.Value)
                                                    point.Count
                                            )
                                        ]
                                ]

                            if index % labelEvery = 0 then
                                Svg.text
                                    [
                                        svg.x (xPos + barWidth / 2.0)
                                        svg.y (chartHeight - 10.0)
                                        svg.custom ("text-anchor", "middle")
                                        svg.fill "#8e8c84"
                                        svg.fontSize 11.0
                                        svg.children [ Html.text point.Label ]
                                    ]
                    ]
            ]

let verticalBars (points: SeriesPoint[]) = verticalBarsWith moneyCompact points

let verticalBarsCount (points: SeriesPoint[]) =
    verticalBarsWith (fun value -> number value) points

let horizontalBarsWith (formatValue: decimal -> string) (items: (string * decimal * string)[]) =
    if items.Length = 0 then
        emptyChart "No distribution data"
    else
        let maxValue =
            items |> Array.map (fun (_, value, _) -> value) |> Array.max |> max 1m

        Html.div
            [
                prop.className "hbar-list"
                prop.children
                    [
                        for label, value, note in items do
                            let width = float value / float maxValue * 100.0

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
                                                                    prop.text (
                                                                        sprintf "%s · %s" (formatValue value) note
                                                                    )
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
                                                                    prop.className "hbar-fill"
                                                                    prop.style [ style.width (length.percent width) ]
                                                                ]
                                                        ]
                                                ]
                                        ]
                                ]
                    ]
            ]

let horizontalBars (items: (string * decimal * string)[]) = horizontalBarsWith moneyCompact items

let private miniBarsCore
    (formatValue: decimal -> string)
    (tooltipForLabel: string -> string option)
    (items: CategoryPoint[])
    =
    if items.Length = 0 then
        emptyChart "No data"
    else
        let maxValue = items |> Array.map (fun point -> point.Value) |> Array.max |> max 1m

        Html.div
            [
                prop.className "mini-bars"
                prop.children
                    [
                        for index, point in Array.indexed items do
                            let rowProps =
                                [ prop.className "mini-bar-row" ]
                                @ (match tooltipForLabel point.Label with
                                   | Some text -> [ prop.title text ]
                                   | None -> [])

                            Html.div (
                                rowProps
                                @ [
                                    prop.children
                                        [
                                            Html.div
                                                [
                                                    prop.className "mini-bar-head"
                                                    prop.children
                                                        [
                                                            Html.span
                                                                [
                                                                    prop.className "mini-bar-label"
                                                                    prop.text point.Label
                                                                ]
                                                            Html.span
                                                                [
                                                                    prop.className "mini-bar-value"
                                                                    prop.text (formatValue point.Value)
                                                                ]
                                                        ]
                                                ]
                                            Html.div
                                                [
                                                    prop.className "mini-bar-track"
                                                    prop.children
                                                        [
                                                            Html.div
                                                                [
                                                                    prop.className "mini-bar-fill"
                                                                    prop.style
                                                                        [
                                                                            style.width (
                                                                                length.percent (
                                                                                    float point.Value / float maxValue
                                                                                    * 100.0
                                                                                )
                                                                            )
                                                                            style.backgroundColor
                                                                                palette.[index % palette.Length]
                                                                        ]
                                                                ]
                                                        ]
                                                ]
                                        ]
                                ]
                            )
                    ]
            ]

let miniBarsWith (formatValue: decimal -> string) (items: CategoryPoint[]) =
    miniBarsCore formatValue (fun _ -> None) items

let miniBars (items: CategoryPoint[]) = miniBarsWith moneyCompact items

let miniBarsCount (items: CategoryPoint[]) =
    miniBarsWith (fun value -> number value) items

/// Mini bars with a hover tooltip resolved from each category label.
let miniBarsWithTooltip (tooltipForLabel: string -> string option) (items: CategoryPoint[]) =
    miniBarsCore moneyCompact tooltipForLabel items

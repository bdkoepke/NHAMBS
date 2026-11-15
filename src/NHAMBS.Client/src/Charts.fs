module NHAMBS.Client.Charts

open System
open Feliz
open NHAMBS.Shared.Domain
open NHAMBS.Client.Formatting

let palette =
    [|
        "#18a999"
        "#4f8ef7"
        "#d8a84e"
        "#7c6ee6"
        "#e2725b"
        "#3fbf7f"
        "#5aa9c9"
        "#b56cd6"
        "#8d99ae"
    |]

let private chartWidth = 900.0
let private chartHeight = 280.0
let private padLeft = 62.0
let private padRight = 18.0
let private padTop = 16.0
let private padBottom = 34.0

let emptyChart (message: string) =
    Html.div [ prop.className "chart-empty"; prop.text message ]

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
                                                    [ svg.offset 0.0; svg.stopColor "#18a999"; svg.stopOpacity 0.34 ]
                                                Svg.stop
                                                    [ svg.offset 1.0; svg.stopColor "#18a999"; svg.stopOpacity 0.02 ]
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
                                    svg.stroke "#e7edf4"
                                    svg.strokeWidth 1.0
                                ]

                            Svg.text
                                [
                                    svg.x (padLeft - 9.0)
                                    svg.y (yPos + 4.0)
                                    svg.custom ("text-anchor", "end")
                                    svg.fill "#7b8794"
                                    svg.fontSize 11.0
                                    svg.children [ Html.text (moneyCompact value) ]
                                ]

                        Svg.path [ svg.d areaPath; svg.fill "url(#issuanceFill)" ]
                        Svg.path
                            [
                                svg.d linePath
                                svg.fill "none"
                                svg.stroke "#18a999"
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
                                        svg.fill "#7b8794"
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
                                    svg.stroke "#18a999"
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
                                                svg.stroke "#eef3f8"
                                                svg.strokeWidth 26.0
                                            ]
                                        yield! segments
                                        Svg.text
                                            [
                                                svg.x 90.0
                                                svg.y 86.0
                                                svg.custom ("text-anchor", "middle")
                                                svg.fill "#0b2239"
                                                svg.fontSize 21.0
                                                svg.custom ("font-weight", "700")
                                                svg.children [ Html.text (moneyCompact total) ]
                                            ]
                                        Svg.text
                                            [
                                                svg.x 90.0
                                                svg.y 104.0
                                                svg.custom ("text-anchor", "middle")
                                                svg.fill "#7b8794"
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
                                    svg.stroke "#e7edf4"
                                    svg.strokeWidth 1.0
                                ]

                            Svg.text
                                [
                                    svg.x (padLeft - 9.0)
                                    svg.y (yPos + 4.0)
                                    svg.custom ("text-anchor", "end")
                                    svg.fill "#7b8794"
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
                                    svg.fill palette.[4]
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
                                        svg.fill "#7b8794"
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

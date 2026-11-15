module NHAMBS.Client.Components

open System
open Feliz
open NHAMBS.Client.Formatting

type Page =
    | Overview
    | Pools
    | Cht
    | CoveredBonds
    | DataQuality

type Loadable<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | Failed of string

let pageTitle =
    function
    | Overview -> "Overview"
    | Pools -> "NHA MBS pools"
    | Cht -> "Canada Mortgage Bonds"
    | CoveredBonds -> "Covered bonds"
    | DataQuality -> "Data & ETL"

let pageHash =
    function
    | Overview -> "#/overview"
    | Pools -> "#/pools"
    | Cht -> "#/cht"
    | CoveredBonds -> "#/covered-bonds"
    | DataQuality -> "#/data"

let pageFromHash (hash: string) =
    match hash with
    | "#/pools" -> Pools
    | "#/cht" -> Cht
    | "#/covered-bonds" -> CoveredBonds
    | "#/data" -> DataQuality
    | _ -> Overview

let PageHeader (eyebrow: string) (title: string) (subtitle: string) (actions: ReactElement list) =
    Html.div
        [
            prop.className "page-header"
            prop.children
                [
                    Html.div
                        [
                            prop.children
                                [
                                    Html.div [ prop.className "eyebrow"; prop.text eyebrow ]
                                    Html.h1 [ prop.className "page-title"; prop.text title ]
                                    Html.p [ prop.className "page-subtitle"; prop.text subtitle ]
                                ]
                        ]
                    Html.div [ prop.className "page-actions"; prop.children actions ]
                ]
        ]

let Card (title: string) (subtitle: string) (actions: ReactElement list) (children: ReactElement list) =
    Html.section
        [
            prop.className "card"
            prop.children
                [
                    if not (String.IsNullOrWhiteSpace title) then
                        Html.div
                            [
                                prop.className "card-head"
                                prop.children
                                    [
                                        Html.div
                                            [
                                                prop.children
                                                    [
                                                        Html.h2 [ prop.className "card-title"; prop.text title ]
                                                        if not (String.IsNullOrWhiteSpace subtitle) then
                                                            Html.p
                                                                [ prop.className "card-subtitle"; prop.text subtitle ]
                                                    ]
                                            ]
                                        Html.div [ prop.className "card-actions"; prop.children actions ]
                                    ]
                            ]
                    Html.div [ prop.className "card-body"; prop.children children ]
                ]
        ]

let KpiCard (label: string) (value: string) (sub: string) (accent: string) =
    Html.div
        [
            prop.className "kpi-card"
            prop.style [ style.custom ("--accent", accent) ]
            prop.children
                [
                    Html.div [ prop.className "kpi-label"; prop.text label ]
                    Html.div [ prop.className "kpi-value"; prop.text value ]
                    Html.div [ prop.className "kpi-sub"; prop.text sub ]
                ]
        ]

let StatusBadge (status: string) =
    let className =
        match status.ToLowerInvariant() with
        | "succeeded" -> "badge badge-success"
        | "failed" -> "badge badge-danger"
        | "running" -> "badge badge-warning"
        | _ -> "badge"

    Html.span [ prop.className className; prop.text (titleCase status) ]

let LoadingBlock (label: string) =
    Html.div
        [
            prop.className "loading-block"
            prop.children [ Html.div [ prop.className "spinner" ]; Html.span [ prop.text label ] ]
        ]

let ErrorBlock (message: string) =
    Html.div
        [
            prop.className "error-block"
            prop.children
                [
                    Html.strong [ prop.text "Could not load data" ]
                    Html.span [ prop.text message ]
                ]
        ]

let LoadableBlock (label: string) (state: Loadable<'T>) (render: 'T -> ReactElement) =
    match state with
    | NotLoaded
    | Loading -> LoadingBlock label
    | Failed message -> ErrorBlock message
    | Loaded value -> render value

let Tabs (tabs: (string * string)[]) (active: string) (onSelect: string -> unit) =
    Html.div
        [
            prop.className "tabs"
            prop.children
                [
                    for key, label in tabs do
                        Html.button
                            [
                                prop.className (if key = active then "tab active" else "tab")
                                prop.onClick (fun _ -> onSelect key)
                                prop.children [ Html.span [ prop.text label ] ]
                            ]
                ]
        ]

let EmptyState (title: string) (message: string) =
    Html.div
        [
            prop.className "empty-state"
            prop.children
                [
                    Html.div [ prop.className "empty-icon"; prop.text "◇" ]
                    Html.strong [ prop.text title ]
                    Html.span [ prop.text message ]
                ]
        ]

let MiniLabel (label: string) (value: string) =
    Html.div
        [
            prop.className "mini-metric"
            prop.children
                [
                    Html.span [ prop.className "mini-metric-label"; prop.text label ]
                    Html.span [ prop.className "mini-metric-value"; prop.text value ]
                ]
        ]

let InlineLink (label: string) (url: string) =
    if String.IsNullOrWhiteSpace url then
        Html.span [ prop.className "muted"; prop.text "—" ]
    else
        Html.a
            [
                prop.className "inline-link"
                prop.href url
                prop.target.blank
                prop.rel "noopener"
                prop.text label
            ]

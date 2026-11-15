module NHAMBS.Client.DataQuality

open Feliz
open Fable.Core.JsInterop
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting

type DataQualityProps =
    {
        Freshness: Loadable<DatasetFreshness[]>
        OnRefresh: unit -> unit
    }

let private freshnessRows (freshness: DatasetFreshness[]) =
    freshness
    |> Array.map (fun dataset ->
        createObj
            [
                "dataset" ==> dataset.DisplayName
                "status" ==> titleCase dataset.Status
                "lastLoaded" ==> dataset.LastLoaded
                "files" ==> dataset.FilesProcessed
                "rows" ==> dataset.RowsLoaded
                "duration" ==> dataset.DurationSeconds
            ])

let private freshnessColumns =
    [|
        AgGrid.textColumn "dataset" "Dataset" 220
        AgGrid.textColumn "status" "Status" 110
        AgGrid.textColumn "lastLoaded" "Last loaded" 180
        AgGrid.numberColumn "files" "Files processed" 140
        AgGrid.numberColumn "rows" "Rows loaded" 140
        AgGrid.numberColumn "duration" "Duration (s)" 120
    |]

let private pipelineStep (index: string) (title: string) (body: string) =
    Html.div
        [
            prop.className "pipeline-step"
            prop.children
                [
                    Html.div [ prop.className "pipeline-index"; prop.text index ]
                    Html.strong [ prop.text title ]
                    Html.span [ prop.text body ]
                ]
        ]

[<ReactComponent>]
let DataQualityPage (props: DataQualityProps) =
    Html.div
        [
            prop.className "page"
            prop.children
                [
                    PageHeader
                        "Operational transparency"
                        "Data & ETL"
                        "How the dashboard is loaded, where each record comes from and when the database was last refreshed."
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
                            prop.className "pipeline"
                            prop.children
                                [
                                    pipelineStep
                                        "01"
                                        "Source documents"
                                        "CMHC NHA MBS information circulars, CHT debt issue list, offering circulars, bond-purchase cancellation history and the covered bond registry."
                                    pipelineStep
                                        "02"
                                        "Parsed JSON"
                                        "Existing parses under out/pools, out/debt-issues.json, out/cht_circulars, out/cht_history and out/covered_bonds are treated as immutable inputs."
                                    pipelineStep
                                        "03"
                                        "Elsa ETL workflow"
                                        "NHAMBS.Etl runs an Elsa workflow from NHAMBS.Workflows; the activities validate and bulk-load PostgreSQL with Npgsql COPY, recording every run in nhambs.etl_runs."
                                    pipelineStep
                                        "04"
                                        "Dashboard API"
                                        "Saturn exposes a Fable.Remoting contract. SQLProvider reads typed entities; parameterized reporting queries power filters and aggregates."
                                    pipelineStep
                                        "05"
                                        "Fable client"
                                        "Elmish and Feliz render the dashboard, with AG Grid for searchable tabular data and SVG charts for market aggregates."
                                ]
                        ]

                    LoadableBlock "Loading ETL freshness" props.Freshness (fun freshness ->
                        Html.div
                            [
                                prop.children
                                    [
                                        Html.div
                                            [
                                                prop.className "freshness-grid freshness-grid-large"
                                                prop.children
                                                    [
                                                        for dataset in freshness do
                                                            Html.div
                                                                [
                                                                    prop.className "freshness-card"
                                                                    prop.children
                                                                        [
                                                                            Html.div
                                                                                [
                                                                                    prop.className "freshness-head"
                                                                                    prop.children
                                                                                        [
                                                                                            Html.strong
                                                                                                [
                                                                                                    prop.text
                                                                                                        dataset
                                                                                                            .DisplayName
                                                                                                ]
                                                                                            StatusBadge dataset.Status
                                                                                        ]
                                                                                ]
                                                                            Html.div
                                                                                [
                                                                                    prop.className "freshness-time"
                                                                                    prop.text dataset.LastLoaded
                                                                                ]
                                                                            Html.div
                                                                                [
                                                                                    prop.className "freshness-meta"
                                                                                    prop.children
                                                                                        [
                                                                                            Html.span
                                                                                                [
                                                                                                    prop.text (
                                                                                                        sprintf
                                                                                                            "%s rows"
                                                                                                            (number (
                                                                                                                decimal
                                                                                                                    dataset
                                                                                                                        .RowsLoaded
                                                                                                            ))
                                                                                                    )
                                                                                                ]
                                                                                            Html.span
                                                                                                [
                                                                                                    prop.text (
                                                                                                        sprintf
                                                                                                            "%d source files"
                                                                                                            dataset
                                                                                                                .FilesProcessed
                                                                                                    )
                                                                                                ]
                                                                                            Html.span
                                                                                                [
                                                                                                    prop.text (
                                                                                                        sprintf
                                                                                                            "%ss"
                                                                                                            (dataset
                                                                                                                .DurationSeconds
                                                                                                                .ToString(
                                                                                                                    "N1"
                                                                                                                ))
                                                                                                    )
                                                                                                ]
                                                                                        ]
                                                                                ]
                                                                        ]
                                                                ]
                                                    ]
                                            ]

                                        Card
                                            "Latest ETL runs"
                                            "One row per dataset, ordered by the most recent loader execution"
                                            []
                                            [
                                                AgGrid.AgGrid
                                                    {
                                                        Columns = freshnessColumns
                                                        Rows = freshnessRows freshness
                                                        Height = "300px"
                                                        OnRowClicked = ignore
                                                    }
                                            ]
                                    ]
                            ])

                    Html.div
                        [
                            prop.className "overview-grid overview-grid-2"
                            prop.children
                                [
                                    Card
                                        "Database design"
                                        "PostgreSQL schema nhambs"
                                        []
                                        [
                                            Html.dl
                                                [
                                                    prop.className "detail-list"
                                                    prop.children
                                                        [
                                                            Html.dt [ prop.text "mbs_pools" ]
                                                            Html.dd
                                                                [
                                                                    prop.text
                                                                        "One row per NHA MBS pool, keyed by CUSIP, including coupon, principal, dates, issuer and loan counts."
                                                                ]
                                                            Html.dt [ prop.text "mbs_pool_insurers / locations" ]
                                                            Html.dd
                                                                [
                                                                    prop.text
                                                                        "Insurer and provincial allocations used by dashboard filters and weighted aggregates."
                                                                ]
                                                            Html.dt
                                                                [ prop.text "mbs_pool_scheduled_maturity / rate_bands" ]
                                                            Html.dd
                                                                [
                                                                    prop.text
                                                                        "Cash-flow and interest-rate reset detail from the circular addendum."
                                                                ]
                                                            Html.dt [ prop.text "cht_issues / cht_circulars" ]
                                                            Html.dd
                                                                [
                                                                    prop.text
                                                                        "CHT outstanding issues and the parsed offering circular library with ratings and identifiers."
                                                                ]
                                                            Html.dt
                                                                [ prop.text "cht_outstanding_history / cancellations" ]
                                                            Html.dd
                                                                [
                                                                    prop.text
                                                                        "Per-series history of bond purchases for cancellation, including cancellation periods and amounts."
                                                                ]
                                                            Html.dt [ prop.text "covered_bond_programs / bonds" ]
                                                            Html.dd
                                                                [
                                                                    prop.text
                                                                        "Registered covered bond programmes, parties, series, ISINs and registry metadata."
                                                                ]
                                                            Html.dt [ prop.text "etl_runs" ]
                                                            Html.dd
                                                                [
                                                                    prop.text
                                                                        "Audit trail for every loader execution, including counts, duration and failure details."
                                                                ]
                                                        ]
                                                ]
                                        ]
                                    Card
                                        "Reproduce locally"
                                        "Commands used to build and refresh the dashboard"
                                        []
                                        [
                                            Html.pre
                                                [
                                                    prop.className "code-block"
                                                    prop.text (
                                                        String.concat
                                                            "\n"
                                                            [
                                                                "# 1. Infrastructure (Pulumi, Docker provider)"
                                                                "cd src/NHAMBS.Pulumi && pulumi up"
                                                                ""
                                                                "# 2. Schema"
                                                                "dotnet run --project src/NHAMBS.Migrations"
                                                                ""
                                                                "# 3. Load JSON extracts"
                                                                "dotnet run --project src/NHAMBS.Etl -- --dataset all"
                                                                ""
                                                                "# 4. API"
                                                                "dotnet run --project src/NHAMBS.Server"
                                                                ""
                                                                "# 5. Client (dev)"
                                                                "cd src/NHAMBS.Client && npm install && npm run dev"
                                                            ]
                                                    )
                                                ]
                                        ]
                                ]
                        ]
                ]
        ]

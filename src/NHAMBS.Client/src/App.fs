module NHAMBS.Client.App

open System
open Feliz
open Elmish
open Elmish.React
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting

type Model =
    {
        Page: Page
        Overview: Loadable<OverviewData>
        Cht: Loadable<ChtData>
        Covered: Loadable<CoveredBondData>
        Freshness: Loadable<DatasetFreshness[]>
        Filters: Loadable<MbsFilterOptions>
        Analytics: Loadable<MbsAnalytics>
        Pools: Loadable<MbsPoolPage>
        Filter: MbsPoolFilter
        SelectedPool: string option
        Detail: Loadable<MbsPoolDetail[]>
        MenuOpen: bool
    }

type Msg =
    | Navigate of Page
    | ToggleMenu
    | Refresh
    | OverviewLoaded of Result<OverviewData, string>
    | ChtLoaded of Result<ChtData, string>
    | CoveredLoaded of Result<CoveredBondData, string>
    | FreshnessLoaded of Result<DatasetFreshness[], string>
    | FiltersLoaded of Result<MbsFilterOptions, string>
    | AnalyticsLoaded of Result<MbsAnalytics, string>
    | PoolsLoaded of Result<MbsPoolPage, string>
    | DetailLoaded of Result<MbsPoolDetail[], string>
    | FilterChanged of MbsPoolFilter
    | ApplyFilters
    | ResetFilters
    | SelectPool of string
    | CloseDetail

let private emptyModel =
    {
        Page = Overview
        Overview = NotLoaded
        Cht = NotLoaded
        Covered = NotLoaded
        Freshness = NotLoaded
        Filters = NotLoaded
        Analytics = NotLoaded
        Pools = NotLoaded
        Filter = emptyMbsFilter
        SelectedPool = None
        Detail = NotLoaded
        MenuOpen = false
    }

let private loadOverview =
    Cmd.OfAsync.either (fun () -> Api.api.GetOverview()) () (Ok >> OverviewLoaded) (fun ex ->
        OverviewLoaded(Error ex.Message))

let private loadCht =
    Cmd.OfAsync.either (fun () -> Api.api.GetCht()) () (Ok >> ChtLoaded) (fun ex -> ChtLoaded(Error ex.Message))

let private loadCovered =
    Cmd.OfAsync.either (fun () -> Api.api.GetCoveredBonds()) () (Ok >> CoveredLoaded) (fun ex ->
        CoveredLoaded(Error ex.Message))

let private loadFreshness =
    Cmd.OfAsync.either (fun () -> Api.api.GetFreshness()) () (Ok >> FreshnessLoaded) (fun ex ->
        FreshnessLoaded(Error ex.Message))

let private loadFilters =
    Cmd.OfAsync.either (fun () -> Api.api.GetMbsFilterOptions()) () (Ok >> FiltersLoaded) (fun ex ->
        FiltersLoaded(Error ex.Message))

let private loadAnalytics filter =
    Cmd.OfAsync.either (fun () -> Api.api.GetMbsAnalytics filter) () (Ok >> AnalyticsLoaded) (fun ex ->
        AnalyticsLoaded(Error ex.Message))

let private loadPools filter =
    let query =
        {
            Filter = filter
            Offset = 0
            Limit = 20000
        }

    Cmd.OfAsync.either (fun () -> Api.api.GetMbsPools query) () (Ok >> PoolsLoaded) (fun ex ->
        PoolsLoaded(Error ex.Message))

let private loadPoolData filter =
    Cmd.batch [ loadAnalytics filter; loadPools filter ]

let private loadDetail cusip =
    Cmd.OfAsync.either (fun () -> Api.api.GetMbsPool cusip) () (Ok >> DetailLoaded) (fun ex ->
        DetailLoaded(Error ex.Message))

let init () =
    let page = Browser.Dom.window.location.hash |> pageFromHash

    let pageCommand =
        match page with
        | Overview -> loadOverview
        | Pools -> Cmd.batch [ loadFilters; loadPoolData emptyMbsFilter ]
        | Cht -> loadCht
        | CoveredBonds -> loadCovered
        | DataQuality -> Cmd.none

    { emptyModel with Page = page }, Cmd.batch [ loadFreshness; pageCommand ]

let private refreshPage model =
    match model.Page with
    | Overview -> Cmd.batch [ loadOverview; loadFreshness ]
    | Pools -> Cmd.batch [ loadFilters; loadFreshness; loadPoolData model.Filter ]
    | Cht -> Cmd.batch [ loadCht; loadFreshness ]
    | CoveredBonds -> Cmd.batch [ loadCovered; loadFreshness ]
    | DataQuality -> loadFreshness

let update (msg: Msg) (model: Model) =
    match msg with
    | Navigate page ->
        if model.Page = page then
            model, Cmd.none
        else
            Interop.setHash (pageHash page)
            Interop.scrollToTop ()

            let model =
                { model with
                    Page = page
                    MenuOpen = false
                }

            let command =
                match page with
                | Overview when model.Overview = NotLoaded -> Cmd.batch [ loadOverview; loadFreshness ]
                | Pools when model.Filters = NotLoaded || model.Pools = NotLoaded ->
                    Cmd.batch [ loadFilters; loadPoolData model.Filter ]
                | Cht when model.Cht = NotLoaded -> loadCht
                | CoveredBonds when model.Covered = NotLoaded -> loadCovered
                | DataQuality when model.Freshness = NotLoaded -> loadFreshness
                | _ -> Cmd.none

            model, command

    | ToggleMenu ->
        { model with
            MenuOpen = not model.MenuOpen
        },
        Cmd.none

    | Refresh -> model, refreshPage model

    | OverviewLoaded(Ok data) -> { model with Overview = Loaded data }, Cmd.none
    | OverviewLoaded(Error message) -> { model with Overview = Failed message }, Cmd.none

    | ChtLoaded(Ok data) -> { model with Cht = Loaded data }, Cmd.none
    | ChtLoaded(Error message) -> { model with Cht = Failed message }, Cmd.none

    | CoveredLoaded(Ok data) -> { model with Covered = Loaded data }, Cmd.none
    | CoveredLoaded(Error message) -> { model with Covered = Failed message }, Cmd.none

    | FreshnessLoaded(Ok data) -> { model with Freshness = Loaded data }, Cmd.none
    | FreshnessLoaded(Error message) ->
        { model with
            Freshness = Failed message
        },
        Cmd.none

    | FiltersLoaded(Ok data) -> { model with Filters = Loaded data }, Cmd.none
    | FiltersLoaded(Error message) -> { model with Filters = Failed message }, Cmd.none

    | AnalyticsLoaded(Ok data) -> { model with Analytics = Loaded data }, Cmd.none
    | AnalyticsLoaded(Error message) ->
        { model with
            Analytics = Failed message
        },
        Cmd.none

    | PoolsLoaded(Ok data) -> { model with Pools = Loaded data }, Cmd.none
    | PoolsLoaded(Error message) -> { model with Pools = Failed message }, Cmd.none

    | DetailLoaded(Ok data) -> { model with Detail = Loaded data }, Cmd.none
    | DetailLoaded(Error message) -> { model with Detail = Failed message }, Cmd.none

    | FilterChanged filter -> { model with Filter = filter }, Cmd.none

    | ApplyFilters ->
        { model with
            Analytics = Loading
            Pools = Loading
        },
        loadPoolData model.Filter

    | ResetFilters ->
        { model with
            Filter = emptyMbsFilter
            Analytics = Loading
            Pools = Loading
        },
        loadPoolData emptyMbsFilter

    | SelectPool cusip ->
        { model with
            SelectedPool = Some cusip
            Detail = Loading
        },
        loadDetail cusip

    | CloseDetail ->
        { model with
            SelectedPool = None
            Detail = NotLoaded
        },
        Cmd.none

let subscriptions (_: Model) : Sub<Msg> =
    [
        ([ "hash-change" ],
         fun dispatch ->
             let handler _ =
                 let page = Browser.Dom.window.location.hash |> pageFromHash
                 dispatch (Navigate page)

             Browser.Dom.window.addEventListener ("hashchange", handler)

             { new IDisposable with
                 member _.Dispose() =
                     Browser.Dom.window.removeEventListener ("hashchange", handler)
             })
    ]

let private navItem (page: Page) (icon: string) (dispatch: Msg -> unit) (activePage: Page) =
    Html.button
        [
            prop.className (if page = activePage then "nav-item active" else "nav-item")
            prop.onClick (fun _ -> dispatch (Navigate page))
            prop.children
                [
                    Html.span [ prop.className "nav-icon"; prop.text icon ]
                    Html.span [ prop.className "nav-label"; prop.text (pageTitle page) ]
                ]
        ]

let private latestFreshness (freshness: Loadable<DatasetFreshness[]>) =
    match freshness with
    | Loaded values ->
        values
        |> Array.sortByDescending (fun dataset -> dataset.LastLoaded)
        |> Array.tryHead
        |> Option.map (fun dataset -> dataset.LastLoaded)
        |> Option.defaultValue "Not loaded"
    | _ -> "Loading…"

let private shell (model: Model) (dispatch: Msg -> unit) =
    let page =
        match model.Page with
        | Overview ->
            LoadableBlock "Loading market overview" model.Overview (fun data ->
                Overview.OverviewPage
                    {
                        Data = data
                        OnNavigate = Navigate >> dispatch
                        OnRefresh = fun () -> dispatch Refresh
                    })
        | Pools ->
            Pools.PoolsPage
                {
                    Analytics = model.Analytics
                    Page = model.Pools
                    Filter = model.Filter
                    Filters = model.Filters
                    SelectedPool = model.SelectedPool
                    Detail = model.Detail
                    OnFilterChange = FilterChanged >> dispatch
                    OnApply = fun () -> dispatch ApplyFilters
                    OnReset = fun () -> dispatch ResetFilters
                    OnSelectPool = SelectPool >> dispatch
                    OnCloseDetail = fun () -> dispatch CloseDetail
                    OnRefresh = fun () -> dispatch Refresh
                }
        | Cht ->
            Cht.ChtPage
                {
                    Data = model.Cht
                    OnRefresh = fun () -> dispatch Refresh
                }
        | CoveredBonds ->
            CoveredBonds.CoveredBondsPage
                {
                    Data = model.Covered
                    OnRefresh = fun () -> dispatch Refresh
                }
        | DataQuality ->
            DataQuality.DataQualityPage
                {
                    Freshness = model.Freshness
                    OnRefresh = fun () -> dispatch Refresh
                }

    Html.div
        [
            prop.className (
                if model.MenuOpen then
                    "app-shell menu-open"
                else
                    "app-shell"
            )
            prop.children
                [
                    Html.aside
                        [
                            prop.className "sidebar"
                            prop.children
                                [
                                    Html.div
                                        [
                                            prop.className "brand"
                                            prop.children
                                                [
                                                    Html.div [ prop.className "brand-mark"; prop.text "N" ]
                                                    Html.div
                                                        [
                                                            prop.children
                                                                [
                                                                    Html.div
                                                                        [
                                                                            prop.className "brand-name"
                                                                            prop.text "NHA MBS"
                                                                        ]
                                                                    Html.div
                                                                        [
                                                                            prop.className "brand-sub"
                                                                            prop.text "Securitization intelligence"
                                                                        ]
                                                                ]
                                                        ]
                                                ]
                                        ]

                                    Html.nav
                                        [
                                            prop.className "nav"
                                            prop.children
                                                [
                                                    Html.div [ prop.className "nav-section"; prop.text "Market" ]
                                                    navItem Overview "◫" dispatch model.Page
                                                    navItem Pools "▦" dispatch model.Page
                                                    Html.div [ prop.className "nav-section"; prop.text "Securities" ]
                                                    navItem Cht "◆" dispatch model.Page
                                                    navItem CoveredBonds "◇" dispatch model.Page
                                                    Html.div [ prop.className "nav-section"; prop.text "Operations" ]
                                                    navItem DataQuality "⚙" dispatch model.Page
                                                ]
                                        ]

                                    Html.div
                                        [
                                            prop.className "sidebar-footer"
                                            prop.children
                                                [
                                                    Html.div
                                                        [ prop.className "sidebar-footer-label"; prop.text "Database" ]
                                                    Html.div
                                                        [
                                                            prop.className "sidebar-footer-value"
                                                            prop.text "PostgreSQL · SQLProvider"
                                                        ]
                                                    Html.div
                                                        [
                                                            prop.className "sidebar-footer-label"
                                                            prop.text "Last ETL refresh"
                                                        ]
                                                    Html.div
                                                        [
                                                            prop.className "sidebar-footer-value"
                                                            prop.text (latestFreshness model.Freshness)
                                                        ]
                                                    Html.div
                                                        [
                                                            prop.className "sidebar-stack"
                                                            prop.text "Saturn · Fable · Feliz · AG Grid"
                                                        ]
                                                ]
                                        ]
                                ]
                        ]

                    Html.div
                        [
                            prop.className "main"
                            prop.children
                                [
                                    Html.header
                                        [
                                            prop.className "topbar"
                                            prop.children
                                                [
                                                    Html.button
                                                        [
                                                            prop.className "menu-toggle"
                                                            prop.onClick (fun _ -> dispatch ToggleMenu)
                                                            prop.text "☰"
                                                        ]
                                                    Html.div
                                                        [
                                                            prop.className "breadcrumb"
                                                            prop.children
                                                                [
                                                                    Html.span [ prop.text "NHA MBS" ]
                                                                    Html.span
                                                                        [
                                                                            prop.className "breadcrumb-sep"
                                                                            prop.text "/"
                                                                        ]
                                                                    Html.strong [ prop.text (pageTitle model.Page) ]
                                                                ]
                                                        ]
                                                    Html.div
                                                        [
                                                            prop.className "topbar-right"
                                                            prop.children
                                                                [
                                                                    Html.div
                                                                        [
                                                                            prop.className "live-pill"
                                                                            prop.children
                                                                                [
                                                                                    Html.span
                                                                                        [ prop.className "live-dot" ]
                                                                                    Html.span
                                                                                        [
                                                                                            prop.text
                                                                                                "PostgreSQL connected"
                                                                                        ]
                                                                                ]
                                                                        ]
                                                                    Html.button
                                                                        [
                                                                            prop.className "icon-button"
                                                                            prop.title "Refresh data"
                                                                            prop.onClick (fun _ -> dispatch Refresh)
                                                                            prop.text "↻"
                                                                        ]
                                                                ]
                                                        ]
                                                ]
                                        ]

                                    Html.main [ prop.className "content"; prop.children [ page ] ]
                                ]
                        ]
                ]
        ]

let view (model: Model) (dispatch: Msg -> unit) = shell model dispatch

let start () =
    Program.mkProgram init update view
    |> Program.withSubscription subscriptions
    |> Program.withReactBatched "app"
    |> Program.withConsoleTrace
    |> Program.run

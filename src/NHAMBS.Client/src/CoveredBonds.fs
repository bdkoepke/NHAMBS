module NHAMBS.Client.CoveredBonds

open System
open Feliz
open Fable.Core.JsInterop
open NHAMBS.Shared.Domain
open NHAMBS.Client
open NHAMBS.Client.Components
open NHAMBS.Client.Formatting
open NHAMBS.Client.Charts

type CoveredBondsProps =
    {
        Data: Loadable<CoveredBondData>
        OnRefresh: unit -> unit
    }

let private programRows (programs: CoveredBondProgramRow[]) =
    programs
    |> Array.map (fun program ->
        createObj
            [
                "issuer" ==> program.IssuerName
                "program" ==> program.ProgramName
                "minimum" ==> program.MinimumAssetPercentage
                "maximum" ==> program.MaximumAssetPercentage
                "bonds" ==> program.BondCount
                "currencies" ==> program.Currencies
                "earliest" ==> program.EarliestMaturity
                "latest" ==> program.LatestMaturity
                "sourceUrl" ==> program.SourceUrl
            ])

let private programColumns =
    [|
        AgGrid.textColumn "issuer" "Issuer" 230
        AgGrid.textColumn "program" "Programme" 280
        AgGrid.rateColumn "minimum" "Min asset %" 110
        AgGrid.rateColumn "maximum" "Max asset %" 110
        AgGrid.numberColumn "bonds" "Bonds" 90
        AgGrid.textColumn "currencies" "Currencies" 160
        AgGrid.dateColumn "earliest" "Earliest maturity" 145
        AgGrid.dateColumn "latest" "Latest maturity" 140
        AgGrid.linkColumn "sourceUrl" "Source" 100
    |]

let private bondRows (bonds: CoveredBondRow[]) =
    bonds
    |> Array.map (fun bond ->
        createObj
            [
                "issuer" ==> bond.IssuerName
                "program" ==> bond.ProgramName
                "series" ==> bond.SeriesDesignation
                "currency" ==> bond.IssueCurrency
                "nominal" ==> bond.NominalAmount
                "isin" ==> bond.Isin
                "coupon" ==> bond.CouponDisplay
                "couponValue" ==> bond.CouponValue
                "issueDate" ==> bond.IssueDate
                "maturityDate" ==> bond.MaturityDate
                "extendedMaturityDate" ==> bond.ExtendedMaturityDate
                "bullet" ==> titleCase bond.BulletType
                "frequency" ==> titleCase bond.InterestPaymentFrequency
                "listing" ==> bond.ListingAuthority
            ])

let private bondColumns =
    [|
        AgGrid.textColumn "issuer" "Issuer" 210
        AgGrid.textColumn "program" "Programme" 240
        AgGrid.textColumn "series" "Series" 90
        AgGrid.textColumn "currency" "Ccy" 70
        AgGrid.moneyColumn "nominal" "Nominal" 150
        AgGrid.textColumn "isin" "ISIN" 170
        AgGrid.textColumn "coupon" "Coupon" 90
        AgGrid.rateColumn "couponValue" "Rate" 85
        AgGrid.dateColumn "issueDate" "Issued" 115
        AgGrid.dateColumn "maturityDate" "Matures" 115
        AgGrid.dateColumn "extendedMaturityDate" "Extended" 115
        AgGrid.textColumn "bullet" "Bullet" 80
        AgGrid.textColumn "frequency" "Frequency" 105
        AgGrid.textColumn "listing" "Listing" 150
    |]

let private absoluteUrl (url: string) =
    if String.IsNullOrWhiteSpace url then
        ""
    elif url.StartsWith("http", StringComparison.OrdinalIgnoreCase) then
        url
    else
        "https://www.cmhc-schl.gc.ca" + url

let private registryRows (registry: CoveredBondRegistryRow[]) =
    registry
    |> Array.map (fun issuer ->
        createObj
            [
                "name" ==> issuer.Name
                "status" ==> titleCase issuer.Status
                "registrationDate" ==> issuer.RegistrationDate
                "program" ==> issuer.ProgramName
                "minimum" ==> issuer.MinimumAssetPercentage
                "maximum" ==> issuer.MaximumAssetPercentage
                "bonds" ==> issuer.BondCount
                "address" ==> issuer.BusinessAddress
                "websiteUrl" ==> absoluteUrl issuer.WebsiteUrl
                "programPage" ==> absoluteUrl issuer.ProgramPage
            ])

let private registryColumns =
    [|
        AgGrid.textColumn "name" "Registered issuer" 240
        AgGrid.textColumn "status" "Status" 90
        AgGrid.dateColumn "registrationDate" "Registered" 120
        AgGrid.textColumn "program" "Programme" 250
        AgGrid.numberColumn "bonds" "Bonds" 90
        AgGrid.textColumn "address" "Business address" 300
        AgGrid.linkColumn "websiteUrl" "Website" 100
        AgGrid.linkColumn "programPage" "Programme page" 125
    |]

[<ReactComponent>]
let CoveredBondsPage (props: CoveredBondsProps) =
    let tab, setTab = React.useState "programs"

    Html.div
        [
            prop.className "page"
            prop.children
                [
                    PageHeader
                        "Canadian registered covered bonds"
                        "Covered bond registry"
                        "Registered programmes, outstanding series and the full CMHC covered bond registry."
                        [
                            Html.button
                                [
                                    prop.className "btn btn-ghost"
                                    prop.onClick (fun _ -> props.OnRefresh())
                                    prop.children [ Html.span [ prop.text "↻" ]; Html.span [ prop.text "Refresh" ] ]
                                ]
                        ]

                    LoadableBlock "Loading covered bond data" props.Data (fun data ->
                        let analytics = data.Analytics

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
                                                            "Registered programmes"
                                                            (number (decimal analytics.ProgramCount))
                                                            (sprintf "%d issuers" analytics.IssuerCount)
                                                            "#2e5771"
                                                        KpiCard
                                                            "Bonds"
                                                            (number (decimal analytics.BondCount))
                                                            "Outstanding series in the registry"
                                                            "#f47c3c"
                                                        KpiCard
                                                            "Currencies"
                                                            (number (decimal analytics.CurrencyCount))
                                                            "Issuance currencies represented"
                                                            "#93c54b"
                                                        KpiCard
                                                            "Earliest maturity"
                                                            (dateShort analytics.EarliestMaturity)
                                                            "Nearest outstanding bond"
                                                            "#29abe0"
                                                        KpiCard
                                                            "Latest maturity"
                                                            (dateShort analytics.LatestMaturity)
                                                            "Longest-dated bond"
                                                            "#d9534f"
                                                    ]
                                            ]

                                        Html.div
                                            [
                                                prop.className "pool-chart-grid"
                                                prop.children
                                                    [
                                                        Card
                                                            "Nominal by currency"
                                                            "Outstanding series nominal amount by issuance currency"
                                                            []
                                                            [
                                                                horizontalBarsWith
                                                                    numberCompact
                                                                    (analytics.ByCurrency
                                                                     |> Array.map (fun point ->
                                                                         point.Label,
                                                                         point.Value,
                                                                         sprintf "%d bonds" point.Count))
                                                            ]
                                                        Card
                                                            "Bonds by maturity year"
                                                            "Series count by contractual maturity"
                                                            []
                                                            [ verticalBarsCount analytics.ByMaturityYear ]
                                                        Card
                                                            "Programme scale"
                                                            "Series count by issuer programme"
                                                            []
                                                            [ miniBarsCount analytics.ByProgram ]
                                                        Card
                                                            "Registry coverage"
                                                            "CMHC registered covered bond programmes"
                                                            []
                                                            [
                                                                Html.div
                                                                    [
                                                                        prop.className "split-metrics"
                                                                        prop.children
                                                                            [
                                                                                MiniLabel
                                                                                    "Programmes"
                                                                                    (number (
                                                                                        decimal analytics.ProgramCount
                                                                                    ))
                                                                                MiniLabel
                                                                                    "Bonds"
                                                                                    (number (
                                                                                        decimal analytics.BondCount
                                                                                    ))
                                                                                MiniLabel
                                                                                    "Currencies"
                                                                                    (number (
                                                                                        decimal analytics.CurrencyCount
                                                                                    ))
                                                                                MiniLabel
                                                                                    "Earliest"
                                                                                    (dateShort
                                                                                        analytics.EarliestMaturity)
                                                                                MiniLabel
                                                                                    "Latest"
                                                                                    (dateShort analytics.LatestMaturity)
                                                                            ]
                                                                    ]
                                                                Html.p
                                                                    [
                                                                        prop.className "muted"
                                                                        prop.text
                                                                            "Nominal amounts are shown in their issuance currency; the dashboard does not convert across currencies."
                                                                    ]
                                                            ]
                                                    ]
                                            ]

                                        Card
                                            ""
                                            ""
                                            []
                                            [
                                                Tabs
                                                    [|
                                                        "programs", sprintf "Programmes (%d)" data.Programs.Length
                                                        "bonds", sprintf "Bonds (%d)" data.Bonds.Length
                                                        "registry", sprintf "Registry (%d)" data.Registry.Length
                                                    |]
                                                    tab
                                                    setTab

                                                if tab = "programs" then
                                                    AgGrid.AgGrid
                                                        {
                                                            Columns = programColumns
                                                            Rows = programRows data.Programs
                                                            Height = "500px"
                                                            Pagination = true
                                                            InitialSortModel = null
                                                            InitialFilterModel = null
                                                            ApplyQuickFilterInternally = true
                                                            HeaderGroupFields = [||]
                                                            OnSortModelChanged = ignore
                                                            OnFilterModelChanged = ignore
                                                            OnQuickFilterChanged = ignore
                                                            OnRowClicked = ignore
                                                        }
                                                elif tab = "bonds" then
                                                    AgGrid.AgGrid
                                                        {
                                                            Columns = bondColumns
                                                            Rows = bondRows data.Bonds
                                                            Height = "640px"
                                                            Pagination = true
                                                            InitialSortModel = null
                                                            InitialFilterModel = null
                                                            ApplyQuickFilterInternally = true
                                                            HeaderGroupFields = [||]
                                                            OnSortModelChanged = ignore
                                                            OnFilterModelChanged = ignore
                                                            OnQuickFilterChanged = ignore
                                                            OnRowClicked = ignore
                                                        }
                                                else
                                                    AgGrid.AgGrid
                                                        {
                                                            Columns = registryColumns
                                                            Rows = registryRows data.Registry
                                                            Height = "460px"
                                                            Pagination = true
                                                            InitialSortModel = null
                                                            InitialFilterModel = null
                                                            ApplyQuickFilterInternally = true
                                                            HeaderGroupFields = [||]
                                                            OnSortModelChanged = ignore
                                                            OnFilterModelChanged = ignore
                                                            OnQuickFilterChanged = ignore
                                                            OnRowClicked = ignore
                                                        }
                                            ]
                                    ]
                            ])
                ]
        ]

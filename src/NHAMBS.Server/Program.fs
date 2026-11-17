module NHAMBS.Server.Program

open System
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open Saturn
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open NHAMBS.Shared
open NHAMBS.Shared.Domain
open NHAMBS.Data

let private dashboardApi: DashboardApi =
    {
        GetOverview =
            fun () ->
                async {
                    let context = Provider.dataContext ()
                    let mbs = MbsRepository.analytics context emptyMbsFilter
                    let cht = (ChtRepository.getData context).Analytics
                    let covered = (CoveredBondRepository.getData context).Analytics
                    let freshness = EtlRepository.getFreshness context

                    return
                        {
                            Mbs = mbs
                            Cht = cht
                            CoveredBonds = covered
                            Freshness = freshness
                        }
                }
        GetMbsAnalytics =
            fun filter ->
                async {
                    let context = Provider.dataContext ()
                    return MbsRepository.analytics context filter
                }
        GetMbsPools =
            fun query ->
                async {
                    let context = Provider.dataContext ()
                    return MbsRepository.search context query
                }
        GetMbsPool =
            fun cusip ->
                async {
                    let context = Provider.dataContext ()
                    return MbsRepository.getDetail context cusip |> Option.toArray
                }
        GetMbsFilterOptions =
            fun () ->
                async {
                    let context = Provider.dataContext ()
                    return MbsRepository.filterOptions context
                }
        GetCht =
            fun () ->
                async {
                    let context = Provider.dataContext ()
                    return ChtRepository.getData context
                }
        GetCoveredBonds =
            fun () ->
                async {
                    let context = Provider.dataContext ()
                    return CoveredBondRepository.getData context
                }
        GetFreshness =
            fun () ->
                async {
                    let context = Provider.dataContext ()
                    return EtlRepository.getFreshness context
                }
        GetForwardCurves = fun () -> async { return ForwardCurveRepository.get () }
    }

let private apiHandler =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder (fun typeName methodName -> sprintf "/api/%s/%s" typeName methodName)
    |> Remoting.withErrorHandler (fun ex route ->
        eprintfn "API error in %s: %s" route.path ex.Message
        ErrorResult.Propagate ex)
    |> Remoting.fromValue dashboardApi
    |> Remoting.buildHttpHandler

let private clientRoot =
    let candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "wwwroot")
            Path.Combine(Directory.GetCurrentDirectory(), "src/NHAMBS.Server/wwwroot")
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
        ]

    candidates
    |> List.tryFind Directory.Exists
    |> Option.defaultValue candidates.[1]

let private spaFallback: HttpHandler =
    fun next ctx ->
        task {
            let path = ctx.Request.Path.Value

            if path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) then
                return! next ctx
            else
                let indexPath = Path.Combine(clientRoot, "index.html")

                if File.Exists indexPath then
                    ctx.Response.ContentType <- "text/html"
                    do! ctx.Response.SendFileAsync indexPath
                    return Some ctx
                else
                    return! next ctx
        }

let private webApp =
    choose [ route "/health" >=> text "ok"; apiHandler; spaFallback ]

let private serverUrl =
    match Environment.GetEnvironmentVariable "ASPNETCORE_URLS" with
    | null
    | "" -> "http://0.0.0.0:8085"
    | value -> value

[<EntryPoint>]
let main _ =
    printfn "NHA MBS securitization API listening on %s" serverUrl

    let app =
        application {
            use_router webApp
            use_static clientRoot
            url serverUrl
        }

    run app
    0

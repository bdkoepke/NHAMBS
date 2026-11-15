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
                    let mbs = MbsRepository.analytics emptyMbsFilter
                    let cht = (ChtRepository.getData ()).Analytics
                    let covered = (CoveredBondRepository.getData ()).Analytics
                    let freshness = EtlRepository.getFreshness ()

                    return
                        {
                            Mbs = mbs
                            Cht = cht
                            CoveredBonds = covered
                            Freshness = freshness
                        }
                }
        GetMbsAnalytics = fun filter -> async { return MbsRepository.analytics filter }
        GetMbsPools = fun query -> async { return MbsRepository.search query }
        GetMbsPool = fun cusip -> async { return MbsRepository.getDetail cusip |> Option.toArray }
        GetMbsFilterOptions = fun () -> async { return MbsRepository.filterOptions () }
        GetCht = fun () -> async { return ChtRepository.getData () }
        GetCoveredBonds = fun () -> async { return CoveredBondRepository.getData () }
        GetFreshness = fun () -> async { return EtlRepository.getFreshness () }
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

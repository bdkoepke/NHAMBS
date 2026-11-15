module NHAMBS.Client.Api

open Fable.Remoting.Client
open NHAMBS.Shared

let api: DashboardApi =
    Remoting.createApi ()
    |> Remoting.withBaseUrl "/api/"
    |> Remoting.buildProxy<DashboardApi>

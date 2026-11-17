module NHAMBS.Workflows.Registration

open Elsa.Extensions
open Elsa.Features.Services

/// Registers the NHAMBS activities and workflows with Elsa.
let register (elsa: IModule) =
    elsa.AddActivity<LoadMbsPoolsActivity>() |> ignore
    elsa.AddActivity<LoadChtActivity>() |> ignore
    elsa.AddActivity<LoadCoveredBondsActivity>() |> ignore
    elsa.AddActivity<LoadArrearsActivity>() |> ignore

    elsa.AddWorkflow<MbsEtlWorkflow>() |> ignore
    elsa.AddWorkflow<ChtEtlWorkflow>() |> ignore
    elsa.AddWorkflow<CoveredBondEtlWorkflow>() |> ignore
    elsa.AddWorkflow<ArrearsEtlWorkflow>() |> ignore
    elsa.AddWorkflow<NhambsEtlWorkflow>() |> ignore

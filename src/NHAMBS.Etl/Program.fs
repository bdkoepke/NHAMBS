module NHAMBS.Etl.Program

open System.IO
open Elsa.Extensions
open Elsa.Workflows
open Microsoft.Extensions.DependencyInjection
open NHAMBS.Etl.Common
open NHAMBS.Workflows

type private Options =
    {
        Dataset: string
        DataRoot: string
        PoolsDir: string
        Manifest: string
    }

let private usage () =
    """
NHA MBS securitization ETL

Runs the parsed CMHC extracts through Elsa workflows.

Usage:
  dotnet run --project src/NHAMBS.Etl -- [options]

Options:
  --dataset <all|mbs|cht|covered|arrears|government-bonds>   Workflow to run (default: all)
  --data-root <path>                Root containing pools/, debt-issues.json, cht_circulars/, cht_history/, covered_bonds/
                                    (default: out)
  --pools-dir <path>                NHA MBS JSON directory (default: <data-root>/pools)
  --manifest <path>                 pool_urls.txt manifest (default: src/NHAMBS.Mbs/pool_urls.txt)
  -h, --help                        Show this help

Connection string:
  NHAMBS_CONNECTION env var (default: Host=localhost;Port=54322;Database=nhambs;Username=nhambs;Password=nhambs)
"""

let private parseArgs (argv: string[]) =
    let mutable dataset = "all"
    let mutable dataRoot = "out"
    let mutable poolsDir = ""
    let mutable manifest = Path.Combine("src", "NHAMBS.Mbs", "pool_urls.txt")
    let mutable help = false

    let rec loop index =
        if index >= argv.Length then
            ()
        else
            match argv.[index] with
            | "--dataset" when index + 1 < argv.Length ->
                dataset <- argv.[index + 1]
                loop (index + 2)
            | "--data-root" when index + 1 < argv.Length ->
                dataRoot <- argv.[index + 1]
                loop (index + 2)
            | "--pools-dir" when index + 1 < argv.Length ->
                poolsDir <- argv.[index + 1]
                loop (index + 2)
            | "--manifest" when index + 1 < argv.Length ->
                manifest <- argv.[index + 1]
                loop (index + 2)
            | "-h"
            | "--help" ->
                help <- true
                loop (index + 1)
            | other ->
                eprintfn "Unknown argument: %s" other
                help <- true
                loop (index + 1)

    loop 0

    let dataRoot = resolvePath dataRoot

    {
        Dataset = dataset
        DataRoot = dataRoot
        PoolsDir =
            resolvePath (
                if System.String.IsNullOrWhiteSpace poolsDir then
                    Path.Combine(dataRoot, "pools")
                else
                    poolsDir
            )
        Manifest = resolvePath manifest
    },
    help

let private selectWorkflow (options: Options) : IWorkflow =
    match options.Dataset.ToLowerInvariant() with
    | "all" -> NhambsEtlWorkflow(options.DataRoot, options.PoolsDir, options.Manifest) :> IWorkflow
    | "mbs"
    | "pools" -> MbsEtlWorkflow(options.PoolsDir, options.Manifest) :> IWorkflow
    | "cht"
    | "circulars" -> ChtEtlWorkflow(options.DataRoot) :> IWorkflow
    | "covered"
    | "covered-bonds" -> CoveredBondEtlWorkflow(options.DataRoot) :> IWorkflow
    | "arrears" -> ArrearsEtlWorkflow() :> IWorkflow
    | "government-bonds"
    | "gov-bonds" -> GovernmentBondEtlWorkflow() :> IWorkflow
    | other -> failwithf "Unknown dataset '%s'. Expected all, mbs, cht, covered, arrears or government-bonds." other

[<EntryPoint>]
let main argv =
    let options, help = parseArgs argv

    if help then
        printfn "%s" (usage ())
        0
    else
        try
            printfn "NHA MBS ETL (Elsa)"
            printfn "  data root: %s" options.DataRoot
            printfn "  dataset:   %s" options.Dataset

            let services = ServiceCollection()
            services.AddElsa(fun elsa -> Registration.register elsa) |> ignore

            use provider = services.BuildServiceProvider()
            let runner = provider.GetRequiredService<IWorkflowRunner>()
            let workflow = selectWorkflow options
            let result = runner.RunAsync(workflow).GetAwaiter().GetResult()
            let state = result.WorkflowState
            let incidents = state.Incidents

            if incidents.Count > 0 then
                for incident in incidents do
                    eprintfn "Workflow incident (%s): %s" incident.ActivityType incident.Message

                1
            elif state.Status <> WorkflowStatus.Finished || state.IsExecuting then
                eprintfn "Workflow did not finish (status: %A, sub-status: %A)." state.Status state.SubStatus

                1
            else
                printfn "ETL finished."
                0
        with ex ->
            eprintfn "ETL failed: %s" ex.Message
            1

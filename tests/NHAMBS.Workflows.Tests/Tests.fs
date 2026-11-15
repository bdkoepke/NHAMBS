module NHAMBS.Workflows.Tests

open System
open System.Threading
open Elsa.Extensions
open Elsa.Workflows
open Elsa.Workflows.Activities
open Microsoft.Extensions.DependencyInjection
open Xunit
open NHAMBS.Workflows

let private services () =
    let collection = ServiceCollection()
    collection.AddElsa(fun elsa -> Registration.register elsa) |> ignore
    collection.BuildServiceProvider()

let private buildRoot (workflow: IWorkflow) =
    use provider = services ()
    let builder = provider.GetRequiredService<IWorkflowBuilderFactory>().CreateBuilder()
    workflow.BuildAsync(builder, CancellationToken.None).AsTask().GetAwaiter().GetResult()
    builder.Root

[<Fact>]
let ``NhambsEtlWorkflow sequences the three dataset activities`` () =
    let root =
        buildRoot (NhambsEtlWorkflow("out", "out/pools", "pool_urls.txt") :> IWorkflow)

    let sequence = Assert.IsType<Sequence>(root)

    let activityTypes =
        sequence.Activities
        |> Seq.map (fun activity -> activity.GetType())
        |> Seq.toList

    Assert.Equal<Type list>(
        [
            typeof<LoadMbsPoolsActivity>
            typeof<LoadChtActivity>
            typeof<LoadCoveredBondsActivity>
        ],
        activityTypes
    )

[<Fact>]
let ``Each dataset workflow builds a single matching activity`` () =
    let cases =
        [
            MbsEtlWorkflow("out/pools", "pool_urls.txt") :> IWorkflow, typeof<LoadMbsPoolsActivity>
            ChtEtlWorkflow("out") :> IWorkflow, typeof<LoadChtActivity>
            CoveredBondEtlWorkflow("out") :> IWorkflow, typeof<LoadCoveredBondsActivity>
        ]

    for workflow, expected in cases do
        let root = buildRoot workflow
        Assert.Equal<Type>(expected, root.GetType())

[<Fact>]
let ``Registration resolves the Elsa workflow runner`` () =
    use provider = services ()
    Assert.NotNull(provider.GetService<IWorkflowRunner>())

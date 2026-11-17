namespace NHAMBS.Workflows

open Elsa.Workflows
open Elsa.Workflows.Activities

/// Loads the NHA MBS pool dataset.
type MbsEtlWorkflow(poolsDir: string, manifestPath: string) =
    inherit WorkflowBase()

    new() = MbsEtlWorkflow(Defaults.poolsDir (), Defaults.manifestPath ())

    override this.Build(builder: IWorkflowBuilder) =
        builder.Name <- "NHA MBS pool ETL"
        builder.Root <- LoadMbsPoolsActivity(poolsDir, manifestPath)

/// Loads the Canada Mortgage Bond dataset.
type ChtEtlWorkflow(dataRoot: string) =
    inherit WorkflowBase()

    new() = ChtEtlWorkflow(Defaults.dataRoot ())

    override this.Build(builder: IWorkflowBuilder) =
        builder.Name <- "CHT ETL"
        builder.Root <- LoadChtActivity(dataRoot)

/// Loads the covered bond dataset.
type CoveredBondEtlWorkflow(dataRoot: string) =
    inherit WorkflowBase()

    new() = CoveredBondEtlWorkflow(Defaults.dataRoot ())

    override this.Build(builder: IWorkflowBuilder) =
        builder.Name <- "Covered bond ETL"
        builder.Root <- LoadCoveredBondsActivity(dataRoot)

/// Loads CBA and CMHC mortgage-arrears observations.
type ArrearsEtlWorkflow() =
    inherit WorkflowBase()

    override this.Build(builder: IWorkflowBuilder) =
        builder.Name <- "Mortgage arrears ETL"
        builder.Root <- LoadArrearsActivity()

/// Loads every parsed dataset in dependency order.
type NhambsEtlWorkflow(dataRoot: string, poolsDir: string, manifestPath: string) =
    inherit WorkflowBase()

    new() = NhambsEtlWorkflow(Defaults.dataRoot (), Defaults.poolsDir (), Defaults.manifestPath ())

    override this.Build(builder: IWorkflowBuilder) =
        builder.Name <- "NHA MBS securitization ETL"

        let sequence = Sequence()

        sequence.Activities.Add(LoadMbsPoolsActivity(poolsDir, manifestPath))
        sequence.Activities.Add(LoadChtActivity(dataRoot))
        sequence.Activities.Add(LoadCoveredBondsActivity(dataRoot))
        sequence.Activities.Add(LoadArrearsActivity())

        builder.Root <- sequence

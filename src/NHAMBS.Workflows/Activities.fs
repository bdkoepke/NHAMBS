namespace NHAMBS.Workflows

open Elsa.Workflows
open NHAMBS.Etl

/// Loads the parsed NHA MBS pool extracts into PostgreSQL.
///
/// The activity delegates to the existing, resumable ETL implementation so the
/// database transaction, audit row and bulk COPY behaviour are preserved.
type LoadMbsPoolsActivity(poolsDir: string, manifestPath: string) =
    inherit CodeActivity()

    new() = LoadMbsPoolsActivity("", "")

    member val PoolsDir = poolsDir with get, set
    member val ManifestPath = manifestPath with get, set

    override this.Execute(_context: ActivityExecutionContext) =
        MbsEtl.run this.PoolsDir this.ManifestPath |> ignore

/// Loads CHT outstanding issues, offering circulars and cancellation history.
type LoadChtActivity(dataRoot: string) =
    inherit CodeActivity()

    new() = LoadChtActivity("")

    member val DataRoot = dataRoot with get, set

    override this.Execute(_context: ActivityExecutionContext) = ChtEtl.run this.DataRoot |> ignore

/// Loads CBA and CMHC mortgage-arrears observations.
type LoadArrearsActivity() =
    inherit CodeActivity()

    override this.Execute(_context: ActivityExecutionContext) = ArrearsEtl.run () |> ignore

/// Loads issue-level Government of Canada bonds and treasury bills.
type LoadGovernmentBondsActivity() =
    inherit CodeActivity()

    override this.Execute(_context: ActivityExecutionContext) = GovernmentBondEtl.run () |> ignore

/// Loads covered bond programmes, their bonds and the CMHC registry.
type LoadCoveredBondsActivity(dataRoot: string) =
    inherit CodeActivity()

    new() = LoadCoveredBondsActivity("")

    member val DataRoot = dataRoot with get, set

    override this.Execute(_context: ActivityExecutionContext) =
        CoveredBondEtl.run this.DataRoot |> ignore

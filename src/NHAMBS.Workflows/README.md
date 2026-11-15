# NHAMBS.Workflows

Elsa Workflows definitions for the NHAMBS ETL. The project contains workflow
definitions and activities only; the database loaders live in
`NHAMBS.Etl.Core` and the command-line host lives in `NHAMBS.Etl`.

## Workflows

| Workflow | Dataset |
| --- | --- |
| `MbsEtlWorkflow` | NHA MBS pool JSON extracts. |
| `ChtEtlWorkflow` | Canada Mortgage Bond issues, circulars and history. |
| `CoveredBondEtlWorkflow` | Covered bond programmes, bonds and registry. |
| `NhambsEtlWorkflow` | All three datasets in sequence. |

Each workflow is built from `CodeActivity` wrappers in `Activities.fs`:

- `LoadMbsPoolsActivity`
- `LoadChtActivity`
- `LoadCoveredBondsActivity`

The wrappers delegate to the resumable loaders in `NHAMBS.Etl.Core`, so the
existing transaction, audit row and binary `COPY` behaviour is preserved.

## Registration

`Registration.register` adds every activity and workflow to Elsa:

```fsharp
services.AddElsa(fun elsa -> NHAMBS.Workflows.Registration.register elsa)
```

## Running

Use the host project:

```bash
dotnet run --project ../NHAMBS.Etl -- --dataset all
dotnet run --project ../NHAMBS.Etl -- --dataset mbs
dotnet run --project ../NHAMBS.Etl -- --dataset cht
dotnet run --project ../NHAMBS.Etl -- --dataset covered
```

Workflow activity failures are reported as Elsa incidents. The host prints the
incident messages and exits with status `1`.

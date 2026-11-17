module NHAMBS.SchemaTool.Program

open System
open System.Reflection
open NHAMBS.Data

/// SQLProvider explores the database lazily. Touching every mapped table and
/// view populates the provider cache, then SaveContextSchema writes the
/// compile-time schema file used by ContextSchemaPath.
[<EntryPoint>]
let main argv =
    let output =
        match argv with
        | [| path |] -> path
        | _ -> failwith "usage: NHAMBS.SchemaTool <output-schema-path>"

    let context = Provider.dataContext ()

    let touch (rows: 'a seq) =
        rows |> Seq.truncate 1 |> Seq.length |> ignore

    touch context.Nhambs.MbsPools
    touch context.Nhambs.MbsPoolInsurers
    touch context.Nhambs.MbsPoolLocations
    touch context.Nhambs.MbsPoolRateBands
    touch context.Nhambs.MbsPoolScheduledMaturity
    touch context.Nhambs.ChtCircularPaymentDates
    touch context.Nhambs.ChtCircularRatings
    touch context.Nhambs.ChtCirculars
    touch context.Nhambs.ChtIssues
    touch context.Nhambs.ChtOfferingCirculars
    touch context.Nhambs.ChtOutstandingHistory
    touch context.Nhambs.ChtOutstandingHistoryCancellations
    touch context.Nhambs.CoveredBondIsins
    touch context.Nhambs.CoveredBondParties
    touch context.Nhambs.CoveredBondPrograms
    touch context.Nhambs.CoveredBondRegistry
    touch context.Nhambs.CoveredBonds
    touch context.Nhambs.EtlAnomalies
    touch context.Nhambs.EtlRuns
    touch context.Nhambs.Insurers
    touch context.Nhambs.IssuerAliases
    touch context.Nhambs.Issuers
    touch context.Nhambs.MaturityBuckets
    touch context.Nhambs.PoolTypes
    touch context.Nhambs.Provinces
    touch context.Nhambs.RateBands
    touch context.Nhambs.RateTypes
    touch context.Nhambs.VChtHistorySummary
    touch context.Nhambs.VChtSummary
    touch context.Nhambs.VCoveredBondProgramSummary
    touch context.Nhambs.VCoveredBondRegistrySummary
    touch context.Nhambs.VMbsInsurerSummary
    touch context.Nhambs.VMbsIssuerSummary
    touch context.Nhambs.VMbsMaturitySummary
    touch context.Nhambs.VMbsMonthly
    touch context.Nhambs.VMbsPoolGrid
    touch context.Nhambs.VMbsPoolTypeSummary
    touch context.Nhambs.VMbsProvinceSummary
    touch context.Nhambs.VMbsRateTypeSummary
    touch context.Nhambs.VMbsUnresolvedEntities

    let interfaceType =
        Type.GetType("FSharp.Data.Sql.Common.ISqlDataContext, FSharp.Data.SqlProvider.Common")

    interfaceType.GetMethod("SaveContextSchema").Invoke(box context, [| box output |])
    |> ignore

    printfn "wrote %s" output
    0

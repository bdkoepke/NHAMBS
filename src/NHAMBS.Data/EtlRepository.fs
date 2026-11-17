module NHAMBS.Data.EtlRepository

open System
open System.Globalization
open NHAMBS.Shared.Domain
open NHAMBS.Data

let private displayName dataset =
    match dataset with
    | "mbs" -> "NHA MBS pools"
    | "cht" -> "Canada Mortgage Bonds"
    | "covered-bonds" -> "Covered bonds"
    | _ -> dataset

let private timestampText (value: DateTime) =
    value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)

let getFreshness (context: Provider.Sql.dataContext) =
    query {
        for run in context.Nhambs.EtlRuns do
            select run
    }
    |> Seq.groupBy (fun run -> run.Dataset)
    |> Seq.map (fun (_, runs) -> runs |> Seq.sortByDescending (fun run -> run.StartedAt) |> Seq.head)
    |> Seq.sortBy (fun run -> run.Dataset)
    |> Seq.map (fun run ->
        let completed = defaultArg run.CompletedAt run.StartedAt
        let duration = (completed - run.StartedAt).TotalSeconds

        {
            Dataset = run.Dataset
            DisplayName = displayName run.Dataset
            LastLoaded = timestampText completed
            FilesProcessed = run.FilesProcessed
            RowsLoaded = int run.RowsLoaded
            Status = run.Status
            DurationSeconds = Math.Round(decimal duration, 1)
        })
    |> Seq.toArray

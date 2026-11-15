module NHAMBS.Data.EtlRepository

open System
open System.Data.Common
open System.Globalization
open Npgsql
open NHAMBS.Shared.Domain
open NHAMBS.Data

let private text (reader: DbDataReader) index =
    if reader.IsDBNull index then "" else reader.GetString index

let private intValue (reader: DbDataReader) index =
    if reader.IsDBNull index then
        0
    else
        Convert.ToInt32(reader.GetValue index, CultureInfo.InvariantCulture)

let private int64Value (reader: DbDataReader) index =
    if reader.IsDBNull index then
        0L
    else
        Convert.ToInt64(reader.GetValue index, CultureInfo.InvariantCulture)

let private timestampText (reader: DbDataReader) index =
    if reader.IsDBNull index then
        ""
    else
        reader.GetDateTime(index).ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)

let private displayName dataset =
    match dataset with
    | "mbs" -> "NHA MBS pools"
    | "cht" -> "Canada Mortgage Bonds"
    | "covered-bonds" -> "Covered bonds"
    | _ -> dataset

let getFreshness () =
    Database.withConnection (fun connection ->
        use command =
            new NpgsqlCommand(
                """
                SELECT DISTINCT ON (dataset)
                       dataset,
                       started_at,
                       completed_at,
                       status,
                       files_processed,
                       rows_loaded,
                       EXTRACT(EPOCH FROM (completed_at - started_at))::numeric(12,1) AS duration_seconds
                FROM nhambs.etl_runs
                ORDER BY dataset, started_at DESC;
                """,
                connection
            )

        use reader = command.ExecuteReader()
        let rows = ResizeArray<DatasetFreshness>()

        while reader.Read() do
            let dataset = text reader 0

            let completed =
                if reader.IsDBNull 2 then
                    reader.GetDateTime 1
                else
                    reader.GetDateTime 2

            rows.Add(
                {
                    Dataset = dataset
                    DisplayName = displayName dataset
                    LastLoaded =
                        completed.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                    FilesProcessed = intValue reader 4
                    RowsLoaded = intValue reader 5
                    Status = text reader 3
                    DurationSeconds = if reader.IsDBNull 6 then 0m else reader.GetDecimal 6
                }
            )

        rows.ToArray())

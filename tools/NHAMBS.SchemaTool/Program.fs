module NHAMBS.SchemaTool.Program

open System
open System.Collections
open System.Reflection
open Npgsql
open NHAMBS.Data

/// SQLProvider explores the database lazily. Touching every table and view in
/// the mapped schema populates the provider cache, then SaveContextSchema
/// writes the compile-time schema used by ContextSchemaPath.
[<EntryPoint>]
let main argv =
    let output =
        match argv with
        | [| path |] -> path
        | _ -> failwith "usage: NHAMBS.SchemaTool <output-schema-path>"

    let context = Provider.dataContext ()

    let names =
        use connection = new NpgsqlConnection(Provider.runtimeConnectionString ())
        connection.Open()

        use command =
            new NpgsqlCommand(
                """
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'nhambs'
                ORDER BY table_name;
                """,
                connection
            )

        use reader = command.ExecuteReader()
        let names = ResizeArray<string>()

        while reader.Read() do
            names.Add(reader.GetString 0)

        names.ToArray()

    let interfaceType =
        Type.GetType("FSharp.Data.Sql.Common.ISqlDataContext, FSharp.Data.SqlProvider.Common")

    let createEntities = interfaceType.GetMethod "CreateEntities"

    for name in names do
        let query = createEntities.Invoke(box context, [| box ("nhambs." + name) |])

        match query with
        | :? IEnumerable as rows -> rows |> Seq.cast<obj> |> Seq.truncate 1 |> Seq.length |> ignore
        | _ -> ()

        printfn "touched nhambs.%s" name

    let save = interfaceType.GetMethod "SaveContextSchema"
    save.Invoke(box context, [| box output |]) |> ignore

    printfn "wrote %s" output
    0

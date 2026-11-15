module NHAMBS.Data.Database

open System
open Npgsql
open NHAMBS.Data

let connectionString () = Provider.runtimeConnectionString ()

let openConnection () =
    let connection = new NpgsqlConnection(connectionString ())
    connection.Open()
    connection

let withConnection (action: NpgsqlConnection -> 'T) =
    use connection = openConnection ()
    action connection

let withContext (action: Provider.Sql.dataContext -> 'T) = action (Provider.dataContext ())

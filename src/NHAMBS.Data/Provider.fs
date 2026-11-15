module NHAMBS.Data.Provider

open System
open FSharp.Data.Sql

[<Literal>]
let private DesignTimeConnection =
    "Host=localhost;Port=54322;Database=nhambs;Username=nhambs;Password=nhambs"

[<Literal>]
let private SchemaPath = __SOURCE_DIRECTORY__ + "/sqlprovider.schema"

/// SQLProvider is compiled against a saved PostgreSQL schema so the solution
/// builds in CI without a live database. The runtime connection string comes
/// from NHAMBS_CONNECTION and is passed to GetDataContext.
type Sql =
    PostgreSql.SqlDataProvider<
        Common.DatabaseProviderTypes.POSTGRESQL,
        DesignTimeConnection,
        Owner="nhambs",
        ContextSchemaPath=SchemaPath,
        UseOptionTypes=Common.NullableColumnType.OPTION
     >

let runtimeConnectionString () =
    match Environment.GetEnvironmentVariable "NHAMBS_CONNECTION" with
    | null
    | "" -> DesignTimeConnection
    | value -> value

let dataContext () =
    Sql.GetDataContext(runtimeConnectionString ())

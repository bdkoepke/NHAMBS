module NHAMBS.Migrations.Program

open System
open System.Reflection
open FluentMigrator.Runner
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

[<Literal>]
let DefaultConnection =
    "Host=localhost;Port=54322;Database=nhambs;Username=nhambs;Password=nhambs"

let connectionString () =
    match Environment.GetEnvironmentVariable "NHAMBS_CONNECTION" with
    | null
    | "" -> DefaultConnection
    | value -> value

[<EntryPoint>]
let main argv =
    let connection = connectionString ()
    let down = argv |> Array.exists (fun a -> a = "--down" || a = "down")

    printfn "NHA MBS migrations"

    printfn
        "  database: %s"
        (connection.Split(';')
         |> Array.tryFind (fun p -> p.StartsWith("Database="))
         |> Option.defaultValue "Database=?")

    let services =
        ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(fun runner ->
                runner
                    .AddPostgres()
                    .WithGlobalConnectionString(connection)
                    .ScanIn(Assembly.GetExecutingAssembly())
                    .For.Migrations()
                |> ignore)
            .AddLogging(fun logging -> logging.AddFluentMigratorConsole() |> ignore)

    use provider = services.BuildServiceProvider(false)
    use scope = provider.CreateScope()
    let runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>()

    if down then
        printfn "Migrating down..."
        runner.MigrateDown(0L)
        printfn "All migrations rolled back."
    else
        printfn "Migrating up..."
        runner.MigrateUp()
        printfn "Database is up to date."

    0

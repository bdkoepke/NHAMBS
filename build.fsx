#!/usr/bin/env -S dotnet fsi

// NHA MBS securitization dashboard build runner.
//
// Usage:
//   dotnet fsi build.fsx                 # Pulumi + migrations + ETL + server + client
//   dotnet fsi build.fsx --target run    # start server + client without infra/migrate/ETL
//   dotnet fsi build.fsx --target build  # compile the .NET solution and production client
//   dotnet fsi build.fsx --help
//
// The script is intentionally dependency-free: it uses the installed dotnet,
// npm, Pulumi and PostgreSQL client tools directly.

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Text
open System.Threading

let root = __SOURCE_DIRECTORY__
let pulumiDir = Path.Combine(root, "src", "NHAMBS.Pulumi")
let clientDir = Path.Combine(root, "src", "NHAMBS.Client")

let migrationsProject =
    Path.Combine(root, "src", "NHAMBS.Migrations", "NHAMBS.Migrations.fsproj")

let etlCoreProject =
    Path.Combine(root, "src", "NHAMBS.Etl.Core", "NHAMBS.Etl.Core.fsproj")

let workflowsProject =
    Path.Combine(root, "src", "NHAMBS.Workflows", "NHAMBS.Workflows.fsproj")

let etlProject = Path.Combine(root, "src", "NHAMBS.Etl", "NHAMBS.Etl.fsproj")

let serverProject =
    Path.Combine(root, "src", "NHAMBS.Server", "NHAMBS.Server.fsproj")

let clientProject =
    Path.Combine(root, "src", "NHAMBS.Client", "NHAMBS.Client.fsproj")

let defaultConnection =
    "Host=localhost;Port=54322;Database=nhambs;Username=nhambs;Password=nhambs"

type Options =
    {
        Target: string
        SkipInfra: bool
        SkipMigrate: bool
        SkipEtl: bool
        NoClient: bool
        NoServer: bool
        WithInfra: bool
        Help: bool
    }

let defaultOptions =
    {
        Target = "dev"
        SkipInfra = false
        SkipMigrate = false
        SkipEtl = false
        NoClient = false
        NoServer = false
        WithInfra = false
        Help = false
    }

let usage () =
    """
NHA MBS dashboard build runner

Usage:
  ./build.fsx [options]
  dotnet fsi build.fsx -- [options]

Targets:
  dev       Pulumi, migrations, ETL, server and Vite client (default)
  run       Start the server and Vite client using the current database
  build     Compile the dashboard .NET projects and the production Fable/Vite client
  ci        Same as build, using npm ci when a lockfile is present
  infra     Bring up PostgreSQL with Pulumi only
  migrate   Run FluentMigrator migrations only
  etl       Run the ETL only
  server    Start the API only
  client    Start the Vite/Fable client only
  stop      Stop server/client processes started by this runner
  help      Show this help

Options:
  --target <name>   Target to run (default: dev)
  --skip-infra      Do not run Pulumi; use NHAMBS_CONNECTION or the default DB
  --with-infra      Run Pulumi even when NHAMBS_CONNECTION is set
  --skip-migrate    Do not run migrations
  --skip-etl        Do not reload the database
  --no-client       Do not start the Vite client
  --no-server       Do not start the API
  -h, --help        Show this help

Environment:
  NHAMBS_CONNECTION          PostgreSQL connection string
  NHAMBS_PULUMI_STACK        Pulumi stack name (default: dev)
  NHAMBS_SERVER_URL          Server URL used when waiting/printing (default: http://localhost:8085)
"""

let parseArgs (argv: string[]) =
    let rec loop index options =
        if index >= argv.Length then
            options
        else
            let arg = argv.[index]

            let nextValue () =
                if index + 1 >= argv.Length then
                    failwithf "Missing value for %s" arg

                argv.[index + 1]

            match arg with
            | "--target" ->
                loop
                    (index + 2)
                    { options with
                        Target = (nextValue ()).ToLowerInvariant()
                    }
            | "--skip-infra" -> loop (index + 1) { options with SkipInfra = true }
            | "--with-infra" -> loop (index + 1) { options with WithInfra = true }
            | "--skip-migrate" -> loop (index + 1) { options with SkipMigrate = true }
            | "--skip-etl" -> loop (index + 1) { options with SkipEtl = true }
            | "--no-client" -> loop (index + 1) { options with NoClient = true }
            | "--no-server" -> loop (index + 1) { options with NoServer = true }
            | "-h"
            | "--help" -> loop (index + 1) { options with Help = true }
            | value when value.StartsWith("--target=", StringComparison.OrdinalIgnoreCase) ->
                loop
                    (index + 1)
                    { options with
                        Target = value.Substring("--target=".Length).ToLowerInvariant()
                    }
            | unknown -> failwithf "Unknown option: %s" unknown

    loop 0 defaultOptions

let environmentWith (connection: string) = [ "NHAMBS_CONNECTION", connection ]

let private createStartInfo (fileName: string) (arguments: string list) (workingDirectory: string) =
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- fileName
    startInfo.WorkingDirectory <- workingDirectory
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true

    for argument in arguments do
        startInfo.ArgumentList.Add argument

    startInfo

let private addEnvironment (startInfo: ProcessStartInfo) (environment: (string * string) list) =
    for key, value in environment do
        startInfo.Environment.[key] <- value

let run
    (label: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    (environment: (string * string) list)
    =
    printfn ""
    printfn "==> %s" label
    printfn "    %s %s" fileName (String.concat " " arguments)

    let startInfo = createStartInfo fileName arguments workingDirectory
    addEnvironment startInfo environment

    use proc = new Process()
    proc.StartInfo <- startInfo

    proc.OutputDataReceived.Add(fun event ->
        if not (isNull event.Data) then
            printfn "    %s" event.Data)

    proc.ErrorDataReceived.Add(fun event ->
        if not (isNull event.Data) then
            eprintfn "    %s" event.Data)

    proc.Start() |> ignore
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()
    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        failwithf "%s failed with exit code %d" label proc.ExitCode

let capture
    (label: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    (environment: (string * string) list)
    =
    let startInfo = createStartInfo fileName arguments workingDirectory
    addEnvironment startInfo environment

    use proc = new Process()
    proc.StartInfo <- startInfo
    proc.Start() |> ignore
    let output = proc.StandardOutput.ReadToEnd()
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        failwithf "%s failed with exit code %d: %s" label proc.ExitCode (error.Trim())

    output.Trim()

let tryRunQuiet (fileName: string) (arguments: string list) (workingDirectory: string) =
    try
        let startInfo = createStartInfo fileName arguments workingDirectory
        use proc = new Process()
        proc.StartInfo <- startInfo
        proc.Start() |> ignore
        proc.StandardOutput.ReadToEnd() |> ignore
        proc.StandardError.ReadToEnd() |> ignore
        proc.WaitForExit()
        proc.ExitCode = 0
    with :? Win32Exception ->
        false

let startBackground
    (name: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    (environment: (string * string) list)
    =
    printfn ""
    printfn "==> Starting %s" name
    printfn "    %s %s" fileName (String.concat " " arguments)

    let startInfo = createStartInfo fileName arguments workingDirectory
    addEnvironment startInfo environment

    let proc = new Process()
    proc.StartInfo <- startInfo

    proc.OutputDataReceived.Add(fun event ->
        if not (isNull event.Data) then
            printfn "[%s] %s" name event.Data)

    proc.ErrorDataReceived.Add(fun event ->
        if not (isNull event.Data) then
            eprintfn "[%s] %s" name event.Data)

    proc.Start() |> ignore
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()
    proc

let stateDir = Path.Combine(root, ".build")
Directory.CreateDirectory stateDir |> ignore

let pidFile (name: string) = Path.Combine(stateDir, name + ".pid")

let writePid (name: string) (processId: int) =
    File.WriteAllText(pidFile name, string processId)

let clearPid (name: string) =
    let path = pidFile name

    if File.Exists path then
        File.Delete path

let stopTrackedProcess (name: string) =
    let path = pidFile name

    if File.Exists path then
        match Int32.TryParse(File.ReadAllText(path).Trim()) with
        | true, processId ->
            try
                use tracked = Process.GetProcessById processId

                if not tracked.HasExited then
                    tracked.Kill(true)
                    tracked.WaitForExit(5000) |> ignore
            with _ ->
                ()

            clearPid name
        | _ -> clearPid name

let stopProcess (proc: Process) =
    try
        if not proc.HasExited then
            proc.Kill(true)
            proc.WaitForExit(5000) |> ignore
    with _ ->
        ()

let stopAll (procs: Process list) =
    procs |> List.iter stopProcess
    clearPid "server"
    clearPid "client"

let activeProcesses = ref ([]: Process list)
let shutdownRequested = ref false

AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> stopAll activeProcesses.Value)

let private handleShutdownSignal (context: PosixSignalContext) =
    context.Cancel <- true
    shutdownRequested.Value <- true
    stopAll activeProcesses.Value

let private signalRegistrations =
    [
        PosixSignalRegistration.Create(PosixSignal.SIGINT, handleShutdownSignal)
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, handleShutdownSignal)
        PosixSignalRegistration.Create(PosixSignal.SIGHUP, handleShutdownSignal)
    ]

let waitForProcesses (procs: Process list) =
    activeProcesses.Value <- procs
    let mutable cancelled = false

    Console.CancelKeyPress.Add(fun event ->
        event.Cancel <- true
        cancelled <- true
        stopAll procs)

    let mutable failure = 0

    while not cancelled
          && not shutdownRequested.Value
          && not (procs |> List.exists (fun p -> p.HasExited)) do
        Thread.Sleep 250

    if not cancelled then
        match procs |> List.tryFind (fun p -> p.HasExited) with
        | Some exitedProcess when exitedProcess.ExitCode <> 0 ->
            failure <- exitedProcess.ExitCode
            eprintfn "One of the child processes exited with code %d; stopping the rest." exitedProcess.ExitCode
        | Some _ -> printfn "A child process exited with code 0; stopping the rest."
        | None -> ()

    stopAll procs
    activeProcesses.Value <- []
    failure

let connectionValue (connection: string) (key: string) (fallback: string) =
    connection.Split(';')
    |> Array.tryFind (fun part -> part.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
    |> Option.map (fun part ->
        let index = part.IndexOf('=')
        part.Substring(index + 1).Trim())
    |> Option.defaultValue fallback

let waitForPostgres (connection: string) =
    let host = connectionValue connection "Host" "localhost"
    let portText = connectionValue connection "Port" "54322"
    let user = connectionValue connection "Username" "nhambs"
    let database = connectionValue connection "Database" "nhambs"

    let port =
        match Int32.TryParse portText with
        | true, value -> value
        | _ -> 54322

    let tcpReady () =
        try
            use client = new TcpClient()
            let task = client.ConnectAsync(host, port)
            task.Wait(1000) && client.Connected
        with _ ->
            false

    let postgresReady () =
        if tryRunQuiet "pg_isready" [ "-h"; host; "-p"; string port; "-U"; user; "-d"; database ] root then
            true
        else
            tcpReady ()

    printfn ""
    printfn "==> Waiting for PostgreSQL at %s:%d" host port

    let mutable ready = false
    let mutable attempt = 0

    while not ready && attempt < 60 do
        attempt <- attempt + 1
        ready <- postgresReady ()

        if not ready then
            Thread.Sleep 1000

    if not ready then
        failwithf "PostgreSQL did not become ready at %s:%d" host port

    printfn "    PostgreSQL is ready."

let private pulumiBackendUrl () =
    try
        let output =
            capture "Reading the Pulumi backend" "pulumi" [ "whoami"; "--verbose" ] root []

        output.Split('\n')
        |> Array.tryFind (fun line -> line.Trim().StartsWith("Backend URL:", StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun line ->
            let index = line.IndexOf(':')
            line.Substring(index + 1).Trim())
    with _ ->
        None

let pulumiEnvironment =
    lazy
        (let environment = [ "PULUMI_SKIP_UPDATE_CHECK", "true" ]

         let hasPassphrase =
             not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable "PULUMI_CONFIG_PASSPHRASE"))
             || not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable "PULUMI_CONFIG_PASSPHRASE_FILE"))

         let isSelfManaged =
             match pulumiBackendUrl () with
             | Some url when not (url.StartsWith("https://api.pulumi.com", StringComparison.OrdinalIgnoreCase)) -> true
             | _ -> false

         if isSelfManaged && not hasPassphrase then
             printfn ""
             printfn "==> Local Pulumi backend detected. Using PULUMI_CONFIG_PASSPHRASE=nhambs-local-dev."
             printfn "    Override it with PULUMI_CONFIG_PASSPHRASE or PULUMI_CONFIG_PASSPHRASE_FILE."

             environment @ [ "PULUMI_CONFIG_PASSPHRASE", "nhambs-local-dev" ]
         else
             environment)

let pulumiStack () =
    match Environment.GetEnvironmentVariable "NHAMBS_PULUMI_STACK" with
    | null
    | "" -> "dev"
    | value -> value

let ensurePulumi () =
    if not (tryRunQuiet "pulumi" [ "whoami" ] root) then
        failwith "Pulumi is not logged in. Run `pulumi login --local` (or your organisation's backend) first."

    let stack = pulumiStack ()

    if not (tryRunQuiet "pulumi" [ "stack"; "select"; stack ] pulumiDir) then
        run "Selecting a Pulumi stack" "pulumi" [ "stack"; "init"; stack ] pulumiDir pulumiEnvironment.Value

    run "Provisioning PostgreSQL with Pulumi" "pulumi" [ "up"; "--yes" ] pulumiDir pulumiEnvironment.Value

let pulumiConnection () =
    try
        let value =
            capture
                "Reading Pulumi connection output"
                "pulumi"
                [ "stack"; "output"; "connectionString"; "--show-secrets" ]
                pulumiDir
                pulumiEnvironment.Value

        if String.IsNullOrWhiteSpace value then None else Some value
    with _ ->
        None

let currentConnection () =
    match Environment.GetEnvironmentVariable "NHAMBS_CONNECTION" with
    | null
    | "" -> defaultConnection
    | value -> value

let resolveDevConnection (options: Options) =
    let configured = Environment.GetEnvironmentVariable "NHAMBS_CONNECTION"

    if options.SkipInfra then
        currentConnection ()
    elif not (String.IsNullOrWhiteSpace configured) && not options.WithInfra then
        printfn ""
        printfn "==> Using NHAMBS_CONNECTION; Pulumi is skipped (pass --with-infra to force it)."
        configured
    else
        ensurePulumi ()
        pulumiConnection () |> Option.defaultValue (currentConnection ())

let dotnetToolRestore () =
    run "Restoring local .NET tools" "dotnet" [ "tool"; "restore" ] root []

let buildDotnet () =
    let projects =
        [
            serverProject
            clientProject
            migrationsProject
            etlCoreProject
            workflowsProject
            etlProject
        ]

    for project in projects do
        run
            (sprintf "Building %s" (Path.GetFileName project))
            "dotnet"
            [ "build"; project; "-v:q"; "-nologo"; "-m:1" ]
            root
            []

let ensureNodeModules (ci: bool) =
    let modules = Path.Combine(clientDir, "node_modules")
    let lockFile = Path.Combine(clientDir, "package-lock.json")

    if Directory.Exists modules && not ci then
        printfn ""
        printfn "==> Client dependencies already installed."
    elif ci && File.Exists lockFile then
        run "Installing client dependencies (npm ci)" "npm" [ "ci"; "--no-audit"; "--no-fund" ] clientDir []
    else
        run "Installing client dependencies (npm install)" "npm" [ "install"; "--no-audit"; "--no-fund" ] clientDir []

let buildClient (ci: bool) =
    ensureNodeModules ci
    run "Building the production client" "npm" [ "run"; "build" ] clientDir []

let migrate (connection: string) =
    run
        "Applying FluentMigrator migrations"
        "dotnet"
        [ "run"; "--project"; migrationsProject ]
        root
        (environmentWith connection)

let runEtl (connection: string) =
    let pools = Path.Combine(root, "out", "pools")

    if not (Directory.Exists pools) then
        failwithf "No parsed pool data found at %s. Populate out/ before running the ETL." pools

    run
        "Loading parsed CMHC data into PostgreSQL"
        "dotnet"
        [ "run"; "--project"; etlProject; "--"; "--dataset"; "all" ]
        root
        (environmentWith connection)

let buildServer () =
    run "Building the API" "dotnet" [ "build"; serverProject; "-v:q"; "-nologo"; "-m:1" ] root []

let serverDll =
    Path.Combine(root, "src", "NHAMBS.Server", "bin", "Debug", "net10.0", "NHAMBS.Server.dll")

let startServer (connection: string) =
    if not (File.Exists serverDll) then
        buildServer ()

    stopTrackedProcess "server"

    let proc =
        startBackground "server" "dotnet" [ "exec"; serverDll ] root (environmentWith connection)

    writePid "server" proc.Id
    proc

let startClient (connection: string) =
    stopTrackedProcess "client"

    let proc =
        startBackground "client" "npm" [ "run"; "dev" ] clientDir (environmentWith connection)

    writePid "client" proc.Id
    proc

let printReady (options: Options) =
    let serverUrl =
        match Environment.GetEnvironmentVariable "NHAMBS_SERVER_URL" with
        | null
        | "" -> "http://localhost:8085"
        | value -> value

    printfn ""
    printfn "============================================================"
    printfn " NHA MBS dashboard is starting"
    printfn "============================================================"

    if not options.NoServer then
        printfn " API:    %s" serverUrl

    if not options.NoClient then
        printfn " Client: http://localhost:5173"

    printfn ""
    printfn " Press Ctrl+C to stop the server and client."
    printfn ""

let dev (options: Options) =
    dotnetToolRestore ()
    let connection = resolveDevConnection options
    printfn ""
    printfn "==> Using database: %s" connection
    waitForPostgres connection

    if not options.SkipMigrate then
        migrate connection

    if not options.SkipEtl then
        runEtl connection

    ensureNodeModules false

    if options.NoServer && options.NoClient then
        failwith "Both --no-server and --no-client were specified; nothing to launch."

    if not options.NoServer then
        buildServer ()

    let processes =
        [
            if not options.NoServer then
                startServer connection
            if not options.NoClient then
                startClient connection
        ]

    printReady options
    waitForProcesses processes

let runOnly (options: Options) =
    let connection = currentConnection ()
    printfn ""
    printfn "==> Using database: %s" connection
    waitForPostgres connection
    ensureNodeModules false

    if options.NoServer && options.NoClient then
        failwith "Both --no-server and --no-client were specified; nothing to launch."

    if not options.NoServer then
        buildServer ()

    let processes =
        [
            if not options.NoServer then
                startServer connection
            if not options.NoClient then
                startClient connection
        ]

    printReady options
    waitForProcesses processes

let runTarget (options: Options) =
    if options.Help then
        printfn "%s" (usage ())
        0
    else
        match options.Target with
        | "dev" -> dev options
        | "run" -> runOnly options
        | "build" ->
            dotnetToolRestore ()
            buildDotnet ()
            buildClient false
            0
        | "ci" ->
            dotnetToolRestore ()
            buildDotnet ()
            buildClient true
            0
        | "infra" ->
            ensurePulumi ()
            pulumiConnection () |> Option.iter (printfn "==> Connection: %s")
            0
        | "migrate" ->
            dotnetToolRestore ()
            migrate (currentConnection ())
            0
        | "etl" ->
            dotnetToolRestore ()
            runEtl (currentConnection ())
            0
        | "server" ->
            dotnetToolRestore ()
            buildServer ()
            let proc = startServer (currentConnection ())
            printReady { options with NoClient = true }
            waitForProcesses [ proc ]
        | "stop" ->
            stopTrackedProcess "server"
            stopTrackedProcess "client"
            printfn "Stopped tracked server/client processes."
            0
        | "help" ->
            printfn "%s" (usage ())
            0
        | "client" ->
            dotnetToolRestore ()
            ensureNodeModules false
            let proc = startClient (currentConnection ())
            printReady { options with NoServer = true }
            waitForProcesses [ proc ]
        | other -> failwithf "Unknown target '%s'. Use --help for the target list." other

let exitCode =
    try
        let arguments = fsi.CommandLineArgs |> Array.skip 1
        let options = parseArgs arguments
        runTarget options
    with ex ->
        eprintfn ""
        eprintfn "BUILD FAILED: %s" ex.Message
        1

if exitCode <> 0 then
    exit exitCode

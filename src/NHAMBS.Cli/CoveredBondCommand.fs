module NHAMBS.Cli.CoveredBondCommand

open System
open System.IO
open NHAMBS.CoveredBonds
open NHAMBS.Sources

let private parseRegistry source html =
    let jsonObject = CoveredBondHtml.parseRegistry source html
    let json = CoveredBondHtml.toJson jsonObject

    match RegistrySchema.validate json with
    | Error errors -> Error errors
    | Ok _ -> Ok(json, Registry.parse json)

let private parseProgram source html =
    let jsonObject = CoveredBondHtml.parseProgram source html
    let json = CoveredBondHtml.toJson jsonObject

    match ProgramSchema.validate json with
    | Error errors -> Error errors
    | Ok _ -> Ok(json, CoveredBondProgram.parse json)

let private printErrors label errors =
    eprintfn "Failed to parse %s:" label

    for error in errors do
        eprintfn "  %s: %s" error.Path error.Message

let private printRegistrySummary (registry: Registry.Root) =
    let active =
        registry.Issuers
        |> Array.filter (fun issuer -> issuer.Status = "active")
        |> Array.length

    eprintfn "Parsed covered bond registry with %d issuers (%d active)" registry.Issuers.Length active

let private printProgramSummary (program: CoveredBondProgram.Root) =
    eprintfn
        "Parsed covered bond program '%s' for %s with %d bonds"
        program.ProgramName
        program.IssuerName
        program.Bonds.Length

let private single source output parse summary =
    try
        let html = Source.readText source

        match parse source html with
        | Error errors ->
            printErrors source errors
            1
        | Ok(json, value) ->
            match output with
            | Some path ->
                File.WriteAllText(path, json + Environment.NewLine)
                eprintfn "Wrote %s" path
            | None -> printfn "%s" json

            summary value
            0
    with ex ->
        eprintfn "Failed to parse %s: %s" source ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

/// Parse the Canadian Covered Bonds Registry page.
let registry (source: string) (output: string option) =
    single source output parseRegistry printRegistrySummary

/// Parse one Canadian registered covered bond program page.
let program (source: string) (output: string option) =
    single source output parseProgram printProgramSummary

/// Resolve a program link. CMHC's links are site-relative, so when the registry
/// came from a local file, resolve against the CMHC host.
let private resolveProgramUrl (registrySource: string) (programPage: string) =
    if Uri.IsWellFormedUriString(programPage, UriKind.Absolute) then
        programPage
    elif Uri.IsWellFormedUriString(registrySource, UriKind.Absolute) then
        Uri(Uri(registrySource), programPage).ToString()
    else
        Uri(Uri "https://www.cmhc-schl.gc.ca/", programPage).ToString()

let private programFileName (programPage: string) =
    let path =
        if Uri.IsWellFormedUriString(programPage, UriKind.Absolute) then
            Uri(programPage).AbsolutePath
        else
            programPage

    let last = path.TrimEnd('/').Split('/') |> Array.last

    let slug =
        let prefix = "canadian-covered-bond-program-"

        if last.StartsWith(prefix, StringComparison.Ordinal) then
            last.Substring(prefix.Length)
        else
            last

    if slug = "" then
        "program.json"
    else
        "program-" + slug + ".json"

let private programHtmlFileName (programPage: string) =
    Path.ChangeExtension(programFileName programPage, ".html")

/// Read a source from a download directory when the corresponding file exists,
/// otherwise fetch/read the original source.
let private readHtml (htmlDir: string option) (source: string) (fileName: string) =
    match htmlDir with
    | Some directory ->
        let localPath = Path.Combine(directory, fileName)

        if File.Exists localPath then
            Source.readText localPath
        else
            Source.readText source
    | None -> Source.readText source

let private describeErrors (errors: ValidationError list) =
    errors
    |> List.map (fun error -> sprintf "%s: %s" error.Path error.Message)
    |> String.concat " | "

let private loadRegistry (source: string) : Result<Registry.Root, ValidationError list> =
    try
        let text = Source.readText source

        if source.EndsWith(".json", StringComparison.OrdinalIgnoreCase) then
            Registry.parseValidated text
        else
            parseRegistry source text |> Result.map snd
    with ex ->
        Error [ { Path = "$"; Message = ex.Message } ]

let private manifestEntries (registrySource: string) (registry: Registry.Root) : Manifest.Entry list =
    [
        if Source.isUrl registrySource then
            Manifest.createEntry "registry" registrySource (Some "registry.html")

        for issuer in registry.Issuers do
            Manifest.createEntry
                issuer.Name
                (resolveProgramUrl registrySource issuer.ProgramPage)
                (Some(programHtmlFileName issuer.ProgramPage))
    ]

let private manifestRows (entries: Manifest.Entry list) =
    entries
    |> List.map (fun entry -> [ entry.Name; defaultArg entry.FileName ""; entry.Url ])

/// Parse the registry and every program page it links to. HTML can be read
/// from an optional download directory and is fetched otherwise. A second run
/// resumes from `batch-report.json`, skipping the registry and program pages
/// that are already complete.
let processBatch (registrySource: string) (outputDir: string) (htmlDir: string option) =
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore

    let reportPath = Path.Combine(outputDir, "batch-report.json")

    let previous =
        BatchReport.readOrImport reportPath (Path.Combine(outputDir, "batch-report.csv")) "covered-bonds-batch"

    let previouslyCompleted = BatchReport.completedFiles previous
    let registryFile = "registry.json"
    let registryPath = Path.Combine(outputDir, registryFile)

    // A completed registry can be reused from the output directory, which also
    // makes an offline resume possible.
    let existingRegistry =
        if previouslyCompleted.Contains registryFile && File.Exists registryPath then
            try
                let json = File.ReadAllText registryPath

                match Registry.parseValidated json with
                | Ok registry -> Some registry
                | Error _ -> None
            with _ ->
                None
        else
            None

    try
        let registryResult: Result<string option * Registry.Root, ValidationError list> =
            match existingRegistry with
            | Some registry -> Ok(None, registry)
            | None when
                not (Source.isUrl registrySource)
                && registrySource.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ->
                try
                    let json = File.ReadAllText registrySource

                    Registry.parseValidated json |> Result.map (fun registry -> Some json, registry)
                with ex ->
                    Error [ { Path = "$"; Message = ex.Message } ]
            | None ->
                readHtml htmlDir registrySource "registry.html"
                |> parseRegistry registrySource
                |> Result.map (fun (json, registry) -> Some json, registry)

        match registryResult with
        | Error errors ->
            printErrors registrySource errors

            let session = BatchReport.Session(reportPath, "covered-bonds-batch", 1, previous)

            session.Record(BatchReport.entry registryFile BatchReport.Status.Failed (Some(describeErrors errors)) [])

            session.Finish()
            1
        | Ok(registryJson, registry) ->
            let programs =
                [
                    for issuer in registry.Issuers do
                        let fileName =
                            try
                                programFileName issuer.ProgramPage
                            with _ ->
                                issuer.Name + ".json"

                        issuer, fileName, Path.Combine(outputDir, fileName)
                ]

            let pending =
                programs
                |> List.filter (fun (_, fileName, path) ->
                    not (previouslyCompleted.Contains fileName && File.Exists path))

            let registryPending =
                match registryJson with
                | Some _ -> 1
                | None -> 0

            let session =
                BatchReport.Session(reportPath, "covered-bonds-batch", registryPending + pending.Length, previous)

            match registryJson with
            | Some json ->
                File.WriteAllText(registryPath, json + Environment.NewLine)
                printRegistrySummary registry
                session.Record(BatchReport.entry registryFile BatchReport.Status.Parsed None [])
            | None -> ()

            for issuer, fileName, path in pending do
                let result =
                    try
                        let programUrl = resolveProgramUrl registrySource issuer.ProgramPage

                        let programHtml =
                            readHtml htmlDir programUrl (programHtmlFileName issuer.ProgramPage)

                        parseProgram programUrl programHtml
                    with ex ->
                        Error [ { Path = "$"; Message = ex.Message } ]

                match result with
                | Ok(programJson, _) ->
                    File.WriteAllText(path, programJson + Environment.NewLine)

                    session.Record(BatchReport.entry fileName BatchReport.Status.Parsed None [ "issuer", issuer.Name ])
                | Error errors ->
                    session.Record(
                        BatchReport.entry
                            fileName
                            BatchReport.Status.Failed
                            (Some(describeErrors errors))
                            [ "issuer", issuer.Name ]
                    )

            session.Finish()

            let parsed, _, _, failed = session.Counts

            let programsParsed = parsed - (if registryJson.IsSome then 1 else 0)

            eprintfn "Parsed %d programs, failed %d. Report: %s" programsParsed failed reportPath

            if failed = 0 then 0 else 1
    with ex ->
        eprintfn "Failed to parse %s: %s" registrySource ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

/// Download the registry and every program page it links to into the HTML
/// cache directory, optionally writing a `pool_urls.txt`-style manifest.
let download (registrySource: string) (htmlDir: string option) (urlsPath: string option) =
    let cacheDir = defaultArg htmlDir "covered_bonds_html"

    try
        match loadRegistry registrySource with
        | Error errors ->
            printErrors registrySource errors
            1
        | Ok registry ->
            let entries = manifestEntries registrySource registry

            match urlsPath with
            | Some path ->
                Manifest.writeCsv path [ "Name"; "File"; "Url" ] (manifestRows entries)
                eprintfn "Wrote %d URLs to %s" entries.Length path
            | None -> ()

            Directory.CreateDirectory cacheDir |> ignore

            let results =
                Manifest.downloadAll ".html" cacheDir (Some "https://www.cmhc-schl.gc.ca/") entries

            Manifest.printSummary results
    with ex ->
        eprintfn "Failed to download covered bond pages from %s: %s" registrySource ex.Message

        if Environment.GetEnvironmentVariable "NHAMBS_DEBUG" = "1" then
            eprintfn "%O" ex

        1

namespace NHAMBS.Cli

open Argu

module Program =

    /// Raised by the Argu exiter so `main` can return the exit code instead of
    /// terminating the process directly.
    exception private CliExit of int

    type private Exiter() =
        interface IExiter with
            member _.Name = "NHAMBS.Cli"

            member _.Exit(message, errorCode) =
                match errorCode with
                | ErrorCode.HelpText ->
                    printfn "%s" message
                    raise (CliExit 0)
                | _ ->
                    eprintfn "%s" message
                    raise (CliExit 2)

    type SourceArguments =
        | [<MainCommand; ExactlyOnce>] Source of path: string
        | [<AltCommandLine("-o"); Unique>] Output of path: string

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Source _ -> "source URL or local file to parse."
                | Output _ -> "write the JSON output to this file (or, for cht-history, directory) instead of stdout."

    /// Dataset types that the unified batch command can run together.
    [<RequireQualifiedAccess>]
    type BatchType =
        | Nhambs
        | CoveredBonds
        | ChtCirculars

    let private parseBatchType (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "nhambs"
        | "pools"
        | "circular"
        | "circulars" -> BatchType.Nhambs
        | "covered-bonds"
        | "covered"
        | "bonds" -> BatchType.CoveredBonds
        | "cht-circulars"
        | "cht" -> BatchType.ChtCirculars
        | other ->
            failwithf
                "Unknown batch type '%s'. Expected a comma-separated list drawn from: nhambs, covered-bonds, cht-circulars."
                other

    let private parseBatchTypes (value: string) =
        value.Split(',') |> Array.map parseBatchType |> Array.distinct |> Array.toList

    [<RequireQualifiedAccess>]
    type BatchArguments =
        | [<Mandatory; Unique>] Types of types: string
        | [<Unique>] Download
        | [<Unique>] Process
        | [<AltCommandLine("--pools-dir"); Unique>] Pools_Directory of path: string
        | [<AltCommandLine("--pools-out"); Unique>] Pools_Output of path: string
        | [<Unique>] Pools_Urls of path: string
        | [<Unique>] Covered_Bonds_Registry of source: string
        | [<AltCommandLine("--covered-bonds-out"); Unique>] Covered_Bonds_Output of path: string
        | [<AltCommandLine("--html-dir"); Unique>] Covered_Bonds_Html of path: string
        | [<Unique>] Covered_Bonds_Urls of path: string
        | [<Unique>] Cht_Debt_Issues of path: string
        | [<AltCommandLine("--pdf-dir"); Unique>] Cht_Pdf_Directory of path: string
        | [<AltCommandLine("--cht-out"); Unique>] Cht_Output of path: string
        | [<AltCommandLine("--cht-history-out"); Unique>] Cht_History_Output of path: string
        | [<Unique>] Cht_Urls of path: string

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Types _ ->
                    "comma-separated dataset types to run: nhambs, covered-bonds, cht-circulars (aliases: pools, covered, cht)."
                | Download -> "download missing source files for the selected dataset types."
                | Process -> "process (parse and validate) the selected dataset types."
                | Pools_Directory _ -> "NHA MBS PDF directory (default: pools)."
                | Pools_Output _ -> "NHA MBS output directory (default: pools_out)."
                | Pools_Urls _ -> "NHA MBS URL manifest for --download (default: pool_urls.txt)."
                | Covered_Bonds_Registry _ ->
                    "covered bonds registry URL, HTML file or registry JSON (required for covered-bonds)."
                | Covered_Bonds_Output _ -> "covered bonds output directory (default: covered_bonds_out)."
                | Covered_Bonds_Html _ -> "directory used to cache/download covered bond HTML pages."
                | Covered_Bonds_Urls _ -> "optional covered bond URL manifest to write."
                | Cht_Debt_Issues _ -> "CHT debt-issues JSON snapshot (required for cht-circulars)."
                | Cht_Pdf_Directory _ ->
                    "CHT download directory for offering circular PDFs and outstanding history workbooks (default: cht_circulars)."
                | Cht_Output _ -> "CHT offering circular output directory (default: cht_circulars_out)."
                | Cht_History_Output _ ->
                    "CHT per-series outstanding history JSON output directory (default: cht_history_out)."
                | Cht_Urls _ -> "optional CHT offering circular URL manifest to write."

    type CliArguments =
        | [<CustomCommandLine("batch")>] Batch of ParseResults<BatchArguments>
        | [<CustomCommandLine("circular")>] Circular of ParseResults<SourceArguments>
        | [<CustomCommandLine("covered-bonds-registry")>] CoveredBondsRegistry of ParseResults<SourceArguments>
        | [<CustomCommandLine("covered-bonds-program")>] CoveredBondsProgram of ParseResults<SourceArguments>
        | [<CustomCommandLine("cht-debt-issues")>] ChtDebtIssues of ParseResults<SourceArguments>
        | [<CustomCommandLine("cht-circular")>] ChtCircular of ParseResults<SourceArguments>
        | [<CustomCommandLine("cht-history")>] ChtHistory of ParseResults<SourceArguments>

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Batch _ -> "download and/or process the dataset types selected with --types."
                | Circular _ -> "parse one CMHC 2834 NHA MBS information circular PDF."
                | CoveredBondsRegistry _ -> "parse the Canadian Covered Bonds Registry page."
                | CoveredBondsProgram _ -> "parse one Canadian registered covered bond program page."
                | ChtDebtIssues _ -> "parse the list of outstanding CHT debt issues."
                | ChtCircular _ -> "parse one CHT offering circular PDF."
                | ChtHistory _ -> "parse one CHT outstanding history workbook, writing one JSON file per series."

    let private runSingle (handler: string -> string option -> int) (args: ParseResults<SourceArguments>) =
        handler (args.GetResult Source) (args.TryGetResult Output)

    let private required (name: string) (value: string option) =
        match value with
        | Some value when not (System.String.IsNullOrWhiteSpace value) -> value
        | _ ->
            eprintfn "Missing required option --%s for the selected dataset type." name
            raise (CliExit 2)

    let private runBatch (args: ParseResults<BatchArguments>) =
        let types =
            match args.TryGetResult BatchArguments.Types with
            | Some value ->
                try
                    parseBatchTypes value
                with ex ->
                    eprintfn "%s" ex.Message
                    raise (CliExit 2)
            | None ->
                eprintfn "Missing required option --types."
                raise (CliExit 2)

        if types.IsEmpty then
            eprintfn "--types must name at least one dataset type."
            raise (CliExit 2)

        let download = args.Contains BatchArguments.Download
        let processData = args.Contains BatchArguments.Process

        if not download && not processData then
            eprintfn "Specify --download, --process, or both."
            raise (CliExit 2)

        // Validate type-specific requirements before starting any pipeline, so
        // a missing option cannot leave the first dataset half-finished.
        let coveredBondsRegistry =
            if types |> List.contains BatchType.CoveredBonds then
                Some(
                    args.TryGetResult BatchArguments.Covered_Bonds_Registry
                    |> required "covered-bonds-registry"
                )
            else
                None

        let chtDebtIssues =
            if types |> List.contains BatchType.ChtCirculars then
                Some(args.TryGetResult BatchArguments.Cht_Debt_Issues |> required "cht-debt-issues")
            else
                None

        let mutable exitCode = 0

        for batchType in types do
            let code =
                match batchType with
                | BatchType.Nhambs ->
                    let inputDirectory =
                        args.TryGetResult BatchArguments.Pools_Directory |> Option.defaultValue "pools"

                    let outputDirectory =
                        args.TryGetResult BatchArguments.Pools_Output |> Option.defaultValue "pools_out"

                    let downloadCode =
                        if download then
                            CircularCommand.download inputDirectory (args.TryGetResult BatchArguments.Pools_Urls)
                        else
                            0

                    let processCode =
                        if processData then
                            CircularCommand.processBatch inputDirectory outputDirectory
                        else
                            0

                    if downloadCode <> 0 then downloadCode else processCode
                | BatchType.CoveredBonds ->
                    let registrySource =
                        coveredBondsRegistry
                        |> Option.defaultWith (fun () -> failwith "covered-bonds registry was not validated")

                    let outputDirectory =
                        args.TryGetResult BatchArguments.Covered_Bonds_Output
                        |> Option.defaultValue "covered_bonds_out"

                    let htmlDirectory =
                        match args.TryGetResult BatchArguments.Covered_Bonds_Html with
                        | Some directory -> Some directory
                        | None when download -> Some "covered_bonds_html"
                        | None -> None

                    let downloadCode =
                        if download then
                            CoveredBondCommand.download
                                registrySource
                                htmlDirectory
                                (args.TryGetResult BatchArguments.Covered_Bonds_Urls)
                        else
                            0

                    let processCode =
                        if processData then
                            CoveredBondCommand.processBatch registrySource outputDirectory htmlDirectory
                        else
                            0

                    if downloadCode <> 0 then downloadCode else processCode
                | BatchType.ChtCirculars ->
                    let debtIssuesPath =
                        chtDebtIssues
                        |> Option.defaultWith (fun () -> failwith "CHT debt issues path was not validated")

                    let pdfDirectory =
                        args.TryGetResult BatchArguments.Cht_Pdf_Directory
                        |> Option.defaultValue "cht_circulars"

                    let outputDirectory =
                        args.TryGetResult BatchArguments.Cht_Output
                        |> Option.defaultValue "cht_circulars_out"

                    let historyOutputDirectory =
                        args.TryGetResult BatchArguments.Cht_History_Output
                        |> Option.defaultValue "cht_history_out"

                    let urlsPath = args.TryGetResult BatchArguments.Cht_Urls

                    let downloadCode =
                        if download then
                            ChtCommand.download debtIssuesPath pdfDirectory urlsPath
                        else
                            0

                    let processCode =
                        if processData then
                            ChtCommand.processBatch
                                debtIssuesPath
                                pdfDirectory
                                outputDirectory
                                historyOutputDirectory
                                (if download then None else urlsPath)
                        else
                            0

                    if downloadCode <> 0 then downloadCode else processCode

            if code <> 0 then
                exitCode <- code

        exitCode

    [<EntryPoint>]
    let main argv =
        let parser =
            ArgumentParser.Create<CliArguments>(programName = "NHAMBS.Cli", errorHandler = Exiter())

        // Argu only generates the long-form help flag; keep the historical -h alias.
        let argv =
            argv
            |> Array.map (fun argument -> if argument = "-h" then "--help" else argument)

        try
            parser.ParseCommandLine argv
            |> fun results ->
                match results.GetSubCommand() with
                | Batch args -> runBatch args
                | Circular args -> runSingle CircularCommand.single args
                | CoveredBondsRegistry args -> runSingle CoveredBondCommand.registry args
                | CoveredBondsProgram args -> runSingle CoveredBondCommand.program args
                | ChtDebtIssues args -> runSingle ChtCommand.debtIssues args
                | ChtCircular args -> runSingle ChtCommand.circular args
                | ChtHistory args -> runSingle ChtCommand.history args
        with CliExit code ->
            code

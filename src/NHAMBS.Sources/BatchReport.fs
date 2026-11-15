module NHAMBS.Sources.BatchReport

open System
open System.Globalization
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Outcome recorded for one item in a batch run.
///
/// `Pending` is for work that was deliberately deferred (for example a file
/// that is still being downloaded) and should be retried by the next run.
[<RequireQualifiedAccess>]
type Status =
    | Parsed
    | Skipped
    | Pending
    | Failed

module Status =

    let toString status =
        match status with
        | Status.Parsed -> "parsed"
        | Status.Skipped -> "skipped"
        | Status.Pending -> "pending"
        | Status.Failed -> "failed"

    let tryParse value =
        match value with
        | "parsed" -> Some Status.Parsed
        | "skipped" -> Some Status.Skipped
        | "pending" -> Some Status.Pending
        | "failed" -> Some Status.Failed
        | _ -> None

    /// Parsed and permanently skipped items do not need to run again.
    let isComplete status =
        match status with
        | Status.Parsed
        | Status.Skipped -> true
        | Status.Pending
        | Status.Failed -> false

/// One item in a batch report. `Metadata` holds command-specific fields such as
/// the pool number, issuer or series; it is written as a JSON object.
type Entry =
    {
        File: string
        Status: Status
        Detail: string option
        Metadata: (string * string) list
    }

let entry file status detail metadata =
    {
        File = file
        Status = status
        Detail = detail
        Metadata = metadata
    }

/// A resumable batch report. One entry is kept per file; retrying a failed
/// item updates its entry instead of appending a duplicate row.
type Report =
    {
        Command: string
        StartedAt: DateTimeOffset
        UpdatedAt: DateTimeOffset
        Total: int
        Completed: int
        Entries: Entry list
    }

let private jsonOptions =
    JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let private stringValue (node: JsonNode) =
    if isNull node then
        None
    else
        try
            Some(node.GetValue<string>())
        with _ ->
            None

let private intValue (node: JsonNode) =
    if isNull node then
        None
    else
        try
            Some(node.GetValue<int>())
        with _ ->
            None

let private dateValue (node: JsonNode) =
    stringValue node
    |> Option.bind (fun value ->
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, date -> Some date
        | _ -> None)

let private parseMetadata (node: JsonNode) =
    match node with
    | :? JsonObject as metadata ->
        [
            for property in metadata do
                match stringValue property.Value with
                | Some value -> property.Key, value
                | None -> ()
        ]
    | _ -> []

let private parseEntry (node: JsonNode) =
    match node with
    | :? JsonObject as item ->
        match stringValue item.["file"] with
        | None -> None
        | Some file ->
            let status =
                stringValue item.["status"]
                |> Option.bind Status.tryParse
                |> Option.defaultValue Status.Failed

            let detail =
                stringValue item.["detail"] |> Option.filter (String.IsNullOrWhiteSpace >> not)

            Some
                {
                    File = file
                    Status = status
                    Detail = detail
                    Metadata = parseMetadata item.["metadata"]
                }
    | _ -> None

/// Read an existing report. A missing report is normal (a fresh run); a
/// malformed report is ignored with a warning so it can be rebuilt.
let read (path: string) : Report option =
    if not (File.Exists path) then
        None
    else
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as root ->
                let now = DateTimeOffset.UtcNow

                let entries =
                    match root.["entries"] with
                    | :? JsonArray as array -> array |> Seq.choose parseEntry |> Seq.toList
                    | _ -> []

                Some
                    {
                        Command = stringValue root.["command"] |> Option.defaultValue ""
                        StartedAt = dateValue root.["startedAt"] |> Option.defaultValue now
                        UpdatedAt = dateValue root.["updatedAt"] |> Option.defaultValue now
                        Total = intValue root.["total"] |> Option.defaultValue 0
                        Completed = intValue root.["completed"] |> Option.defaultValue 0
                        Entries = entries
                    }
            | _ ->
                eprintfn "Ignoring batch report %s: expected a JSON object." path
                None
        with ex ->
            eprintfn "Ignoring unreadable batch report %s: %s" path ex.Message
            None

let private camelCase (value: string) =
    if value = "" then
        value
    else
        value.Substring(0, 1).ToLowerInvariant() + value.Substring(1)

let private legacyStatus (value: string) (detail: string option) =
    match value.Trim().ToLowerInvariant() with
    | "parsed" -> Status.Parsed
    | "skipped" when detail = Some "recently modified" -> Status.Pending
    | "skipped" -> Status.Skipped
    | "pending" -> Status.Pending
    | _ -> Status.Failed

/// Import a CSV report written by an older version of the CLI so that an
/// existing output directory can resume without re-parsing everything. Only
/// used when the JSON report does not exist yet.
let private readLegacyCsv (path: string) (command: string) : Report option =
    try
        let records =
            File.ReadAllText path
            |> Source.parseCsvRecords
            |> List.filter (fun record -> record |> List.exists (String.IsNullOrWhiteSpace >> not))

        if records.Length < 2 then
            None
        else
            let header = records.[0] |> List.map (fun value -> value.Trim().TrimStart('\uFEFF'))

            let find name =
                header
                |> List.tryFindIndex (fun value -> value.Equals(name, StringComparison.OrdinalIgnoreCase))

            let fileIndex = find "File" |> Option.defaultValue 0
            let statusIndex = find "Status" |> Option.defaultValue 1
            let detailIndex = find "Detail"

            let entries =
                records
                |> List.skip 1
                |> List.choose (fun fields ->
                    let field index =
                        fields |> List.tryItem index |> Option.defaultValue ""

                    let file = field fileIndex

                    if file = "" then
                        None
                    else
                        let detail =
                            detailIndex
                            |> Option.map field
                            |> Option.filter (String.IsNullOrWhiteSpace >> not)

                        let metadata =
                            header
                            |> List.indexed
                            |> List.choose (fun (index, name) ->
                                if index = fileIndex || index = statusIndex || detailIndex = Some index then
                                    None
                                else
                                    let value = field index

                                    if value = "" then None else Some(camelCase name, value))

                        Some
                            {
                                File = file
                                Status = legacyStatus (field statusIndex) detail
                                Detail = detail
                                Metadata = metadata
                            })

            if entries.IsEmpty then
                None
            else
                let now = DateTimeOffset.UtcNow

                Some
                    {
                        Command = command
                        StartedAt = now
                        UpdatedAt = now
                        Total = entries.Length
                        Completed = entries.Length
                        Entries = entries
                    }
    with ex ->
        eprintfn "Ignoring unreadable legacy batch report %s: %s" path ex.Message
        None

/// Read the JSON report, falling back to a legacy CSV report from an older
/// CLI version when the JSON report has not been created yet.
let readOrImport (jsonPath: string) (legacyCsvPath: string) (command: string) =
    match read jsonPath with
    | Some report -> Some report
    | None when File.Exists legacyCsvPath -> readLegacyCsv legacyCsvPath command
    | None -> None

/// File names that a previous report marked as finished.
let completedFiles (report: Report option) =
    report
    |> Option.map (fun value ->
        value.Entries
        |> List.filter (fun item -> Status.isComplete item.Status)
        |> List.map (fun item -> item.File)
        |> Set.ofList)
    |> Option.defaultValue Set.empty

let private entryJson (value: Entry) =
    let node = JsonObject()
    node.["file"] <- JsonValue.Create value.File
    node.["status"] <- JsonValue.Create(Status.toString value.Status)

    match value.Detail with
    | Some detail when not (String.IsNullOrWhiteSpace detail) -> node.["detail"] <- JsonValue.Create detail
    | _ -> ()

    if not value.Metadata.IsEmpty then
        let metadata = JsonObject()

        for key, item in value.Metadata do
            metadata.[key] <- JsonValue.Create item

        node.["metadata"] <- metadata

    node

let toJson (report: Report) =
    let root = JsonObject()
    root.["command"] <- JsonValue.Create report.Command

    root.["startedAt"] <- JsonValue.Create(report.StartedAt.ToString("o", CultureInfo.InvariantCulture))

    root.["updatedAt"] <- JsonValue.Create(report.UpdatedAt.ToString("o", CultureInfo.InvariantCulture))

    root.["total"] <- JsonValue.Create report.Total
    root.["completed"] <- JsonValue.Create report.Completed

    let entries = JsonArray()

    for item in report.Entries do
        entries.Add(entryJson item)

    root.["entries"] <- entries
    root.ToJsonString(jsonOptions)

/// Write a report atomically so an interrupted run never leaves a truncated
/// JSON file behind.
let write (path: string) (report: Report) =
    let directory = Path.GetDirectoryName path

    if not (String.IsNullOrEmpty directory) then
        Directory.CreateDirectory directory |> ignore

    let temporary = path + ".tmp"
    File.WriteAllText(temporary, toJson report + Environment.NewLine)
    File.Move(temporary, path, true)

module Progress =

    let private output = obj ()

    let private suffix (parts: string list) =
        let text =
            String.concat "; " parts
            |> fun value -> value.Replace("\r", " ").Replace("\n", " ")

        if text = "" then
            ""
        elif text.Length > 200 then
            " (" + text.Substring(0, 197) + "...)"
        else
            " (" + text + ")"

    let private describe (value: Entry) =
        let detail = value.Detail |> Option.filter (String.IsNullOrWhiteSpace >> not)

        suffix
            [
                yield! value.Metadata |> List.map (fun (key, item) -> sprintf "%s: %s" key item)

                match detail with
                | Some text -> yield text
                | None -> ()
            ]

    let private write (completed: int) (total: int) (status: string) (label: string) (detail: string) =
        let total = max total completed

        let percent =
            if total = 0 then
                100.0
            else
                float completed * 100.0 / float total

        let counter = (string completed).PadLeft((string total).Length) + "/" + string total

        let text = sprintf "[%s %5.1f%%] %s %s%s" counter percent status label detail

        // Parallel workers finish out of order; keep each line intact.
        lock output (fun () -> Console.Error.WriteLine text)

    /// Print one batch progress line, for example
    /// `[  1234/45128   2.7%] parsed 62917Z2F0.pdf (poolNumber: 62917Z2F0)`.
    let line (completed: int) (total: int) (value: Entry) =
        write completed total (Status.toString value.Status) value.File (describe value)

    /// Print progress for work that is not tracked in a batch report, such as a
    /// download.
    let item (completed: int) (total: int) (status: string) (label: string) (detail: string option) =
        write completed total status label (suffix (Option.toList detail))

/// Mutable state for one batch run. Entries from the previous report are
/// retained, and the report is snapshotted periodically so an interrupted run
/// can resume from the last snapshot.
type Session(path: string, command: string, total: int, previous: Report option) =
    let sync = obj ()

    let entries =
        System.Collections.Generic.Dictionary<string, Entry>(StringComparer.Ordinal)

    let startedAt =
        previous
        |> Option.map (fun report -> report.StartedAt)
        |> Option.defaultWith (fun () -> DateTimeOffset.UtcNow)

    let mutable completed = 0
    let mutable parsed = 0
    let mutable skipped = 0
    let mutable pending = 0
    let mutable failed = 0
    let mutable changesSinceWrite = 0
    let mutable lastWrite = DateTimeOffset.UtcNow

    do
        match previous with
        | Some report ->
            for item in report.Entries do
                entries.[item.File] <- item
        | None -> ()

    member _.Total = total

    member _.IsComplete(file: string) =
        match entries.TryGetValue file with
        | true, value -> Status.isComplete value.Status
        | _ -> false

    /// Counts for this run only: parsed, skipped, pending and failed.
    member _.Counts = lock sync (fun () -> parsed, skipped, pending, failed)

    member private _.Snapshot() =
        let ordered = entries.Values |> Seq.sortBy (fun item -> item.File) |> Seq.toList

        {
            Command = command
            StartedAt = startedAt
            UpdatedAt = DateTimeOffset.UtcNow
            Total = total
            Completed = completed
            Entries = ordered
        }

    member private this.Write() =
        write path (this.Snapshot())
        lastWrite <- DateTimeOffset.UtcNow
        changesSinceWrite <- 0

    /// Record an outcome, print progress, and checkpoint the report every 250
    /// items or 10 seconds, whichever comes first.
    member this.Record(value: Entry) =
        let completedNow =
            lock sync (fun () ->
                entries.[value.File] <- value
                completed <- completed + 1
                changesSinceWrite <- changesSinceWrite + 1

                match value.Status with
                | Status.Parsed -> parsed <- parsed + 1
                | Status.Skipped -> skipped <- skipped + 1
                | Status.Pending -> pending <- pending + 1
                | Status.Failed -> failed <- failed + 1

                let now = DateTimeOffset.UtcNow

                if changesSinceWrite >= 250 || (now - lastWrite).TotalSeconds >= 10.0 then
                    this.Write()

                completed)

        Progress.line completedNow total value

    /// Write the final report, including any outcomes since the last
    /// checkpoint.
    member this.Finish() = lock sync (fun () -> this.Write())

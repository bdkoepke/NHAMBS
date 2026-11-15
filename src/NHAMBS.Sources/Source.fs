module NHAMBS.Sources.Source

open System
open System.IO
open System.Net.Http

let private httpClient =
    let client = new HttpClient()
    client.Timeout <- TimeSpan.FromSeconds(120.0)
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NHAMBS.Cli/1.0")
    client

let isUrl (value: string) =
    value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)

/// Read a URL or local text file.
let readText (source: string) =
    if isUrl source then
        httpClient.GetStringAsync(source).GetAwaiter().GetResult()
    else
        File.ReadAllText source

/// Download a URL to a local file.
let downloadFile (url: string) (path: string) =
    let directory = Path.GetDirectoryName path

    if not (String.IsNullOrEmpty directory) then
        Directory.CreateDirectory(directory) |> ignore

    let bytes = httpClient.GetByteArrayAsync(url).GetAwaiter().GetResult()
    File.WriteAllBytes(path, bytes)

/// Create a temporary file with the given extension, run an action, and delete
/// the file afterwards.
let withTempFile (extension: string) (action: string -> 'T) =
    let path =
        Path.Combine(Path.GetTempPath(), "nhambs-" + Guid.NewGuid().ToString("N") + extension)

    try
        action path
    finally
        try
            File.Delete path
        with _ ->
            ()

let csvField (value: string) =
    "\"" + value.Replace("\"", "\"\"") + "\""

/// Minimal RFC 4180-style CSV row splitter (quotes and doubled quotes).
let splitCsvLine (line: string) =
    let fields = ResizeArray<string>()
    let field = System.Text.StringBuilder()
    let mutable inQuotes = false
    let mutable index = 0

    while index < line.Length do
        match line.[index] with
        | '"' when inQuotes && index + 1 < line.Length && line.[index + 1] = '"' ->
            field.Append('"') |> ignore
            index <- index + 1
        | '"' -> inQuotes <- not inQuotes
        | ',' when not inQuotes ->
            fields.Add(field.ToString())
            field.Clear() |> ignore
        | character -> field.Append(character) |> ignore

        index <- index + 1

    fields.Add(field.ToString())
    fields |> Seq.toList

/// Split CSV text into records, honoring quoted fields that contain commas or
/// newlines. Used when importing reports whose error details can span lines.
let parseCsvRecords (text: string) =
    let records = ResizeArray<string list>()
    let fields = ResizeArray<string>()
    let field = System.Text.StringBuilder()
    let mutable inQuotes = false
    let mutable index = 0

    let endField () =
        fields.Add(field.ToString())
        field.Clear() |> ignore

    let endRecord () =
        endField ()
        records.Add(fields |> Seq.toList)
        fields.Clear()

    while index < text.Length do
        match text.[index] with
        | '"' when inQuotes && index + 1 < text.Length && text.[index + 1] = '"' ->
            field.Append('"') |> ignore
            index <- index + 1
        | '"' -> inQuotes <- not inQuotes
        | ',' when not inQuotes -> endField ()
        | '\r' when not inQuotes ->
            endRecord ()

            if index + 1 < text.Length && text.[index + 1] = '\n' then
                index <- index + 1
        | '\n' when not inQuotes -> endRecord ()
        | character -> field.Append(character) |> ignore

        index <- index + 1

    if field.Length > 0 || fields.Count > 0 then
        endRecord ()

    records |> Seq.toList

/// Number of files to process or download concurrently. Defaults to the
/// processor count; set `NHAMBS_PARALLELISM` to tune it (OCR and large
/// downloads in particular are CPU and memory hungry).
let parallelism =
    match Int32.TryParse(Environment.GetEnvironmentVariable "NHAMBS_PARALLELISM") with
    | true, value when value > 0 -> value
    | _ -> Environment.ProcessorCount

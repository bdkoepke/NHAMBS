module NHAMBS.Sources.Manifest

open System
open System.IO
open System.Linq
open System.Text.RegularExpressions
open NHAMBS.Sources

/// One downloadable source from a `pool_urls.txt`-style URL list.
///
/// `FileName` is an explicit target file name when the list provides one
/// (`File`, `FileName` or `CUSIP` columns). Otherwise a name is derived from
/// `Name` or the URL.
type Entry =
    {
        Name: string
        Url: string
        FileName: string option
    }

type DownloadStatus =
    | Downloaded
    | Skipped
    | Failed of detail: string

type DownloadResult =
    {
        Entry: Entry
        Url: string
        Path: string
        Status: DownloadStatus
    }

/// Construct a manifest entry without needing the record labels in scope.
let createEntry (name: string) (url: string) (fileName: string option) : Entry =
    {
        Name = name
        Url = url
        FileName = fileName
    }

let private headerName (value: string) =
    value.Trim().TrimStart('\uFEFF').ToLowerInvariant()

let private findColumn (header: string list) (names: string list) =
    names |> List.tryPick (fun name -> header |> List.tryFindIndex ((=) name))

let private fieldAt (fields: string list) index =
    fields
    |> List.tryItem index
    |> Option.defaultValue ""
    |> fun value -> value.Trim()

let private nameFromUrl (url: string) =
    if Source.isUrl url then
        Path.GetFileNameWithoutExtension(Uri(url).AbsolutePath)
    else
        Path.GetFileNameWithoutExtension url

/// Read a URL list. Supports the CSV shape used by `pool_urls.txt`
/// (`PoolName,CUSIP,Url`) and plain text files with one URL per line. The URL
/// column is matched case-insensitively; relative URLs are resolved by the
/// downloader.
let read (path: string) : Result<Entry list, string list> =
    try
        let lines =
            File.ReadAllLines path
            |> Array.map (fun line -> line.TrimEnd('\r'))
            |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace line))
            |> Array.filter (fun line -> not (line.TrimStart().StartsWith("#", StringComparison.Ordinal)))

        if lines.Length = 0 then
            Error [ sprintf "URL list is empty: %s" path ]
        else
            let firstFields = Source.splitCsvLine lines.[0]
            let header = firstFields |> List.map headerName

            let csvEntries (urlIndex: int) (nameIndex: int option) (fileIndex: int option) =
                lines.[1..]
                |> Array.choose (fun line ->
                    let fields = Source.splitCsvLine line
                    let url = fieldAt fields urlIndex

                    if url = "" || url = "\\#" then
                        None
                    else
                        let fileName =
                            fileIndex
                            |> Option.map (fun index -> fieldAt fields index)
                            |> Option.filter (fun value -> value <> "")

                        let name =
                            nameIndex
                            |> Option.map (fun index -> fieldAt fields index)
                            |> Option.filter (fun value -> value <> "")
                            |> Option.defaultWith (fun () ->
                                match fileName with
                                | Some value -> Path.GetFileNameWithoutExtension value
                                | None -> nameFromUrl url)

                        Some
                            {
                                Name = name
                                Url = url
                                FileName = fileName
                            })
                |> Array.toList

            match findColumn header [ "url" ] with
            | Some urlIndex ->
                let nameIndex =
                    findColumn header [ "name"; "poolname"; "pool name"; "series"; "designation"; "title" ]

                let fileIndex = findColumn header [ "file"; "filename"; "file name"; "cusip" ]

                match csvEntries urlIndex nameIndex fileIndex with
                | [] -> Error [ sprintf "No URLs found in %s" path ]
                | entries -> Ok entries
            | None when lines.[0].Contains(",") -> Error [ sprintf "URL list %s is missing a Url column" path ]
            | None ->
                // Plain text: one URL per line.
                let entries =
                    lines
                    |> Array.map (fun line -> line.Trim())
                    |> Array.filter (fun line -> line <> "")
                    |> Array.map (fun url ->
                        {
                            Name = nameFromUrl url
                            Url = url
                            FileName = None
                        })
                    |> Array.toList

                if entries.IsEmpty then
                    Error [ sprintf "No URLs found in %s" path ]
                else
                    Ok entries
    with ex ->
        Error [ sprintf "Failed to read %s: %s" path ex.Message ]

let private sanitizeFileName (value: string) =
    let cleaned = Regex.Replace(value.Trim(), @"[^A-Za-z0-9._-]+", "-").Trim('-', '.')

    if cleaned = "" then "download" else cleaned

/// Target file name for an entry. Explicit names are kept as-is (minus any
/// directory components); derived names are sanitized.
let fileName (extension: string) (entry: Entry) =
    let name =
        match entry.FileName with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Path.GetFileName(value.Trim())
        | _ when not (String.IsNullOrWhiteSpace entry.Name) -> sanitizeFileName entry.Name
        | _ -> sanitizeFileName (nameFromUrl entry.Url)

    if name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) then
        name
    else
        name + extension

let private absoluteUrl (baseUrl: string option) (url: string) =
    if Source.isUrl url then
        url
    else
        let baseUri =
            baseUrl
            |> Option.bind (fun value ->
                match Uri.TryCreate(value, UriKind.Absolute) with
                | true, uri -> Some uri
                | _ -> None)

        match baseUri with
        | Some uri -> Uri(uri, url).ToString()
        | None -> url

/// Download every entry to `destination`, skipping files that already exist and
/// are not empty. Downloads run in parallel according to `NHAMBS_PARALLELISM`.
let downloadAll
    (extension: string)
    (destination: string)
    (baseUrl: string option)
    (entries: Entry list)
    : DownloadResult array =
    Directory.CreateDirectory destination |> ignore

    let total = entries.Length
    let completed = ref 0

    (List.toArray entries)
        .AsParallel()
        .AsOrdered()
        .WithDegreeOfParallelism(Source.parallelism)
        .Select(fun entry ->
            let url = absoluteUrl baseUrl entry.Url
            let path = Path.Combine(destination, fileName extension entry)

            let status =
                try
                    if File.Exists path && FileInfo(path).Length > 0L then
                        Skipped
                    else
                        Source.downloadFile url path
                        Downloaded
                with ex ->
                    Failed ex.Message

            let statusText, detail =
                match status with
                | Downloaded -> "downloaded", None
                | Skipped -> "skipped", None
                | Failed detail -> "failed", Some detail

            let completedNow = System.Threading.Interlocked.Increment(&completed.contents)

            BatchReport.Progress.item completedNow total statusText (Path.GetFileName path) detail

            {
                Entry = entry
                Url = url
                Path = path
                Status = status
            })
        .ToArray()

/// Print a download summary and return a process exit code.
let printSummary (results: DownloadResult array) =
    let downloaded =
        results
        |> Array.filter (fun result -> result.Status = Downloaded)
        |> Array.length

    let skipped =
        results |> Array.filter (fun result -> result.Status = Skipped) |> Array.length

    let failures =
        results
        |> Array.choose (fun result ->
            match result.Status with
            | Failed detail -> Some(result, detail)
            | _ -> None)

    eprintfn "Downloaded %d, skipped %d, failed %d." downloaded skipped failures.Length

    for result, detail in failures do
        eprintfn "Failed to download %s: %s" result.Url detail

    if failures.Length = 0 then 0 else 1

/// Quote a CSV value only when it needs quoting, so simple manifests stay
/// readable and match the original `pool_urls.txt` format.
let private csvValue (value: string) =
    if
        value.Contains(",")
        || value.Contains("\"")
        || value.Contains("\n")
        || value.Contains("\r")
    then
        Source.csvField value
    else
        value

/// Write a CSV URL list with the given header and rows. Values containing
/// commas, quotes or line breaks are quoted so they round-trip through `read`.
let writeCsv (path: string) (header: string list) (rows: string list list) =
    let directory = Path.GetDirectoryName path

    if not (String.IsNullOrEmpty directory) then
        Directory.CreateDirectory(directory) |> ignore

    use writer = new StreamWriter(path)
    fprintfn writer "%s" (String.concat "," header)

    for row in rows do
        fprintfn writer "%s" (row |> List.map csvValue |> String.concat ",")

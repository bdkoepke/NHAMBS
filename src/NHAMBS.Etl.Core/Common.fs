module NHAMBS.Etl.Common

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Npgsql
open NpgsqlTypes

[<Literal>]
let DefaultConnection =
    "Host=localhost;Port=54322;Database=nhambs;Username=nhambs;Password=nhambs"

let repoRoot () =
    let rec find (directory: DirectoryInfo) =
        if isNull directory then
            Directory.GetCurrentDirectory()
        elif File.Exists(Path.Combine(directory.FullName, "NHAMBS.slnx")) then
            directory.FullName
        else
            find directory.Parent

    find (DirectoryInfo(AppContext.BaseDirectory))

let connectionString () =
    match Environment.GetEnvironmentVariable "NHAMBS_CONNECTION" with
    | null
    | "" -> DefaultConnection
    | value -> value

let openConnection () =
    let connection = new NpgsqlConnection(connectionString ())
    connection.Open()
    connection

let isoDate (value: DateTime) =
    value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let parseDate (value: string) =
    if String.IsNullOrWhiteSpace value then
        None
    else
        match DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
        | true, date -> Some(date.Date)
        | _ -> None

let parseDateRequired (value: string) =
    parseDate value |> Option.defaultValue (DateTime(1900, 1, 1))

let tryDecimal (value: JsonElement) =
    if value.ValueKind = JsonValueKind.Number then
        match value.TryGetDecimal() with
        | true, d -> Some d
        | _ -> None
    elif value.ValueKind = JsonValueKind.String then
        match Decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture) with
        | true, d -> Some d
        | _ -> None
    else
        None

let tryInt (value: JsonElement) =
    if value.ValueKind = JsonValueKind.Number then
        match value.TryGetInt32() with
        | true, i -> Some i
        | _ -> None
    elif value.ValueKind = JsonValueKind.String then
        match Int32.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture) with
        | true, i -> Some i
        | _ -> None
    else
        None

let strOrEmpty (value: JsonElement) =
    if value.ValueKind = JsonValueKind.String then
        value.GetString()
    else
        ""

let prop (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value -> Some value
    | _ -> None

let nested (path: string list) (element: JsonElement) =
    let rec loop current =
        function
        | [] -> Some current
        | head :: tail ->
            match prop head current with
            | Some next -> loop next tail
            | None -> None

    loop element path

let optString (path: string list) (element: JsonElement) =
    nested path element
    |> Option.bind (fun value ->
        if value.ValueKind = JsonValueKind.String then
            Some(value.GetString())
        else
            None)
    |> Option.map (fun value -> value.Trim())

let stringOr (path: string list) (fallback: string) (element: JsonElement) =
    optString path element |> Option.defaultValue fallback

let optDecimal (path: string list) (element: JsonElement) =
    nested path element |> Option.bind tryDecimal

let optInt (path: string list) (element: JsonElement) =
    nested path element |> Option.bind tryInt

let optBool (path: string list) (element: JsonElement) =
    nested path element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        | _ -> None)

let optElement (path: string list) (element: JsonElement) = nested path element

let arrayItems (path: string list) (element: JsonElement) =
    nested path element
    |> Option.bind (fun value ->
        if value.ValueKind = JsonValueKind.Array then
            value.EnumerateArray() |> Seq.toList |> Some
        else
            None)
    |> Option.defaultValue []

let jsonRaw (element: JsonElement) = element.GetRawText()

let jsonOf (value: obj) =
    JsonSerializer.Serialize(value, JsonSerializerOptions(WriteIndented = false))

let finiteDecimal (value: decimal) =
    if value > 100000000000000m || value < -100000000000000m then
        0m
    else
        value

/// Some CMHC PDFs encode issuer names with a shifted character map (for
/// example, every character is 29 code points below the intended glyph).
/// Strings containing control characters are repaired with the inverse shift.
let repairShiftedName (name: string) =
    if String.IsNullOrWhiteSpace name then
        name
    elif name.Contains "DLVVH" && name.Contains "SRSXODLUH" then
        // A recurring font-encoding artifact in a small number of CMHC circulars.
        "Caisse Populaire acadienne ltée"
    elif name |> Seq.exists Char.IsControl then
        let repaired =
            name |> Seq.map (fun ch -> char (int ch + 29)) |> Seq.toArray |> String

        if repaired |> Seq.exists Char.IsControl then
            name
        else
            repaired
    else
        name

/// Remove OCR boilerplate that is routinely glued onto a party name, along
/// with stray control characters, and collapse whitespace.
let private cleanEntityName (name: string) =
    if String.IsNullOrWhiteSpace name then
        ""
    else
        let repaired = repairShiftedName name

        let firstLine =
            repaired.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue repaired

        let cutMarkers =
            [
                "The term "
                "the term "
                "Term “mortgage"
                "are adjustable payment"
                "This pool"
                "The mortgages"
            ]

        let withoutBoilerplate =
            cutMarkers
            |> List.fold
                (fun (value: string) marker ->
                    let index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase)

                    if index > 0 then value.Substring(0, index) else value)
                firstLine

        Regex.Replace(withoutBoilerplate, @"[\x00-\x1F\x7F]", " ")
        |> fun value -> Regex.Replace(value, @"\s+", " ")
        |> fun value -> value.Trim().TrimEnd('.', ',', ';')

let normalizeIssuer (name: string) =
    let cleaned = cleanEntityName name

    if String.IsNullOrWhiteSpace cleaned then
        "unknown"
    else
        let decomposed = cleaned.Normalize(NormalizationForm.FormD)
        let builder = StringBuilder()

        for ch in decomposed do
            if CharUnicodeInfo.GetUnicodeCategory ch <> UnicodeCategory.NonSpacingMark then
                builder.Append(ch) |> ignore

        let lowered = builder.ToString().ToLowerInvariant().Replace("&", " and ")
        let cleaned = Regex.Replace(lowered, "[^a-z0-9 ]", " ")

        let words =
            cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

        let words =
            match words with
            | head :: tail when head = "the" -> tail
            | _ -> words

        let suffixes =
            [
                "inc"
                "incorporated"
                "ltd"
                "limited"
                "corp"
                "corporation"
                "company"
                "co"
                "ulc"
                "lp"
                "llp"
                "plc"
                "gp"
                "ltee"
                "lnc"
            ]

        let rec dropSuffixes (items: string list) =
            match List.rev items with
            | last :: rest when List.contains last suffixes -> dropSuffixes (List.rev rest)
            | _ -> items

        let normalized = words |> dropSuffixes |> String.concat " "

        let result =
            if String.IsNullOrWhiteSpace normalized then
                cleaned.Trim()
            else
                normalized

        if result.Length > 240 then
            result.Substring(0, 240)
        else
            result

/// Issuer names that are clearly an OCR field-label or a non-name token are
/// kept out of the reference tables. Alias resolution itself lives in
/// `nhambs.issuer_aliases` and is applied by the ETL/reference refresh.
let private invalidIssuerText (key: string) (cleaned: string) =
    String.IsNullOrWhiteSpace cleaned
    || key = "unknown"
    || key = "company"
    || key = "canad"
    || key.StartsWith "wac "
    || key.StartsWith "cdor "
    || key.Contains "pool no"
    || key.Contains "first payment"
    || key.Contains "homeowner loan"
    || key.Contains "tout individu"
    || cleaned.Contains("POOL NO", StringComparison.OrdinalIgnoreCase)
    || cleaned.Contains("FIRST PAYMENT", StringComparison.OrdinalIgnoreCase)

let issuerDisplayName (name: string) =
    let cleaned = cleanEntityName name
    let key = normalizeIssuer cleaned

    if invalidIssuerText key cleaned then
        "Unknown issuer"
    else
        cleaned

/// Normalize the insurer labels found in the addendum tables and the cover
/// page. Combined labels such as "CMHC and Sagen" are kept as a deterministic
/// combination of the canonical names.
let canonicalInsurerName (name: string) =
    let cleaned = cleanEntityName name
    let key = normalizeIssuer cleaned
    let compact = key.Replace(" ", "")

    let known =
        [
            if
                compact.Contains "cmhc"
                || compact = "cmwc"
                || compact.StartsWith "canadamortgage"
                || ((compact.Contains "canada" || compact.Contains "canadian")
                    && compact.Contains "mortgage"
                    && compact.Contains "housing")
            then
                "Canada Mortgage and Housing Corporation (CMHC)"

            if compact.Contains "canadaguarant" || compact.Contains "canadaguarante" then
                "Canada Guaranty Mortgage Insurance Company"

            if
                compact.Contains "genw"
                || compact.StartsWith "gecapital"
                || compact = "ge"
                || (compact.StartsWith "ge" && compact |> Seq.exists Char.IsDigit)
            then
                "Genworth Financial Mortgage Insurance Company Canada"

            if compact.Contains "sagen" then
                "Sagen Mortgage Insurance Company Canada"

            if compact.Contains "aig" then
                "AIG Mortgage Insurance Company Canada"
        ]

    match known |> List.distinct with
    | [] ->
        if
            String.IsNullOrWhiteSpace cleaned
            || key = "unknown"
            || compact.Contains "termortgageloan"
            || compact.StartsWith "term"
            || compact.Contains "asusedherein"
        then
            "Unknown insurer"
        else
            cleaned
    | [ single ] -> single
    | many -> String.concat " and " many

let provinceNames =
    [
        "newfoundland", "Newfoundland and Labrador"
        "novaScotia", "Nova Scotia"
        "newBrunswick", "New Brunswick"
        "princeEdwardIsland", "Prince Edward Island"
        "quebec", "Quebec"
        "ontario", "Ontario"
        "manitoba", "Manitoba"
        "saskatchewan", "Saskatchewan"
        "alberta", "Alberta"
        "britishColumbia", "British Columbia"
        "northwestTerritories", "Northwest Territories"
        "yukon", "Yukon"
        "nunavut", "Nunavut"
    ]

let provinceLookup = dict provinceNames

let startRun (connection: NpgsqlConnection) (dataset: string) (sourcePath: string) =
    use command =
        new NpgsqlCommand(
            """
            INSERT INTO nhambs.etl_runs (dataset, started_at, status, source_path)
            VALUES (@dataset, now(), 'running', @source_path)
            RETURNING run_id;
            """,
            connection
        )

    command.Parameters.AddWithValue("dataset", dataset) |> ignore
    command.Parameters.AddWithValue("source_path", sourcePath) |> ignore
    command.ExecuteScalar() :?> int64

let completeRun
    (connection: NpgsqlConnection)
    (runId: int64)
    (files: int)
    (skipped: int)
    (rows: int64)
    (error: string option)
    =
    use command =
        new NpgsqlCommand(
            """
            UPDATE nhambs.etl_runs
            SET completed_at = now(),
                status = @status,
                files_processed = @files,
                files_skipped = @skipped,
                rows_loaded = @rows,
                error = @error
            WHERE run_id = @run_id;
            """,
            connection
        )

    let status = if error.IsNone then "succeeded" else "failed"
    command.Parameters.AddWithValue("status", status) |> ignore
    command.Parameters.AddWithValue("files", files) |> ignore
    command.Parameters.AddWithValue("skipped", skipped) |> ignore
    command.Parameters.AddWithValue("rows", rows) |> ignore

    command.Parameters.AddWithValue("error", (error |> Option.map box |> Option.defaultValue (box DBNull.Value)))
    |> ignore

    command.Parameters.AddWithValue("run_id", runId) |> ignore
    command.ExecuteNonQuery() |> ignore

type CopyWriter(connection: NpgsqlConnection, table: string, columns: string list) =
    let copy =
        connection.BeginBinaryImport(
            sprintf "COPY %s (%s) FROM STDIN (FORMAT BINARY)" table (String.concat ", " columns)
        )

    member _.StartRow() = copy.StartRow()
    member _.Write(value: string) = copy.Write(value)
    member _.Write(value: decimal) = copy.Write(value)
    member _.Write(value: int) = copy.Write(value)
    member _.Write(value: int64) = copy.Write(value)
    member _.WriteDate(value: DateTime) = copy.Write(value, NpgsqlDbType.Date)

    member _.WriteTimestampTz(value: DateTime) =
        copy.Write(value, NpgsqlDbType.TimestampTz)

    member _.Write(value: bool) = copy.Write(value)
    member _.WriteJson(value: string) = copy.Write(value, NpgsqlDbType.Jsonb)
    member _.WriteNull() = copy.WriteNull()
    member _.Complete() = copy.Complete()

    interface IDisposable with
        member _.Dispose() = copy.Dispose()

let writeOption (writer: CopyWriter) (value: 'T option) (write: 'T -> unit) =
    match value with
    | Some v -> write v
    | None -> writer.WriteNull()

let private sha256Hex (bytes: byte[]) =
    SHA256.HashData bytes
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

let log (message: string) =
    printfn "%s  %s" (DateTime.Now.ToString("HH:mm:ss")) message

/// Land the raw source documents in raw before any parsing or
/// transformation. A dataset is replaced atomically per run: the old raw
/// rows are deleted, then the current files are copied in with their checksum,
/// size, timestamp and ETL run id. Returns a path-to-id map for nhambs lineage.
let writeRawFiles
    (connection: NpgsqlConnection)
    (runId: int64)
    (dataset: string)
    (paths: string seq)
    : Dictionary<string, int64> =
    let paths = Seq.toArray paths

    use delete =
        new NpgsqlCommand("DELETE FROM raw.source_files WHERE dataset = @dataset;", connection)

    delete.Parameters.AddWithValue("dataset", dataset) |> ignore
    delete.ExecuteNonQuery() |> ignore

    use writer =
        new CopyWriter(
            connection,
            "raw.source_files",
            [
                "dataset"
                "source_path"
                "file_name"
                "checksum_sha256"
                "size_bytes"
                "source_modified_at"
                "run_id"
                "document"
            ]
        )

    let mutable totalBytes = 0L

    for path in paths do
        let bytes = File.ReadAllBytes path
        totalBytes <- totalBytes + int64 bytes.Length

        writer.StartRow()
        writer.Write(dataset)
        writer.Write(path)
        writer.Write(Path.GetFileName path)
        writer.Write(sha256Hex bytes)
        writer.Write(int64 bytes.Length)
        writer.WriteTimestampTz(File.GetLastWriteTimeUtc path)
        writer.Write(runId)
        writer.WriteJson(Encoding.UTF8.GetString bytes)

    writer.Complete() |> ignore
    (writer :> IDisposable).Dispose()

    use command =
        new NpgsqlCommand(
            "SELECT source_path, source_file_id FROM raw.source_files WHERE dataset = @dataset;",
            connection
        )

    command.Parameters.AddWithValue("dataset", dataset) |> ignore
    use reader = command.ExecuteReader()
    let ids = Dictionary<string, int64>(StringComparer.Ordinal)

    while reader.Read() do
        ids.[reader.GetString 0] <- reader.GetInt64 1

    log (sprintf "  landed %d source files (%.1f MB) in raw.%s" paths.Length (float totalBytes / 1048576.0) dataset)
    ids

let sourceFileId (ids: Dictionary<string, int64>) (path: string) =
    match ids.TryGetValue path with
    | true, id -> id
    | _ -> failwithf "Source file was not landed in raw: %s" path

let readJson (path: string) =
    use stream = File.OpenRead path
    JsonDocument.Parse(stream)

let resolvePath (path: string) =
    if Path.IsPathRooted path then
        path
    else
        Path.GetFullPath(Path.Combine(repoRoot (), path))

let ensureDirectory (path: string) =
    if not (Directory.Exists path) then
        failwithf "Directory not found: %s" path

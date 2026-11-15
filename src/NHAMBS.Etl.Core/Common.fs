module NHAMBS.Etl.Common

open System
open System.Globalization
open System.IO
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

let normalizeIssuer (name: string) =
    if String.IsNullOrWhiteSpace name then
        "unknown"
    else
        let decomposed = name.Normalize(NormalizationForm.FormD)
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

let log (message: string) =
    printfn "%s  %s" (DateTime.Now.ToString("HH:mm:ss")) message

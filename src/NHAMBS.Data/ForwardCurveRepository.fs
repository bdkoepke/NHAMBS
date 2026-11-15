module NHAMBS.Data.ForwardCurveRepository

open System
open System.Globalization
open System.IO
open System.Text.Json
open NHAMBS.Shared.Domain

let private repoRoot () =
    let rec find (directory: DirectoryInfo) =
        if isNull directory then
            Directory.GetCurrentDirectory()
        elif File.Exists(Path.Combine(directory.FullName, "NHAMBS.slnx")) then
            directory.FullName
        else
            find directory.Parent

    find (DirectoryInfo(Directory.GetCurrentDirectory()))

/// Resolves the forward-curves JSON. An explicit NHAMBS_FORWARD_CURVES path
/// wins; otherwise the ETL output directory, the repository root and the
/// application directory are checked in that order.
let private resolvePath () =
    let configured =
        match Environment.GetEnvironmentVariable "NHAMBS_FORWARD_CURVES" with
        | null
        | "" -> None
        | value -> Some(Path.GetFullPath value)

    match configured with
    | Some path -> path
    | None ->
        let root = repoRoot ()

        let candidates =
            [
                Path.Combine(root, "out", "forward_curves.json")
                Path.Combine(root, "forward_curves.json")
                Path.Combine(AppContext.BaseDirectory, "forward_curves.json")
            ]

        candidates
        |> List.tryFind File.Exists
        |> Option.defaultValue (List.head candidates)

let private tryProperty (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value -> Some value
    | _ -> None

let private text (name: string) (element: JsonElement) =
    tryProperty name element
    |> Option.bind (fun value ->
        if value.ValueKind = JsonValueKind.String then
            Option.ofObj (value.GetString())
        elif value.ValueKind = JsonValueKind.Null then
            None
        else
            Some(value.ToString()))
    |> Option.defaultValue ""

let private decimalValue (name: string) (element: JsonElement) =
    tryProperty name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.Number ->
            match value.TryGetDecimal() with
            | true, parsed -> Some parsed
            | _ -> None
        | JsonValueKind.String ->
            match Decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture) with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None)

let private intValue (name: string) (fallback: int) (element: JsonElement) =
    tryProperty name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None)
    |> Option.defaultValue fallback

let private boolValue (name: string) (element: JsonElement) =
    tryProperty name element
    |> Option.map (fun value -> value.ValueKind = JsonValueKind.True)
    |> Option.defaultValue false

let private parsePoint (element: JsonElement) =
    let date = text "date" element

    if String.IsNullOrWhiteSpace date then
        None
    else
        decimalValue "value" element
        |> Option.map (fun value -> { Date = date; Value = value })

let private parsePoints (element: JsonElement) =
    match tryProperty "data" element with
    | Some data when data.ValueKind = JsonValueKind.Array ->
        data.EnumerateArray()
        |> Seq.choose parsePoint
        |> Seq.sortBy (fun point -> point.Date)
        |> Seq.toArray
    | _ -> [||]

let private parseCurve (asOfDate: string) (element: JsonElement) =
    let curveAsOf = text "date" element

    {
        Id = text "id" element
        Name = text "name" element
        ShortName =
            let shortName = text "shortName" element

            if String.IsNullOrWhiteSpace shortName then
                text "name" element
            else
                shortName
        DataType = text "dataType" element
        Stale = boolValue "stale" element
        DecimalPrecision = intValue "decimalPrecision" 3 element
        AsOfDate =
            if String.IsNullOrWhiteSpace curveAsOf then
                asOfDate
            else
                curveAsOf
        UpdatedOn = text "updatedOn" element
        Points = parsePoints element
    }

let get () : ForwardCurveSet =
    let path = resolvePath ()

    if not (File.Exists path) then
        {
            Name = ""
            AsOfDate = ""
            Curves = [||]
            SourceFile = path
        }
    else
        use document = JsonDocument.Parse(File.ReadAllText path)
        let root = document.RootElement
        let result = tryProperty "result" root

        match result with
        | None ->
            {
                Name = ""
                AsOfDate = ""
                Curves = [||]
                SourceFile = path
            }
        | Some result ->
            let asOfDate = text "date" result

            let curves =
                match tryProperty "data" result with
                | Some data when data.ValueKind = JsonValueKind.Array ->
                    data.EnumerateArray()
                    |> Seq.map (parseCurve asOfDate)
                    |> Seq.filter (fun curve -> not (String.IsNullOrWhiteSpace curve.Id))
                    |> Seq.toArray
                | Some data when data.ValueKind = JsonValueKind.Object ->
                    let curve = parseCurve asOfDate data

                    if String.IsNullOrWhiteSpace curve.Id then
                        [||]
                    else
                        [| curve |]
                | _ -> [||]

            {
                Name = text "name" result
                AsOfDate = asOfDate
                Curves = curves
                SourceFile = path
            }

module NHAMBS.Cli.OutstandingHistoryXlsx

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open ExcelDataReader

// ExcelDataReader asks for the legacy code pages when it reads workbook
// metadata, even for XLSX files. Register the provider once at module load.
do Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

// ---------------------------------------------------------------------------
// Small parsing helpers
// ---------------------------------------------------------------------------

let private normalizeText (value: string) =
    value.Replace('\u00a0', ' ')
    |> fun text -> Regex.Replace(text, @"\s+", " ")
    |> fun text -> text.Trim()

let private labelKey (value: string) =
    normalizeText value
    |> fun text -> text.ToLowerInvariant()
    |> fun text -> Regex.Replace(text, @"[^a-z0-9]+", " ")
    |> fun text -> text.Trim()

let private cellAt (row: obj[]) index =
    if index < row.Length then row.[index] else null

let private cellText (value: obj) =
    match value with
    | null -> ""
    | :? string as text -> normalizeText text
    | :? DateTime as date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    | :? double as number -> number.ToString("R", CultureInfo.InvariantCulture)
    | :? IFormattable as value -> value.ToString(null, CultureInfo.InvariantCulture)
    | _ -> string value

let private rowText (row: obj[]) index = cellAt row index |> cellText

let private dateFormats =
    [|
        "MMMM d, yyyy"
        "MMMM dd, yyyy"
        "MMM d, yyyy"
        "MMM. d, yyyy"
        "M/d/yyyy"
        "MM/dd/yyyy"
        "yyyy-MM-dd"
    |]

let private formatDate (date: DateTime) =
    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let private tryDate (value: obj) =
    match value with
    | :? DateTime as date -> Some(formatDate date)
    | :? double as serial when serial > 0.0 && serial < 2958466.0 -> Some(formatDate (DateTime.FromOADate serial))
    | :? decimal as serial -> Some(formatDate (DateTime.FromOADate(float serial)))
    | :? string as text ->
        let text = normalizeText text

        if text = "" then
            None
        else
            match
                DateTime.TryParseExact(text, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces)
            with
            | true, date -> Some(formatDate date)
            | _ ->
                match DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces) with
                | true, date -> Some(formatDate date)
                | _ -> None
    | _ -> None

let private tryDecimal (value: obj) =
    match value with
    | :? double as number -> Some(decimal number)
    | :? decimal as number -> Some number
    | :? int as number -> Some(decimal number)
    | :? int64 as number -> Some(decimal number)
    | :? string as text ->
        let cleaned =
            normalizeText text
            |> fun value -> value.Replace(",", "").Replace("$", "").Replace("%", "")

        match Decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture) with
        | true, number -> Some number
        | _ -> None
    | _ -> None

let private formatPercent (value: decimal) =
    value.ToString("0.############", CultureInfo.InvariantCulture) + "%"

let private normalizeIndex (value: string) =
    let text = normalizeText value
    let key = text.ToLowerInvariant()

    if key.Contains "cdor" then
        if Regex.IsMatch(key, @"\b3\b|trois|3\s*mois") then
            "3m CDOR"
        else
            "CDOR"
    elif key.Contains "corra" then
        "CORRA"
    else
        text

let private parseFixedCoupon (coupon: JsonObject) (displayText: string) (rate: decimal) =
    coupon["displayText"] <- JsonValue.Create(if displayText = "" then formatPercent rate else displayText)
    coupon["kind"] <- JsonValue.Create "fixed"
    coupon["fixedRatePercent"] <- JsonValue.Create rate
    coupon

let private floatingPattern =
    @"^(?<index>.+?)\s*(?<sign>[+-])\s*(?<spread>\d+(?:[.,]\d+)?)\s*%\s*$"

let private parseFloatingCoupon (coupon: JsonObject) (displayText: string) =
    let m =
        Regex.Match(displayText, floatingPattern, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    if not m.Success then
        None
    else
        let sign = if m.Groups.["sign"].Value = "-" then -1M else 1M

        let spread =
            m.Groups.["spread"].Value.Replace(',', '.')
            |> fun value -> Decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)

        coupon["displayText"] <- JsonValue.Create displayText
        coupon["kind"] <- JsonValue.Create "floating"
        coupon["index"] <- JsonValue.Create(normalizeIndex m.Groups.["index"].Value)
        coupon["spreadPercent"] <- JsonValue.Create(sign * spread)
        Some coupon

let private parseCoupon (value: obj) =
    let coupon = JsonObject()

    match value with
    | :? double as number ->
        let rate = Math.Round(number * 100.0, 10) |> decimal
        parseFixedCoupon coupon (formatPercent rate) rate
    | :? decimal as number ->
        let rate = Math.Round(number * 100M, 10)
        parseFixedCoupon coupon (formatPercent rate) rate
    | :? string as text ->
        let displayText = normalizeText text

        match parseFloatingCoupon coupon displayText with
        | Some coupon -> coupon
        | None ->
            match tryDecimal (box displayText) with
            | Some rate -> parseFixedCoupon coupon displayText rate
            | None -> failwithf "Could not parse coupon '%s'." displayText
    | _ -> failwith "Could not parse an empty coupon cell."

let private seriesName (value: obj) =
    let text =
        match value with
        | :? double as number -> number.ToString("0", CultureInfo.InvariantCulture)
        | :? decimal as number -> number.ToString("0", CultureInfo.InvariantCulture)
        | _ -> cellText value

    if text.StartsWith("Series ", StringComparison.OrdinalIgnoreCase) then
        text
    else
        "Series " + text

let private languageFromSource (source: string) =
    let text = source.ToLowerInvariant()

    if
        text.Contains "-fr."
        || text.Contains "_fr."
        || text.Contains "/fr/"
        || text.Contains "\\fr\\"
    then
        "fr"
    else
        "en"

// ---------------------------------------------------------------------------
// Workbook parsing
// ---------------------------------------------------------------------------

let private readRows (path: string) =
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

    use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    use reader = ExcelReaderFactory.CreateReader(stream)
    let rows = ResizeArray<obj[]>()

    while reader.Read() do
        let values = Array.zeroCreate<obj> reader.FieldCount
        reader.GetValues(values) |> ignore
        rows.Add values

    rows.ToArray()

let private findTitle (rows: obj[][]) =
    rows
    |> Array.tryPick (fun row ->
        row
        |> Array.tryPick (fun value ->
            let text = cellText value

            if text.StartsWith("History of Bond Purchases for Cancellation", StringComparison.OrdinalIgnoreCase) then
                Some text
            else
                None))

let private asOfDate (title: string) =
    let m =
        Regex.Match(title, @"as of\s+(.+)$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    if not m.Success then
        failwithf "Could not find the 'as of' date in the history title '%s'." title

    match tryDate (box m.Groups.[1].Value) with
    | Some date -> date
    | None -> failwithf "Could not parse the 'as of' date '%s'." m.Groups.[1].Value

let private couponStarts (rows: obj[][]) =
    rows
    |> Array.indexed
    |> Array.choose (fun (index, row) ->
        if labelKey (rowText row 1) = "coupon" then
            Some index
        else
            None)

let private findLabelRow (rows: obj[][]) start label =
    let key = labelKey label

    let rec loop index =
        if index >= rows.Length || index > start + 8 then
            failwithf "Could not find the '%s' row in the history workbook." label
        elif labelKey (rowText rows.[index] 1) = key then
            index
        else
            loop (index + 1)

    loop start

let private issueFromBlock (rows: obj[][]) start blockEnd =
    let series =
        rows.[findLabelRow rows start "series"]
        |> fun row -> rowText row 2 |> seriesName

    let coupon =
        rows.[findLabelRow rows start "coupon"] |> fun row -> parseCoupon (cellAt row 2)

    let maturityDate =
        let rowIndex = findLabelRow rows start "maturity"

        match tryDate (cellAt rows.[rowIndex] 2) with
        | Some date -> date
        | None -> failwithf "Could not parse the maturity date for %s." series

    let identifier = rows.[findLabelRow rows start "cusip"] |> fun row -> rowText row 2

    let cancellationHeader = findLabelRow rows start "cancellation period"
    let fromToHeader = findLabelRow rows (cancellationHeader + 1) "from"
    let cancellations = JsonArray()

    for rowIndex in fromToHeader + 1 .. blockEnd - 1 do
        let row = rows.[rowIndex]
        let fromValue = cellAt row 1
        let toValue = cellAt row 2
        let amountValue = cellAt row 3

        let fromDate = tryDate fromValue
        let toDate = tryDate toValue
        let amount = tryDecimal amountValue

        // Blank styled rows and the workbook disclaimer have no date or amount
        // cells, so they are not cancellation records.
        if fromDate.IsSome || toDate.IsSome || amount.IsSome then
            match fromDate, toDate, amount with
            | Some fromDate, Some toDate, Some amount ->
                let cancellation = JsonObject()
                cancellation["fromDate"] <- JsonValue.Create fromDate
                cancellation["toDate"] <- JsonValue.Create toDate
                cancellation["cancelledAmount"] <- JsonValue.Create amount
                cancellations.Add cancellation
            | _ -> failwithf "Could not parse cancellation row %d for %s." (rowIndex + 1) series

    let issue = JsonObject()
    issue["series"] <- JsonValue.Create series
    issue["coupon"] <- coupon
    issue["maturityDate"] <- JsonValue.Create maturityDate
    issue["identifier"] <- JsonValue.Create identifier
    issue["cancellations"] <- cancellations
    issue

/// Parse every issue block in a history of bond purchases for cancellation
/// workbook as a separate JSON object shaped like
/// `cmhc-cht-outstanding-history.schema.json`.
let parse (source: string) (path: string) : JsonObject list =
    let rows = readRows path

    let title =
        findTitle rows
        |> Option.defaultWith (fun () -> failwith "Could not find the history title in the workbook.")

    let asOf = asOfDate title
    let starts = couponStarts rows

    if starts.Length = 0 then
        failwith "The history workbook has no issue blocks."

    [
        for index in 0 .. starts.Length - 1 do
            let start = starts.[index]

            let blockEnd =
                if index + 1 < starts.Length then
                    starts.[index + 1]
                else
                    rows.Length

            let issue = issueFromBlock rows start blockEnd

            let document = JsonObject()
            document["language"] <- JsonValue.Create(languageFromSource source)

            if source <> "" then
                document["sourceUrl"] <- JsonValue.Create source

            document["title"] <- JsonValue.Create title
            document["asOfDate"] <- JsonValue.Create asOf

            for property in issue do
                document[property.Key] <- property.Value.DeepClone()

            document
    ]

/// Serialize one parsed outstanding history value as pretty-printed JSON.
let toJson (value: JsonObject) =
    value.ToJsonString(JsonSerializerOptions(WriteIndented = true))

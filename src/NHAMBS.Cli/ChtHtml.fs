module NHAMBS.Cli.ChtHtml

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open FSharp.Data

// ---------------------------------------------------------------------------
// Small parsing helpers
// ---------------------------------------------------------------------------

let private normalizeText (value: string) =
    value.Replace('\u00a0', ' ')
    |> fun text -> Regex.Replace(text, @"\s+", " ")
    |> fun text -> text.Trim()

/// A case-insensitive key for matching printed table labels. Punctuation and
/// whitespace differences are removed so that footnote markers and slash
/// spacing do not affect matching.
let private labelKey (value: string) =
    normalizeText value
    |> fun text -> text.ToLowerInvariant()
    |> fun text -> Regex.Replace(text, @"[^a-z0-9]+", " ")
    |> fun text -> text.Trim()

let private cellText (cell: HtmlNode) = normalizeText (cell.InnerText())

let private cellsOfRow (row: HtmlNode) =
    row.Elements()
    |> List.filter (fun element -> element.Name() = "td" || element.Name() = "th")

let private rowsOfTable (table: HtmlNode) = table.CssSelect("tr")

let private hrefOf (cell: HtmlNode) =
    cell.CssSelect("a")
    |> List.tryHead
    |> Option.map (fun link -> link.AttributeValue("href"))

let private parseDate (value: string) =
    let text = normalizeText value

    let formats =
        [|
            "MMM. d, yyyy"
            "MMM. dd, yyyy"
            "MMM d, yyyy"
            "MMM dd, yyyy"
            "MMMM d, yyyy"
            "MMMM dd, yyyy"
            "d MMM yyyy"
            "dd MMM yyyy"
            "yyyy-MM-dd"
        |]

    DateTime
        .ParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces)
        .ToString("yyyy-MM-dd")

let private parseDecimal (value: string) =
    let cleaned =
        normalizeText value
        |> fun text -> text.Replace(",", "").Replace("$", "").Replace("%", "")

    Decimal.Parse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture)

let private parseFrequency (value: string) =
    let key = value.ToLowerInvariant() |> fun text -> Regex.Replace(text, @"[^a-z]", "")

    if key.Contains "semi" && (key.Contains "annual" || key.Contains "anual") then
        "semi-annually"
    elif key.Contains "quarter" then
        "quarterly"
    elif key.Contains "month" then
        "monthly"
    elif key.Contains "annual" || key.Contains "anual" then
        "annually"
    else
        "other"

let private couponPattern =
    @"^(?<index>.+?)\s*(?<sign>[+-])\s*(?<spread>\d+(?:\.\d+)?)\s*(?<unit>bps|%)?\s*$"

let private parseCoupon (value: string) =
    let displayText = normalizeText value

    let floating =
        Regex.Match(displayText, couponPattern, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    let coupon = JsonObject()
    coupon["displayText"] <- JsonValue.Create displayText

    if floating.Success then
        let index = normalizeText floating.Groups.["index"].Value
        let sign = if floating.Groups.["sign"].Value = "-" then -1M else 1M
        let spread = parseDecimal floating.Groups.["spread"].Value
        let unit = floating.Groups.["unit"].Value.Trim().ToLowerInvariant()

        let spreadPercent =
            let magnitude = if unit = "bps" then spread / 100M else spread
            sign * magnitude

        coupon["kind"] <- JsonValue.Create "floating"
        coupon["index"] <- JsonValue.Create index
        coupon["spreadPercent"] <- JsonValue.Create spreadPercent
    elif Regex.IsMatch(displayText, "[A-Za-z]", RegexOptions.CultureInvariant) then
        failwithf "Could not parse floating coupon '%s'." displayText
    else
        coupon["kind"] <- JsonValue.Create "fixed"
        coupon["fixedRatePercent"] <- JsonValue.Create(parseDecimal displayText)

    coupon

let private findHeader (predicate: string -> bool) (headers: string list) = headers |> List.tryFind predicate

let private tryValue (predicate: string -> bool) (pairs: (string * HtmlNode) list) =
    pairs
    |> List.tryPick (fun (key, cell) -> if predicate key then Some cell else None)

let private requireValue description predicate pairs =
    match tryValue predicate pairs with
    | Some cell -> cell
    | None -> failwithf "Could not find %s." description

let private isDebtIssuesTable (table: HtmlNode) =
    let headers =
        rowsOfTable table
        |> List.tryHead
        |> Option.map (fun row -> cellsOfRow row |> List.map (cellText >> labelKey))
        |> Option.defaultValue []

    findHeader ((=) "issue") headers |> Option.isSome
    && findHeader ((=) "coupon") headers |> Option.isSome
    && findHeader ((=) "maturity date") headers |> Option.isSome
    && findHeader (fun key -> key.StartsWith("outstanding", StringComparison.Ordinal)) headers
       |> Option.isSome

let private asOfDate (document: HtmlDocument) =
    let heading =
        document.CssSelect("h1") @ document.CssSelect("h2") @ document.CssSelect("h3")
        |> List.tryFind (fun heading ->
            (normalizeText (heading.InnerText()))
                .Contains("Outstanding Debt Securities as of", StringComparison.OrdinalIgnoreCase))

    let text =
        match heading with
        | Some heading -> normalizeText (heading.InnerText())
        | None -> normalizeText (document.Body().InnerText())

    let m =
        Regex.Match(text, @"as of\s+(.+)$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    if not m.Success then
        failwith "Could not find the 'as of' date in the CHT debt issues heading."

    parseDate m.Groups.[1].Value

let private parseCirculars (cell: HtmlNode) =
    let circulars = JsonArray()

    for link in cell.CssSelect("a") do
        let designation = normalizeText (link.InnerText())

        if designation <> "" then
            let circular = JsonObject()
            circular["designation"] <- JsonValue.Create designation
            circular["url"] <- JsonValue.Create(link.AttributeValue("href"))
            circulars.Add circular

    if circulars.Count = 0 then
        failwith "A CHT debt issue row has no offering circular links."

    circulars

/// Parse the CMHC list of outstanding CHT debt issues into a JSON object shaped
/// like `cmhc-cht-debt-issues.schema.json`.
let parseDebtIssues (sourceUrl: string) (html: string) : JsonObject =
    let document = HtmlDocument.Parse html

    let table =
        document.CssSelect("main table")
        |> List.tryFind isDebtIssuesTable
        |> Option.orElseWith (fun () -> document.CssSelect("table") |> List.tryFind isDebtIssuesTable)
        |> Option.defaultWith (fun () -> failwith "Could not find the CHT debt issues table.")

    let rows = rowsOfTable table

    let headerRow =
        rows
        |> List.tryHead
        |> Option.defaultWith (fun () -> failwith "The CHT debt issues table is empty.")

    let headers =
        cellsOfRow headerRow |> List.map (fun cell -> labelKey (cellText cell))

    let issues = JsonArray()

    for row in rows |> List.tail do
        let cells = cellsOfRow row

        if cells.Length = headers.Length then
            let pairs = List.zip headers cells

            let issue = JsonObject()

            issue["series"] <- JsonValue.Create(cellText (requireValue "issue name" ((=) "issue") pairs))

            issue["settlementDate"] <-
                JsonValue.Create(
                    parseDate (
                        cellText (
                            requireValue
                                "settlement date"
                                (fun key ->
                                    key.StartsWith("settlement date of original issue", StringComparison.Ordinal))
                                pairs
                        )
                    )
                )

            issue["coupon"] <- parseCoupon (cellText (requireValue "coupon" ((=) "coupon") pairs))

            issue["maturityDate"] <-
                JsonValue.Create(parseDate (cellText (requireValue "maturity date" ((=) "maturity date") pairs)))

            issue["currency"] <- JsonValue.Create(cellText (requireValue "currency" ((=) "ccy") pairs))

            issue["interestPaymentFrequency"] <-
                JsonValue.Create(
                    parseFrequency (
                        cellText (
                            requireValue
                                "interest payment frequency"
                                (fun key -> key.StartsWith("interest payment frequency", StringComparison.Ordinal))
                                pairs
                        )
                    )
                )

            issue["issuedToInvestors"] <-
                JsonValue.Create(
                    parseDecimal (
                        cellText (
                            requireValue
                                "issued to investors"
                                (fun key -> key.StartsWith("issued to investors", StringComparison.Ordinal))
                                pairs
                        )
                    )
                )

            let governmentCell =
                requireValue
                    "issued to the Government of Canada"
                    (fun key -> key.StartsWith("issued to the government of canada", StringComparison.Ordinal))
                    pairs

            issue["issuedToGovernmentOfCanada"] <-
                JsonValue.Create(
                    if (cellText governmentCell) = "" then
                        0M
                    else
                        parseDecimal (cellText governmentCell)
                )

            issue["totalIssuance"] <-
                JsonValue.Create(
                    parseDecimal (
                        cellText (
                            requireValue
                                "total issuance"
                                (fun key -> key.StartsWith("total issuance", StringComparison.Ordinal))
                                pairs
                        )
                    )
                )

            let outstandingCell =
                requireValue
                    "outstanding amount"
                    (fun key -> key.StartsWith("outstanding", StringComparison.Ordinal))
                    pairs

            issue["outstanding"] <- JsonValue.Create(parseDecimal (cellText outstandingCell))

            match hrefOf outstandingCell with
            | Some url when url <> "" -> issue["outstandingHistoryUrl"] <- JsonValue.Create url
            | _ -> ()

            issue["identifier"] <-
                JsonValue.Create(
                    cellText (
                        requireValue
                            "CUSIP/ISIN"
                            (fun key -> key.StartsWith("cusip isin", StringComparison.Ordinal))
                            pairs
                    )
                )

            issue["offeringCirculars"] <-
                parseCirculars (
                    requireValue
                        "offering circulars"
                        (fun key -> key.StartsWith("pdf offering circulars", StringComparison.Ordinal))
                        pairs
                )

            issues.Add issue

    if issues.Count = 0 then
        failwith "The CHT debt issues table has no issue rows."

    let root = JsonObject()
    root["language"] <- JsonValue.Create "en"

    if sourceUrl <> "" then
        root["sourceUrl"] <- JsonValue.Create sourceUrl

    root["asOfDate"] <- JsonValue.Create(asOfDate document)
    root["issues"] <- issues
    root

/// Serialize a parsed CHT debt issues value as pretty-printed JSON.
let toJson (value: JsonObject) =
    value.ToJsonString(JsonSerializerOptions(WriteIndented = true))

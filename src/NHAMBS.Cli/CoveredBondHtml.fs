module NHAMBS.Cli.CoveredBondHtml

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open FSharp.Data

// ---------------------------------------------------------------------------
// Small parsing helpers
// ---------------------------------------------------------------------------

/// Collapse HTML whitespace (including non-breaking spaces) and trim.
let private normalizeText (value: string) =
    value.Replace('\u00a0', ' ')
    |> fun text -> Regex.Replace(text, @"\s+", " ")
    |> fun text -> text.Trim()

/// A case-insensitive key for matching printed table labels. Punctuation and
/// whitespace differences are removed so that variants such as
/// "Account Bank / Guaranteed Deposit Account Provider" and
/// "Account Bank/Guaranteed Deposit Account Provider" compare equal.
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

let private pairsOfTable (table: HtmlNode) =
    rowsOfTable table
    |> List.choose (fun row ->
        match cellsOfRow row with
        | label :: value :: _ -> Some(labelKey (cellText label), cellText value)
        | _ -> None)

let private tryFindValue (pairs: (string * string) list) (predicate: string -> bool) =
    pairs
    |> List.tryPick (fun (key, value) -> if predicate key then Some value else None)

let private requireValue (pairs: (string * string) list) (description: string) (predicate: string -> bool) =
    match tryFindValue pairs predicate with
    | Some value when value <> "" -> value
    | _ -> failwithf "Could not find %s." description

let private isNotApplicable (value: string) =
    let normalized = value.Trim().ToUpperInvariant()
    normalized = "NA" || normalized = "N/A" || normalized = "N.A."

let private optionalValue (pairs: (string * string) list) (predicate: string -> bool) =
    tryFindValue pairs predicate
    |> Option.map normalizeText
    |> Option.filter (fun value -> value <> "" && not (isNotApplicable value))

let private parseDate (value: string) =
    let text = normalizeText value

    let formats =
        [|
            "MMMM d, yyyy"
            "MMMM dd, yyyy"
            "MMMM d yyyy"
            "MMMM dd yyyy"
            "d MMMM, yyyy"
            "dd MMMM, yyyy"
            "d MMMM yyyy"
            "dd MMMM yyyy"
            "MMM d, yyyy"
            "MMM dd, yyyy"
            "MMM d yyyy"
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

let private parseBulletType (value: string) =
    let key = value.ToLowerInvariant()

    if key.Contains "hard" then "hard"
    elif key.Contains "soft" then "soft"
    else "other"

let private couponPattern =
    @"^(?<index>.+?)\s*\+\s*(?<spread>\d+(?:\.\d+)?)\s*(?<unit>bps|%|per\s+annum)?(?:\s+per\s+annum)?\s*$"

let private parseCoupon (value: string) =
    let displayText = normalizeText value

    let floating =
        Regex.Match(displayText, couponPattern, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    let coupon = JsonObject()
    coupon["displayText"] <- JsonValue.Create displayText

    if floating.Success then
        let index = normalizeText floating.Groups.["index"].Value
        let spread = parseDecimal floating.Groups.["spread"].Value
        let unit = floating.Groups.["unit"].Value.Trim().ToLowerInvariant()

        let spreadPercent = if unit = "bps" then spread / 100M else spread

        coupon["kind"] <- JsonValue.Create "floating"
        coupon["index"] <- JsonValue.Create index
        coupon["spreadPercent"] <- JsonValue.Create spreadPercent
    elif Regex.IsMatch(displayText, "[A-Za-z]", RegexOptions.CultureInvariant) then
        failwithf "Could not parse floating coupon '%s'." displayText
    else
        coupon["kind"] <- JsonValue.Create "fixed"
        coupon["fixedRatePercent"] <- JsonValue.Create(parseDecimal displayText)

    coupon

let private isinPattern = "^[A-Z]{2}[A-Z0-9]{9}[0-9]$"

let private parseIsin (value: string) =
    let displayText = normalizeText value
    let entries = ResizeArray<JsonObject>()
    let mutable remaining = displayText

    // Labelled ISINs, e.g. "Reg S: USC0574BAB48 Rule 144A: US06418BAB45".
    for m in Regex.Matches(displayText, @"(?<label>[A-Za-z0-9 ]{1,24}?)\s*:\s*(?<isin>[A-Z0-9]{12})") do
        let entry = JsonObject()
        let label = normalizeText m.Groups.["label"].Value

        if label <> "" then
            entry["designation"] <- JsonValue.Create label

        entry["value"] <- JsonValue.Create(m.Groups.["isin"].Value)
        entries.Add entry
        remaining <- remaining.Replace(m.Value, " ")

    // Plain ISINs with no printed designation.
    for m in Regex.Matches(remaining, @"\b[A-Z0-9]{12}\b") do
        if Regex.IsMatch(m.Value, isinPattern) then
            let entry = JsonObject()
            entry["value"] <- JsonValue.Create m.Value
            entries.Add entry

    if entries.Count = 0 then
        failwithf "Could not find an ISIN in '%s'." displayText

    for entry in entries do
        if not (Regex.IsMatch(entry["value"].GetValue<string>(), isinPattern)) then
            failwithf "Invalid ISIN '%s' in '%s'." (entry["value"].GetValue<string>()) displayText

    let isin = JsonObject()
    isin["displayText"] <- JsonValue.Create displayText
    let array = JsonArray()
    entries |> Seq.iter (fun entry -> array.Add entry)
    isin["entries"] <- array
    isin

// ---------------------------------------------------------------------------
// Registry
// ---------------------------------------------------------------------------

let private isRegistryTable (table: HtmlNode) =
    let headerKeys =
        rowsOfTable table
        |> List.tryHead
        |> Option.map (fun row -> cellsOfRow row |> List.map (cellText >> labelKey))
        |> Option.defaultValue []

    headerKeys |> List.exists (fun key -> key.Contains "registered issuer name")
    && (headerKeys |> List.exists (fun key -> key.Contains "registered programs"))

/// Parse the CMHC Canadian Covered Bonds Registry page into a JSON object
/// shaped like `cmhc-covered-bonds-registry.schema.json`.
let parseRegistry (sourceUrl: string) (html: string) : JsonObject =
    let document = HtmlDocument.Parse html

    let tables =
        let inMain = document.CssSelect("main table") |> List.filter isRegistryTable

        if not inMain.IsEmpty then
            inMain
        else
            document.CssSelect("table") |> List.filter isRegistryTable

    if tables.IsEmpty then
        failwith "Could not find the covered bonds registry table."

    let rows = tables |> List.collect (fun table -> rowsOfTable table |> List.tail)

    let issuers = JsonArray()

    for row in rows do
        match cellsOfRow row with
        | [ name; address; registrationDate; status; website; programs ] when normalizeText (cellText name) <> "" ->
            let statusKey = (cellText status).ToUpperInvariant()

            let normalizedStatus =
                match statusKey with
                | "A" -> "active"
                | "S" -> "suspended"
                | other -> failwithf "Unknown registry status '%s'." other

            let issuer = JsonObject()
            issuer["name"] <- JsonValue.Create(normalizeText (cellText name))
            issuer["businessAddress"] <- JsonValue.Create(normalizeText (cellText address))
            issuer["registrationDate"] <- JsonValue.Create(parseDate (cellText registrationDate))
            issuer["status"] <- JsonValue.Create normalizedStatus
            issuer["websiteUrl"] <- JsonValue.Create(hrefOf website |> Option.defaultValue (cellText website))
            issuer["programPage"] <- JsonValue.Create(hrefOf programs |> Option.defaultValue (cellText programs))
            issuers.Add issuer
        | _ -> ()

    if issuers.Count = 0 then
        failwith "The covered bonds registry table has no issuer rows."

    let root = JsonObject()
    root["language"] <- JsonValue.Create "en"

    if sourceUrl <> "" then
        root["sourceUrl"] <- JsonValue.Create sourceUrl

    root["issuers"] <- issuers
    root

// ---------------------------------------------------------------------------
// Program
// ---------------------------------------------------------------------------

let private isProgramTable (table: HtmlNode) =
    let keys = pairsOfTable table |> List.map fst

    keys |> List.contains "issuer name"
    && keys |> List.contains "program name"
    && keys
       |> List.exists (fun key -> key.StartsWith("minimum asset percentage", StringComparison.Ordinal))

let private isBondTable (table: HtmlNode) =
    let keys = pairsOfTable table |> List.map fst

    keys
    |> List.exists (fun key -> key.StartsWith("registration date", StringComparison.Ordinal))
    && keys |> List.contains "isin"
    && keys
       |> List.exists (fun key -> key.StartsWith("coupon rate", StringComparison.Ordinal))

let private parseBond (table: HtmlNode) =
    let pairs = pairsOfTable table

    let required description predicate =
        requireValue pairs description predicate

    let optional predicate = optionalValue pairs predicate

    let bond = JsonObject()

    bond["registrationDate"] <-
        JsonValue.Create(parseDate (required "registration date" (fun key -> key = "registration date")))

    bond["issueDate"] <- JsonValue.Create(parseDate (required "date of issue" (fun key -> key = "date of issue")))
    bond["seriesDesignation"] <- JsonValue.Create(required "series designation" (fun key -> key = "series designation"))
    bond["issueCurrency"] <- JsonValue.Create(required "issue currency" (fun key -> key = "issue currency"))

    bond["nominalAmount"] <-
        JsonValue.Create(parseDecimal (required "nominal amount" (fun key -> key = "nominal amount")))

    bond["isin"] <- parseIsin (required "ISIN" (fun key -> key = "isin"))

    bond["bulletType"] <-
        JsonValue.Create(
            parseBulletType (
                required "hard bullet/soft bullet" (fun key -> key.StartsWith("hard bullet", StringComparison.Ordinal))
            )
        )

    bond["maturityDate"] <- JsonValue.Create(parseDate (required "maturity date" (fun key -> key = "maturity date")))

    match optional (fun key -> key.StartsWith("extended maturity", StringComparison.Ordinal)) with
    | Some value -> bond["extendedMaturityDate"] <- JsonValue.Create(parseDate value)
    | None -> ()

    bond["coupon"] <-
        parseCoupon (required "coupon rate" (fun key -> key.StartsWith("coupon rate", StringComparison.Ordinal)))

    bond["interestPaymentFrequency"] <-
        JsonValue.Create(
            parseFrequency (
                required "interest payment frequency" (fun key ->
                    key.StartsWith("interest payment date", StringComparison.Ordinal))
            )
        )

    match optional (fun key -> key.StartsWith("listing authority", StringComparison.Ordinal)) with
    | Some value -> bond["listingAuthority"] <- JsonValue.Create value
    | None -> ()

    match optional (fun key -> key.StartsWith("other material characteristics", StringComparison.Ordinal)) with
    | Some value -> bond["otherMaterialCharacteristics"] <- JsonValue.Create value
    | None -> ()

    bond

/// Parse one CMHC Canadian registered covered bond program page into a JSON
/// object shaped like `cmhc-covered-bond-program.schema.json`.
let parseProgram (sourceUrl: string) (html: string) : JsonObject =
    let document = HtmlDocument.Parse html
    let tables = document.CssSelect("table")

    let profile =
        tables
        |> List.tryFind isProgramTable
        |> Option.defaultWith (fun () -> failwith "Could not find the covered bond program profile table.")

    let profilePairs = pairsOfTable profile

    let required description predicate =
        requireValue profilePairs description predicate

    let optional predicate = optionalValue profilePairs predicate

    let parties = JsonObject()

    parties["guarantorEntity"] <-
        JsonValue.Create(
            required "guarantor entity" (fun key -> key.StartsWith("guarantor entity", StringComparison.Ordinal))
        )

    parties["counterpartiesToCoveredBondCollateralHedge"] <-
        JsonValue.Create(
            required "counterparties to the covered bond collateral hedge" (fun key ->
                key.StartsWith("counterparty", StringComparison.Ordinal))
        )

    parties["eligibleLoanServicers"] <-
        JsonValue.Create(
            required "eligible loan servicers" (fun key ->
                key.StartsWith("eligible loan servicer", StringComparison.Ordinal))
        )

    parties["cashManager"] <- JsonValue.Create(required "cash manager" (fun key -> key = "cash manager"))

    match optional (fun key -> key = "originators") with
    | Some value -> parties["originators"] <- JsonValue.Create value
    | None -> ()

    parties["coverPoolMonitor"] <-
        JsonValue.Create(required "cover pool monitor" (fun key -> key = "cover pool monitor"))

    match optional (fun key -> key = "custodian") with
    | Some value -> parties["custodian"] <- JsonValue.Create value
    | None -> ()

    match optional (fun key -> key = "bond trustee") with
    | Some value -> parties["bondTrustee"] <- JsonValue.Create value
    | None -> ()

    match
        optional (fun key ->
            key.StartsWith("bond trustee", StringComparison.Ordinal)
            && key.Contains "custodian"
            && key.Contains "titleholder")
    with
    | Some value -> parties["bondTrusteeCustodianAndTitleholder"] <- JsonValue.Create value
    | None -> ()

    match optional (fun key -> key.StartsWith("paying agent", StringComparison.Ordinal)) with
    | Some value -> parties["payingAgent"] <- JsonValue.Create value
    | None -> ()

    parties["portfolioManager"] <- JsonValue.Create(required "portfolio manager" (fun key -> key = "portfolio manager"))

    parties["accountBank"] <-
        JsonValue.Create(
            required "account bank" (fun key ->
                key.StartsWith("account bank", StringComparison.Ordinal)
                && not (key.StartsWith("standby account bank", StringComparison.Ordinal)))
        )

    parties["standbyAccountBank"] <-
        JsonValue.Create(
            required "standby account bank" (fun key ->
                key.StartsWith("standby account bank", StringComparison.Ordinal))
        )

    let root = JsonObject()
    root["language"] <- JsonValue.Create "en"

    if sourceUrl <> "" then
        root["sourceUrl"] <- JsonValue.Create sourceUrl

    root["issuerName"] <- JsonValue.Create(required "issuer name" (fun key -> key = "issuer name"))
    root["programName"] <- JsonValue.Create(required "program name" (fun key -> key = "program name"))
    root["parties"] <- parties

    root["minimumAssetPercentage"] <-
        JsonValue.Create(
            parseDecimal (
                required "minimum asset percentage" (fun key ->
                    key.StartsWith("minimum asset percentage", StringComparison.Ordinal))
            )
        )

    root["maximumAssetPercentage"] <-
        JsonValue.Create(
            parseDecimal (
                required "maximum asset percentage" (fun key ->
                    key.StartsWith("maximum asset percentage", StringComparison.Ordinal))
            )
        )

    let bonds = JsonArray()

    tables
    |> List.filter isBondTable
    |> List.iter (fun table -> bonds.Add(parseBond table))

    if bonds.Count = 0 then
        failwith "The covered bond program page has no bond issuance tables."

    root["bonds"] <- bonds
    root

/// Serialize a parsed covered-bond value as pretty-printed JSON.
let toJson (value: JsonObject) =
    value.ToJsonString(JsonSerializerOptions(WriteIndented = true))

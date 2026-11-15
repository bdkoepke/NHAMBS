module NHAMBS.Cli.OfferingCircularPdf

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

// ---------------------------------------------------------------------------
// Small parsing helpers
// ---------------------------------------------------------------------------

let private normalizeText (value: string) =
    value.Replace('\u00a0', ' ')
    |> fun text -> Regex.Replace(text, @"\s+", " ")
    |> fun text -> text.Trim()

let private monthNames =
    "January|February|March|April|May|June|July|August|September|October|November|December"

let private datePattern = sprintf @"(?:%s)\s+\d{1,2},\s+\d{4}" monthNames

let private tryMatch (pattern: string) (text: string) =
    let m =
        Regex.Match(text, pattern, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    if m.Success then Some m else None

let private matchOrFail (label: string) (pattern: string) (text: string) =
    match tryMatch pattern text with
    | Some m -> m
    | None -> failwithf "Could not find %s in the offering circular." label

let private group (m: Match) (index: int) = m.Groups.[index].Value.Trim()

let private parseDate (value: string) =
    let text = normalizeText value

    let formats =
        [|
            "MMMM d, yyyy"
            "MMMM dd, yyyy"
            "MMM d, yyyy"
            "MMM. d, yyyy"
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

let private normalizeFrequency (value: string) =
    let key = value.ToLowerInvariant() |> fun text -> Regex.Replace(text, @"[^a-z]", "")

    if key.Contains "semi" && (key.Contains "annual" || key.Contains "anual") then
        "semi-annually"
    elif key.Contains "half" then
        "semi-annually"
    elif key.Contains "quarter" then
        "quarterly"
    elif key.Contains "month" then
        "monthly"
    elif key.Contains "annual" || key.Contains "anual" then
        "annually"
    else
        "other"

let private parseInterestDates (value: string) =
    let normalized = normalizeText value

    let parts =
        normalized.Replace(" and ", ",").Split(',')
        |> Array.map (fun part -> normalizeText part)
        |> Array.filter (fun part -> part <> "")

    if parts.Length < 2 then
        failwithf "Expected at least two recurring interest payment dates, got '%s'." value

    parts
    |> Array.map (fun part ->
        let m =
            Regex.Match(
                part,
                sprintf @"^(%s)\s*(\d{1,2})$" monthNames,
                RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
            )

        if not m.Success then
            failwithf "Could not parse interest payment date '%s'." part

        sprintf "%s %d" (group m 1) (Int32.Parse(group m 2, CultureInfo.InvariantCulture)))

// ---------------------------------------------------------------------------
// Cover-page terms
// ---------------------------------------------------------------------------

let private parseCoupon (cover: string) (full: string) =
    let coupon = JsonObject()

    if cover.Contains("Floating Rate Canada Mortgage Bonds", StringComparison.OrdinalIgnoreCase) then
        let index, spread =
            let trimBasisPoints description value sign =
                let magnitude = parseDecimal value / 100M
                description, (if sign = "-" then -magnitude else magnitude)

            let patterns =
                [
                    @"3[\s-]?Month\s+CDOR[^.]{0,320}?\b(plus|less|minus)\s+([\d.]+)\s+basis\s+points", "3 Month CDOR"
                    @"Daily\s+Compounded\s+CORRA[^.]{0,320}?\b(plus|less|minus)\s+([\d.]+)\s+basis\s+points",
                    "Daily Compounded CORRA"
                    @"\bCORRA\b[^.]{0,320}?\b(plus|less|minus)\s+([\d.]+)\s+basis\s+points", "CORRA"
                ]

            patterns
            |> List.tryPick (fun (pattern, description) ->
                match tryMatch pattern full with
                | None -> None
                | Some m ->
                    let sign =
                        match (group m 1).ToLowerInvariant() with
                        | "less"
                        | "minus" -> "-"
                        | _ -> "+"

                    Some(trimBasisPoints description (group m 2) sign))
            |> Option.defaultWith (fun () ->
                failwith "Could not find the floating-rate index and spread in the offering circular.")

        coupon["displayText"] <- JsonValue.Create "Floating Rate"
        coupon["kind"] <- JsonValue.Create "floating"
        coupon["index"] <- JsonValue.Create index
        coupon["spreadPercent"] <- JsonValue.Create spread
    else
        let m = matchOrFail "fixed coupon" @"([\d.]+)%\s+Canada Mortgage Bonds" cover
        let displayText = (group m 1) + "%"

        coupon["displayText"] <- JsonValue.Create displayText
        coupon["kind"] <- JsonValue.Create "fixed"
        coupon["fixedRatePercent"] <- JsonValue.Create(parseDecimal (group m 1))

    coupon

let private parseRatings (full: string) =
    let m =
        tryMatch
            @"[“""]([^”""]+)[”""]\s+by\s+([^,]+),\s+[“""]([^”""]+)[”""]\s+by\s+([^,]+)\s+and\s+[“""]([^”""]+)[“”""]\s+by\s+([^.]+)\."
            full

    m
    |> Option.map (fun m ->
        let ratings = JsonArray()

        for ratingIndex, agencyIndex in [ 1, 2; 3, 4; 5, 6 ] do
            let rating = JsonObject()
            rating["rating"] <- JsonValue.Create(group m ratingIndex)
            rating["agency"] <- JsonValue.Create(group m agencyIndex)
            ratings.Add rating

        ratings)

let private tryLastMatch (pattern: string) (text: string) =
    let matches =
        Regex.Matches(text, pattern, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    if matches.Count = 0 then
        None
    else
        Some matches.[matches.Count - 1]

let private parseIdentifiers (full: string) =
    let commonCode =
        tryLastMatch @"Common Code(?:\s+of)?\s+([0-9]{6,12})" full
        |> Option.map (fun m -> group m 1)

    let isin =
        tryLastMatch @"ISIN(?:\s+of)?\s+(CA\s?[A-Z0-9]{10})" full
        |> Option.map (fun m -> (group m 1).Replace(" ", ""))

    let cusip =
        tryLastMatch @"CUSIP(?:\s+number)?(?:\s+of)?\s+([A-Z0-9]{9})\b" full
        |> Option.map (fun m -> group m 1)

    if commonCode.IsNone && isin.IsNone && cusip.IsNone then
        None
    else
        let identifiers = JsonObject()

        commonCode
        |> Option.iter (fun value -> identifiers["commonCode"] <- JsonValue.Create value)

        isin |> Option.iter (fun value -> identifiers["isin"] <- JsonValue.Create value)

        cusip
        |> Option.iter (fun value -> identifiers["cusip"] <- JsonValue.Create value)

        Some identifiers

/// Parse the key terms from an offering circular PDF's extracted text into a
/// JSON object shaped like `cmhc-cht-offering-circular.schema.json`.
let parseText (sourceUrl: string) (text: string) : JsonObject =
    let full = normalizeText text
    let cover = full.Substring(0, min 3000 full.Length)

    let documentDate =
        let m = matchOrFail "document date" datePattern cover

        parseDate m.Value

    let series =
        let m = matchOrFail "series" @"Series\s+(\d+)" full
        "Series " + group m 1

    let aggregatePrincipalAmount =
        let m =
            matchOrFail "aggregate principal amount" @"C\$([\d,]+)\s+Aggregate Principal Amount" full

        parseDecimal (group m 1)

    let maturityDate =
        let m = matchOrFail "maturity date" (@"to mature\s+(" + datePattern + @")") full

        parseDate (group m 1)

    let issuePricePercent =
        let m = matchOrFail "issue price" @"ISSUE PRICE:\s*([\d.]+)%" full

        parseDecimal (group m 1)

    let settlementDate =
        let m =
            matchOrFail "settlement date" (@"on\s+or\s+about\s+(" + datePattern + @")") full

        parseDate (group m 1)

    let firstInterestPaymentDate =
        let m =
            matchOrFail
                "first interest payment date"
                (@"first interest payment which shall be payable on\s+(" + datePattern + @")")
                full

        parseDate (group m 1)

    let interestFrequency, interestDates =
        let fixedFrequency = tryMatch @"(Half-yearly)\s+interest\s*\(([^)]+)\)" full

        match fixedFrequency with
        | Some m -> normalizeFrequency (group m 1), parseInterestDates (group m 2)
        | None ->
            let floatingFrequency =
                matchOrFail
                    "interest payment frequency"
                    @"Interest on the Global Bond will be payable\s+(quarterly)\s*\(on ([^)]+)\)"
                    full

            normalizeFrequency (group floatingFrequency 1), parseInterestDates (group floatingFrequency 2)

    let minimumDenomination =
        let m =
            matchOrFail "minimum denomination" @"minimum denominations of C\$([\d,]+)" full

        parseDecimal (group m 1)

    let root = JsonObject()
    root["language"] <- JsonValue.Create "en"

    if sourceUrl <> "" then
        root["sourceUrl"] <- JsonValue.Create sourceUrl

    root["series"] <- JsonValue.Create series
    root["documentDate"] <- JsonValue.Create documentDate
    root["currency"] <- JsonValue.Create "CAD"
    root["aggregatePrincipalAmount"] <- JsonValue.Create aggregatePrincipalAmount
    root["coupon"] <- parseCoupon cover full
    root["maturityDate"] <- JsonValue.Create maturityDate
    root["issuePricePercent"] <- JsonValue.Create issuePricePercent

    match tryMatch @"yield at date of issue\s+(?:approximately|about)\s*([\d.]+)%" full with
    | Some m -> root["yieldPercent"] <- JsonValue.Create(parseDecimal (group m 1))
    | None -> ()

    root["settlementDate"] <- JsonValue.Create settlementDate
    root["firstInterestPaymentDate"] <- JsonValue.Create firstInterestPaymentDate
    root["interestPaymentFrequency"] <- JsonValue.Create interestFrequency

    root["interestPaymentDates"] <-
        JsonArray(interestDates |> Array.map (fun value -> JsonValue.Create value :> JsonNode))

    root["minimumDenomination"] <- JsonValue.Create minimumDenomination
    root["issuerName"] <- JsonValue.Create "Canada Housing Trust No. 1"
    root["guarantorName"] <- JsonValue.Create "Canada Mortgage and Housing Corporation"

    match tryMatch @"Legal Entity Identifier \(LEI\):\s*([A-Z0-9]+)" full with
    | Some m -> root["legalEntityIdentifier"] <- JsonValue.Create(group m 1)
    | None -> ()

    match tryMatch @"Official List of the\s+([A-Za-z ]+?)\s+(?:and|for)" full with
    | Some m -> root["listingExchange"] <- JsonValue.Create(group m 1)
    | None -> ()

    match parseRatings full with
    | Some ratings -> root["ratings"] <- ratings
    | None -> ()

    match parseIdentifiers full with
    | Some identifiers -> root["identifiers"] <- identifiers
    | None -> ()

    root

/// Serialize a parsed offering circular as pretty-printed JSON.
let toJson (value: JsonObject) =
    value.ToJsonString(JsonSerializerOptions(WriteIndented = true))

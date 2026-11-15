module NHAMBS.Cli.CircularPdf

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

// ---------------------------------------------------------------------------
// Small parsing helpers
// ---------------------------------------------------------------------------

let private tryMatch (pattern: string) (text: string) =
    let m =
        Regex.Match(text, pattern, RegexOptions.Multiline ||| RegexOptions.CultureInvariant)

    if m.Success then Some m else None

let private matchOrFail (label: string) (pattern: string) (text: string) =
    match tryMatch pattern text with
    | Some m -> m
    | None -> failwithf "Could not find %s." label

let private group (m: Match) (index: int) = m.Groups.[index].Value.Trim()

/// Escape a printed label for a regex while allowing OCR to insert extra
/// spaces between words.
let private escapedLabel (label: string) =
    Regex.Escape(label).Replace(@"\ ", @"[ \t]+")

let private parseDecimal (value: string) =
    let cleaned =
        value.Replace(",", "").Replace("$", "").Replace("%", "").Trim().TrimEnd('.')

    Decimal.Parse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture)

let private parseDate (value: string) =
    // Some extractions lose the space between the month and day, or after the
    // comma ("June1,2008", "March 1,2008").
    let normalized =
        value
        |> fun text -> Regex.Replace(text, @"(?<=[A-Za-z])(?=\d)", " ")
        |> fun text -> Regex.Replace(text, @",\s*", ", ")
        |> fun text -> Regex.Replace(text, @"\s+", " ")

    DateTime.Parse(normalized, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd")

let private normalizeMonthNames (value: string) =
    // Dotnet's MMM format only accepts three-letter abbreviations, while the
    // 2007-2010 forms print "June", "July" and "Sept".
    let months =
        [
            "January", "Jan"
            "February", "Feb"
            "March", "Mar"
            "April", "Apr"
            "June", "Jun"
            "July", "Jul"
            "August", "Aug"
            "September", "Sep"
            "Sept", "Sep"
            "October", "Oct"
            "November", "Nov"
            "December", "Dec"
            // Some circulars print the interest-adjustment dates with French
            // month names even in the English document.
            "janvier", "Jan"
            "janv", "Jan"
            "jan", "Jan"
            "février", "Feb"
            "fevrier", "Feb"
            "févr", "Feb"
            "fevr", "Feb"
            "fév", "Feb"
            "fev", "Feb"
            "mars", "Mar"
            "mar", "Mar"
            "avril", "Apr"
            "avr", "Apr"
            "mai", "May"
            "juin", "Jun"
            "juillet", "Jul"
            "juil", "Jul"
            "août", "Aug"
            "aout", "Aug"
            "aoû", "Aug"
            "septembre", "Sep"
            "sept", "Sep"
            "octobre", "Oct"
            "novembre", "Nov"
            "décembre", "Dec"
            "decembre", "Dec"
            "déc", "Dec"
        ]

    months
    |> List.fold
        (fun (text: string) (full, abbreviated) -> Regex.Replace(text, full, abbreviated, RegexOptions.IgnoreCase))
        value

let private parseMonthYear (value: string) =
    let formats =
        [|
            "MMM/yyyy"
            "M/yyyy"
            "MM/yyyy"
            "MMM, yyyy"
            "MMMM, yyyy"
            "MMM yyyy"
            "MMMM yyyy"
            "MMM-yy"
            "MMM-yyyy"
            "MM/dd/yy"
            "M/d/yy"
            "MM/dd/yyyy"
            "M/d/yyyy"
            "dd MMM, yyyy"
            "d MMM, yyyy"
            "dd MMM yyyy"
            "d MMM yyyy"
            "dd-MMM-yy"
            "d-MMM-yy"
            "dd-MMM-yyyy"
            "d-MMM-yyyy"
        |]

    DateTime
        .ParseExact(normalizeMonthNames value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None)
        .ToString("yyyy-MM-dd")

/// Numeric tokens on a line together with their character offsets. Offsets are
/// used to map table cells to their printed columns. Handles comma or space
/// thousands separators and parenthesized negatives.
let private numericTokens (line: string) =
    let pattern =
        @"\(\s*-?[\d, ]+\.\d{1,6}\s*\)|-?\d+(?:(?:,[ \t]*|[ \t])\d{3})*\.\d{1,6}"

    Regex.Matches(line, pattern, RegexOptions.CultureInvariant)
    |> Seq.choose (fun m ->
        let raw = m.Value
        let isNegative = raw.Contains "(" || raw.StartsWith "-"
        let digits = Regex.Replace(raw, @"[^\d.]", "").TrimEnd('.')

        try
            let value = parseDecimal digits
            Some(m.Index, (if isNegative then -value else value))
        with _ ->
            None)
    |> Seq.toList

/// All decimal-looking tokens on a line. Used for table rows, where columns are
/// separated only by whitespace.
let private numericValues (line: string) = numericTokens line |> List.map snd

/// Find a heading as a complete line. Anchoring avoids matching prose such as
/// "see the section entitled 'Mortgage Pool Profile at Issue Date'".
let private tryFindHeading (marker: string) (startAt: int) (text: string) =
    let pattern = @"(?m)^[ \t\f\r]*" + escapedLabel marker + @"[ \t\r]*$"

    let regex =
        Regex(pattern, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant, TimeSpan.FromSeconds 5.0)

    let m = regex.Match(text, startAt)

    if m.Success then Some m else None

let private trySectionBetween (startMarker: string) (endMarker: string) (text: string) =
    match tryFindHeading startMarker 0 text with
    | None -> None
    | Some start ->
        let afterStart = start.Index + start.Length

        match tryFindHeading endMarker afterStart text with
        | None -> None
        | Some finish -> Some(text.Substring(afterStart, finish.Index - afterStart))

let private sectionBetween (startMarker: string) (endMarker: string) (text: string) =
    match trySectionBetween startMarker endMarker text with
    | Some section -> section
    | None -> failwithf "Could not find section '%s' ending at '%s'." startMarker endMarker

/// Slice from the first matching start heading to the first matching end
/// heading. Each printed form revision renamed some headings, so callers can
/// supply several accepted spellings.
let private sectionBetweenAny (startMarkers: string list) (endMarkers: string list) (text: string) =
    let starts =
        startMarkers
        |> List.choose (fun marker ->
            tryFindHeading marker 0 text
            |> Option.map (fun m -> m.Index, m.Index + m.Length, marker))

    match starts with
    | [] -> failwithf "Could not find any of the section headings: %A." startMarkers
    | _ ->
        let start, afterStart, startMarker =
            starts |> List.minBy (fun (index, _, _) -> index)

        let finishes =
            endMarkers
            |> List.choose (fun marker ->
                tryFindHeading marker afterStart text |> Option.map (fun m -> m.Index, marker))

        match finishes with
        | [] -> failwithf "Could not find any section end (%A) after '%s'." endMarkers startMarker
        | _ ->
            let finish, _ = finishes |> List.minBy fst
            text.Substring(afterStart, finish - afterStart)

// ---------------------------------------------------------------------------
// Parsed pieces
// ---------------------------------------------------------------------------

type private Coupon =
    | Fixed of displayText: string * rate: decimal
    | Indexed of displayText: string * kind: string * index: string * spread: decimal

type private ScheduledRow =
    {
        RemainingFaceValue: decimal
        WeightedAverageMortgageRatePercent: decimal option
        WeightedAverageRemainingAmortizationMonths: decimal option
        IndexSpreadTotalPercent: decimal option
        IndexSpreadFullTermPercent: decimal option
        IndexSpreadIntroductoryPercent: decimal option
        SumOfMonthlyPaymentEquivalents: decimal option
        WeightedAverageIntroductoryPeriodsRemaining: decimal option
    }

// ---------------------------------------------------------------------------
// Cover page
// ---------------------------------------------------------------------------

/// The checkbox glyph is not always printed as a text `X`. Older typesetting
/// uses the `¥`/`■` checkbox characters, and some flattened PDFs draw the mark
/// as a graphic, leaving no marker at all.
let private tryParseRateType (text: string) =
    let m =
        tryMatch @"(?m)^[ \t]*(?:X|¥|■|☒|✓|✔)[ \t]*(Fixed Rate|Variable Rate|Adjustable Rate|WAC)\b" text

    m
    |> Option.map (fun m ->
        match group m 1 with
        | "Fixed Rate" -> "fixed"
        | "Variable Rate" -> "variable"
        | "Adjustable Rate" -> "adjustable"
        | "WAC" -> "wac"
        | value -> failwithf "Unknown rate type '%s'." value)

/// NHA MBS pool types imply their rate type, which lets the parser recover the
/// selected checkbox when the mark was rasterized or the form predates printed
/// checkbox labels.
let private rateTypeForPoolType (poolType: string) =
    match poolType with
    | "880"
    | "881"
    | "980"
    | "981" -> "adjustable"
    | "885"
    | "886"
    | "985"
    | "986" -> "variable"
    | "987" -> "wac"
    | _ -> "fixed"

let private parseWacRateType (text: string) =
    match
        tryMatch
            @"Composition[ \t]+of[ \t]+Mortgage[ \t]+Loan[ \t]+Pool[\s\S]{0,600}?\bX[ \t]*(Adjustable|Variable)\b"
            text
    with
    | Some m ->
        match group m 1 with
        | "Adjustable" -> "adjustable"
        | "Variable" -> "variable"
        | value -> failwithf "Unknown WAC composition '%s'." value
    | None ->
        // On flattened older forms the radio mark is a graphic. The payment
        // wording distinguishes adjustable (payments reset) from variable
        // (payments stay level).
        match tryMatch @"payments\s+of\s+interest\s+and\s+principal\s+will\s+(not\s+)?be\s+adjusted" text with
        | Some m when group m 1 = "" -> "adjustable"
        | Some _ -> "variable"
        | None -> failwith "Could not find the WAC composition radio."

let private parsePoolNumber (text: string) =
    let m = matchOrFail "pool number" @"POOL[ \t]+NO[.:]*[ \t]*([0-9][0-9\s-]*)" text
    let digits = Regex.Replace(group m 1, @"\D", "")

    if digits.Length <> 8 then
        failwithf "Expected an eight-digit pool number, got '%s'." digits

    digits

let private tryParseDateField (label: string) (rawText: string) =
    // Some flattened fonts encode a space as 0x03 and the digit 1 as 0x14.
    let text = rawText.Replace('\u0003', ' ').Replace('\u0014', '1')
    let datePattern = @"([A-Za-z]+\s*\d{1,2},\s*\d{4})"
    let escaped = escapedLabel label

    match tryMatch (escaped + @":?\s*" + datePattern) text with
    | Some m -> Some(parseDate (group m 1))
    | None ->
        // A few flattened forms split the date so that the day/year lands on
        // the label line and the month name wraps onto the following line.
        match tryMatch (escaped + @":[^\n]*\n[ \t]*" + datePattern) text with
        | Some m -> Some(parseDate (group m 1))
        | None ->
            // Some PDFs have two overprinted text layers; the month/day and
            // the year end up on different lines, interleaved with a stale
            // copy of the date.
            match
                tryMatch
                    (escaped
                     + @":[^\n]*(?:\n[^\n]*){0,2}?\b([A-Za-z]+ \d{1,2}),[^\n]*\n[^\n]*?(\d{4})")
                    text
            with
            | Some m -> Some(parseDate (group m 1 + ", " + group m 2))
            | None ->
                // OCR can place the value above its label.
                match tryMatch escaped text with
                | None -> None
                | Some labelMatch ->
                    let windowStart = max 0 (labelMatch.Index - 120)
                    let beforeLabel = text.Substring(windowStart, labelMatch.Index - windowStart)
                    let dates = Regex.Matches(beforeLabel, datePattern, RegexOptions.CultureInvariant)

                    if dates.Count = 0 then
                        None
                    else
                        Some(parseDate dates.[dates.Count - 1].Groups.[1].Value)

let private parseDateField (label: string) (text: string) =
    match tryParseDateField label text with
    | Some date -> date
    | None -> failwithf "Could not find %s." label

let private parseDateFieldAny (labels: string list) (text: string) =
    match labels |> List.tryPick (fun label -> tryParseDateField label text) with
    | Some date -> date
    | None -> failwithf "Could not find %s." (String.concat " or " labels)

let private parseCoupon (text: string) =
    let m =
        matchOrFail
            "Mortgage-Backed Securities coupon"
            @"(?m)^[ \t]*(.+?)[ \t]*Mortgage[ \t]*-[ \t]*Backed(?:[ \t]*\r?\n[ \t]*)*[ \t]*Securities"
            text

    let display = group m 1 |> fun value -> Regex.Replace(value, @"\s+", " ").Trim()
    let normalizedDisplay = display.Replace('–', '-').Replace('—', '-')

    let parseSpread (indexName: string) =
        let spread =
            matchOrFail
                (sprintf "%s spread" indexName)
                (Regex.Escape indexName + @"\s*([+-]\s*[\d.]+)\s*%")
                normalizedDisplay

        parseDecimal ((group spread 1).Replace(" ", ""))

    if display.Contains("CDOR", StringComparison.OrdinalIgnoreCase) then
        Indexed(display, "cdor", "CDOR", parseSpread "CDOR")
    elif display.Contains("CORRA", StringComparison.OrdinalIgnoreCase) then
        Indexed(display, "corra", "One-Month Daily Compounded CORRA", parseSpread "CORRA")
    elif display.Contains("WAC", StringComparison.OrdinalIgnoreCase) then
        Indexed(display, "wac", "WAC", parseSpread "WAC")
    else
        let rate = parseDecimal (display.Replace("%", ""))
        Fixed(display, rate)

let private parseIssuer (text: string) =
    let m =
        matchOrFail "issuer name" @"(?m)^[ \t]*[IiLl]ssued[ \t]+[Bb]y[ \t]*\r?\n(?:[ \t]*\r?\n)*[ \t]*(.+?)[ \t]*$" text

    group m 1

let private tryParseInsurer (text: string) =
    // The phrase can wrap between "loans" and "insured by".
    match tryMatch @"mortgages?(?:\s+loans)?(?:\s+are)?\s+insured\s+by" text with
    | None -> None
    | Some marker ->
        let after = text.Substring(marker.Index + marker.Length).TrimStart()

        let stop =
            [
                after.IndexOf("For details", StringComparison.Ordinal)
                after.IndexOf("\n\n", StringComparison.Ordinal)
                after.IndexOf(". ", StringComparison.Ordinal)
                after.IndexOf(".\n", StringComparison.Ordinal)
                after.IndexOf(".", StringComparison.Ordinal)
            ]
            |> List.filter (fun index -> index >= 0)
            |> function
                | [] -> after.Length
                | indexes -> List.min indexes

        let value =
            after.Substring(0, stop)
            |> fun value -> Regex.Replace(value, @"\s+", " ")
            |> fun value -> value.Trim().TrimEnd('.')

        let value =
            if value.StartsWith("the ", StringComparison.OrdinalIgnoreCase) then
                value.Substring(4)
            else
                value

        if value = "" then None else Some value

let private parseCustodian (text: string) =
    // Newer PDFs have readable text, so prefer the full phrase.
    match tryMatch @"held in\s+custody by\s+([\s\S]+?),\s*on\s+behalf\s+of\s+[IiLl]nvest\w*" text with
    | Some m -> Regex.Replace(group m 1, @"\s+", " ").Trim()
    | None ->
        // The 2016 PDF has a broken font encoding for the words before the
        // name, so capture "..., on behalf of Investors" and strip the
        // all-uppercase garbled tokens that precede the readable name.
        let m =
            matchOrFail "custodian" @"([^\r\n]+?),?\s+on\s+behalf\s+of\s+[IiLl]nvest\w*" text

        let candidate: string = group m 1

        let tokens: string[] =
            Regex.Split(candidate, @"[\s\u0003]+")
            |> Array.filter (fun token -> token <> "")

        let readable: string[] =
            tokens
            |> Array.skipWhile (fun token ->
                token = token.ToUpperInvariant()
                && token.Length > 1
                && Regex.IsMatch(token, @"[A-Z]"))
            |> Array.truncate 10

        if readable.Length = 0 then
            candidate.Trim()
        else
            String.Join(" ", readable).Trim()

// ---------------------------------------------------------------------------
// Addendum: scheduled maturity, insurers, loan types
// ---------------------------------------------------------------------------

type private ScheduledColumn =
    | ColRemainingFaceValue
    | ColWeightedAverageRate
    | ColRemainingAmortization
    | ColIndexSpreadTotal
    | ColIndexSpreadFullTerm
    | ColIndexSpreadIntroductory
    | ColSumOfMonthlyPayments
    | ColIntroductoryPeriods

/// Read the `($)`, `(%)`, `(Months)` unit row and return each printed column's
/// offset. Fixed-rate forms leave the introductory-period and spread cells
/// blank, so a row can contain fewer numbers than the header; offsets let us
/// leave the omitted cells unset instead of shifting later values left.
let private scheduledColumns (section: string) =
    let unitPattern = @"\(\$\)|\(%\)|\((?:Months|mois)\)"

    let candidates: MatchCollection[] =
        section.Split('\n')
        |> Array.map (fun line -> Regex.Matches(line, unitPattern, RegexOptions.IgnoreCase))
        |> Array.filter (fun matches -> matches.Count >= 3)

    candidates
    |> Array.sortByDescending (fun matches -> matches.Count)
    |> Array.tryHead
    |> Option.map (fun matches ->
        let mutable spreadIndex = 0

        matches
        |> Seq.mapi (fun index m ->
            let kind =
                match index with
                | 0 -> ColRemainingFaceValue
                | 1 -> ColWeightedAverageRate
                | 2 -> ColRemainingAmortization
                | _ ->
                    match m.Value.ToLowerInvariant() with
                    | "($)" -> ColSumOfMonthlyPayments
                    | "(months)"
                    | "(mois)" -> ColIntroductoryPeriods
                    | _ ->
                        let kind =
                            match spreadIndex with
                            | 0 -> ColIndexSpreadTotal
                            | 1 -> ColIndexSpreadFullTerm
                            | _ -> ColIndexSpreadIntroductory

                        spreadIndex <- spreadIndex + 1
                        kind

            m.Index, kind)
        |> Seq.toList)

let private parseScheduledMaturity (text: string) =
    let section =
        sectionBetweenAny
            [
                "MORTGAGE LOAN POOL PROFILE AT ISSUE DATE"
                "MORTGAGE POOL PROFILE AT ISSUE DATE"
            ]
            [
                "MORTGAGE LOAN POOL PROFILE BY INSURER"
                "DOLLAR PERCENTAGE PROFILE BY LOAN TYPE"
                "DESCRIPTION OF MORTGAGE LOANS"
                "DESCRIPTION OF MORTGAGES"
            ]
            text

    let lines = section.Split('\n') |> Array.filter (fun line -> line.Trim() <> "")

    let trimmedLines = lines |> Array.map (fun line -> line.Trim())
    let columns = scheduledColumns section

    let collapseSpaces (value: string) =
        Regex.Replace(value, @"\s+", "").ToLowerInvariant()

    let hasValues lineIndex =
        (numericTokens lines.[lineIndex]).Length >= 3

    let tryFindLineFor (label: string) =
        let normalizedLabel = collapseSpaces label

        let candidates =
            trimmedLines
            |> Array.indexed
            |> Array.filter (fun (_, line) ->
                (collapseSpaces line).StartsWith(normalizedLabel, StringComparison.Ordinal))
            |> Array.map fst

        // Some layouts put the label and values on one line; others put the
        // values immediately above the label. Look only at adjacent lines so
        // that a multi-line header starting with "Maturity of ..." is not
        // mistaken for the Maturity data row.
        candidates
        |> Array.tryPick (fun index ->
            if hasValues index then
                Some lines.[index]
            elif index > 0 && hasValues (index - 1) then
                Some lines.[index - 1]
            elif index + 1 < lines.Length && hasValues (index + 1) then
                Some lines.[index + 1]
            else
                None)

    let findLineFor (label: string) =
        tryFindLineFor label
        |> Option.defaultWith (fun () -> failwithf "Could not find scheduled maturity row '%s'." label)

    let toRowByPosition (values: decimal list) =
        let at index = values |> List.tryItem index

        {
            RemainingFaceValue = at 0 |> Option.defaultValue 0M
            WeightedAverageMortgageRatePercent = at 1
            WeightedAverageRemainingAmortizationMonths = at 2
            IndexSpreadTotalPercent = at 3
            IndexSpreadFullTermPercent = at 4
            IndexSpreadIntroductoryPercent = at 5
            SumOfMonthlyPaymentEquivalents = at 6
            WeightedAverageIntroductoryPeriodsRemaining = at 7
        }

    let toRow (line: string) =
        match columns with
        | None -> toRowByPosition (numericValues line)
        | Some columns ->
            let cells =
                numericTokens line
                |> List.map (fun (offset, value) ->
                    let _, kind =
                        columns |> List.minBy (fun (columnOffset, _) -> abs (offset - columnOffset))

                    kind, value)

            let cell kind =
                cells
                |> List.tryPick (fun (cellKind, value) -> if cellKind = kind then Some value else None)

            {
                RemainingFaceValue = cell ColRemainingFaceValue |> Option.defaultValue 0M
                WeightedAverageMortgageRatePercent = cell ColWeightedAverageRate
                WeightedAverageRemainingAmortizationMonths = cell ColRemainingAmortization
                IndexSpreadTotalPercent = cell ColIndexSpreadTotal
                IndexSpreadFullTermPercent = cell ColIndexSpreadFullTerm
                IndexSpreadIntroductoryPercent = cell ColIndexSpreadIntroductory
                SumOfMonthlyPaymentEquivalents = cell ColSumOfMonthlyPayments
                WeightedAverageIntroductoryPeriodsRemaining = cell ColIntroductoryPeriods
            }

    let findLineForAny (labels: string list) =
        labels
        |> List.tryPick (fun label ->
            try
                Some(findLineFor label)
            with _ ->
                None)
        |> Option.defaultWith (fun () -> failwithf "Could not find scheduled maturity row %A." labels)

    let zeroRow = toRowByPosition []

    let rows =
        [
            "5 Months", "fiveMonths"
            "4 Months", "fourMonths"
            "3 Months", "threeMonths"
            "2 Months", "twoMonths"
            "1 Month", "oneMonth"
        ]
        |> List.map (fun (label, key) -> key, tryFindLineFor label |> Option.map toRow |> Option.defaultValue zeroRow)

    let maturityLine = findLineForAny [ "Maturity"; "At Maturity" ]
    let maturityRow = "maturity", toRow maturityLine

    let totalLine =
        // Use the data row itself, not the multi-line header that also starts
        // with "Maturity of ...".
        let maturityIndex = lines |> Array.findIndex (fun line -> line = maturityLine)

        let hasValues lineIndex =
            (numericTokens lines.[lineIndex]).Length >= 3

        [ maturityIndex + 1 .. lines.Length - 1 ]
        |> List.tryFind hasValues
        |> Option.map (fun i -> lines.[i])
        |> Option.defaultWith (fun () ->
            // Fallback for extractions that place all values before the labels.
            lines
            |> Array.rev
            |> Array.tryFind (fun line -> (numericTokens line).Length >= 3)
            |> Option.defaultWith (fun () -> failwith "Could not find the scheduled maturity total row."))

    rows @ [ maturityRow; "total", toRow totalLine ]

let private parseInsurerTable (section: string) =
    // A few French-produced tables use a comma decimal separator in the
    // percent column while keeping a dot in the dollar column.
    let parseValue (value: string) =
        if value.Contains(",") && not (value.Contains(".")) then
            parseDecimal (value.Replace(",", "."))
        else
            parseDecimal value

    section.Split('\n')
    |> Array.choose (fun line ->
        let m =
            Regex.Match(
                line,
                @"^(.+?)\s+\$?\s*([\d,]+\.\d{2,4})\s+([\d,]+\.\d{1,4}|[\d.]+,\d{1,4})\s*%?\s*$",
                RegexOptions.CultureInvariant
            )

        if m.Success then
            Some(group m 1, parseValue (group m 2), parseValue (group m 3))
        else
            None)
    |> Array.toList

let private parseInsurers (text: string) =
    match trySectionBetween "MORTGAGE LOAN POOL PROFILE BY INSURER" "DOLLAR PERCENTAGE PROFILE BY LOAN TYPE" text with
    | None -> []
    | Some section -> parseInsurerTable section

let private parseLoanTypeProfile (text: string) =
    let parsePercent label =
        match tryMatch (escapedLabel label + @":[ \t]*([\d.]+)[ \t]*%") text with
        | Some m -> parseDecimal (group m 1)
        | None -> 0M

    parsePercent "Homeowner", parsePercent "Multiple Family", parsePercent "Social Housing"

// ---------------------------------------------------------------------------
// Addendum: RAM and location tables
// ---------------------------------------------------------------------------

let private parseRamDistribution (text: string) =
    // A few flattened PDFs insert spaces between the heading's letters.
    let heading =
        tryMatch
            @"(?i)(?:RAM|REMAINING[ \t]+AMORTIZATION)[ \t]+D[ \t]*I[ \t]*S[ \t]*T[ \t]*R[ \t]*I[ \t]*B[ \t]*U[ \t]*T[ \t]*I[ \t]*O[ \t]*N"
            text
        |> Option.defaultWith (fun () -> failwith "Could not find the RAM distribution section.")

    // The RAM table is the last table on the addendum page. End the section at
    // the page break rather than at a heading, since the insurer table appears
    // before it in the printed layout.
    let pageBreak = text.IndexOf('\f', heading.Index)
    let finish = if pageBreak > heading.Index then pageBreak else text.Length
    let section = text.Substring(heading.Index, finish - heading.Index)

    let rows =
        section.Split('\n')
        |> Array.choose (fun line ->
            // Older circulars print zero values as "$ -" in the RAM table.
            let normalized = Regex.Replace(line, @"\$\s*-", "$$0.00")

            // Some extractions use a dot as the thousands separator, e.g.
            // "$13,041.665.23".
            let normalized = Regex.Replace(normalized, @"\.(?=\d{3}\.)", ",")
            let values = numericValues normalized
            if values.Length = 6 then Some values else None)
        |> Array.toList

    let zeros = List.replicate 6 0M
    let first = List.tryItem 0 rows |> Option.defaultValue zeros
    let second = List.tryItem 1 rows |> Option.defaultValue zeros

    // On some forms the printed amortization row is blank and the actual
    // distribution is on the following, unlabelled row. Prefer the row that
    // carries the data so that the amortization bins sum to the pool total.
    if List.forall ((=) 0M) first && not (List.forall ((=) 0M) second) then
        second, first
    else
        first, second

let private provinceRows =
    [
        "newfoundland", [ "Newfoundland" ]
        "novaScotia", [ "Nova Scotia" ]
        "newBrunswick", [ "New Brunswick" ]
        "princeEdwardIsland", [ "P.E.I."; "Prince Edward Island" ]
        "quebec", [ "Quebec" ]
        "ontario", [ "Ontario" ]
        "manitoba", [ "Manitoba" ]
        "saskatchewan", [ "Saskatchewan" ]
        "alberta", [ "Alberta" ]
        "britishColumbia", [ "British Columbia" ]
        "northwestTerritories", [ "N.W.T."; "Northwest Territories" ]
        "yukon", [ "Yukon" ]
        "nunavut", [ "Nunavut" ]
    ]

let private parseLocations (text: string) =
    let section =
        sectionBetweenAny
            [ "LOCATION OF MORTGAGE LOANS"; "LOCATION OF MORTGAGES" ]
            [
                "MORTGAGE LOAN POOL PROFILE BY INTEREST ADJUSTMENT DATE"
                "MORTGAGE POOL PROFILE BY INTEREST ADJUSTMENT DATE"
            ]
            text

    let lines =
        section.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.filter (fun line -> line <> "")

    let valuesOn (line: string) =
        match numericTokens line with
        | [ _, loansPercent; _, principalAmountPercent ] -> Some(loansPercent, principalAmountPercent)
        | _ -> None

    // Some form revisions omit provinces whose share is zero, so look up each
    // province by name and default the missing ones rather than relying on row
    // order or a fixed row count.
    let provinces =
        provinceRows
        |> List.map (fun (key, labels) ->
            let compact (value: string) =
                Regex.Replace(value, @"\s+", "").ToLowerInvariant()

            let row =
                lines
                |> Array.tryPick (fun line ->
                    let compactLine = compact line

                    if
                        labels
                        |> List.exists (fun label -> compactLine.StartsWith(compact label, StringComparison.Ordinal))
                    then
                        valuesOn line
                    else
                        None)

            key, row |> Option.defaultValue (0M, 0M))

    let total =
        lines
        |> Array.rev
        |> Array.tryPick valuesOn
        |> Option.defaultWith (fun () -> failwith "Could not find the location total row.")

    // OCR occasionally reads a leading 1 as 4. The printed total is
    // authoritative, so when one province's percentage is impossible and the
    // total is 100, recover the province share from the other rows.
    let totalLoans, totalPrincipal = total
    let sumLoans = provinces |> List.sumBy (fun (_, (loans, _)) -> loans)
    let sumPrincipal = provinces |> List.sumBy (fun (_, (_, principal)) -> principal)

    let provinces =
        provinces
        |> List.map (fun (key, (loans, principal)) ->
            let loans =
                if loans > 100M && totalLoans > 0M then
                    max 0M (totalLoans - (sumLoans - loans))
                else
                    loans

            let principal =
                if principal > 100M && totalPrincipal > 0M then
                    max 0M (totalPrincipal - (sumPrincipal - principal))
                else
                    principal

            key, (loans, principal))

    provinces @ [ "total", total ]

// ---------------------------------------------------------------------------
// Addendum: interest adjustment table
// ---------------------------------------------------------------------------

let private parseInterestAdjustment (text: string) =
    let heading =
        [
            "MORTGAGE LOAN POOL PROFILE BY INTEREST ADJUSTMENT DATE"
            "MORTGAGE POOL PROFILE BY INTEREST ADJUSTMENT DATE"
        ]
        |> List.tryPick (fun marker -> tryFindHeading marker 0 text)
        |> Option.defaultWith (fun () -> failwith "Could not find the interest adjustment profile section.")

    let start = heading.Index

    // The interest-adjustment table fits on one page. Stop at the page break so
    // that a duplicated table in an appendix is not read as extra rate bands.
    let pageBreak = text.IndexOf('\f', start)

    let section =
        if pageBreak > start then
            text.Substring(start, pageBreak - start)
        else
            text.Substring(start)

    let monthPattern =
        "Jan(?:uary|v(?:ier)?)?|F[ée]b(?:ruary|r(?:ier)?)?|Mar(?:ch|s)?|Apr(?:il)?|Avr(?:il)?|May|Mai|Jun(?:e)?|Juin|Jul(?:y|let|il)?|Aug(?:ust)?|Ao[uû]t?|Sep(?:t(?:ember|embre)?)?|Oct(?:ober|obre)?|Nov(?:ember|embre)?|D[ée]c(?:ember|embre)?"

    let dateTokens =
        Regex.Matches(
            section,
            sprintf
                @"(?:%s)/\d{4}|\d{1,2}/\d{1,2}/\d{2,4}|\d{1,2}/\d{4}|\d{1,2}-(?:%s)-\d{2,4}|\d{1,2}\s+(?:%s),?\s+\d{4}|(?:%s),?\s+\d{4}|(?:%s)-\d{2,4}"
                monthPattern
                monthPattern
                monthPattern
                monthPattern
                monthPattern,
            RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase
        )
        |> Seq.map (fun m -> m.Value)
        |> Seq.distinct
        |> Seq.truncate 6
        |> Seq.toList

    if dateTokens.Length <> 6 then
        failwithf "Expected six interest adjustment dates, found %d." dateTokens.Length

    let dates = dateTokens |> List.map parseMonthYear

    let lines =
        section.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.filter (fun line -> line <> "")

    // Some extractions use the dot as the thousands separator as well as the
    // decimal separator, e.g. "1.111.030.29".
    let normalizeThousandsDots (value: string) =
        Regex.Replace(value, @"\.(?=\d{3}\.)", ",")

    let rateBands =
        lines
        |> Array.choose (fun line ->
            let m =
                Regex.Match(line, @"^(\d+\.\d+\s*-\s*\d+\.\d+)\s+(.+)$", RegexOptions.CultureInvariant)

            if not m.Success then
                None
            else
                let values = numericValues (normalizeThousandsDots (group m 2))

                if values.Length >= 7 then
                    let padded =
                        values @ List.replicate (max 0 (8 - values.Length)) 0M |> List.truncate 8

                    Some(group m 1, padded.[0..5], padded.[6], padded.[7])
                else
                    None)
        |> Array.toList

    let normalizeLabel (value: string) =
        Regex.Replace(value, @"\s+", "").ToUpperInvariant()

    let findTotalRow (label: string) =
        let expected = normalizeLabel label

        lines
        |> Array.tryPick (fun line ->
            let normalized = normalizeLabel line

            if normalized.StartsWith(expected, StringComparison.Ordinal) then
                let values = numericValues (normalizeThousandsDots line)

                if not values.IsEmpty then
                    Some(values @ List.replicate (max 0 (8 - values.Length)) 0M |> List.truncate 8)
                else
                    None
            else
                None)
        |> Option.defaultWith (fun () -> failwithf "Could not find the '%s' row." label)

    let totals = findTotalRow "TOTAL"

    let percentOfTotal =
        let values = findTotalRow "% OF TOTAL"

        // The percent-of-total row must total 100. OCR sometimes reads the
        // leading 1 as 4; use the six date cells to recover the total.
        if values.[6] > 100M then
            let dateTotal = values.[0..5] |> List.sum

            if abs (dateTotal - 100M) <= 1M then
                values |> List.mapi (fun index value -> if index = 6 then dateTotal else value)
            else
                values
        else
            values

    let wac = findTotalRow "WAC%"

    dates, rateBands, totals, percentOfTotal, wac

// ---------------------------------------------------------------------------
// JSON construction
// ---------------------------------------------------------------------------

let private addString (obj: JsonObject) (key: string) (value: string) = obj.[key] <- JsonValue.Create value

let private addDecimal (obj: JsonObject) (key: string) (value: decimal) = obj.[key] <- JsonValue.Create value

let private addInt (obj: JsonObject) (key: string) (value: int) = obj.[key] <- JsonValue.Create value

let private addOptionalDecimal (obj: JsonObject) (key: string) (value: decimal option) =
    match value with
    | Some number -> addDecimal obj key number
    | None -> ()

let private decimalArray (values: decimal list) =
    let array = JsonArray()
    values |> List.iter (fun value -> array.Add(JsonValue.Create value))
    array

let private stringArray (values: string list) =
    let array = JsonArray()
    values |> List.iter (fun value -> array.Add(JsonValue.Create value))
    array

let private addScheduledRow (parent: JsonObject) (key: string) (row: ScheduledRow) =
    let obj = JsonObject()
    addDecimal obj "remainingFaceValue" row.RemainingFaceValue
    addOptionalDecimal obj "weightedAverageMortgageRatePercent" row.WeightedAverageMortgageRatePercent
    addOptionalDecimal obj "weightedAverageRemainingAmortizationMonths" row.WeightedAverageRemainingAmortizationMonths
    addOptionalDecimal obj "indexSpreadTotalPercent" row.IndexSpreadTotalPercent
    addOptionalDecimal obj "indexSpreadFullTermPercent" row.IndexSpreadFullTermPercent
    addOptionalDecimal obj "indexSpreadIntroductoryPercent" row.IndexSpreadIntroductoryPercent
    addOptionalDecimal obj "sumOfMonthlyPaymentEquivalents" row.SumOfMonthlyPaymentEquivalents

    addOptionalDecimal
        obj
        "weightedAverageIntroductoryPeriodsRemainingMonths"
        row.WeightedAverageIntroductoryPeriodsRemaining

    parent.[key] <- obj

let private totalRowObject (values: decimal list) =
    let obj = JsonObject()
    obj.["byDate"] <- decimalArray values.[0..5]
    addDecimal obj "total" values.[6]
    addDecimal obj "percentOfTotal" values.[7]
    obj

let private parseAggregatePrincipal (text: string) =
    let sameLine =
        tryMatch
            (escapedLabel "Aggregate Principal Amount"
             + @"[ \t]*(?::[ \t]*)*\$?[ \t]*([^\r\n]+)")
            text
        |> Option.bind (fun m -> numericTokens (group m 1) |> List.tryHead |> Option.map snd)

    match sameLine with
    | Some amount -> amount
    | None ->
        // The 2004 form prints the amount above its label.
        let labelIndex =
            match tryMatch (escapedLabel "Aggregate Principal Amount") text with
            | Some m -> m.Index
            | None -> -1

        if labelIndex < 0 then
            failwith "Could not find aggregate principal amount."

        let windowStart = max 0 (labelIndex - 400)
        let beforeLabel = text.Substring(windowStart, labelIndex - windowStart)

        match numericTokens beforeLabel |> List.tryLast with
        | Some(_, amount) -> amount
        | None -> failwith "Could not find aggregate principal amount."

/// Parse layout-preserving text from a CMHC 2834 circular into a JSON object
/// shaped like `cmhc-2834-i-v2-extended-en.schema.json`.
let parseText (text: string) : JsonObject =
    let poolNumber = parsePoolNumber text
    let poolType = poolNumber.Substring(0, 3)

    let rateType =
        match tryParseRateType text with
        | Some rateType -> rateType
        | None -> rateTypeForPoolType poolType

    let coupon = parseCoupon text
    let issuerName = parseIssuer text
    let custodian = parseCustodian text

    let aggregatePrincipalAmount =
        try
            parseAggregatePrincipal text
        with _ ->
            // On a few 2006-era forms the page-1 amount is drawn separately
            // and does not survive text extraction. The RAM distribution bins
            // sum to the same total remaining face value.
            let ramTotal =
                try
                    let byRam, _ = parseRamDistribution text
                    List.sum byRam
                with _ ->
                    0M

            if ramTotal > 0M then
                ramTotal
            else
                failwith "Could not find aggregate principal amount."

    let insurerRows = parseInsurers text

    let mortgageInsurer =
        match tryParseInsurer text with
        | Some name -> name
        | None ->
            // 2015-era forms omit the prose sentence and only name the
            // insurer in the addendum table.
            match insurerRows with
            | (name, _, _) :: _ -> name
            | [] -> failwith "Could not find the mortgage insurer."

    let root = JsonObject()
    root.["language"] <- JsonValue.Create "en"

    match tryMatch @"CMHC 2834\s+([0-9]+/[0-9]+/[0-9]+)" text with
    | Some m -> addString root "formVersion" ("CMHC 2834 " + group m 1)
    | None -> ()

    addString root "rateType" rateType
    addString root "poolType" poolType

    if rateType = "wac" then
        addString root "wacRateType" (parseWacRateType text)

    let parties = JsonObject()
    addString parties "issuerName" issuerName
    addString parties "custodian" custodian
    addString parties "mortgageInsurer" mortgageInsurer
    root.["parties"] <- parties

    let mbs = JsonObject()

    let couponObject = JsonObject()

    match coupon with
    | Fixed(displayText, rate) ->
        addString couponObject "displayText" displayText
        addString couponObject "kind" "fixed"
        addDecimal couponObject "fixedRatePercent" rate
    | Indexed(displayText, kind, index, spread) ->
        addString couponObject "displayText" displayText
        addString couponObject "kind" kind
        addString couponObject "index" index
        addDecimal couponObject "spreadPercent" spread

    mbs.["coupon"] <- couponObject

    addDecimal mbs "aggregatePrincipalAmount" aggregatePrincipalAmount

    addString mbs "poolNumber" poolNumber
    addString mbs "issueDate" (parseDateField "ISSUE DATE" text)
    addString mbs "settlementDate" (parseDateField "SETTLEMENT DATE" text)
    addString mbs "firstPaymentDue" (parseDateField "FIRST PAYMENT DUE" text)
    addString mbs "maturityDate" (parseDateField "MATURITY DATE" text)

    addString mbs "finalPaymentDue" (parseDateFieldAny [ "FINAL PAYMENT DUE"; "FINAL PAYMENT DATE" ] text)
    root.["mbs"] <- mbs

    let addendum = JsonObject()
    addString addendum "issuerName" issuerName

    addString
        addendum
        "couponDisplayText"
        (match coupon with
         | Fixed(display, _) -> display
         | Indexed(display, _, _, _) -> display)

    let totalLoans =
        let label = @"(?i)Total[ \t]+Number[ \t]+of[ \t]+Mortgages?(?:[ \t]+Loans)?"

        let parseInteger (value: string) =
            Int32.Parse(value, CultureInfo.InvariantCulture)

        match tryMatch (label + @":?[ \t]*(?:\r?\n[ \t]*){0,3}(\d+)") text with
        | Some m -> parseInteger (group m 1)
        | None ->
            // OCR sometimes places the count above its label.
            match tryMatch label text with
            | Some m ->
                let windowStart = max 0 (m.Index - 80)
                let beforeLabel = text.Substring(windowStart, m.Index - windowStart)
                let numbers = Regex.Matches(beforeLabel, @"\b\d+\b", RegexOptions.CultureInvariant)

                if numbers.Count = 0 then
                    failwith "Could not find total number of mortgage loans."

                parseInteger numbers.[numbers.Count - 1].Value
            | None -> failwith "Could not find total number of mortgage loans."

    addInt addendum "totalNumberOfMortgageLoans" totalLoans

    addString
        addendum
        "weightedAverageInterestAdjustmentDate"
        (parseDateField "Weighted Average Interest Adjustment Date" text)

    addString addendum "weightedAverageMaturityDate" (parseDateField "Weighted Average Maturity Date" text)

    let scheduledMaturity = JsonObject()

    match tryMatch @"Spread \(to\s*([^\)]*)\)" text with
    | Some m when (group m 1).Trim() <> "" -> addString scheduledMaturity "indexSpreadIndex" ((group m 1).Trim())
    | _ -> ()

    let scheduledRows = parseScheduledMaturity text

    for key, row in scheduledRows do
        addScheduledRow scheduledMaturity key row

    addendum.["scheduledMaturity"] <- scheduledMaturity

    let insurers =
        match insurerRows with
        | [] ->
            // Some older forms omit the by-insurer table when every loan has
            // the same insurer. The cover page still names that insurer.
            [ mortgageInsurer, aggregatePrincipalAmount, 100M ]
        | rows -> rows

    let insurerProfile = JsonObject()
    let insurerArray = JsonArray()

    for name, dollarValue, percentOfLoans in insurers do
        let row = JsonObject()
        addString row "name" name
        addDecimal row "dollarValue" dollarValue
        addDecimal row "percentOfLoans" percentOfLoans
        insurerArray.Add row

    insurerProfile.["insurers"] <- insurerArray
    addendum.["insurerProfile"] <- insurerProfile

    let homeowner, multipleFamily, socialHousing = parseLoanTypeProfile text
    let loanTypeProfile = JsonObject()
    addDecimal loanTypeProfile "homeownerPercent" homeowner
    addDecimal loanTypeProfile "multipleFamilyPercent" multipleFamily
    addDecimal loanTypeProfile "socialHousingPercent" socialHousing
    addendum.["loanTypeProfile"] <- loanTypeProfile

    let byRam, byTerm = parseRamDistribution text
    let ramDistribution = JsonObject()
    ramDistribution.["byRemainingAmortizationMonths"] <- decimalArray byRam
    ramDistribution.["byRemainingTermMonths"] <- decimalArray byTerm
    addendum.["ramDistribution"] <- ramDistribution

    let locationRows = parseLocations text
    let locationObject = JsonObject()

    for key, (loansPercent, principalAmountPercent) in locationRows do
        let row = JsonObject()
        addDecimal row "loansPercent" loansPercent
        addDecimal row "principalAmountPercent" principalAmountPercent
        locationObject.[key] <- row

    addendum.["locationOfMortgageLoans"] <- locationObject

    let dates, rateBands, totals, percentOfTotal, wac = parseInterestAdjustment text
    let interestAdjustment = JsonObject()
    interestAdjustment.["interestAdjustmentDates"] <- stringArray dates

    let bandArray = JsonArray()

    for rateBand, byDate, total, percent in rateBands do
        let row = JsonObject()
        addString row "rateBand" rateBand
        row.["remainingFaceValueByDate"] <- decimalArray byDate
        addDecimal row "total" total
        addDecimal row "percentOfTotal" percent
        bandArray.Add row

    interestAdjustment.["rateBands"] <- bandArray
    interestAdjustment.["totals"] <- totalRowObject totals
    interestAdjustment.["percentOfTotal"] <- totalRowObject percentOfTotal
    interestAdjustment.["wac"] <- totalRowObject wac
    addendum.["interestAdjustmentProfile"] <- interestAdjustment

    root.["addendum"] <- addendum

    let prepayment = JsonObject()

    if poolType = "970" || poolType = "975" then
        // Detect the checkbox only if the X is visibly present near the sentence.
        let isChecked =
            tryMatch @"(?m)^[ \t]*X[ \t]*.*Issuer to check this box" text |> Option.isSome

        prepayment.["bonaFideSaleIndemnityExempt"] <- JsonValue.Create isChecked

    if poolType <> "966" && poolType <> "990" then
        addString prepayment "sectionA" ""
        addString prepayment "sectionB" ""

    addString prepayment "otherDisclosures" ""
    root.["prepaymentProvisions"] <- prepayment

    root

/// Parse a CMHC 2834 PDF into a JSON object shaped like
/// `cmhc-2834-i-v2-extended-en.schema.json`.
let parse (pdfPath: string) : JsonObject =
    PdfText.extractText pdfPath |> parseText

/// Serialize a parsed circular as pretty-printed JSON.
let toJson (circular: JsonObject) =
    circular.ToJsonString(JsonSerializerOptions(WriteIndented = true))

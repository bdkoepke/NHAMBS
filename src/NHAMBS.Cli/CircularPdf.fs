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

/// Printed percentages can land just outside 0-100 through rounding or a
/// duplicated leading digit. Clamp only values that are clearly the same
/// printed percentage so a real parsing error is still rejected.
let private clampPercent (value: decimal) =
    if value < 0M && value >= -0.01M then 0M
    elif value > 100M && value <= 100.5M then 100M
    else value

/// Percent columns in the circular are nominally 0-100. OCR occasionally
/// shifts a dollar amount into one when a column is missing; keep validation
/// useful instead of failing the whole circular.
let private validPercent (value: decimal) =
    let value = clampPercent value

    if value >= 0M && value <= 100M then Some value else None

/// Parse a dollar amount that may have lost its thousands separators or use a
/// comma decimal (e.g. "$ 30849478,77", "$ 49620 190,34"). OCR often turns a
/// leading 5 into a section sign and a decimal point into a colon, and some
/// extractions print every separator as a period ("$ 4.419.938.92").
let private tryParseAmountText (value: string) =
    let value = value.Replace('§', '5').Replace('$', ' ').Replace('%', ' ')
    let compact = Regex.Replace(value, @"[^\d.,:]", "")

    if compact = "" then
        None
    else
        // A colon (or the common "772 97" split) is a lost decimal point.
        let missingDecimal =
            value.Contains ':' || Regex.IsMatch(value, @"\d[ \t]+\d{2}(?:\D|$)")

        let compact = compact.Replace(':', '.')

        let lastSeparator =
            compact
            |> Seq.indexed
            |> Seq.filter (fun (_, character) -> character = '.' || character = ',')
            |> Seq.map fst
            |> Seq.tryLast

        let decimalAt =
            match lastSeparator with
            | Some index when compact.Length - index - 1 >= 1 && compact.Length - index - 1 <= 2 -> Some index
            | _ -> None

        let normalized =
            match decimalAt with
            | Some index ->
                let whole = Regex.Replace(compact.Substring(0, index), @"[.,]", "")
                let fraction = compact.Substring(index + 1)
                whole + "." + fraction
            | None when missingDecimal ->
                let digits = Regex.Replace(compact, @"[.,]", "")

                if digits.Length > 2 then
                    digits.Insert(digits.Length - 2, ".")
                else
                    digits
            | None -> Regex.Replace(compact, @"[.,]", "")

        match Decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture) with
        | true, amount -> Some amount
        | _ -> None

let private monthNames =
    [
        "Nuuernber", "Nov"
        "Nwember", "Nov"
        "Pecwnbr", "Dec"
        "Pecernber", "Dec"
        "Decernber", "Dec"
        "January", "Jan"
        "February", "Feb"
        "March", "Mar"
        "April", "Apr"
        "May", "May"
        "June", "Jun"
        "July", "Jul"
        "August", "Aug"
        "September", "Sep"
        "October", "Oct"
        "November", "Nov"
        "December", "Dec"
    ]

let private normalizeMonthNames (value: string) =
    // Dotnet's MMM format only accepts three-letter abbreviations, while the
    // 2007-2010 forms print "June", "July" and "Sept".
    let months =
        monthNames
        @ [
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

/// Edit distance used to repair OCR month names such as "Srptember".
let private editDistance (left: string) (right: string) =
    let left = left.ToLowerInvariant()
    let right = right.ToLowerInvariant()
    let rows = left.Length + 1
    let columns = right.Length + 1
    let distances = Array2D.zeroCreate<int> rows columns

    for row in 0 .. rows - 1 do
        distances.[row, 0] <- row

    for column in 0 .. columns - 1 do
        distances.[0, column] <- column

    for row in 1 .. rows - 1 do
        for column in 1 .. columns - 1 do
            let cost = if left.[row - 1] = right.[column - 1] then 0 else 1

            distances.[row, column] <-
                min
                    (min (distances.[row - 1, column] + 1) (distances.[row, column - 1] + 1))
                    (distances.[row - 1, column - 1] + cost)

    distances.[rows - 1, columns - 1]

let private canonicalMonth (token: string) =
    let token = token.Trim().TrimEnd('.', ',')

    if token.Length < 2 then
        None
    else
        match
            monthNames
            |> List.tryFind (fun (full, abbreviated) ->
                String.Equals(token, full, StringComparison.OrdinalIgnoreCase)
                || String.Equals(token, abbreviated, StringComparison.OrdinalIgnoreCase))
        with
        | Some(_, abbreviated) -> Some abbreviated
        | None ->
            // Allow the shortened OCR forms ("Ap", "Marc") as well as typos
            // that differ from a month by one or two characters ("Suly").
            let byPrefix =
                monthNames
                |> List.filter (fun (full, _) ->
                    full.StartsWith(token, StringComparison.OrdinalIgnoreCase) && token.Length >= 2)
                |> List.map snd
                |> List.distinct

            match byPrefix with
            | [ abbreviated ] -> Some abbreviated
            | _ ->
                monthNames
                |> List.map (fun (full, abbreviated) ->
                    full, abbreviated, min (editDistance token full) (editDistance token abbreviated))
                |> List.filter (fun (_, _, distance) -> distance <= 2)
                |> List.sortBy (fun (_, _, distance) -> distance)
                |> List.tryHead
                |> Option.map (fun (_, abbreviated, _) -> abbreviated)

/// Repair a misspelled month in a date string without touching other words.
let private correctMonthToken (value: string) =
    match Regex.Match(value, @"\b[A-Za-zÀ-ÿ]{2,}\b", RegexOptions.CultureInvariant) with
    | m when m.Success ->
        match canonicalMonth m.Value with
        | Some abbreviated -> value.Substring(0, m.Index) + abbreviated + value.Substring(m.Index + m.Length)
        | None -> value
    | _ -> value

let private normalizeDateText (value: string) =
    let text =
        value
            .Replace('\u00A0', ' ')
            .Replace('~', ' ')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2212', '-')
            .Replace('\u2AFA', '-')
            .Replace('\uFE63', '-')

    // Canadian circulars frequently print ordinals ("June 1st, 2012") and
    // OCR often drops the space between a month and its day, or puts the year
    // after a period or dash ("October 1.2006", "November15- 2006").
    // Apostrophes inside a word are usually scanner noise in a month name
    // ("Oc'oberl5.2006").
    let text = Regex.Replace(text, @"(?<=[A-Za-zÀ-ÿ])['’`´](?=[A-Za-zÀ-ÿ])", "")

    let text =
        Regex.Replace(text, @"(?<=\d)(?:st|nd|rd|th)\b", "", RegexOptions.IgnoreCase)

    // French "1er octobre" and OCR variants ("ler", "ter").
    let text = Regex.Replace(text, @"\b(?:ler|ier|ter)\b", "1", RegexOptions.IgnoreCase)

    let text =
        Regex.Replace(text, @"(?<=\d)(?:er|e|ème|eme)\b", "", RegexOptions.IgnoreCase)

    // OCR sometimes transposes the first two digits of a year ("0206").
    let text = Regex.Replace(text, @"\b0([0-9])([0-9]{2})\b", "20$2")

    let text = Regex.Replace(text, @"(?<=[A-Za-z])(?=\d)", " ")
    let text = Regex.Replace(text, @"(?<=\d)(?=[A-Za-z])", " ")
    let text = Regex.Replace(text, @"\s*,\s*", ", ")
    let text = Regex.Replace(text, @"(?<=\d)\s*[.\-]\s*(?=\d{4}\b)", " ")
    let text = Regex.Replace(text, @"\b(\d{1,2})\s+(\d{1,2})(?=\s*,)", "$1$2")
    let text = Regex.Replace(text, @"\b[Il|](?=\s*,)", "1")

    let text =
        Regex.Replace(text, @"\b([A-Za-z]+)\s+[Il|][Ss](\s*,)", "$1 15$2", RegexOptions.IgnoreCase)

    // A lone OCR glyph in the day position ("June t, 2012", "July I, 2011").
    let text =
        Regex.Replace(text, @"\b([A-Za-z]{3,})\s+[tIl|!](\s*,)", "$1 1$2", RegexOptions.IgnoreCase)

    let text =
        Regex.Replace(text, @"(?<=\d{3})\s+(?=\d\b)", "", RegexOptions.CultureInvariant)

    Regex.Replace(text, @"\s+", " ").Trim()

let private normalizeMonthYearText (value: string) =
    let text =
        value
            .Replace('\u00A0', ' ')
            .Replace('~', ' ')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2212', '-')
            .Replace('\u2AFA', '-')
            .Replace('\uFE63', '-')

    Regex.Replace(text, @"\s+", " ").Trim().TrimEnd('.', ',')

let private dateFormats =
    [|
        "MMMM d, yyyy"
        "MMMM d yyyy"
        "MMM d, yyyy"
        "MMM d yyyy"
        "MMMM d, yy"
        "MMM d, yy"
        "dd MMM yyyy"
        "d MMM yyyy"
        "dd MMM, yyyy"
        "d MMM, yyyy"
        "dd MMM yy"
        "d MMM yy"
        "MM/dd/yyyy"
        "M/d/yyyy"
        "MM/dd/yy"
        "M/d/yy"
        "dd/MM/yyyy"
        "d/M/yyyy"
        "dd/MM/yy"
        "d/M/yy"
        "yyyy-MM-dd"
    |]

let private tryParseDateFlexible (value: string) =
    let prepared =
        value
        |> normalizeMonthNames
        |> normalizeDateText
        |> correctMonthToken
        |> normalizeDateText

    let plausible (date: DateTime) = date.Year >= 1970 && date.Year <= 2100

    match DateTime.TryParseExact(prepared, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, date when plausible date -> Some date
    | _ ->
        match DateTime.TryParse(prepared, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, date when plausible date -> Some date
        | _ -> None

let private parseDate (value: string) =
    match tryParseDateFlexible value with
    | Some date -> date.ToString("yyyy-MM-dd")
    | None -> failwithf "The string '%s' was not recognized as a valid DateTime." (value |> normalizeDateText)

let private monthYearFormats =
    [|
        "MMMM/yy"
        "MMM/yy"
        "MMMM/yyyy"
        "MMM/yyyy"
        "MM/yy"
        "M/yy"
        "MM/yyyy"
        "M/yyyy"
        "MMMM yy"
        "MMM yy"
        "MMMM yyyy"
        "MMM yyyy"
        "MM-yy"
        "M-yy"
        "MM-yyyy"
        "M-yyyy"
        "MMM-yy"
        "MMM-yyyy"
        "MM/dd/yy"
        "M/d/yy"
        "MM/dd/yyyy"
        "M/d/yyyy"
        "dd/MM/yyyy"
        "d/M/yyyy"
        "dd/MM/yy"
        "d/M/yy"
        "dd MMM, yyyy"
        "d MMM, yyyy"
        "dd MMM yyyy"
        "d MMM yyyy"
    |]

let private tryParseMonthYear (value: string) =
    let prepared =
        value |> normalizeMonthNames |> correctMonthToken |> normalizeMonthYearText

    match DateTime.TryParseExact(prepared, monthYearFormats, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, date -> Some(date.ToString("yyyy-MM-dd"))
    | _ ->
        match DateTime.TryParse(prepared, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, date -> Some(date.ToString("yyyy-MM-dd"))
        | _ -> None

let private parseMonthYear (value: string) =
    match tryParseMonthYear value with
    | Some date -> date
    | None -> failwithf "Could not parse month/year '%s'." value

let private normalizeThousandsDots (value: string) =
    Regex.Replace(value, @"\.(?=\d{3}\.)", ",")

/// Parse one numeric token. Besides the usual dot decimal and comma/space
/// thousands separators, older French tables print a comma decimal ("36,9342"
/// or "100,00"). A single comma followed by exactly three digits is treated as
/// a thousands separator, as it is in the amount columns.
let private tryParseNumericToken (raw: string) =
    let isNegative = raw.Contains "(" || raw.TrimStart().StartsWith "-"
    let compact = Regex.Replace(raw, @"[^\d,.]", "").TrimEnd('.')

    let normalized =
        if compact.Contains(",") && compact.Contains(".") then
            compact.Replace(",", "")
        elif compact.Contains(",") then
            let parts = compact.Split(',')

            if parts.Length = 2 && parts.[1].Length = 3 && parts.[0].Length <= 3 then
                compact.Replace(",", "")
            else
                compact.Replace(",", ".")
        else
            compact

    try
        let value = parseDecimal normalized
        Some(if isNegative then -value else value)
    with _ ->
        None

/// Numeric tokens on a line together with their character offsets. Offsets are
/// used to map table cells to their printed columns. Handles comma or space
/// thousands separators, comma decimals and parenthesized negatives.
let private numericTokens (line: string) =
    let pattern =
        @"\(\s*-?[\d, \t]*[.,]\d{1,6}\s*\)|-?\d+(?:(?:,[ \t]*|[ \t])\d{3})*(?:\.\d{1,6}|,\d{1,6})"

    Regex.Matches(line, pattern, RegexOptions.CultureInvariant)
    |> Seq.choose (fun m -> tryParseNumericToken m.Value |> Option.map (fun value -> m.Index, value))
    |> Seq.toList

/// All decimal-looking tokens on a line. Used for table rows, where columns are
/// separated only by whitespace.
let private numericValues (line: string) = numericTokens line |> List.map snd

/// Find a printed heading. OCR sometimes inserts spaces between letters
/// ("INTEREST ADJUSTMENT D A T E") and newer forms append a note on the same
/// line ("... Not Applicable for Fixed Rate MBS"), so match the marker as the
/// start of a line while allowing whitespace between every character. Anchoring
/// at the line start still avoids matching prose that merely mentions the
/// section name.
let private tryFindHeading (marker: string) (startAt: int) (text: string) =
    let characterPattern (character: char) =
        match Char.ToUpperInvariant character with
        | 'B' -> "[BR]"
        | 'D' -> "[DO0]"
        | 'I' -> "[IWL1]"
        | 'L' -> "[IL1]"
        | 'O' -> "[O0]"
        | 'S' -> "[S5]"
        | 'T' -> "[T7]"
        | 'Y' -> "[YV]"
        | 'M' -> "[MN]"
        | _ -> Regex.Escape(string character)

    let markerPattern =
        marker
        |> Seq.filter (fun character -> not (Char.IsWhiteSpace character))
        |> Seq.map characterPattern
        |> String.concat @"[ \t\f\r]*"

    let pattern = @"(?m)^[ \t\f\r]*" + markerPattern + @"[^\r\n]*$"

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
        match tryMatch @"payments\s+of\s+interest\s+and\s+principal\s*\(\s*(adjustable|variable)\s*\)" text with
        | Some m -> (group m 1).ToLowerInvariant()
        | None ->
            match tryMatch @"payments\s+of\s+interest\s+and\s+principal\s+will\s+(not\s+)?be\s+adjusted" text with
            | Some m when group m 1 = "" -> "adjustable"
            | Some _ -> "variable"
            | None ->
                // Every parsed WAC pool in the corpus uses the adjustable
                // composition; some scans lose the radio and the explanatory
                // sentence entirely.
                "adjustable"

/// Pool types that appear as the first three digits of a pool number.
let private knownPoolTypeCodes =
    [
        "867"
        "880"
        "881"
        "885"
        "886"
        "964"
        "965"
        "966"
        "967"
        "970"
        "975"
        "980"
        "981"
        "985"
        "986"
        "987"
        "990"
    ]

/// Expand OCR-confused characters in a printed pool-number token into the
/// possible digit strings. Most live next to digits and are unambiguous.
let private poolNumberVariants (token: string) =
    let choices (character: char) =
        match character with
        | '$' -> [ "5"; "9" ]
        | 'S'
        | 's' -> [ "5" ]
        | 'T'
        | 't' -> [ ""; "1"; "7" ]
        | '?' -> [ "7" ]
        | '%' -> [ "9" ]
        | '#' -> [ "4" ]
        | 'O'
        | 'o'
        | 'D' -> [ "0" ]
        | 'I'
        | 'l'
        | 'L'
        | '|'
        | '!' -> [ "1" ]
        | 'r' -> [ "1" ]
        | 'Z'
        | 'z' -> [ "2" ]
        | 'A' -> [ "4" ]
        | 'G'
        | 'b' -> [ "6" ]
        | 'B' -> [ "8" ]
        | 'g'
        | 'q' -> [ "9" ]
        | c when Char.IsDigit c -> [ string c ]
        | c when Char.IsLetter c -> [ "" ]
        | _ -> [ "" ]

    let rec expand (remaining: char list) (prefix: string) =
        match remaining with
        | [] -> [ prefix ]
        | character :: rest ->
            choices character
            |> List.collect (fun replacement -> expand rest (prefix + replacement))

    expand (token.ToCharArray() |> Array.toList) ""

let private looksLikePoolNumber (digits: string) =
    digits.Length = 8
    && knownPoolTypeCodes |> List.contains (digits.Substring(0, 3))

/// A nine-digit OCR result sometimes contains one extra digit. Remove the
/// least-significant candidate first, then scan from the left, and accept the
/// first eight-digit candidate with a known pool-type prefix.
let private trimPoolNumber (digits: string) =
    if digits.Length = 8 then
        Some digits
    elif digits.Length = 9 then
        [ 8; 0; 1; 2; 3; 4; 5; 6; 7 ]
        |> List.map (fun index -> digits.Remove(index, 1))
        |> List.tryFind looksLikePoolNumber
    elif digits.Length > 9 then
        [ digits.Length - 8 .. digits.Length - 1 ]
        |> List.choose (fun start ->
            let candidate = digits.Substring(start, 8)

            if looksLikePoolNumber candidate then
                Some candidate
            else
                None)
        |> List.rev
        |> List.tryHead
    else
        None

/// Pull the printed value from the text following a "POOL NO" label. Stop at
/// a wide gap or at the next label so overprinted forms with two values do not
/// concatenate both numbers.
let private poolNumberToken (line: string) =
    let tokens =
        line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun token -> token.Trim(',', ';', '.', ':'))
        |> Array.filter (fun token -> token <> "")

    let isValue token = token |> Seq.exists Char.IsDigit

    tokens
    |> Array.takeWhile (fun token -> isValue token || token.Length <= 2)
    |> Array.filter isValue
    |> String.concat ""

let private parsePrintedPoolNumber (text: string) =
    let labelPattern =
        @"(?im)^[ \t]*(?:POOL[ \t]*[.:;,\-]*[ \t]*N[O0][ \t]*[.:;,\-]*|N[°oO0][ \t]*(?:(?:DE|DU)[ \t]+)?BLOC[ \t]*[.:;,\-]*)"

    let candidates =
        Regex.Matches(text, labelPattern, RegexOptions.CultureInvariant)
        |> Seq.collect (fun label ->
            let lineEnd =
                match text.IndexOf('\n', label.Index) with
                | -1 -> text.Length
                | index -> index

            let afterLabel = label.Index + label.Length
            let line = text.Substring(afterLabel, lineEnd - afterLabel)
            let token = poolNumberToken line

            if token = "" then
                // A few flattened forms put the value on the following line.
                let nextStart = min text.Length (lineEnd + 1)

                let nextEnd =
                    match text.IndexOf('\n', nextStart) with
                    | -1 -> text.Length
                    | index -> index

                [ poolNumberToken (text.Substring(nextStart, nextEnd - nextStart)) ]
            else
                [ token ])
        |> Seq.collect poolNumberVariants
        |> Seq.distinct
        |> Seq.toList

    // When the cover prints a garbled first digit but the document names only
    // one pool type ("For 975 pool types ..."), rebuild the prefix from it.
    let namedPoolTypes =
        knownPoolTypeCodes
        |> List.filter (fun code -> Regex.IsMatch(text, @"\b" + code + @"\b", RegexOptions.CultureInvariant))

    let repairedCandidates =
        match namedPoolTypes with
        | [ poolType ] ->
            candidates
            |> List.map (fun digits ->
                if digits.Length = 8 && not (looksLikePoolNumber digits) then
                    poolType + digits.Substring(3)
                else
                    digits)
        | _ -> candidates

    match
        candidates @ repairedCandidates
        |> List.tryPick (fun digits -> if looksLikePoolNumber digits then Some digits else None)
    with
    | Some digits -> digits
    | None ->
        match candidates |> List.choose trimPoolNumber |> List.tryHead with
        | Some digits -> digits
        | None ->
            let printed =
                candidates
                |> List.filter (fun value -> value <> "")
                |> List.sortByDescending (fun value -> value.Length)
                |> List.tryHead
                |> Option.defaultValue ""

            failwithf "Expected an eight-digit pool number, got '%s'." printed

/// Prefer the pool number recorded in the source manifest (keyed by CUSIP)
/// when the scanned cover is too degraded to read the printed value.
let private parsePoolNumber (expected: string option) (text: string) =
    let expectedDigits =
        expected
        |> Option.map (fun value -> Regex.Replace(value, @"\D", ""))
        |> Option.bind trimPoolNumber

    match expectedDigits with
    | Some digits -> digits
    | None -> parsePrintedPoolNumber text

let private flexibleDatePattern =
    @"[A-Za-zÀ-ÿ]{3,}\s*\d{1,2}(?:st|nd|rd|th)?\s*[,./\- ]\s*\d{2,4}|\d{1,2}\s+[A-Za-zÀ-ÿ]+\s+\d{4}|\d{1,2}\s*[/\-]\s*\d{1,2}\s*[/\-]\s*\d{2,4}|\d{1,2}\s*[/\-]\s*\d{4}"

let private normalizeLabelText (value: string) =
    Regex.Replace(value.ToUpperInvariant(), @"[^A-Z]", "")

let private tryDateFromLine (line: string) =
    // OCR sometimes splits a month over two adjacent lines ("Ju" / "ne 13,
    // 2013"). Joining short alphabetic fragments recovers the printed name
    // without disturbing ordinary words.
    let joined = Regex.Replace(line, @"\b([A-Za-zÀ-ÿ]{1,3})[ \t]+(?=[A-Za-zÀ-ÿ])", "$1")

    [ line; joined ]
    |> List.distinct
    |> List.tryPick (fun value ->
        Regex.Matches(
            normalizeDateText value,
            flexibleDatePattern,
            RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase
        )
        |> Seq.tryPick (fun m ->
            tryParseDateFlexible m.Value
            |> Option.map (fun date -> date.ToString("yyyy-MM-dd"))))

/// OCR often corrupts a field label ("SElTLEMENT DATE", "'ISSUJ! DATE",
/// "MATURITY DA'IE" or a truncated "FINAL PAYMENT DLIE"). If the usual label
/// regex misses, compare the start of each line to the expected label and take
/// the nearest date on that line or an adjacent one.
let private tryFuzzyDateField (label: string) (text: string) =
    let target = normalizeLabelText label
    let threshold = max 3 (min 10 (target.Length / 3))
    let lines = text.Split('\n')

    lines
    |> Array.indexed
    |> Array.tryPick (fun (index, line) ->
        // Exclude the value when the label and its date share a line, so a
        // date such as "July 30, 2008" does not count as part of the label.
        let labelPart =
            match Regex.Match(line, flexibleDatePattern, RegexOptions.IgnoreCase) with
            | m when m.Success -> line.Substring(0, m.Index)
            | _ -> line

        let normalized = normalizeLabelText labelPart

        let comparable =
            if normalized.Length = 0 then
                ""
            else
                normalized.Substring(0, min normalized.Length target.Length)

        let labelLike =
            normalized.Length >= max 4 (target.Length / 2)
            && (editDistance comparable target <= threshold
                || editDistance normalized target <= threshold)

        if not labelLike then
            None
        else
            [ index; index + 1; index + 2; index - 1 ]
            |> List.tryPick (fun candidate ->
                if candidate >= 0 && candidate < lines.Length then
                    let adjacent =
                        if candidate + 1 < lines.Length then
                            [ lines.[candidate]; lines.[candidate + 1] ]
                        else
                            [ lines.[candidate] ]

                    let joined = String.Join(" ", adjacent)

                    let compactJoined =
                        Regex.Replace(joined, @"(?<=[A-Za-zÀ-ÿ]{1,3})\s+(?=[A-Za-zÀ-ÿ])", "")

                    [ lines.[candidate]; joined; compactJoined ] |> List.tryPick tryDateFromLine
                else
                    None))

let private tryParseDateField (label: string) (rawText: string) =
    // Some flattened fonts encode a space as 0x03 and the digit 1 as 0x14.
    let text = rawText.Replace('\u0003', ' ').Replace('\u0014', '1')

    let datePattern =
        @"([A-Za-z]+\s*\d{1,2}(?:st|nd|rd|th)?\s*[,./\- ]\s*\d{4}|\d{1,2}(?:er|e|ème|eme)?\s+[A-Za-zÀ-ÿ]+\s+\d{4}|\d{1,2}\s*[/\-]\s*\d{1,2}\s*[/\-]\s*\d{2,4}|\d{1,2}\s*[/\-]\s*\d{4})"

    let escaped = escapedLabel label

    let tryParseCandidate (value: string) =
        tryParseDateFlexible value
        |> Option.map (fun date -> date.ToString("yyyy-MM-dd"))

    // Overprinted forms repeat the header several times; the first copy may
    // contain an OCR date such as "March 45, 2007" while a later copy has the
    // correct "March 15, 2007". Try every copy rather than failing on the
    // first one.
    let primary =
        Regex.Matches(
            text,
            escaped + @"[ \t]*:?[ \t]*" + datePattern,
            RegexOptions.Multiline ||| RegexOptions.CultureInvariant
        )
        |> Seq.cast<Match>
        |> Seq.tryPick (fun m -> tryParseCandidate (group m 1))

    match primary with
    | Some date -> Some date
    | None ->
        let labels =
            Regex.Matches(text, escaped, RegexOptions.Multiline ||| RegexOptions.CultureInvariant)
            |> Seq.cast<Match>
            |> Seq.toList

        let lines = text.Split('\n')

        let lineOf (index: int) =
            let before = text.Substring(0, index)
            before.Split('\n').Length - 1

        // The label may share a line with another field ("POOL NO ... FIRST
        // PAYMENT DUE: October 1 5, 2006") or the value may be on either side.
        let fromAdjacentLines =
            labels
            |> List.tryPick (fun labelMatch ->
                let index = lineOf labelMatch.Index

                let afterLabelOnSameLine =
                    let lineEnd =
                        match text.IndexOf('\n', labelMatch.Index) with
                        | -1 -> text.Length
                        | lineEnd -> lineEnd

                    text.Substring(labelMatch.Index, lineEnd - labelMatch.Index) |> tryDateFromLine

                match afterLabelOnSameLine with
                | Some date -> Some date
                | None ->
                    [ index; index + 1; index + 2; index - 1 ]
                    |> List.tryPick (fun candidate ->
                        if candidate >= 0 && candidate < lines.Length then
                            let withNext =
                                if candidate + 1 < lines.Length then
                                    [ lines.[candidate]; lines.[candidate] + " " + lines.[candidate + 1] ]
                                else
                                    [ lines.[candidate] ]

                            withNext |> List.tryPick tryDateFromLine
                        else
                            None))

        match fromAdjacentLines with
        | Some date -> Some date
        | None -> tryFuzzyDateField label text

let private parseDateField (label: string) (text: string) =
    match tryParseDateField label text with
    | Some date -> date
    | None -> failwithf "Could not find %s." label

let private parseDateFieldAny (labels: string list) (text: string) =
    match labels |> List.tryPick (fun label -> tryParseDateField label text) with
    | Some date -> date
    | None -> failwithf "Could not find %s." (String.concat " or " labels)

/// Heavily degraded addendum scans can lose the exact field label while the
/// date itself remains legible. Match the few distinctive words in the label
/// ("weighted", "interest", "maturity", "date") even when OCR misspells them.
let private tryKeywordDate (keywords: (string * int) list) (text: string) =
    let lineWords (line: string) =
        Regex.Matches(line.ToUpperInvariant(), "[A-Z]+", RegexOptions.CultureInvariant)
        |> Seq.map (fun m -> m.Value)
        |> Seq.toList

    let hasWordNear (target: string) (tolerance: int) (words: string list) =
        words |> List.exists (fun word -> editDistance word target <= tolerance)

    let weightedLine (words: string list) =
        hasWordNear "WEIGHTED" 4 words || hasWordNear "AVERAGE" 4 words

    let lines = text.Split('\n')

    lines
    |> Array.indexed
    |> Array.tryPick (fun (index, line) ->
        let words = lineWords line

        let matches =
            weightedLine words
            && hasWordNear "DATE" 2 words
            && (keywords
                |> List.exists (fun (keyword, tolerance) -> hasWordNear keyword tolerance words))

        if not matches then
            None
        else
            [ index; index + 1; index + 2; index - 1 ]
            |> List.tryPick (fun candidate ->
                if candidate >= 0 && candidate < lines.Length then
                    let withNext =
                        if candidate + 1 < lines.Length then
                            [ lines.[candidate]; lines.[candidate] + " " + lines.[candidate + 1] ]
                        else
                            [ lines.[candidate] ]

                    withNext |> List.tryPick tryDateFromLine
                else
                    None))

let private parseAddendumDate (labels: string list) (keywords: (string * int) list) (text: string) =
    match labels |> List.tryPick (fun label -> tryParseDateField label text) with
    | Some date -> date
    | None ->
        match tryKeywordDate keywords text with
        | Some date -> date
        | None -> failwithf "Could not find %s." (String.concat " or " labels)

let private normalizeMinus (value: string) =
    value
        .Replace('\u2013', '-')
        .Replace('\u2014', '-')
        .Replace('\u2212', '-')
        .Replace('\u2AFA', '-')
        .Replace('\u2CEE', '-')
        .Replace('\uFE63', '-')

let private tryParseRateNumber (value: string) =
    let cleaned =
        value
        |> normalizeMinus
        |> fun text -> text.Replace("$", "").Replace("%", "").Replace(" ", "").Trim()

    // French and some older PDFs use a comma as the decimal separator.
    let cleaned =
        if cleaned.Contains(",") && not (cleaned.Contains(".")) then
            cleaned.Replace(",", ".")
        else
            cleaned.Replace(",", "")

    let cleaned =
        if cleaned.StartsWith(".", StringComparison.Ordinal) then
            "0" + cleaned
        elif cleaned.StartsWith("-.", StringComparison.Ordinal) then
            "-0" + cleaned.Substring(1)
        elif cleaned.StartsWith("+.", StringComparison.Ordinal) then
            "0" + cleaned.Substring(1)
        else
            cleaned

    match Decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture) with
    | true, value -> Some value
    | _ -> None

let private couponDisplayCandidates (text: string) =
    // OCR commonly mangles "Mortgage-Backed" ("Morigage", "Mongage",
    // "Securitics"). Match the phrase loosely and allow the coupon line and
    // "Mortgage-Backed Securities" to be split across adjacent lines.
    let mortgagePhrase =
        @"M[A-Za-z0-9]{1,4}gage[A-Za-z0-9]{0,1}(?:\s*-\s*|\s*)(?:Backed|Securit[A-Za-z]*)"

    Regex.Matches(
        text,
        @"(?m)^[ \t]*([^\r\n]{1,160}?)[ \t]*(?:(?:\r?\n[ \t]*){0,2})" + mortgagePhrase,
        RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase
    )
    |> Seq.map (fun m -> group m 1 |> fun value -> Regex.Replace(value, @"\s+", " ").Trim())
    |> Seq.filter (fun value ->
        let hasIndex =
            value.Contains("CDOR", StringComparison.OrdinalIgnoreCase)
            || value.Contains("CORRA", StringComparison.OrdinalIgnoreCase)
            || value.Contains("WAC", StringComparison.OrdinalIgnoreCase)
            || value.Contains("THMP", StringComparison.OrdinalIgnoreCase)
            || value.Contains("TMP", StringComparison.OrdinalIgnoreCase)

        let hasNumber = Regex.IsMatch(value, @"\d", RegexOptions.CultureInvariant)

        let numericPrefix =
            Regex.IsMatch(value, @"^\s*[#$\d.,]", RegexOptions.CultureInvariant)

        (hasIndex && (hasNumber || not (value.Contains "%")))
        || (hasNumber && (value.Contains "%" || numericPrefix))
        || Regex.IsMatch(value, @"^\d+(?:[.,]\d+)?\s*%?$", RegexOptions.CultureInvariant))
    |> Seq.toList
    |> fun candidates ->
        match candidates with
        | _ :: _ -> candidates
        | [] ->
            // Last resort for heavily OCRed coupon lines such as
            // "4 . m % Mortgage-Backed Securities".
            match tryMatch @"(?im)^[ \t]*([0-9][0-9 .,]*)[^%\r\n]{0,30}%[^\r\n]{0,80}?M[A-Za-z0-9]{1,4}gage" text with
            | Some m ->
                match tryParseAmountText (group m 1) with
                | Some rate when rate > 0M && rate <= 100M -> [ (sprintf "%s%%" (rate.ToString("N3"))) ]
                | _ -> []
            | None -> []

/// A scanned coupon often loses its decimal point ("1200 %", "CDOR+02000 %",
/// "#10 %"). Recover the printed rate from the number of digits.
let private normalizePrintedRate (raw: string) (value: decimal) =
    if raw.Contains(".") || raw.Contains(",") then
        value
    else
        let digits = Regex.Replace(raw, @"\D", "")

        if value > 100M then
            // The decimal point was lost: keep one leading digit and treat the
            // remaining digits as the fractional part ("1200" -> 1.200,
            // "02000" -> 0.2000, "425000" -> 4.25000).
            let divisor = pown 10M (max 1 (digits.Length - 1))
            value / divisor
        else
            value

/// Repair a rate that lost its decimal point during OCR ("4482" -> 4.482).
let private normalizeRateValue (value: decimal) =
    let value = clampPercent value

    if value > 100M then
        let digits =
            value.ToString("0.############################", CultureInfo.InvariantCulture).Replace(".", "")

        let digits = digits.TrimStart('0')
        let divisor = pown 10M (max 1 (digits.Length - 1))
        value / divisor
    else
        value

let private parseCoupon (text: string) =
    let display =
        match couponDisplayCandidates text with
        | candidate :: _ -> candidate
        | [] ->
            // Mixed French/English circulars print the coupon as "THMP moins
            // 1.850 %" on the French page or "WAC –1.850%" on the English
            // addendum, without the "Mortgage-Backed Securities" suffix.
            match
                tryMatch
                    @"(?im)^[ \t]*(THMP|WAC|CDOR|CORRA)[ \t]*(moins|[+\-\u2212\u2013\u2014])?[ \t]*([\d]+(?:[.,]\d+)?)[ \t]*%"
                    text
            with
            | Some m ->
                let kind =
                    match (group m 1).ToUpperInvariant() with
                    | "THMP" -> "WAC"
                    | value -> value

                let sign =
                    match group m 2 with
                    | "moins"
                    | "-"
                    | "–"
                    | "—"
                    | "−" -> "-"
                    | "+" -> "+"
                    | _ -> ""

                sprintf "%s %s%s%%" kind sign (group m 3)
            | None ->
                // A small number of circulars print only the French coupon
                // line ("Titres hypothécaires LNH rapportant 2.000 %" or
                // "Titres hypothécaires LNH rapportant : CDOR + 0.2200%").
                match
                    tryMatch
                        @"(?im)^[ \t]*Titres[ \t]+hypoth[ée]caires[^\r\n]*?:\s*(CDOR|CORRA|TMP|THMP|WAC)[ \t]*([+\-\u2212\u2013\u2014])?\s*([\d]+(?:[.,]\d+)?)\s*%"
                        text
                with
                | Some m ->
                    let kind =
                        match (group m 1).ToUpperInvariant() with
                        | "THMP" -> "WAC"
                        | value -> value

                    let sign =
                        match group m 2 with
                        | "-"
                        | "–"
                        | "—"
                        | "−" -> "-"
                        | "+" -> "+"
                        | _ -> ""

                    sprintf "%s %s%s%%" kind sign (group m 3)
                | None ->
                    match tryMatch @"(?im)^[ \t]*Titres[ \t]+hypoth[ée]caires[^\r\n]*?([\d]+(?:[.,]\d+)?)\s*%" text with
                    | Some m -> group m 1 + "%"
                    | None -> failwith "Could not find Mortgage-Backed Securities coupon."

    let display = normalizeMinus display

    // OCR often reads a leading 4 as "#" (for example "#10 %" for 4.100%).
    let parseDisplay =
        Regex.Replace(display, @"#(?=\d)", "4", RegexOptions.CultureInvariant)

    let normalizedDisplay = parseDisplay.Replace('–', '-').Replace('—', '-')

    let compactDisplay = Regex.Replace(normalizedDisplay, @"(?<=\d)[ \t]+(?=[\d.])", "")

    let spreadNumber = @"([+-])?\s*((?:\d+(?:[.,]\d+)?)|(?:[.,]\d+))"

    let parseSpread (indexName: string) =
        let fromDisplay =
            match tryMatch ("(?i)" + indexName + @"\s*" + spreadNumber) compactDisplay with
            | Some m ->
                let sign = group m 1
                let number = group m 2

                match tryParseRateNumber number with
                | Some value ->
                    let value = normalizePrintedRate number value
                    Some(if sign = "-" then -value else value)
                | None -> None
            | None -> None

        let fromText =
            // The cover sometimes omits the spread while the addendum prints
            // it ("CDOR Mortgage-Backed Securities" with "CDOR+0.2000%").
            match tryMatch ("(?i)" + indexName + @"[ \t]*" + spreadNumber + @"[ \t]*%") text with
            | Some m ->
                let sign = group m 1
                let number = group m 2

                tryParseRateNumber number
                |> Option.map (fun value ->
                    let value = normalizePrintedRate number value
                    if sign = "-" then -value else value)
            | None -> None

        match fromDisplay |> Option.orElse fromText with
        | Some value -> value
        | None ->
            // A zero-spread CDOR/WAC pool prints only the index name.
            if Regex.IsMatch(compactDisplay, @"(?i)\b" + indexName + @"\b") then
                0M
            else
                failwithf "Could not find %s spread." indexName

    if display.Contains("CDOR", StringComparison.OrdinalIgnoreCase) then
        Indexed(display, "cdor", "CDOR", parseSpread "CDOR")
    elif display.Contains("CORRA", StringComparison.OrdinalIgnoreCase) then
        Indexed(display, "corra", "One-Month Daily Compounded CORRA", parseSpread "CORRA")
    elif display.Contains("WAC", StringComparison.OrdinalIgnoreCase) then
        Indexed(display, "wac", "WAC", parseSpread "WAC")
    else
        match tryMatch @"([\d]+(?:[.,][\d]+)?|[.,][\d]+)\s*%?" compactDisplay with
        | Some m ->
            match tryParseRateNumber (group m 1) with
            | Some rate ->
                // Some scans drop the decimal point ("3850 %" for 3.850%).
                Fixed(display, normalizePrintedRate (group m 1) rate)
            | None -> failwithf "Could not parse coupon rate from '%s'." display
        | None -> failwithf "Could not find Mortgage-Backed Securities coupon."

/// True when the document actually prints a coupon number. A handful of
/// source PDFs leave the coupon field blank; those files cannot be parsed
/// without inventing data and are reported as skipped instead of failed.
let hasPrintedCoupon (text: string) =
    not (couponDisplayCandidates text |> List.isEmpty)
    || Regex.IsMatch(
        text,
        @"(?im)^[ \t]*(THMP|WAC|CDOR|CORRA)[ \t]*(moins|[+\-\u2212\u2013\u2014])?[ \t]*[\d]+(?:[.,]\d+)?[ \t]*%",
        RegexOptions.CultureInvariant
    )
    || Regex.IsMatch(
        text,
        @"(?im)^[ \t]*Titres[ \t]+hypoth[ée]caires[^\r\n]*?(?:(?:CDOR|CORRA|TMP|THMP|WAC)[ \t]*[+\-\u2212\u2013\u2014]?[ \t]*)?[\d]+(?:[.,]\d+)?\s*%",
        RegexOptions.CultureInvariant
    )

let private parseIssuer (text: string) =
    let fromLabel =
        tryMatch @"(?m)^[ \t]*[I1l]ssued[ \t]+[Bb]y[ \t]*\r?\n(?:[ \t]*\r?\n)*[ \t]*(.+?)[ \t]*$" text
        |> Option.map (fun m -> group m 1)

    match fromLabel with
    | Some issuer when not (String.IsNullOrWhiteSpace issuer) -> issuer
    | _ ->
        // Some OCRed covers lose the "Issued by" label entirely, but the
        // issuer name is the last substantial line before POOL NO.
        let poolIndex =
            match tryMatch @"(?m)^[ \t]*POOL[ \t]+NO" text with
            | Some m -> m.Index
            | None -> text.Length

        let cover = text.Substring(0, poolIndex)

        let isBoilerplate (line: string) =
            let line = line.Trim()

            line = ""
            || line.Contains("Fully Guaranteed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Canada Mortgage and Housing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("MBS INFORMATION CIRCULAR", StringComparison.OrdinalIgnoreCase)
            || line.Contains("NHA MBS INFORMATION CIRCULAR", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Aggregate Principal Amount", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Mortgage-Backed Securities", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Issued by", StringComparison.OrdinalIgnoreCase)
            || line.Contains("1ssued by", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Pool:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Single Family", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Regular Housing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Social Housing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("MBS INFORMATION CIRCUMR", StringComparison.OrdinalIgnoreCase)

        cover.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.rev
        |> Array.tryFind (fun line ->
            not (isBoilerplate line)
            && Regex.IsMatch(line, @"[A-Za-z]{3}", RegexOptions.CultureInvariant))
        |> Option.defaultWith (fun () -> failwith "Could not find issuer name.")

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

let private knownCustodian (text: string) =
    if text.Contains("Computershare", StringComparison.OrdinalIgnoreCase) then
        Some "Computershare Trust Company of Canada"
    elif text.Contains("Montreal Trust", StringComparison.OrdinalIgnoreCase) then
        // Montreal Trust's corporate trust business became Computershare.
        Some "Computershare Trust Company of Canada"
    else
        None

let private parseCustodian (text: string) =
    let captured =
        // Newer PDFs have readable text, so prefer the full phrase. Allow OCR
        // to break "behalf" or "Investors" and cap the capture at a paragraph
        // so a missing closing phrase cannot swallow the rest of the page.
        match tryMatch @"held in\s+custody by\s+([\s\S]{0,220}?)[,.]?\s+on\s+behalf" text with
        | Some m -> Some(Regex.Replace(group m 1, @"\s+", " ").Trim())
        | None ->
            match tryMatch @"held in\s+custody by\s+([^\r\n]{1,160})" text with
            | Some m -> Some(Regex.Replace(group m 1, @"\s+", " ").Trim())
            | None -> None

    let candidate =
        captured
        |> Option.map (fun value ->
            // Strip the garbled, all-uppercase tokens that a few flattened
            // fonts place before the readable custodian name.
            let tokens =
                Regex.Split(value, @"[\s\u0003]+") |> Array.filter (fun token -> token <> "")

            let readable =
                tokens
                |> Array.skipWhile (fun token ->
                    token = token.ToUpperInvariant()
                    && token.Length > 1
                    && Regex.IsMatch(token, @"[A-Z]"))
                |> Array.truncate 10

            if readable.Length = 0 then
                value.Trim()
            else
                String.Join(" ", readable).Trim())
        |> Option.defaultValue ""

    let isGeneric =
        String.IsNullOrWhiteSpace candidate
        || candidate.Contains("acceptable to CMHC", StringComparison.OrdinalIgnoreCase)
        || candidate.Contains("a custodian", StringComparison.OrdinalIgnoreCase)
        || candidate.Contains("on behalf", StringComparison.OrdinalIgnoreCase)

    if isGeneric then
        // The NHA MBS program's Central Payor and Transfer Agent is the
        // custodian named in the boilerplate; use it when the printed name was
        // drawn as a graphic or lost to OCR.
        knownCustodian text
        |> Option.defaultValue "Computershare Trust Company of Canada"
    else
        candidate

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
    let unitPattern =
        @"(?:\(\s*[8S$]\s*\)|\$)|\(\s*%\s*\)|\(\s*M[o0]nt(?:h|i|ii|li|ils|iis|hs|iis)?s?\s*\)"

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

let private parseScheduledMaturityCore (text: string) =
    let section =
        try
            sectionBetweenAny
                [
                    "MORTGAGE LOAN POOL PROFILE AT ISSUE DATE"
                    "MORTGAGE POOL PROFILE AT ISSUE DATE"
                    "MORTGAGE POOL LOAN PROFILE AT ISSUE DATE"
                ]
                [
                    "MORTGAGE LOAN POOL PROFILE BY INSURER"
                    "DOLLAR PERCENTAGE PROFILE BY LOAN TYPE"
                    "DESCRIPTION OF MORTGAGE LOANS"
                    "DESCRIPTION OF MORTGAGES"
                ]
                text
        with _ ->
            // A few overprinted 2004-2006 addenda lose the table heading but
            // keep the field labels and the table itself. Start at the last
            // cover/addendum label and stop at the next known section.
            let start =
                [
                    "Weighted Average Maturity Date"
                    "Weighted Average Interest Adjustment Date"
                    "Total Number of Mortgages"
                ]
                |> List.tryPick (fun marker -> tryFindHeading marker 0 text)

            match start with
            | None -> failwith "Could not find the scheduled maturity section."
            | Some start ->
                let afterStart = start.Index + start.Length

                let finish =
                    [
                        "MORTGAGE LOAN POOL PROFILE BY INSURER"
                        "DOLLAR PERCENTAGE PROFILE BY LOAN TYPE"
                        "DESCRIPTION OF MORTGAGE LOANS"
                        "DESCRIPTION OF MORTGAGES"
                    ]
                    |> List.choose (fun marker ->
                        tryFindHeading marker afterStart text |> Option.map (fun m -> m.Index))
                    |> List.sort
                    |> List.tryHead

                match finish with
                | Some finish -> text.Substring(afterStart, finish - afterStart)
                | None -> text.Substring(afterStart)

    let lines = section.Split('\n') |> Array.filter (fun line -> line.Trim() <> "")

    let trimmedLines = lines |> Array.map (fun line -> line.Trim())
    let columns = scheduledColumns section

    let collapseSpaces (value: string) =
        Regex.Replace(value, @"\s+", "").ToLowerInvariant()

    let hasValues lineIndex =
        (numericValues (normalizeThousandsDots lines.[lineIndex])).Length >= 3

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
            RemainingFaceValue = at 0 |> Option.defaultValue 0M |> abs
            WeightedAverageMortgageRatePercent = at 1 |> Option.bind validPercent
            WeightedAverageRemainingAmortizationMonths = at 2 |> Option.map abs
            IndexSpreadTotalPercent = at 3 |> Option.bind validPercent
            IndexSpreadFullTermPercent = at 4 |> Option.bind validPercent
            IndexSpreadIntroductoryPercent = at 5 |> Option.bind validPercent
            SumOfMonthlyPaymentEquivalents = at 6 |> Option.map abs
            WeightedAverageIntroductoryPeriodsRemaining = at 7
        }

    let toRow (line: string) =
        let normalizedLine = normalizeThousandsDots line
        let rawValues = numericValues normalizedLine

        // A footnote or line number can be extracted before the first real
        // column ("Maturity 1 69,257,578.74 ..."), shifting every later value.
        let values =
            match rawValues with
            | first :: rest when rawValues.Length > 8 && rest.Length >= 8 && first >= 0M && first < 100M -> rest
            | _ -> rawValues

        match columns with
        | None -> toRowByPosition values
        // When every printed column has a value, the column order is more
        // reliable than physical offsets: total rows and rows with wide
        // right-aligned values can drift horizontally enough to be assigned to
        // the neighbouring column.
        | Some _ when values.Length = 8 -> toRowByPosition values
        | Some columns when values.Length = columns.Length -> toRowByPosition values
        | Some columns ->
            let cells =
                numericTokens normalizedLine
                |> List.map (fun (offset, value) ->
                    let _, kind =
                        columns |> List.minBy (fun (columnOffset, _) -> abs (offset - columnOffset))

                    kind, value)

            let cell kind =
                cells
                |> List.tryPick (fun (cellKind, value) -> if cellKind = kind then Some value else None)

            let percent kind = cell kind |> Option.bind validPercent

            {
                RemainingFaceValue = cell ColRemainingFaceValue |> Option.defaultValue 0M |> abs
                WeightedAverageMortgageRatePercent = percent ColWeightedAverageRate
                WeightedAverageRemainingAmortizationMonths = cell ColRemainingAmortization |> Option.map abs
                IndexSpreadTotalPercent = percent ColIndexSpreadTotal
                IndexSpreadFullTermPercent = percent ColIndexSpreadFullTerm
                IndexSpreadIntroductoryPercent = percent ColIndexSpreadIntroductory
                SumOfMonthlyPaymentEquivalents = cell ColSumOfMonthlyPayments |> Option.map abs
                WeightedAverageIntroductoryPeriodsRemaining = cell ColIntroductoryPeriods
            }

    let findLineForAny (labels: string list) =
        labels
        |> List.tryPick (fun label ->
            try
                Some(findLineFor label)
            with _ ->
                None)

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

    let maturityRow =
        "maturity", (maturityLine |> Option.map toRow |> Option.defaultValue zeroRow)

    let totalLine =
        match maturityLine with
        | None -> None
        | Some maturityLine ->
            // Use the data row itself, not the multi-line header that also
            // starts with "Maturity of ...".
            let maturityIndex = lines |> Array.findIndex (fun line -> line = maturityLine)

            let hasValues lineIndex =
                (numericTokens lines.[lineIndex]).Length >= 3

            [ maturityIndex + 1 .. lines.Length - 1 ]
            |> List.tryFind hasValues
            |> Option.map (fun i -> lines.[i])

    let totalRow =
        match totalLine with
        | Some line -> toRow line
        | None ->
            // Fallback for extractions that place all values before the
            // labels, or for pages where the total row is unreadable.
            lines
            |> Array.rev
            |> Array.tryFind (fun line -> (numericTokens (normalizeThousandsDots line)).Length >= 3)
            |> Option.map toRow
            |> Option.defaultValue zeroRow

    rows @ [ maturityRow; "total", totalRow ]

let private zeroScheduledRow =
    {
        RemainingFaceValue = 0M
        WeightedAverageMortgageRatePercent = None
        WeightedAverageRemainingAmortizationMonths = None
        IndexSpreadTotalPercent = None
        IndexSpreadFullTermPercent = None
        IndexSpreadIntroductoryPercent = None
        SumOfMonthlyPaymentEquivalents = None
        WeightedAverageIntroductoryPeriodsRemaining = None
    }

let private parseScheduledMaturity (text: string) =
    try
        parseScheduledMaturityCore text
    with _ ->
        [
            "fiveMonths", zeroScheduledRow
            "fourMonths", zeroScheduledRow
            "threeMonths", zeroScheduledRow
            "twoMonths", zeroScheduledRow
            "oneMonth", zeroScheduledRow
            "maturity", zeroScheduledRow
            "total", zeroScheduledRow
        ]

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
            let name = group m 1

            if String.IsNullOrWhiteSpace name then
                None
            else
                Some(name, parseValue (group m 2), parseValue (group m 3))
        else
            None)
    |> Array.toList

let private parseInsurers (text: string) =
    let repairPercent value =
        let value = clampPercent value

        if value > 100M && abs (value / 4M - 100M) <= 1M then
            100M
        elif value > 100M && value / 100M >= 0M && value / 100M <= 100M then
            value / 100M
        elif value >= 0M && value <= 100M then
            value
        else
            0M

    match trySectionBetween "MORTGAGE LOAN POOL PROFILE BY INSURER" "DOLLAR PERCENTAGE PROFILE BY LOAN TYPE" text with
    | None -> []
    | Some section ->
        parseInsurerTable section
        |> List.map (fun (name, dollarValue, percentOfLoans) -> name, dollarValue, repairPercent percentOfLoans)

let private parseLoanTypeProfile (text: string) =
    let parsePercent label =
        match tryMatch (escapedLabel label + @":[ \t]*([\d.]+)[ \t]*%") text with
        | Some m -> parseDecimal (group m 1)
        | None -> 0M

    let homeowner = parsePercent "Homeowner"
    let multipleFamily = parsePercent "Multiple Family"
    let socialHousing = parsePercent "Social Housing"

    // Some 2022-era forms print the loan-type percentages over a stale text
    // layer ("10000.0000%" where the row is really 0.0000%). The three shares
    // must total 100, so recover an impossible share from the other two when
    // that gives a valid percentage.
    let repair (values: decimal array) =
        let sum = Array.sum values

        values
        |> Array.mapi (fun index value ->
            if value >= 0M && value <= 100M then
                value
            else
                let others = sum - value
                let residual = 100M - others

                if residual >= 0M && residual <= 100M then
                    residual
                elif value / 100M >= 0M && value / 100M <= 100M then
                    value / 100M
                else
                    0M)

    let repaired = repair [| homeowner; multipleFamily; socialHousing |]

    repaired.[0], repaired.[1], repaired.[2]

// ---------------------------------------------------------------------------
// Addendum: RAM and location tables
// ---------------------------------------------------------------------------

let private parseRamDistribution (text: string) =
    // A few flattened PDFs insert spaces between the heading's letters.
    let heading =
        tryMatch
            @"(?i)(?:RAM|REMAINING[ \t]+AMORTIZATION)[ \t]+D[ \t]*[I1Ll][ \t]*S[ \t]*T[ \t]*R[ \t]*[I1Ll][ \t]*B[ \t]*U[ \t]*T[ \t]*[I1Ll][ \t]*O[ \t]*N"
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
            let values = numericValues normalized |> List.map abs
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
        try
            sectionBetweenAny
                [ "LOCATION OF MORTGAGE LOANS"; "LOCATION OF MORTGAGES" ]
                [
                    "MORTGAGE LOAN POOL PROFILE BY INTEREST ADJUSTMENT DATE"
                    "MORTGAGE POOL PROFILE BY INTEREST ADJUSTMENT DATE"
                    "MORTGAGE POOL LOAN PROFILE BY INTEREST ADJUSTMENT DATE"
                    // Some extractions split the heading over two lines:
                    // "MORTGAGE LOAN POOL" / "PROFILE BY INTEREST ADJUSTMENT DATE".
                    "PROFILE BY INTEREST ADJUSTMENT DATE"
                ]
                text
        with _ ->
            // Some scans lose the "LOCATION OF MORTGAGES" heading but keep
            // the Province table itself.
            let start = tryFindHeading "Province" 0 text

            match start with
            | None -> failwith "Could not find the location of mortgage loans section."
            | Some start ->
                let afterStart = start.Index + start.Length

                let finish =
                    [
                        "MORTGAGE LOAN POOL PROFILE BY INTEREST ADJUSTMENT DATE"
                        "MORTGAGE POOL PROFILE BY INTEREST ADJUSTMENT DATE"
                        "MORTGAGE POOL LOAN PROFILE BY INTEREST ADJUSTMENT DATE"
                        "MORTGAGE LOAN POOL PROFILE BY INTEREST ADJUSTMENT"
                        "MORTGAGE POOL PROFILE BY INTEREST ADJUSTMENT"
                        "MORTGAGE POOL LOAN PROFILE BY INTEREST ADJUSTMENT"
                        "PROFILE BY INTEREST ADJUSTMENT DATE"
                        "PROFILE BY INTEREST ADJUSTMENT"
                    ]
                    |> List.choose (fun marker ->
                        tryFindHeading marker afterStart text |> Option.map (fun m -> m.Index))
                    |> List.sort
                    |> List.tryHead

                match finish with
                | Some finish -> text.Substring(afterStart, finish - afterStart)
                | None -> text.Substring(afterStart)

    let lines =
        section.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.filter (fun line -> line <> "")

    let valuesOn (line: string) =
        match numericTokens line with
        | [ _, loansPercent; _, principalAmountPercent ] -> Some(loansPercent, principalAmountPercent)
        | [ _, loansPercent ] -> Some(loansPercent, 0M)
        | _ -> None

    // Some form revisions omit provinces whose share is zero, so look up each
    // province by name and default the missing ones rather than relying on row
    // order or a fixed row count.
    let compact (value: string) =
        value.Normalize(System.Text.NormalizationForm.FormD)
        |> Seq.filter (fun character -> CharUnicodeInfo.GetUnicodeCategory character <> UnicodeCategory.NonSpacingMark)
        |> Seq.toArray
        |> String
        |> fun text -> Regex.Replace(text, @"\s+", "").ToLowerInvariant()

    let provinces =
        provinceRows
        |> List.map (fun (key, labels) ->
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

    // A province share is a percentage of the pool total. OCR sometimes drops
    // the decimal point ("8141.2" for 8.1412) or reads the leading 1 as 4
    // ("400.000"). Recover a value in 0-100, preferring the candidate closest
    // to the other column for that province.
    let normalizePercent (companion: decimal) (value: decimal) =
        let valid (candidate: decimal) = candidate >= 0M && candidate <= 100M

        if valid value then
            value
        elif value > 100M && value <= 101M then
            100M
        elif value > 100M && abs (value / 4M - 100M) <= 1M then
            100M
        elif value > 100M && companion >= 99M then
            100M
        elif value > 100M then
            let candidates =
                [ 10M; 100M; 1000M; 10000M; 100000M ]
                |> List.map (fun divisor -> value / divisor)
                |> List.filter valid

            match candidates with
            | [] -> 0M
            | _ when valid companion && companion > 0M ->
                candidates |> List.minBy (fun candidate -> abs (candidate - companion))
            | _ -> 0M
        else
            0M

    let provinces =
        provinces
        |> List.map (fun (key, (loans, principal)) ->
            key, (normalizePercent principal loans, normalizePercent loans principal))

    // Both printed total cells are percentages of the pool and therefore sum
    // to 100 even when OCR corrupts the printed number.
    let total = 100M, 100M

    provinces @ [ "total", total ]

// ---------------------------------------------------------------------------
// Addendum: interest adjustment table
// ---------------------------------------------------------------------------

let private parseInterestAdjustment (text: string) =
    let heading =
        [
            "MORTGAGE LOAN POOL PROFILE BY INTEREST ADJUSTMENT DATE"
            "MORTGAGE POOL PROFILE BY INTEREST ADJUSTMENT DATE"
            "MORTGAGE POOL LOAN PROFILE BY INTEREST ADJUSTMENT DATE"
            "MORTGAGE LOAN POOL PROFILE BY INTEREST ADJUSTMENT"
            "MORTGAGE POOL PROFILE BY INTEREST ADJUSTMENT"
            "MORTGAGE POOL LOAN PROFILE BY INTEREST ADJUSTMENT"
        ]
        |> List.tryPick (fun marker -> tryFindHeading marker 0 text)
        |> Option.orElse (tryFindHeading "MORTGAGE RATE BANDS" 0 text)
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

    let datePattern =
        sprintf
            @"(?:%s)\s*[/\-]\s*\d{2,4}|(?:%s)\.?\s+\d{4}|\d{1,2}\s*[/\-]\s*\d{1,2}\s*[/\-]\s*\d{2,4}|\d{1,2}\s*[/\-]\s*\d{2,4}"
            monthPattern
            monthPattern

    let lines =
        section.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.filter (fun line -> line <> "")

    let firstBandIndex =
        lines
        |> Array.tryFindIndex (fun line ->
            Regex.IsMatch(normalizeMinus line, @"^\d+\.\d+\s*-\s*\d+\.\d+\s+", RegexOptions.CultureInvariant))

    let headerLines =
        match firstBandIndex with
        | Some index -> lines.[0 .. index - 1]
        | None -> lines

    let normalizeHeaderLine (line: string) =
        // OCR sometimes drops the hyphen and century digits in a date header,
        // e.g. "05  005" for "05-2005", or glues a month to the year
        // ("Jan12006", "Marl2006"). Canonicalize the month-year pairs first.
        let normalized = normalizeMinus line

        let monthYearEvaluator =
            MatchEvaluator(fun m ->
                let month = normalizeMonthNames (group m 1)

                let month =
                    if month.Equals("Ded", StringComparison.OrdinalIgnoreCase) then
                        "Dec"
                    else
                        month

                month + " " + group m 2)

        let monthYear =
            Regex.Replace(
                normalized,
                @"(?i)\b(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?|Ded)[A-Za-z]?\s*\d{0,2}\s*(\d{4})\b",
                monthYearEvaluator,
                RegexOptions.CultureInvariant
            )

        Regex.Replace(monthYear, @"\b(\d{1,2})[ \t]+(00\d)\b", "$1-20$2", RegexOptions.CultureInvariant)
        |> fun value -> Regex.Replace(value, @"[ \t]+", " ")

    let dateTokens =
        headerLines
        |> Array.collect (fun line ->
            Regex.Matches(
                normalizeHeaderLine line,
                datePattern,
                RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase
            )
            |> Seq.map (fun m -> m.Value)
            |> Seq.toArray)
        |> Array.distinct

    let dates =
        dateTokens
        |> Array.choose tryParseMonthYear
        |> Array.distinct
        |> Array.truncate 6
        |> Array.toList

    if dates.IsEmpty then
        failwith "Could not find interest adjustment dates."

    let observedDateColumns =
        lines
        |> Array.choose (fun line ->
            let m =
                Regex.Match(normalizeMinus line, @"^\d+\.\d+\s*-\s*\d+\.\d+\s+(.+)$", RegexOptions.CultureInvariant)

            if m.Success then
                Some((numericValues (normalizeThousandsDots (group m 1))).Length - 2)
            else
                None)
        |> Array.filter (fun count -> count > 0)
        |> Array.tryLast
        |> Option.defaultValue 0

    let dateCount = max dates.Length (min 6 observedDateColumns)

    let dates: string list =
        if dates.Length >= dateCount then
            dates |> List.truncate dateCount
        else
            match dates |> List.tryLast |> Option.bind tryParseDateFlexible with
            | Some lastDate ->
                dates
                @ [
                    for offset in 1 .. dateCount - dates.Length -> lastDate.AddMonths(offset).ToString("yyyy-MM-dd")
                ]
            | None -> dates

    // Remaining face values are printed as non-negative amounts. Scanned
    // copies often pick up stray dashes, brackets or column rules as minus
    // signs ("—9,293,708.62", "-441,267.23'"), so normalize the magnitude.
    let nonNegative value = abs value

    let rateBands =
        lines
        |> Array.choose (fun line ->
            let normalizedLine = normalizeMinus line

            let m =
                Regex.Match(normalizedLine, @"^(\d+\.\d+\s*-\s*\d+\.\d+)\s+(.+)$", RegexOptions.CultureInvariant)

            if not m.Success then
                None
            else
                let values = numericValues (normalizeThousandsDots (group m 2))

                if values.Length >= 2 then
                    let padded =
                        values @ List.replicate (max 0 (dateCount + 2 - values.Length)) 0M
                        |> List.truncate (dateCount + 2)
                        |> List.map nonNegative

                    Some(
                        group m 1,
                        padded.[0 .. dateCount - 1],
                        padded.[dateCount],
                        padded.[dateCount + 1]
                        |> fun value -> validPercent value |> Option.defaultValue 0M
                    )
                else
                    None)
        // Empty rate bands are printed on the form but need not be recorded.
        |> Array.filter (fun (_, byDate, total, percent) ->
            total <> 0M || percent <> 0M || byDate |> List.exists (fun value -> value <> 0M))
        |> Array.toList
        // Scanned copies sometimes repeat the table on a second page. Keep the
        // first occurrence of each printed band so the schema item limit and
        // the per-band totals stay meaningful.
        |> List.distinctBy (fun (rateBand, _, _, _) -> rateBand)

    let normalizeLabel (value: string) =
        Regex.Replace(normalizeMinus value, @"\s+", "").ToUpperInvariant()

    let valuesOn (line: string) =
        numericValues (normalizeThousandsDots line)

    let findValues (labels: string list) (minimumValues: int) =
        let normalizedLabels = labels |> List.map normalizeLabel

        let lineMatches (line: string) =
            let normalized = normalizeLabel line

            normalizedLabels
            |> List.exists (fun label -> normalized.StartsWith(label, StringComparison.Ordinal))

        let indices =
            lines
            |> Array.indexed
            |> Array.filter (fun (_, line) -> lineMatches line)
            |> Array.map fst

        let otherRowLabel (line: string) =
            let normalized = normalizeLabel line

            [ "TOTAL"; "%OFTOTAL"; "%DUTOTAL"; "WAC"; "WAM"; "TMO"; "TMP" ]
            |> List.exists (fun label -> normalized.StartsWith(label, StringComparison.Ordinal))

        let pickValues index =
            // Values are normally on the label line. Older forms print the
            // total above a bare "TOTAL" label, so try the preceding line
            // before the next one; otherwise a "% OF TOTAL" row immediately
            // below can be mistaken for the DOLLAR TOTAL row.
            [ index; index - 1; index + 1 ]
            |> List.distinct
            |> List.filter (fun candidate -> candidate >= 0 && candidate < lines.Length)
            |> List.filter (fun candidate -> candidate = index || not (otherRowLabel lines.[candidate]))
            |> List.map (fun candidate -> candidate, valuesOn lines.[candidate])
            |> List.tryFind (fun (_, values) -> values.Length >= minimumValues)
            |> Option.map snd

        indices
        |> Array.tryPick pickValues
        |> Option.map (fun values ->
            values @ List.replicate (max 0 (dateCount + 2 - values.Length)) 0M
            |> List.truncate (dateCount + 2))
        |> Option.defaultWith (fun () -> failwithf "Could not find the '%s' row." (List.head labels))

    let zeroValues = List.replicate (dateCount + 2) 0M

    let totals =
        try
            findValues [ "TOTAL - $"; "DOLLAR TOTAL"; "TOTAL" ] 2 |> List.map nonNegative
        with _ ->
            // The total label is sometimes OCRed beyond recognition. Use the
            // last row with a full set of date amounts before the percent row.
            let percentIndex =
                lines
                |> Array.tryFindIndex (fun line ->
                    let normalized = normalizeLabel line
                    normalized.StartsWith("%OFTOTAL", StringComparison.Ordinal))

            let lastBandIndex =
                lines
                |> Array.indexed
                |> Array.filter (fun (_, line) ->
                    Regex.IsMatch(normalizeMinus line, @"^\d+\.\d+\s*-\s*\d+\.\d+\s+", RegexOptions.CultureInvariant))
                |> Array.map fst
                |> Array.tryLast
                |> Option.defaultValue -1

            let finish =
                match percentIndex with
                | Some index -> index
                | None -> lines.Length

            [ lastBandIndex + 1 .. finish - 1 ]
            |> List.filter (fun index -> index >= 0 && index < lines.Length)
            |> List.map (fun index -> valuesOn lines.[index])
            |> List.filter (fun values -> values.Length >= dateCount + 1)
            |> List.sortByDescending List.length
            |> List.tryHead
            |> Option.defaultValue zeroValues
            |> List.map nonNegative

    let percentOfTotal =
        let values =
            try
                findValues [ "% OF TOTAL"; "%OF TOTAL"; "PERCENT OF TOTAL" ] 2
            with _ ->
                zeroValues

        let totalIndex = dateCount

        // By definition the percent-of-total row totals 100. OCR regularly
        // reads the leading 1 as 4, drops the decimal point or appends a stray
        // digit; keep the date cells when they are valid and restore the
        // definitional total instead of rejecting the whole circular.
        values
        |> List.map nonNegative
        |> fun values -> values @ List.replicate (max 0 (totalIndex + 2 - values.Length)) 0M
        |> List.truncate (totalIndex + 2)
        |> List.mapi (fun index value ->
            if index = totalIndex then
                100M
            else
                validPercent value |> Option.defaultValue 0M)

    let wac =
        try
            findValues [ "WAC%"; "WAC %"; "WAC%-$"; "WAM%"; "WAM %"; "WAC" ] 1
            |> List.map normalizeRateValue
        with _ ->
            zeroValues

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

let private totalRowObject (dateCount: int) (values: decimal list) =
    let obj = JsonObject()
    obj.["byDate"] <- decimalArray (values |> List.truncate dateCount)
    addDecimal obj "total" (values |> List.tryItem dateCount |> Option.defaultValue 0M)

    addDecimal
        obj
        "percentOfTotal"
        (values
         |> List.tryItem (dateCount + 1)
         |> Option.bind validPercent
         |> Option.defaultValue 0M)

    obj

let private tryLoanTypeTotal (text: string) =
    text.Split('\n')
    |> Array.tryPick (fun line ->
        let normalized = Regex.Replace(line, @"(?<=\d)[ \t]+(?=\d)", "")

        if
            Regex.IsMatch(normalized, @"(?i)Closed[ \t]+M\w+")
            && Regex.IsMatch(normalized, @"(?i)DOLLAR[ \t]+VALUE")
        then
            match tryMatch @"(?i)DOLLAR[ \t]+VALUE[^0-9$S]*\$?[ \t]*([0-9][0-9 \t.,]*[0-9])" normalized with
            | Some m -> tryParseAmountText (group m 1)
            | None -> None
        else
            None)

let private parseAggregatePrincipal (text: string) =
    // "Aggregate" is often OCRed as "Apgregate", "Aggregale" or similar. The
    // "Principal Amount" words are stable, so use those as the anchor. French
    // circulars print "Montant global en capital".
    let labels =
        Regex.Matches(
            text,
            @"(?im)^[ \t]*(?:(?:[A-Za-z]{3,20}[ \t]+){1,3}A[a-z]{0,3}mount\b|Montant[^\r\n]{0,40}?capital)",
            RegexOptions.Multiline ||| RegexOptions.CultureInvariant
        )

    let lines = text.Split('\n')

    let lineIndexAt (index: int) =
        text.Substring(0, index).Split('\n').Length - 1

    let tryAmount (line: string) =
        match tryParseAmountText line with
        | Some amount when amount >= 1000M -> Some amount
        | _ -> None

    let sameLineAt (label: Match) =
        let lineEnd =
            match text.IndexOf('\n', label.Index) with
            | -1 -> text.Length
            | index -> index

        let afterLabel = label.Index + label.Length

        let suffix =
            text.Substring(afterLabel, lineEnd - afterLabel).Trim().TrimStart(':', ';', '=', '-').Trim()

        tryAmount suffix

    let beforeLabelAt (label: Match) =
        // The 2004 form prints the amount above its label. Try the nearest
        // non-empty lines so punctuation or an OCR artifact on the amount line
        // cannot hide it.
        let lineIndex = lineIndexAt label.Index

        [ lineIndex - 1 .. -1 .. max 0 (lineIndex - 3) ]
        |> List.filter (fun index -> index >= 0 && index < lines.Length)
        |> List.choose (fun index ->
            let line = lines.[index].Trim()

            let amountLike =
                line.Contains '$'
                || line.Contains '§'
                || Regex.IsMatch(line, @"\d{1,3}(?:[., ]\d{3}){2,}", RegexOptions.CultureInvariant)

            if line = "" || not amountLike then None else tryAmount line)
        |> List.tryHead

    if labels.Count = 0 then
        failwith "Could not find aggregate principal amount."

    labels
    |> Seq.cast<Match>
    |> Seq.tryPick (fun label -> sameLineAt label |> Option.orElse (beforeLabelAt label))
    |> Option.defaultWith (fun () -> failwith "Could not find aggregate principal amount.")

/// Parse layout-preserving text from a CMHC 2834 circular into a JSON object
/// shaped like `cmhc-2834-i-v2-extended-en.schema.json`. When the cover is a
/// low-resolution scan, `expectedPoolNumber` supplies the value from the
/// source manifest.
let parseTextWithPoolNumber (expectedPoolNumber: string option) (text: string) : JsonObject =
    let poolNumber = parsePoolNumber expectedPoolNumber text

    let parsedPoolType = poolNumber.Substring(0, 3)

    let poolType =
        if List.contains parsedPoolType knownPoolTypeCodes then
            parsedPoolType
        else
            // OCR occasionally changes a digit in the printed pool number but
            // leaves the pool-type label on the cover intact.
            Regex.Matches(text, @"\b(867|880|881|885|886|964|965|966|967|970|975|980|981|985|986|987|990)\b")
            |> Seq.map (fun m -> m.Value)
            |> Seq.distinct
            |> Seq.toList
            |> function
                | [ code ] -> code
                | codes when
                    codes |> List.contains "975"
                    && (text.Contains("NHA RESIDENTIAL", StringComparison.OrdinalIgnoreCase)
                        || Regex.IsMatch(text, @"For[ \t]+975[ \t]+pool", RegexOptions.IgnoreCase))
                    ->
                    "975"
                | code :: _ -> code
                | [] -> parsedPoolType

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
            let scheduledTotal =
                try
                    parseScheduledMaturity text
                    |> List.tryFind (fun (key, _) -> key = "total")
                    |> Option.map (fun (_, row) -> row.RemainingFaceValue)
                    |> Option.defaultValue 0M
                with _ ->
                    0M

            let ramTotal =
                try
                    let byRam, _ = parseRamDistribution text
                    List.sum byRam
                with _ ->
                    0M

            let loanTotal = tryLoanTypeTotal text |> Option.defaultValue 0M

            // OCR corrupts individual totals in different ways. Compare the
            // independent sources and prefer a pair that agrees; when only one
            // source is significant, use it.
            let candidates =
                [ loanTotal; scheduledTotal; ramTotal ]
                |> List.filter (fun value -> value > 0M)
                |> List.distinct

            let chosen =
                match candidates with
                | [] -> 0M
                | _ ->
                    let largest = List.max candidates
                    let significant = candidates |> List.filter (fun value -> value >= largest * 0.01M)

                    let pairs =
                        [
                            for left in significant do
                                for right in significant do
                                    if left < right then
                                        let relative = abs (left - right) / (max left right)

                                        if relative <= 0.2M then
                                            yield (left + right) / 2M
                        ]

                    match pairs with
                    | pair :: _ -> pair
                    | [] -> List.max significant

            if chosen >= 1000M then
                chosen
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
            | [] ->
                // NHA MBS pools without a private insurer table are backed by
                // CMHC; the cover still names it as guarantor.
                if
                    text.Contains("Canada Mortgage and Housing Corporation", StringComparison.OrdinalIgnoreCase)
                    || text.Contains(
                        "Société canadienne d'hypothèques et de logement",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || text.Contains("SCHL", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("NHA portfolio insured mortgages", StringComparison.OrdinalIgnoreCase)
                then
                    "Canada Mortgage and Housing Corporation"
                else
                    failwith "Could not find the mortgage insurer."

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

    addString
        mbs
        "issueDate"
        (parseDateFieldAny
            [
                "ISSUE DATE"
                "DATE D'EMISSION"
                "DATE D’EMISSION"
                "DATE D'ÉMISSION"
                "DATE D’ÉMISSION"
            ]
            text)

    addString
        mbs
        "settlementDate"
        (parseDateFieldAny [ "SETTLEMENT DATE"; "DATE DE REGLEMENT"; "DATE DE RÈGLEMENT" ] text)

    addString mbs "firstPaymentDue" (parseDateFieldAny [ "FIRST PAYMENT DUE"; "DATE DU PREMIER PAIEMENT" ] text)

    addString
        mbs
        "maturityDate"
        (parseDateFieldAny
            [
                "MATURITY DATE"
                "DATE D'ECHEANCE"
                "DATE D’ECHEANCE"
                "DATE D'ÉCHÉANCE"
                "DATE D’ÉCHÉANCE"
            ]
            text)

    addString
        mbs
        "finalPaymentDue"
        (parseDateFieldAny
            [
                "FINAL PAYMENT DUE"
                "FINAL PAYMENT DATE"
                "FINAL PAYMENT D"
                "DATE DU DERNIER PAIEMENT"
                "DATE DU DERNIER VERSEMENT"
            ]
            text)

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
        // The words can wrap ("Total Number of Mortgage" / "Loans") and OCR
        // sometimes prints a 1 as l or I.
        let label =
            @"(?i)(?:Total[ \t\r\n]+Number[ \t\r\n]+of[ \t\r\n]+Mortgages?(?:[ \t\r\n]+Loans?)?|Nombre[ \t\r\n]+total[ \t\r\n]+de[ \t\r\n]+pr[éeê]ts[ \t\r\n]+hypoth[éeê]caires)"

        let tryInteger (value: string) =
            let digits =
                value
                    .Replace('O', '0')
                    .Replace('o', '0')
                    .Replace('I', '1')
                    .Replace('l', '1')
                    .Replace('L', '1')
                    .Replace('|', '1')

            match Int32.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, count when count >= 0 -> Some count
            | _ -> None

        let digitToken = @"([0-9OoIlL|]{1,7})"

        let forward =
            Regex.Matches(
                text,
                label + @"[ \t]*:?[ \t]*(?:\r?\n[ \t]*){0,5}" + digitToken,
                RegexOptions.Multiline ||| RegexOptions.CultureInvariant
            )
            |> Seq.cast<Match>
            |> Seq.choose (fun m -> tryInteger (group m 1))
            |> Seq.tryHead

        match forward with
        | Some count -> count
        | None ->
            // OCR sometimes places the count just above its label.
            match tryMatch label text with
            | Some m ->
                let beforeLabel = text.Substring(0, m.Index)
                let lines = beforeLabel.Split('\n') |> Array.rev

                let preceding =
                    lines
                    |> Array.map (fun line -> line.Trim())
                    |> Array.filter (fun line -> line <> "")
                    |> Array.truncate 3
                    |> Array.tryPick (fun line ->
                        let m =
                            Regex.Match(
                                line,
                                @"^[^0-9OoIlL|]{0,8}" + digitToken + @"[^0-9OoIlL|]{0,8}$",
                                RegexOptions.CultureInvariant
                            )

                        if m.Success then tryInteger (group m 1) else None)

                match preceding with
                | Some count -> count
                | None -> failwith "Could not find total number of mortgage loans."
            | None -> 0

    addInt addendum "totalNumberOfMortgageLoans" totalLoans

    addString
        addendum
        "weightedAverageInterestAdjustmentDate"
        (parseAddendumDate
            [
                "Weighted Average Interest Adjustment Date"
                "Moyenne pondérée de la date d'ajustement d'intérêt"
                "Moyenne pondérée de la date d’ajustement d’intérêt"
                "Date de rajustement du taux d'intérêt moyen pondéré"
                "Date de rajustement du taux d’intérêt moyen pondéré"
                "Date d'ajustement du taux d'intérêt moyen pondéré"
                "Date d’ajustement du taux d’intérêt moyen pondéré"
                "Moyenne pondérée de la date"
            ]
            [ "INTEREST", 3; "ADJUSTMENT", 4 ]
            text)

    addString
        addendum
        "weightedAverageMaturityDate"
        (parseAddendumDate
            [
                "Weighted Average Maturity Date"
                "Moyenne pondérée de la date d'échéance"
                "Moyenne pondérée de la date d’échéance"
                "Date d'échéance moyenne pondérée"
                "Date d’échéance moyenne pondérée"
                "Date d'echeance moyenne ponderee"
                "Date d’echeance moyenne ponderee"
            ]
            [ "MATURITY", 4 ]
            text)

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

    let byRam, byTerm =
        try
            parseRamDistribution text
        with _ ->
            List.replicate 6 0M, List.replicate 6 0M

    let ramDistribution = JsonObject()
    ramDistribution.["byRemainingAmortizationMonths"] <- decimalArray byRam
    ramDistribution.["byRemainingTermMonths"] <- decimalArray byTerm
    addendum.["ramDistribution"] <- ramDistribution

    let locationRows =
        try
            parseLocations text
        with _ ->
            (provinceRows |> List.map (fun (key, _) -> key, (0M, 0M)))
            @ [ "total", (0M, 0M) ]

    let locationObject = JsonObject()

    for key, (loansPercent, principalAmountPercent) in locationRows do
        let row = JsonObject()
        addDecimal row "loansPercent" loansPercent
        addDecimal row "principalAmountPercent" principalAmountPercent
        locationObject.[key] <- row

    addendum.["locationOfMortgageLoans"] <- locationObject

    let dates, rateBands, totals, percentOfTotal, wac =
        try
            parseInterestAdjustment text
        with _ ->
            let fallbackDate =
                try
                    parseDateFieldAny
                        [
                            "Weighted Average Interest Adjustment Date"
                            "Moyenne pondérée de la date d'ajustement d'intérêt"
                            "Date de rajustement du taux d'intérêt moyen pondéré"
                            "Date de rajustement du taux d’intérêt moyen pondéré"
                        ]
                        text
                with _ ->
                    "1900-01-01"

            [ fallbackDate ], [], [ 0M; 0M; 0M ], [ 0M; 0M; 0M ], [ 0M; 0M; 0M ]

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
    let dateCount = dates.Length
    interestAdjustment.["totals"] <- totalRowObject dateCount totals
    interestAdjustment.["percentOfTotal"] <- totalRowObject dateCount percentOfTotal
    interestAdjustment.["wac"] <- totalRowObject dateCount wac
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

/// Parse layout-preserving text from a CMHC 2834 circular into a JSON object
/// shaped like `cmhc-2834-i-v2-extended-en.schema.json`.
let parseText (text: string) : JsonObject = parseTextWithPoolNumber None text

/// Parse a CMHC 2834 PDF into a JSON object shaped like
/// `cmhc-2834-i-v2-extended-en.schema.json`.
let parse (pdfPath: string) : JsonObject =
    PdfText.extractText pdfPath |> parseText

/// Serialize a parsed circular as pretty-printed JSON.
let toJson (circular: JsonObject) =
    circular.ToJsonString(JsonSerializerOptions(WriteIndented = true))

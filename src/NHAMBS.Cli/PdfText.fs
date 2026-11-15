module NHAMBS.Cli.PdfText

open System
open System.ComponentModel
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.RegularExpressions

// ---------------------------------------------------------------------------
// Process helpers
// ---------------------------------------------------------------------------

let private runProcess (fileName: string) (arguments: string list) =
    try
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- fileName

        for argument in arguments do
            startInfo.ArgumentList.Add argument

        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false

        use proc = Process.Start startInfo
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwithf "%s exited with code %d: %s" fileName proc.ExitCode (error.Trim())

        output
    with :? Win32Exception ->
        failwithf "%s was not found. Install it and ensure it is on PATH." fileName

/// Extract text from a PDF using Poppler's `pdftotext -layout`.
///
/// The CMHC circulars are flattened PDFs; their printed tables are much easier
/// to parse from layout-preserving text than from a coordinate stream. Poppler
/// is available in the current environment and is used as the extraction
/// backend. Swap this function for an iText/PdfPig layout extractor if the CLI
/// needs to be a single self-contained binary.
let extractLayout (path: string) : string =
    runProcess "pdftotext" [ "-layout"; path; "-" ]

// ---------------------------------------------------------------------------
// OCR fallback
// ---------------------------------------------------------------------------

let private tesseractExecutable =
    match Environment.GetEnvironmentVariable "NHAMBS_TESSERACT" with
    | null
    | "" -> "tesseract"
    | path -> path

let private hasCoverText (text: string) =
    // The label, not a prose mention of a "pool number".
    Regex.IsMatch(text, @"(?m)^[ \t\f]*POOL[ \t]+NO[.:]", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

let private hasAddendumText (text: string) =
    // Match the printed heading as a whole line; the body text also mentions
    // "Mortgage Pool Profile at Issue Date".
    Regex.IsMatch(
        text,
        @"(?m)^[ \t\f]*MORTGAGE[ \t]+(?:LOAN[ \t]+)?POOL[ \t]+PROFILE[ \t]+AT[ \t]+ISSUE[ \t]+DATE[ \t]*$",
        RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
    )
    || Regex.IsMatch(
        text,
        @"(?m)^[ \t\f]*LOCATION[ \t]+OF[ \t]+MORTGAGE(?:S|[ \t]+LOANS)[ \t]*$",
        RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
    )

let private pageCount (path: string) =
    let output = runProcess "pdfinfo" [ path ]

    match Regex.Match(output, @"(?m)^Pages:\s+(\d+)", RegexOptions.CultureInvariant) with
    | m when m.Success -> Int32.Parse(m.Groups.[1].Value, CultureInfo.InvariantCulture)
    | _ -> 1

let private renderPages (path: string) (first: int) (last: int) (directory: string) =
    let prefix = Path.Combine(directory, sprintf "page-%03d" first)

    runProcess
        "pdftoppm"
        [
            "-f"
            string first
            "-l"
            string last
            "-r"
            "300"
            "-gray"
            "-png"
            path
            prefix
        ]
    |> ignore

    Directory.GetFiles(directory, Path.GetFileName prefix + "*.png") |> Array.sort

type private OcrWord =
    {
        Block: int
        Paragraph: int
        Line: int
        Left: int
        Top: int
        Text: string
    }

let private parseOcrTsv (path: string) =
    File.ReadLines path
    |> Seq.skip 1
    |> Seq.choose (fun line ->
        let parts = line.Split('\t')

        if parts.Length < 12 || parts.[0] <> "5" || String.IsNullOrWhiteSpace parts.[11] then
            None
        else
            let tryInt (value: string) =
                match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, number -> Some number
                | _ -> None

            let confidence =
                match Double.TryParse(parts.[10], NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, value -> value
                | _ -> 0.0

            if confidence < 0.0 then
                None
            else
                match tryInt parts.[2], tryInt parts.[3], tryInt parts.[4], tryInt parts.[6], tryInt parts.[7] with
                | Some block, Some paragraph, Some lineNumber, Some left, Some top ->
                    Some
                        {
                            Block = block
                            Paragraph = paragraph
                            Line = lineNumber
                            Left = left
                            Top = top
                            Text = String.Join("\t", parts.[11..]).Trim()
                        }
                | _ -> None)
    |> Seq.toList

/// Reconstruct approximate fixed-width text from Tesseract's word boxes. The
/// parser relies on printed columns, so the pixel offsets are converted to
/// character columns rather than emitting one word per line.
let private layoutFromOcrWords (words: OcrWord list) =
    let charactersPerPixel = 1.0 / 12.0

    words
    |> List.groupBy (fun word -> word.Block, word.Paragraph, word.Line)
    |> List.map (fun (_, lineWords) ->
        let lineWords = lineWords |> List.sortBy (fun word -> word.Left)
        let builder = StringBuilder()
        let mutable cursor = 0

        for word in lineWords do
            let column = int (float word.Left * charactersPerPixel)

            if column > cursor then
                builder.Append(' ', column - cursor) |> ignore
            elif cursor > 0 then
                builder.Append(' ') |> ignore

            builder.Append(word.Text) |> ignore
            cursor <- builder.Length

        (lineWords |> List.map (fun word -> word.Top) |> List.min, builder.ToString().TrimEnd()))
    |> List.sortBy fst
    |> List.map snd
    |> String.concat "\n"

let private ocrImage (image: string) (directory: string) (index: int) =
    let outputBase = Path.Combine(directory, sprintf "ocr-%04d" index)

    runProcess tesseractExecutable [ image; outputBase; "--psm"; "3"; "-l"; "eng"; "tsv" ]
    |> ignore

    let tsvPath = outputBase + ".tsv"

    if File.Exists tsvPath then
        parseOcrTsv tsvPath |> layoutFromOcrWords
    else
        ""

let private fixTableUnits (line: string) =
    let looksLikeUnitRow =
        (line.Contains "(%)" || line.Contains "(%}" || line.Contains "{%)")
        && line.Contains("Month", StringComparison.OrdinalIgnoreCase)

    if not looksLikeUnitRow then
        line
    else
        line
            .Replace("(3)", "($)")
            .Replace("(S)", "($)")
            .Replace("Ch)", "($)")
            .Replace("(%}", "(%)")
            .Replace("{%)", "(%)")
            .Replace("{Months)", "(Months)")
            .Replace("(Months}", "(Months)")

let private normalizeOcr (text: string) =
    let months =
        [
            "Septembor", "September"
            "Septemver", "September"
            "Decernber", "December"
            "Decamber", "December"
            "Decamtber", "December"
            "Oecember", "December"
            "becember", "December"
            "Avgust", "August"
            "Judy", "July"
            "duly", "July"
            "suty", "July"
            "Mari", "Mar"
            "Maren", "Mar"
            "Apri", "Apr"
            "Aprl", "Apr"
            "Api", "Apr"
            "apm", "Apr"
            "Jane", "June"
            "Jurv", "Jun"
            "Sune", "Jun"
            "sune", "Jun"
            "dune", "Jun"
            "vune", "Jun"
            "Feburary", "February"
            "Febuary", "February"
            "Srptember", "September"
            "Welghted", "Weighted"
            "Maturlty", "Maturity"
            "MBS Maturlty", "MBS Maturity"
            "Sacial", "Social"
        ]

    let text = text.Replace("\r", "").Replace("%}", "%")

    let text = text.Split('\n') |> Array.map fixTableUnits |> String.concat "\n"

    let text =
        months
        |> List.fold
            (fun (value: string) (ocr, correct) ->
                Regex.Replace(
                    value,
                    @"\b" + Regex.Escape ocr + @"\b",
                    correct,
                    RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
                ))
            text

    // Years are sometimes read with a letter for the final digit.
    let text = Regex.Replace(text, @"\b201B\b", "2018", RegexOptions.CultureInvariant)

    // Insert a separator between an OCR-glued month and year ("Mar2018").
    let text =
        Regex.Replace(
            text,
            @"\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)(\d{4})\b",
            "$1 $2",
            RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
        )

    // Where OCR uses a comma as the decimal separator, convert it. Commas in
    // groups of three remain thousands separators.
    let text =
        Regex.Replace(text, @"(?<=\d),(?=\d{2}(?:\D|$))", ".", RegexOptions.CultureInvariant)

    Regex.Replace(text, @"(?<=\d),(?=\d{4}(?:\D|$))", ".", RegexOptions.CultureInvariant)

let private withTempDirectory (action: string -> string) =
    let directory =
        Path.Combine(Path.GetTempPath(), "nhambs-ocr-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore

    try
        action directory
    finally
        try
            Directory.Delete(directory, true)
        with _ ->
            ()

/// OCR a PDF page range and return approximate layout-preserving text.
let private extractOcrRange (path: string) (first: int) (last: int) (directory: string) =
    let imageFiles = renderPages path first last directory

    imageFiles
    |> Array.mapi (fun index image -> ocrImage image directory (first * 100 + index))
    |> Array.filter (fun text -> text <> "")
    |> String.concat "\n\f\n"

/// OCR only the pages that the circular parser needs: the cover (page 1) and
/// the addendum (usually pages 5-10, occasionally later or earlier).
let private extractOcr (path: string) (needCover: bool) (needAddendum: bool) =
    withTempDirectory (fun directory ->
        let pages = pageCount path

        let range first last =
            if first > pages then
                ""
            else
                extractOcrRange path first (min last pages) directory

        let mutable text = ""

        if needCover then
            // Cover fields and the insurer/custodian sentences run through
            // page 4 on many form revisions.
            text <- text + range 1 4

        if needAddendum then
            let addendum = range 5 10
            text <- text + addendum

            if not (hasAddendumText text) then
                text <- text + range 11 15

            if not (hasAddendumText text) then
                text <- text + range 3 5

        if needCover && not (hasCoverText text) then
            text <- text + range 3 4

        normalizeOcr text)

/// Serializes writes when `NHAMBS_DUMP_TEXT` is set, since batch mode can
/// extract several PDFs concurrently.
let private dumpTextLock = obj ()

/// Extract text from a text-based PDF, falling back to OCR when the cover or
/// addendum is a scanned image.
let extractText (path: string) : string =
    let layout = extractLayout path
    let hasCover = hasCoverText layout
    let hasAddendum = hasAddendumText layout

    let text =
        if hasCover && hasAddendum then
            layout
        else
            let ocr = extractOcr path (not hasCover) (not hasAddendum)

            if String.IsNullOrWhiteSpace ocr then layout
            elif not hasCover then ocr + "\n" + layout
            else layout + "\n" + ocr

    match Environment.GetEnvironmentVariable "NHAMBS_DUMP_TEXT" with
    | null
    | "" -> ()
    | output -> lock dumpTextLock (fun () -> File.WriteAllText(output, text))

    text

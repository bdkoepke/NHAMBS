module NHAMBS.Etl.ArrearsParser

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.RegularExpressions
open ExcelDataReader
open NHAMBS.Etl.ArrearsTypes

// ExcelDataReader asks for the legacy code pages even when reading XLSX
// metadata. Register the provider once for the whole assembly.
do Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

let private ensureCodePages () =
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

let private normalizeText (value: string) =
    value.Replace('\u00a0', ' ')
    |> fun text -> Regex.Replace(text, @"\s+", " ")
    |> fun text -> text.Trim()

let private normalizeKey (value: string) =
    let decomposed = normalizeText(value).Normalize(NormalizationForm.FormD)
    let builder = StringBuilder()

    for character in decomposed do
        if CharUnicodeInfo.GetUnicodeCategory character <> UnicodeCategory.NonSpacingMark then
            builder.Append(character) |> ignore

    let cleaned =
        builder.ToString().ToLowerInvariant()
        |> fun text -> Regex.Replace(text, @"[^a-z0-9]+", " ")
        |> fun text -> text.Trim()

    Regex.Replace(cleaned, @"\s+", " ")

let private slug (value: string) =
    normalizeKey value |> fun text -> text.Replace(' ', '-')

let private cellAt (row: obj[]) index =
    if index >= 0 && index < row.Length then
        row.[index]
    else
        null

let private cellText (value: obj) =
    match value with
    | null -> ""
    | :? string as text -> normalizeText text
    | :? DateTime as date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    | :? double as number -> number.ToString("R", CultureInfo.InvariantCulture)
    | :? decimal as number -> number.ToString(CultureInfo.InvariantCulture)
    | :? IFormattable as value -> value.ToString(null, CultureInfo.InvariantCulture)
    | _ -> string value

let private tryDecimal (value: obj) =
    match value with
    | null -> None
    | :? decimal as number -> Some number
    | :? double as number -> Some(decimal number)
    | :? single as number -> Some(decimal number)
    | :? int as number -> Some(decimal number)
    | :? int64 as number -> Some(decimal number)
    | :? string as text ->
        let cleaned =
            normalizeText text |> fun value -> value.Replace(",", "").Replace("%", "")

        if cleaned = "" || cleaned = "*" then
            None
        else
            match Decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture) with
            | true, number -> Some number
            | _ -> None
    | _ -> None

let private tryDate (value: obj) =
    let plausibleDate (date: DateTime) = date.Year >= 1900 && date.Year <= 2100

    match value with
    | :? DateTime as date when plausibleDate date -> Some date
    | :? double as serial when serial >= 20000.0 && serial <= 80000.0 ->
        let date = DateTime.FromOADate serial

        if plausibleDate date then Some date else None
    | :? decimal as serial when serial >= 20000m && serial <= 80000m ->
        let date = DateTime.FromOADate(float serial)

        if plausibleDate date then Some date else None
    | :? string as text ->
        let text = normalizeText text

        if Regex.IsMatch(text, @"^[0-9]{4}$") then
            None
        else
            match DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces) with
            | true, date when plausibleDate date -> Some date
            | _ -> None
    | _ -> None

let private monthStart (date: DateTime) = DateTime(date.Year, date.Month, 1)

let private monthEnd (date: DateTime) =
    monthStart(date).AddMonths(1).AddDays(-1.0)

let private quarterPeriod (year: int) (quarter: int) =
    let start = DateTime(year, (quarter - 1) * 3 + 1, 1)
    start, start.AddMonths(3).AddDays(-1.0)

let private tryQuarter (value: obj) =
    let text = cellText value

    let matched =
        Regex.Match(text, @"^(?<year>[0-9]{4})[ ]*Q(?<quarter>[1-4])$", RegexOptions.IgnoreCase)

    if matched.Success then
        Some(Int32.Parse(matched.Groups.["year"].Value), Int32.Parse(matched.Groups.["quarter"].Value))
    else
        None

let private readWorkbook (path: string) =
    use stream = File.OpenRead path
    use reader = ExcelReaderFactory.CreateReader stream
    let sheets = ResizeArray<string * obj[][]>()
    let mutable more = true

    while more do
        let name = reader.Name
        let rows = ResizeArray<obj[]>()

        while reader.Read() do
            rows.Add(Array.init reader.FieldCount (fun index -> reader.GetValue index))

        sheets.Add(name, rows.ToArray())
        more <- reader.NextResult()

    sheets.ToArray()

// ---------------------------------------------------------------------------
// Canadian Bankers Association
// ---------------------------------------------------------------------------

let private cbaGeographies =
    [
        "CAN",
        {
            Code = "canada"
            Name = "Canada"
            Level = "country"
            ProvinceCode = None
            SortOrder = 0
        }
        "AC",
        {
            Code = "atlantic"
            Name = "Atlantic"
            Level = "region"
            ProvinceCode = None
            SortOrder = 50
        }
        "QC",
        {
            Code = "quebec"
            Name = "Quebec"
            Level = "province"
            ProvinceCode = Some "quebec"
            SortOrder = 14
        }
        "ON",
        {
            Code = "ontario"
            Name = "Ontario"
            Level = "province"
            ProvinceCode = Some "ontario"
            SortOrder = 15
        }
        "MB",
        {
            Code = "manitoba"
            Name = "Manitoba"
            Level = "province"
            ProvinceCode = Some "manitoba"
            SortOrder = 16
        }
        "SK",
        {
            Code = "saskatchewan"
            Name = "Saskatchewan"
            Level = "province"
            ProvinceCode = Some "saskatchewan"
            SortOrder = 17
        }
        "AB",
        {
            Code = "alberta"
            Name = "Alberta"
            Level = "province"
            ProvinceCode = Some "alberta"
            SortOrder = 18
        }
        "BC",
        {
            Code = "britishColumbia"
            Name = "British Columbia"
            Level = "province"
            ProvinceCode = Some "britishColumbia"
            SortOrder = 19
        }
        "Terr.",
        {
            Code = "territories"
            Name = "Territories"
            Level = "region"
            ProvinceCode = None
            SortOrder = 51
        }
    ]
    |> dict

let private cbaObservation
    (sourceCode: string)
    (periodStart: DateTime)
    (periodEnd: DateTime)
    (geography: ArrearsGeography)
    (metric: string)
    (value: decimal)
    =
    {
        SourceCode = sourceCode
        PeriodType = "monthly"
        PeriodStart = periodStart
        PeriodEnd = periodEnd
        GeographyCode = geography.Code
        LenderType = "chartered_banks"
        InsuredStatus = "all"
        ProductType = "residential_mortgage"
        ArrearsStage = "90_plus_days"
        Metric = metric
        Value = value
        IsSuppressed = false
    }

let private parseCbaRows (geography: ArrearsGeography) (rows: obj[][]) =
    let observations = ResizeArray<ArrearsObservation>()
    let mutable suppressed = 0

    for row in rows do
        for dateIndex, totalIndex, arrearsIndex, rateIndex in [ 0, 1, 2, 3; 5, 6, 7, 8 ] do
            match tryDate (cellAt row dateIndex) with
            | None -> ()
            | Some date ->
                // The two column blocks use either month-end or first-of-month
                // dates. Normalise both to the calendar month.
                let periodEnd = monthEnd date
                let periodStart = monthStart date

                match tryDecimal (cellAt row totalIndex) with
                | Some value ->
                    observations.Add(cbaObservation "cba" periodStart periodEnd geography "mortgage_count" value)
                | None -> ()

                match tryDecimal (cellAt row arrearsIndex) with
                | Some value ->
                    observations.Add(cbaObservation "cba" periodStart periodEnd geography "arrears_count" value)
                | None -> suppressed <- suppressed + 1

                match tryDecimal (cellAt row rateIndex) with
                | Some value ->
                    observations.Add(
                        cbaObservation "cba" periodStart periodEnd geography "arrears_rate_percent" (value * 100m)
                    )
                | None -> suppressed <- suppressed + 1

    observations.ToArray(), suppressed

/// Parse the CBA "Current and Historical Mortgage Arrears Data" workbook.
let parseCba (path: string) : ArrearsParseResult =
    ensureCodePages ()
    let sheets = readWorkbook path
    let observations = ResizeArray<ArrearsObservation>()
    let geographies = ResizeArray<ArrearsGeography>()
    let warnings = ResizeArray<ArrearsWarning>()
    let mutable suppressed = 0

    for sheetName, rows in sheets do
        match cbaGeographies.TryGetValue sheetName with
        | true, geography ->
            geographies.Add geography
            let parsed, sheetSuppressed = parseCbaRows geography rows
            observations.AddRange parsed
            suppressed <- suppressed + sheetSuppressed
        | _ ->
            warnings.Add(
                {
                    SourceCode = "cba"
                    Geography = sheetName
                    Period = ""
                    Message = "Unrecognised CBA worksheet; ignored."
                }
            )

    let deduplicated =
        observations.ToArray()
        |> Array.distinctBy (fun observation ->
            observation.SourceCode, observation.PeriodEnd, observation.GeographyCode, observation.Metric)

    {
        Observations = deduplicated
        Geographies = geographies.ToArray()
        Warnings = warnings.ToArray()
        Suppressed = suppressed
        LatestPeriodEnd =
            deduplicated
            |> Array.map (fun observation -> observation.PeriodEnd)
            |> Array.sortDescending
            |> Array.tryHead
    }

// ---------------------------------------------------------------------------
// Canada Mortgage and Housing Corporation
// ---------------------------------------------------------------------------

let private cmhcProvinceByKey (name: string) =
    match normalizeKey name with
    | "newfoundland"
    | "newfoundland and labrador" -> Some "newfoundland"
    | "prince edward island" -> Some "princeEdwardIsland"
    | "nova scotia" -> Some "novaScotia"
    | "new brunswick" -> Some "newBrunswick"
    | "quebec" -> Some "quebec"
    | "ontario" -> Some "ontario"
    | "manitoba" -> Some "manitoba"
    | "saskatchewan" -> Some "saskatchewan"
    | "alberta" -> Some "alberta"
    | "british columbia" -> Some "britishColumbia"
    | _ -> None

let private cmhcObservation
    (periodStart: DateTime)
    (periodEnd: DateTime)
    (geography: ArrearsGeography)
    (value: decimal)
    =
    {
        SourceCode = "cmhc"
        PeriodType = "quarterly"
        PeriodStart = periodStart
        PeriodEnd = periodEnd
        GeographyCode = geography.Code
        LenderType = "all_lenders"
        InsuredStatus = "all"
        ProductType = "residential_mortgage"
        ArrearsStage = "90_plus_days"
        Metric = "arrears_rate_percent"
        Value = value
        IsSuppressed = false
    }

/// Parse the CMHC "Mortgage Delinquency Rate: Canada, Provinces and CMAs"
/// workbook. The sheet has one row per geography and one column per quarter.
let parseCmhc (path: string) : ArrearsParseResult =
    ensureCodePages ()
    let sheets = readWorkbook path
    let observations = ResizeArray<ArrearsObservation>()
    let geographies = ResizeArray<ArrearsGeography>()
    let warnings = ResizeArray<ArrearsWarning>()
    let mutable suppressed = 0

    let sheet =
        sheets
        |> Array.tryFind (fun (name, _) -> name.ToLowerInvariant().Contains "delinquency")
        |> Option.orElse (sheets |> Array.tryHead)

    match sheet with
    | None ->
        { ArrearsParseResult.empty with
            Warnings =
                [|
                    {
                        SourceCode = "cmhc"
                        Geography = ""
                        Period = ""
                        Message = "The workbook has no worksheets."
                    }
                |]
        }
    | Some(_, rows) ->
        let headerIndex =
            rows
            |> Array.tryFindIndex (fun row ->
                row |> Array.exists (fun value -> normalizeKey (cellText value) = "geography"))

        match headerIndex with
        | None ->
            { ArrearsParseResult.empty with
                Warnings =
                    [|
                        {
                            SourceCode = "cmhc"
                            Geography = ""
                            Period = ""
                            Message = "Could not find the Geography header row."
                        }
                    |]
            }
        | Some headerIndex ->
            let header = rows.[headerIndex]

            let quarters =
                header
                |> Array.mapi (fun index value -> index, tryQuarter value)
                |> Array.choose (fun (index, value) ->
                    match value with
                    | Some(year, quarter) -> Some(index, year, quarter)
                    | None -> None)

            let mutable section = "country"
            let mutable cmaIndex = 0

            for rowIndex in headerIndex + 1 .. rows.Length - 1 do
                let row = rows.[rowIndex]
                let geographyName = cellText (cellAt row 1)

                if geographyName.StartsWith("Source:", StringComparison.OrdinalIgnoreCase) then
                    section <- "done"
                elif section <> "done" && geographyName <> "" then
                    let key = normalizeKey geographyName

                    if key = "provinces" then
                        section <- "province"
                    elif key.StartsWith("selected metropolitan areas", StringComparison.Ordinal) then
                        section <- "cma"
                    elif key.StartsWith("notes", StringComparison.Ordinal) then
                        section <- "done"
                    else
                        let geography =
                            match section with
                            | "country" ->
                                Some
                                    {
                                        Code = "canada"
                                        Name = "Canada"
                                        Level = "country"
                                        ProvinceCode = None
                                        SortOrder = 0
                                    }
                            | "province" ->
                                cmhcProvinceByKey geographyName
                                |> Option.map (fun code ->
                                    {
                                        Code = code
                                        Name = geographyName
                                        Level = "province"
                                        ProvinceCode = Some code
                                        SortOrder = 10
                                    })
                            | "cma" ->
                                Some
                                    {
                                        Code = "cma_" + slug geographyName
                                        Name = geographyName
                                        Level = "cma"
                                        ProvinceCode = None
                                        SortOrder = 100 + cmaIndex
                                    }
                            | _ -> None

                        match geography with
                        | None ->
                            warnings.Add(
                                {
                                    SourceCode = "cmhc"
                                    Geography = geographyName
                                    Period = ""
                                    Message = "Unrecognised CMHC geography; ignored."
                                }
                            )
                        | Some geography ->
                            if section = "cma" then
                                cmaIndex <- cmaIndex + 1

                            geographies.Add geography

                            for columnIndex, year, quarter in quarters do
                                match tryDecimal (cellAt row columnIndex) with
                                | Some value ->
                                    let periodStart, periodEnd = quarterPeriod year quarter

                                    observations.Add(cmhcObservation periodStart periodEnd geography value)
                                | None -> suppressed <- suppressed + 1

            let deduplicated =
                observations.ToArray()
                |> Array.distinctBy (fun observation ->
                    observation.PeriodEnd, observation.GeographyCode, observation.Metric)

            {
                Observations = deduplicated
                Geographies = geographies.ToArray()
                Warnings = warnings.ToArray()
                Suppressed = suppressed
                LatestPeriodEnd =
                    deduplicated
                    |> Array.map (fun observation -> observation.PeriodEnd)
                    |> Array.sortDescending
                    |> Array.tryHead
            }

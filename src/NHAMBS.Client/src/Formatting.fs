module NHAMBS.Client.Formatting

open System

let private monthNames =
    [|
        "January"
        "February"
        "March"
        "April"
        "May"
        "June"
        "July"
        "August"
        "September"
        "October"
        "November"
        "December"
    |]

let private shortMonths =
    [|
        "Jan"
        "Feb"
        "Mar"
        "Apr"
        "May"
        "Jun"
        "Jul"
        "Aug"
        "Sep"
        "Oct"
        "Nov"
        "Dec"
    |]

let number (value: decimal) = value.ToString("N0")
let number1 (value: decimal) = value.ToString("N1")
let number2 (value: decimal) = value.ToString("N2")

let moneyCompact (value: decimal) =
    let absolute = abs value
    let sign = if value < 0m then "-" else ""

    let format (divisor: decimal) (suffix: string) (decimals: int) =
        let scaled = absolute / divisor

        let text =
            if decimals = 0 then
                scaled.ToString("N0")
            else
                scaled.ToString("N" + string decimals)

        sprintf "%s$%s%s" sign text suffix

    if absolute >= 1_000_000_000_000m then
        format 1_000_000_000_000m "T" 2
    elif absolute >= 1_000_000_000m then
        format 1_000_000_000m "B" 2
    elif absolute >= 1_000_000m then
        format 1_000_000m "M" 1
    elif absolute >= 1_000m then
        format 1_000m "K" 0
    else
        sprintf "%s$%s" sign (absolute.ToString("N0"))

let numberCompact (value: decimal) =
    let absolute = abs value
    let sign = if value < 0m then "-" else ""

    let format (divisor: decimal) (suffix: string) (decimals: int) =
        let scaled = absolute / divisor

        let text =
            if decimals = 0 then
                scaled.ToString("N0")
            else
                scaled.ToString("N" + string decimals)

        sprintf "%s%s%s" sign text suffix

    if absolute >= 1_000_000_000_000m then
        format 1_000_000_000_000m "T" 2
    elif absolute >= 1_000_000_000m then
        format 1_000_000_000m "B" 2
    elif absolute >= 1_000_000m then
        format 1_000_000m "M" 1
    elif absolute >= 1_000m then
        format 1_000m "K" 0
    else
        absolute.ToString("N0")

/// CHT issuance is published in millions of CAD.
let moneyFromMillions (millions: decimal) = moneyCompact (millions * 1_000_000m)

let moneyFull (value: decimal) = sprintf "$%s" (value.ToString("N2"))

let percent (value: decimal) = sprintf "%s%%" (value.ToString("N2"))

let percent1 (value: decimal) = sprintf "%s%%" (value.ToString("N1"))

let rate (value: decimal) =
    if value = 0m then
        "—"
    else
        sprintf "%s%%" (value.ToString("N3"))

let months (value: decimal) =
    if value = 0m then
        "—"
    else
        sprintf "%s mo" (value.ToString("N0"))

let dateShort (value: string) =
    if String.IsNullOrWhiteSpace value || value.Length < 10 then
        "—"
    else
        match
            Int32.TryParse(value.Substring(0, 4)),
            Int32.TryParse(value.Substring(5, 2)),
            Int32.TryParse(value.Substring(8, 2))
        with
        | (true, year), (true, month), (true, day) when month >= 1 && month <= 12 ->
            sprintf "%s %d, %d" shortMonths.[month - 1] day year
        | _ -> value

let monthLabel (value: string) =
    if String.IsNullOrWhiteSpace value || value.Length < 7 then
        value
    else
        match Int32.TryParse(value.Substring(0, 4)), Int32.TryParse(value.Substring(5, 2)) with
        | (true, year), (true, month) when month >= 1 && month <= 12 -> sprintf "%s %d" shortMonths.[month - 1] year
        | _ -> value

let truncate (maxLength: int) (value: string) =
    if String.IsNullOrWhiteSpace value then ""
    elif value.Length <= maxLength then value
    else value.Substring(0, maxLength - 1) + "…"

let titleCase (value: string) =
    if String.IsNullOrWhiteSpace value then
        ""
    else
        value.Substring(0, 1).ToUpperInvariant() + value.Substring(1)

let poolTypeLabel (code: string) (description: string) =
    if String.IsNullOrWhiteSpace code then
        description
    else
        sprintf "%s · %s" code description

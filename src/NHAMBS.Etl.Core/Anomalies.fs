module NHAMBS.Etl.Anomalies

open System
open System.Globalization

/// A data-quality issue found while loading an ETL record.
type Anomaly =
    {
        Dataset: string
        RecordKey: string
        Field: string
        RawValue: string
        CorrectedValue: string option
        Severity: string
        Message: string
    }

/// Date fields checked when loading an NHA MBS pool.
type MbsPoolDates =
    {
        IssueDate: DateTime option
        SettlementDate: DateTime option
        FirstPaymentDue: DateTime option
        MaturityDate: DateTime option
        FinalPaymentDue: DateTime option
    }

type MbsDateNormalization =
    {
        Dates: MbsPoolDates
        Anomalies: Anomaly list
    }

let private dateText (value: DateTime) =
    value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let private isFuture (today: DateTime) (value: DateTime) = value.Date > today.Date

/// Detects impossible future dates and repairs obvious century/year typos
/// using the settlement date as an anchor. For example, an issue date of
/// 2103-12-01 with a settlement date of 2013-12-19 becomes 2013-12-01.
///
/// A future issue date that cannot be repaired is cleared so the grid never
/// presents a pool with a future issue date; the original value is retained in
/// the anomaly record.
let normalizeMbsDates (dataset: string) (recordKey: string) (today: DateTime) (dates: MbsPoolDates) =
    let anomalies = ResizeArray<Anomaly>()

    let add field rawValue correctedValue severity message =
        anomalies.Add(
            {
                Dataset = dataset
                RecordKey = recordKey
                Field = field
                RawValue = rawValue
                CorrectedValue = correctedValue
                Severity = severity
                Message = message
            }
        )

    let originalIssueDate = dates.IssueDate

    let issueDate, offsetYears =
        match originalIssueDate with
        | Some issue when isFuture today issue ->
            let offset =
                match dates.SettlementDate with
                | Some settlement when not (isFuture today settlement) && issue.Year > settlement.Year ->
                    let years = issue.Year - settlement.Year
                    let candidate = issue.AddYears(-years)

                    if
                        candidate.Date <= today.Date
                        && candidate.Date <= settlement.Date
                        && candidate.Date >= settlement.AddYears(-1)
                    then
                        Some years
                    else
                        None
                | _ -> None

            match offset with
            | Some years ->
                let corrected = issue.AddYears(-years)

                let settlementText =
                    dates.SettlementDate |> Option.map dateText |> Option.defaultValue "unknown"

                add
                    "issue_date"
                    (dateText issue)
                    (Some(dateText corrected))
                    "corrected"
                    (sprintf "Future issue date corrected by %d years using settlement date %s." years settlementText)

                Some corrected, Some years
            | None ->
                add "issue_date" (dateText issue) None "error" "Issue date is in the future and could not be corrected."

                None, None
        | _ -> originalIssueDate, None

    let adjustWithIssueOffset field (value: DateTime option) =
        match value, offsetYears, originalIssueDate with
        | Some date, Some years, Some issue when date.Year >= issue.Year ->
            let corrected = date.AddYears(-years)

            add
                field
                (dateText date)
                (Some(dateText corrected))
                "corrected"
                (sprintf "Future %s corrected by %d years to match the corrected issue date." field years)

            Some corrected
        | _ -> value

    let firstPaymentDue =
        adjustWithIssueOffset "first_payment_due" dates.FirstPaymentDue

    let maturityDate = adjustWithIssueOffset "maturity_date" dates.MaturityDate

    let finalPaymentDue =
        adjustWithIssueOffset "final_payment_due" dates.FinalPaymentDue

    let checkImplausibleFuture field (value: DateTime option) =
        match value with
        | Some date when date.Year > today.Year + 50 ->
            add field (dateText date) None "error" (sprintf "%s is implausibly far in the future." field)
        | _ -> ()

    checkImplausibleFuture "first_payment_due" firstPaymentDue
    checkImplausibleFuture "maturity_date" maturityDate
    checkImplausibleFuture "final_payment_due" finalPaymentDue

    match issueDate, dates.SettlementDate with
    | Some issue, Some settlement when issue.Date > settlement.Date ->
        add
            "issue_date"
            (dateText issue)
            None
            "warning"
            (sprintf "Issue date is after settlement date %s." (dateText settlement))
    | _ -> ()

    match issueDate, maturityDate with
    | Some issue, Some maturity when maturity.Date < issue.Date ->
        add
            "maturity_date"
            (dateText maturity)
            None
            "error"
            (sprintf "Maturity date is before issue date %s." (dateText issue))
    | _ -> ()

    match issueDate, firstPaymentDue with
    | Some issue, Some firstPayment when firstPayment.Date < issue.Date ->
        add
            "first_payment_due"
            (dateText firstPayment)
            None
            "warning"
            (sprintf "First payment due is before issue date %s." (dateText issue))
    | _ -> ()

    {
        Dates =
            {
                IssueDate = issueDate
                SettlementDate = dates.SettlementDate
                FirstPaymentDue = firstPaymentDue
                MaturityDate = maturityDate
                FinalPaymentDue = finalPaymentDue
            }
        Anomalies = List.ofSeq anomalies
    }

/// The longest plausible weighted-average remaining amortization for an NHA MBS
/// pool. Fifty years is deliberately generous; values beyond it are parsing
/// artifacts (for example, a PDF field that concatenates two numbers) and no
/// longer fit the NUMERIC(10, 5) column used for the pool header.
let MaxMbsAmortizationMonths = 600m

let isPlausibleMbsAmortization (months: decimal) =
    months >= 0m && months <= MaxMbsAmortizationMonths

/// The longest plausible weighted-average introductory period remaining on the
/// loans in an NHA MBS pool, in months. The same generous fifty-year bound is
/// used here because the introductory period cannot outlast the amortization.
let MaxMbsIntroductoryMonths = 600m

let isPlausibleMbsIntroductoryMonths (months: decimal) =
    months >= 0m && months <= MaxMbsIntroductoryMonths

let private normalizeMbsMonths
    (maxMonths: decimal)
    (description: string)
    (dataset: string)
    (recordKey: string)
    (field: string)
    (months: decimal option)
    =
    match months with
    | Some value when value < 0m || value > maxMonths ->
        let rawValue = value.ToString(CultureInfo.InvariantCulture)

        None,
        Some
            {
                Dataset = dataset
                RecordKey = recordKey
                Field = field
                RawValue = rawValue
                CorrectedValue = None
                Severity = "error"
                Message =
                    sprintf
                        "%s of %s months is outside the plausible 0-%s month range; value cleared."
                        description
                        rawValue
                        (maxMonths.ToString(CultureInfo.InvariantCulture))
            }
    | _ -> months, None

/// Clears an implausible weighted-average remaining amortization and records an
/// anomaly so the original value remains visible in nhambs.etl_anomalies.
let normalizeMbsAmortization =
    normalizeMbsMonths MaxMbsAmortizationMonths "Weighted-average remaining amortization"

/// Clears an implausible weighted-average introductory period and records an
/// anomaly so the original value remains visible in nhambs.etl_anomalies.
let normalizeMbsIntroductoryMonths =
    normalizeMbsMonths MaxMbsIntroductoryMonths "Weighted-average introductory period remaining"

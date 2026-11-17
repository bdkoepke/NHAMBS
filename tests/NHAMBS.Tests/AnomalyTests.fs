module NHAMBS.AnomalyTests

open System
open Xunit
open NHAMBS.Etl.Anomalies

let private dates issueDate settlementDate firstPaymentDue maturityDate finalPaymentDue =
    {
        IssueDate = issueDate
        SettlementDate = settlementDate
        FirstPaymentDue = firstPaymentDue
        MaturityDate = maturityDate
        FinalPaymentDue = finalPaymentDue
    }

[<Fact>]
let ``Future issue date is repaired from the settlement date`` () =
    let poolDates =
        dates
            (Some(DateTime(2103, 12, 1)))
            (Some(DateTime(2013, 12, 19)))
            (Some(DateTime(2104, 1, 15)))
            (Some(DateTime(2018, 12, 1)))
            (Some(DateTime(2018, 12, 15)))

    let normalized =
        normalizeMbsDates "mbs" "62935ZY71" (DateTime(2026, 11, 15)) poolDates

    Assert.Equal(Some(DateTime(2013, 12, 1)), normalized.Dates.IssueDate)
    Assert.Equal(Some(DateTime(2014, 1, 15)), normalized.Dates.FirstPaymentDue)
    Assert.Equal(Some(DateTime(2018, 12, 1)), normalized.Dates.MaturityDate)

    Assert.Contains(
        normalized.Anomalies,
        fun anomaly ->
            anomaly.Field = "issue_date"
            && anomaly.Severity = "corrected"
            && anomaly.CorrectedValue = Some "2013-12-01"
    )

[<Fact>]
let ``Unrepairable future issue date is cleared and recorded as an error`` () =
    let poolDates =
        dates
            (Some(DateTime(2030, 1, 1)))
            (Some(DateTime(2030, 1, 15)))
            (Some(DateTime(2030, 2, 1)))
            (Some(DateTime(2040, 1, 1)))
            (Some(DateTime(2040, 1, 15)))

    let normalized =
        normalizeMbsDates "mbs" "TEST0001" (DateTime(2026, 11, 15)) poolDates

    Assert.Equal(None, normalized.Dates.IssueDate)
    Assert.Contains(normalized.Anomalies, fun anomaly -> anomaly.Field = "issue_date" && anomaly.Severity = "error")

[<Fact>]
let ``Plausible dates produce no anomalies`` () =
    let poolDates =
        dates
            (Some(DateTime(2020, 1, 1)))
            (Some(DateTime(2020, 1, 10)))
            (Some(DateTime(2020, 2, 1)))
            (Some(DateTime(2025, 1, 1)))
            (Some(DateTime(2025, 1, 15)))

    let normalized =
        normalizeMbsDates "mbs" "TEST0002" (DateTime(2026, 11, 15)) poolDates

    Assert.Empty(normalized.Anomalies)
    Assert.Equal(poolDates, normalized.Dates)

[<Fact>]
let ``Future maturity dates for active pools are not anomalies`` () =
    let poolDates =
        dates
            (Some(DateTime(2020, 1, 1)))
            (Some(DateTime(2020, 1, 10)))
            (Some(DateTime(2020, 2, 1)))
            (Some(DateTime(2032, 1, 1)))
            (Some(DateTime(2032, 1, 15)))

    let normalized =
        normalizeMbsDates "mbs" "TEST0004" (DateTime(2026, 11, 15)) poolDates

    Assert.Empty(normalized.Anomalies)

[<Fact>]
let ``Implausible amortization is cleared and recorded as an error`` () =
    let value, anomaly =
        normalizeMbsAmortization "mbs" "62917ZYQ1" "scheduled_wam" (Some 118715104.4m)

    Assert.Equal(None, value)
    Assert.True(anomaly.IsSome)

    let recorded = anomaly.Value

    Assert.Equal("scheduled_wam", recorded.Field)
    Assert.Equal("error", recorded.Severity)
    Assert.Equal("118715104.4", recorded.RawValue)
    Assert.Equal(None, recorded.CorrectedValue)

[<Fact>]
let ``Plausible amortization is retained without an anomaly`` () =
    let value, anomaly =
        normalizeMbsAmortization "mbs" "TEST0006" "scheduled_wam" (Some 281.934m)

    Assert.Equal(Some 281.934m, value)
    Assert.True(anomaly.IsNone)

[<Fact>]
let ``Implausible introductory period is cleared and recorded as an error`` () =
    let value, anomaly =
        normalizeMbsIntroductoryMonths "mbs" "62918ZE58" "scheduled_introductory_months" (Some 250203.19m)

    Assert.Equal(None, value)
    Assert.True(anomaly.IsSome)

    let recorded = anomaly.Value

    Assert.Equal("scheduled_introductory_months", recorded.Field)
    Assert.Equal("error", recorded.Severity)
    Assert.Equal("250203.19", recorded.RawValue)
    Assert.Equal(None, recorded.CorrectedValue)

[<Fact>]
let ``Plausible introductory period is retained without an anomaly`` () =
    let value, anomaly =
        normalizeMbsIntroductoryMonths "mbs" "TEST0007" "scheduled_introductory_months" (Some 36m)

    Assert.Equal(Some 36m, value)
    Assert.True(anomaly.IsNone)

[<Fact>]
let ``Implausibly distant maturity date is recorded as an error`` () =
    let poolDates =
        dates
            (Some(DateTime(2020, 1, 1)))
            (Some(DateTime(2020, 1, 10)))
            (Some(DateTime(2020, 2, 1)))
            (Some(DateTime(2100, 1, 1)))
            (Some(DateTime(2100, 1, 15)))

    let normalized =
        normalizeMbsDates "mbs" "TEST0005" (DateTime(2026, 11, 15)) poolDates

    Assert.Contains(normalized.Anomalies, fun anomaly -> anomaly.Field = "maturity_date" && anomaly.Severity = "error")

[<Fact>]
let ``OCR year 2610 is repaired from the final payment date`` () =
    let poolDates =
        dates
            (Some(DateTime(2007, 6, 1)))
            (Some(DateTime(2007, 6, 27)))
            (Some(DateTime(2007, 7, 15)))
            (Some(DateTime(2610, 11, 1)))
            (Some(DateTime(2010, 11, 15)))

    let normalized =
        normalizeMbsDates "mbs" "62918ZUW1" (DateTime(2026, 11, 15)) poolDates

    Assert.Equal(Some(DateTime(2010, 11, 1)), normalized.Dates.MaturityDate)

    Assert.Contains(
        normalized.Anomalies,
        fun anomaly ->
            anomaly.Field = "maturity_date"
            && anomaly.Severity = "corrected"
            && anomaly.CorrectedValue = Some "2010-11-01"
    )

[<Fact>]
let ``OCR year 0201 is repaired from the final payment date`` () =
    let poolDates =
        dates
            (Some(DateTime(2007, 6, 1)))
            (Some(DateTime(2007, 6, 27)))
            (Some(DateTime(2007, 7, 15)))
            (Some(DateTime(201, 9, 1)))
            (Some(DateTime(2011, 9, 15)))

    let normalized =
        normalizeMbsDates "mbs" "62918ZUN1" (DateTime(2026, 11, 15)) poolDates

    Assert.Equal(Some(DateTime(2011, 9, 1)), normalized.Dates.MaturityDate)

    Assert.Contains(
        normalized.Anomalies,
        fun anomaly ->
            anomaly.Field = "maturity_date"
            && anomaly.Severity = "corrected"
            && anomaly.CorrectedValue = Some "2011-09-01"
    )

[<Fact>]
let ``Maturity before issue date is recorded as an error`` () =
    let poolDates =
        dates
            (Some(DateTime(2020, 1, 1)))
            (Some(DateTime(2020, 1, 10)))
            (Some(DateTime(2020, 2, 1)))
            (Some(DateTime(2019, 1, 1)))
            (Some(DateTime(2019, 1, 15)))

    let normalized =
        normalizeMbsDates "mbs" "TEST0003" (DateTime(2026, 11, 15)) poolDates

    Assert.Contains(normalized.Anomalies, fun anomaly -> anomaly.Field = "maturity_date" && anomaly.Severity = "error")

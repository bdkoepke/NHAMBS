# NHAMBS.Cht

Typed mapping and validation for CMHC Canada Housing Trust (CHT) JSON.

## What is here

- `schema/cmhc-cht-debt-issues.schema.json` — the authoritative JSON Schema
  (Draft 2020-12) for the "List of outstanding CHT debt issues" page.
- `schema/cmhc-cht-offering-circular.schema.json` — the authoritative JSON
  Schema for the key terms printed on an offering circular PDF.
- `schema/cmhc-cht-outstanding-history.schema.json` — the authoritative JSON
  Schema for one issue block from the history of bond purchases for
  cancellation workbook linked from the outstanding amounts on the debt issues
  page.
- `schema/*.provider.schema.json` — flattened copies used only by
  `FSharp.Data.JsonProvider`.
- `Schema.fs` — full schema validation with `JsonSchema.Net`.
- `DebtIssues.fs` — the `JsonProvider` type and `DebtIssues.load` helpers.
- `OfferingCircular.fs` — the `JsonProvider` type and
  `OfferingCircular.load` helpers.
- `OutstandingHistory.fs` — the `JsonProvider` type and
  `OutstandingHistory.load` helpers.

The HTML, PDF and XLSX parsing lives in `NHAMBS.Cli` (the `cht-*` commands).
`NHAMBS.Cli batch --types cht-circulars --download --process` fetches missing
offering circular PDFs and the outstanding history workbook linked from the
debt-issues page, and writes JSON for both in the same style as NHA MBS
`pool_urls.txt` manifests.

## Why provider schemas?

`FSharp.Data`'s `JsonProvider` can infer types from a JSON Schema via the
`Schema` static parameter, but its schema reader only understands `type`,
`properties`, `required`, `items` and `format`. It does **not** resolve `$ref`,
and it ignores `enum`, `allOf` and conditionals. Passing the real schema
directly would therefore produce untyped `IJsonDocument` properties.

`generate-provider-schema.fsx` resolves the `$ref`s and strips the
validation-only keywords to produce the flattened provider schemas. The real
schemas remain the single source of truth.

Regenerate the provider schemas after changing any real schema:

```bash
cd src/NHAMBS.Cht
dotnet fsi generate-provider-schema.fsx
```

## Usage

```fsharp
open NHAMBS.Cht

match DebtIssues.load "out/debt-issues.json" with
| Ok issues ->
    printfn "%d issues as of %O" issues.Issues.Length issues.AsOfDate

    for issue in issues.Issues do
        printfn "%s %s %M million outstanding" issue.Series issue.Currency issue.Outstanding
| Error errors ->
    for error in errors do
        printfn "%s: %s" error.Path error.Message

match OfferingCircular.load "out/cht_circulars/series-77-initial-issue.json" with
| Ok circular ->
    printfn "%s issued %M" circular.Series circular.AggregatePrincipalAmount
| Error errors ->
    for error in errors do
        printfn "%s: %s" error.Path error.Message

match OutstandingHistory.load "out/cht_history/series-77-history.json" with
| Ok history ->
    printfn "%s has %d cancellation periods as of %O" history.Series history.Cancellations.Length history.AsOfDate
| Error errors ->
    for error in errors do
        printfn "%s: %s" error.Path error.Message
```

`DebtIssues.parse`, `OfferingCircular.parse` and `OutstandingHistory.parse`
skip validation and are useful when the input is already known to be valid.

## Data notes

### Debt issues page

- `asOfDate` comes from the page heading ("Outstanding Debt Securities as of
  October 15, 2026").
- Dates are ISO 8601 (`YYYY-MM-DD`), even where the printed page uses values
  such as `Feb. 23, 2017`.
- Amounts are JSON numbers in **millions** of the issue currency. A blank
  "Issued to the Government of Canada" cell is represented as `0`.
- Coupons are normalized to either a fixed percentage or an index plus spread;
  `3m CDOR -0.18%` becomes an index of `3m CDOR` and a spread of `-0.18`.
- Offering circulars keep their printed designations (`Initial Issue`,
  `Re-opening b`, etc.) and URLs.

### Offering circular PDFs

- The PDF schema is a **terms/intake** schema: it captures the cover-page
  economics, recurring interest dates, issuer/guarantor, listing, ratings and
  security identifiers. It is not a full transcription of the prospectus.
- Fixed coupons use `fixedRatePercent`; floating coupons use `index` and
  `spreadPercent` (basis points divided by 100).
- `interestPaymentDates` keeps the printed month/day values, for example
  `["June 15", "December 15"]`.
- Identifiers are taken from the last occurrence in the PDF so that temporary
  codes on a re-opening circular resolve to the permanent common code, ISIN and
  CUSIP.

### Outstanding history workbook

- The workbook contains one block per series. Each block becomes its own JSON
  file named from the series, for example `series-77-history.json`, so it can be
  associated with every offering circular for that series.
- The workbook heading supplies `title` and `asOfDate`; the October 15, 2026
  workbook reads "History of Bond Purchases for Cancellation as of October 15,
  2026".
- Fixed coupons are stored as Excel percentage fractions (for example
  `2.35E-2`) and are normalized to `fixedRatePercent: 2.35`.
- Floating coupons keep their printed text but gain an `index` and
  `spreadPercent`; `CDOR à 3 mois - 0.18%` becomes `3m CDOR` and `-0.18`, and
  `CORRA + 0.30%` becomes `CORRA` and `0.30`.
- Series values are normalized to the debt-issues form (`77` becomes
  `Series 77`) so the datasets can be matched.
- Excel serial dates are converted to ISO 8601 calendar dates.
- Cancellation periods are an array within each series document and are empty
  when the workbook records no cancellations for that series.

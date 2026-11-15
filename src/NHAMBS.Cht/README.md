# NHAMBS.Cht

Typed mapping and validation for CMHC Canada Housing Trust (CHT) JSON.

## What is here

- `schema/cmhc-cht-debt-issues.schema.json` — the authoritative JSON Schema
  (Draft 2020-12) for the "List of outstanding CHT debt issues" page.
- `schema/cmhc-cht-offering-circular.schema.json` — the authoritative JSON
  Schema for the key terms printed on an offering circular PDF.
- `schema/*.provider.schema.json` — flattened copies used only by
  `FSharp.Data.JsonProvider`.
- `Schema.fs` — full schema validation with `JsonSchema.Net`.
- `DebtIssues.fs` — the `JsonProvider` type and `DebtIssues.load` helpers.
- `OfferingCircular.fs` — the `JsonProvider` type and
  `OfferingCircular.load` helpers.

The HTML and PDF parsing lives in `NHAMBS.Cli` (the `cht-*` commands).
`NHAMBS.Cli batch --types cht-circulars --download --cht-urls` can fetch
missing offering circular PDFs and write a `circular_urls.txt` manifest in the
same style as NHA MBS `pool_urls.txt`.

## Why two schemas?

`FSharp.Data`'s `JsonProvider` can infer types from a JSON Schema via the
`Schema` static parameter, but its schema reader only understands `type`,
`properties`, `required`, `items` and `format`. It does **not** resolve `$ref`,
and it ignores `enum`, `allOf` and conditionals. Passing the real schema
directly would therefore produce untyped `IJsonDocument` properties.

`generate-provider-schema.fsx` resolves the `$ref`s and strips the
validation-only keywords to produce the flattened provider schemas. The real
schemas remain the single source of truth.

Regenerate the provider schemas after changing either real schema:

```bash
cd src/NHAMBS.Cht
dotnet fsi generate-provider-schema.fsx
```

## Usage

```fsharp
open NHAMBS.Cht

match DebtIssues.load "cht_out/debt-issues.json" with
| Ok issues ->
    printfn "%d issues as of %O" issues.Issues.Length issues.AsOfDate

    for issue in issues.Issues do
        printfn "%s %s %M million outstanding" issue.Series issue.Currency issue.Outstanding
| Error errors ->
    for error in errors do
        printfn "%s: %s" error.Path error.Message

match OfferingCircular.load "cht_circulars_out/series-77-initial-issue.json" with
| Ok circular ->
    printfn "%s issued %M" circular.Series circular.AggregatePrincipalAmount
| Error errors ->
    for error in errors do
        printfn "%s: %s" error.Path error.Message
```

`DebtIssues.parse` and `OfferingCircular.parse` skip validation and are useful
when the input is already known to be valid.

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

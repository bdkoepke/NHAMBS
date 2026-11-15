# NHAMBS.CoveredBonds

Typed mapping and validation for CMHC Canadian registered covered bond JSON.

## What is here

- `schema/cmhc-covered-bonds-registry.schema.json` — the authoritative JSON
  Schema (Draft 2020-12) for the Canadian Covered Bonds Registry page.
- `schema/cmhc-covered-bond-program.schema.json` — the authoritative JSON
  Schema for a registered covered bond program page, including its bond
  issuance parameters.
- `schema/*.provider.schema.json` — flattened copies used only by
  `FSharp.Data.JsonProvider`.
- `Schema.fs` — full schema validation with `JsonSchema.Net`.
- `Registry.fs` — the `JsonProvider` type and `Registry.load` helpers.
- `Program.fs` — the `CoveredBondProgram` provider and load helpers.

The HTML that produces these JSON files is parsed by `NHAMBS.Cli` (the
`covered-bonds-*` commands). `NHAMBS.Cli batch --types covered-bonds --download
--covered-bonds-urls` can cache the registry and program pages and write a
`program_urls.txt` manifest in the same style as NHA MBS `pool_urls.txt`;
`--html-dir` then parses the cached pages.
The schemas were derived from the live registry and from all ten currently
published program pages (Bank of Nova Scotia, BMO, CIBC, Equitable Bank,
Fairstone, FCDQ, Laurentian Bank, National Bank, RBC and TD).

## Why two schemas?

`FSharp.Data`'s `JsonProvider` can infer types from a JSON Schema via the
`Schema` static parameter, but its schema reader only understands `type`,
`properties`, `required`, `items` and `format`. It does **not** resolve `$ref`,
and it ignores `enum`, `allOf`, `anyOf` and conditionals. Passing the real
schema directly would therefore produce untyped `IJsonDocument` properties.

`generate-provider-schemas.fsx` resolves the `$ref`s and strips the
validation-only keywords to produce the flattened provider schemas. The real
schemas remain the single source of truth.

Regenerate the provider schemas after changing either real schema:

```bash
cd src/NHAMBS.CoveredBonds
dotnet fsi generate-provider-schemas.fsx
```

## Usage

```fsharp
open NHAMBS.CoveredBonds

// One registry file: validate against the real schema, then parse.
match Registry.load "covered_bonds_out/registry.json" with
| Ok registry ->
    for issuer in registry.Issuers do
        printfn "%s (%s) - %O" issuer.Name issuer.Status issuer.RegistrationDate
| Error errors ->
    for error in errors do
        printfn "%s: %s" error.Path error.Message

// One program file.
match CoveredBondProgram.load "covered_bonds_out/program-bank-of-nova-scotia.json" with
| Ok program ->
    printfn "%s - %d bonds" program.ProgramName program.Bonds.Length
| Error errors ->
    for error in errors do
        printfn "%s: %s" error.Path error.Message
```

`Registry.parse` and `CoveredBondProgram.parse` skip validation and are useful
when the input is already known to be valid.

## Data notes

- Dates are ISO 8601 (`YYYY-MM-DD`), even where the printed page uses values
  such as `22 July 2013` or `March 11 2024`.
- Printed `NA`/`N/A` values in optional fields (listing authority, other
  material characteristics) are omitted.
- Floating coupons are normalized to an index and a percentage spread. Values
  printed in basis points are divided by 100 (`105 bps` becomes `1.05`).
- A US-dollar Reg S / Rule 144A issue keeps both ISINs with their printed
  designations.
- The Fairstone page uses a combined
  `Bond Trustee, Custodian and Titleholder` row instead of separate custodian
  and bond-trustee rows; the registry uses `bondTrusteeCustodianAndTitleholder`
  for that case.

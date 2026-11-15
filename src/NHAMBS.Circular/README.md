# NHAMBS.Circular

Typed mapping and validation for CMHC 2834 NHA MBS Information Circular JSON.

## What is here

- `schema/cmhc-2834-i-v2-extended-en.schema.json` — the authoritative JSON
  Schema (Draft 2020-12) for the circular values. It is embedded in this
  assembly and used by `Schema.validate`.
- `schema/cmhc-2834-i-v2-extended-en.provider.schema.json` — a flattened copy
  used only by `FSharp.Data.JsonProvider`.
- `Circular.fs` — the `JsonProvider` type and the `Circular.load` /
  `Circular.loadMany` helpers.
- `Schema.fs` — full schema validation with `JsonSchema.Net`.

## Why two schemas?

`FSharp.Data`'s `JsonProvider` can infer types from a JSON Schema via the
`Schema` static parameter, but its schema reader only understands `type`,
`properties`, `required`, `items` and `format`. It does **not** resolve `$ref`,
and it ignores `enum`, `allOf` and conditionals. Passing the real schema
directly would therefore produce untyped `IJsonDocument` properties.

`generate-provider-schema.fsx` resolves the `$ref`s and strips the
validation-only keywords to produce the flattened provider schema. The real
schema remains the single source of truth.

Regenerate the provider schema after changing the real schema:

```bash
cd src/NHAMBS.Circular
dotnet fsi generate-provider-schema.fsx
```

## Usage

```fsharp
open NHAMBS.Circular

// One file: validate against the real schema, then parse with the provider.
match Circular.load "circulars/97535054.json" with
| Ok circular ->
    printfn "%s %s %M"
        circular.Mbs.PoolNumber
        circular.Parties.IssuerName
        circular.Mbs.AggregatePrincipalAmount
| Error errors ->
    for error in errors do
        printfn "%s: %s" error.Path error.Message

// A batch of files. Each result keeps its source path.
for path, result in Circular.loadDirectory "circulars" do
    match result with
    | Ok circular -> printfn "%s -> %s" path circular.Mbs.PoolNumber
    | Error errors -> printfn "%s -> %A" path errors
```

`Circular.parse` skips validation and is useful when the input is already known
to be valid. `Circular.summary` returns a small anonymous record with the fields
normally used for indexing a folder of circulars.

The `NHAMBS.Mbs` scraper and the `NHAMBS.Cli batch --types nhambs --download`
phase use a `pool_urls.txt` URL manifest (`PoolName,CUSIP,Url`) to discover and
download circular PDFs before they are parsed into this form.

# NHAMBS.Cli

Unified command-line interface for the CMHC/NHA MBS data in this repository.

## Commands

Argument parsing is handled by [Argu](https://github.com/fsprojects/Argu).
Run `--help` for the subcommand list, or `<command> --help` for the arguments
of a specific command. Commands taking a single source print JSON to stdout
unless `--output`/`-o` is given.

### URL manifests

Download steps share a `pool_urls.txt`-style URL manifest. A manifest is a CSV
with a header containing a `Url` column, plus optional `Name`/`PoolName`/
`Series`/`Designation` and `File`/`CUSIP` columns. A plain text file with one
URL per line is also accepted. Downloads skip files that already exist and are
not empty, run in parallel (`NHAMBS_PARALLELISM` controls the degree), print
progress, and return a non-zero exit code if any entry fails.

### Batch command

All batch pipelines use one command:

```bash
NHAMBS.Cli batch --types <comma-separated-types> (--download | --process | both) [options]
```

`--types` accepts `nhambs`, `covered-bonds` and `cht-circulars` (aliases:
`pools`, `covered`, `cht`). The relevant pipelines run in the order given.

- `--download` downloads missing source files for the selected types.
- `--process` parses and validates the selected types.
- Specify `--download`, `--process`, or both; with both, each type is downloaded
  before it is processed.

Common and type-specific options:

- `--pools-dir` / `--pools-out` / `--pools-urls` configure NHA MBS input,
  output and manifest. Defaults: `pools`, `pools_out`, `pool_urls.txt`. If the
  manifest does not exist, the download phase scrapes CMHC to create it.
- `--covered-bonds-registry` is required for `covered-bonds`.
- `--covered-bonds-out` / `--html-dir` / `--covered-bonds-urls` configure
  covered bond output, HTML cache and manifest. Defaults: `covered_bonds_out`
  and `covered_bonds_html`.
- `--cht-debt-issues` is required for `cht-circulars`.
- `--pdf-dir` / `--cht-out` / `--cht-history-out` / `--cht-urls` configure the
  CHT download directory (offering circular PDFs and history workbooks),
  circular JSON output, per-series history JSON output and manifest. Defaults:
  `cht_circulars`, `cht_circulars_out`, `cht_history_out`.

For example, download and process all three pipelines:

```bash
dotnet run --project src/NHAMBS.Cli -- batch \
  --types nhambs,covered-bonds,cht-circulars \
  --covered-bonds-registry 'https://www.cmhc-schl.gc.ca/professionals/project-funding-and-mortgage-financing/securitization/canadian-registered-covered-bonds/canadian-covered-bonds-registry' \
  --cht-debt-issues out/debt-issues.json \
  --download \
  --process
```

Each selected pipeline's process phase writes a resumable `batch-report.json` in
its output directory, using the same report format for NHA MBS, CHT and covered
bonds. A legacy `batch-report.csv` (or, for CHT, `circulars-report.csv`) is
imported once when the JSON report does not exist yet.

### NHA MBS pool URLs

The `nhambs` download phase scrapes CMHC for the current pool URLs, writes
`pool_urls.txt` (`PoolName,CUSIP,Url`) when the manifest does not already exist,
and downloads missing PDFs into `pools/`:

```bash
dotnet run --project src/NHAMBS.Cli -- batch --types nhambs --download
```

Process the downloaded PDFs:

```bash
dotnet run --project src/NHAMBS.Cli -- batch --types nhambs --process
```

`--pools-dir`, `--pools-out` and `--pools-urls` override the default PDF
directory, output directory and manifest path.

### NHA MBS information circulars

Parse a flattened CMHC 2834 NHA MBS Information Circular PDF into JSON that
matches `cmhc-2834-i-v2-extended-en.schema.json`:

```bash
dotnet run --project src/NHAMBS.Cli -- \
  circular docs/97507987.pdf \
  --output pools_out/97507987.json
```

Parse every PDF under a directory, optionally downloading missing PDFs first
from `pool_urls.txt` (or another manifest passed with `--pools-urls`):

```bash
dotnet run --project src/NHAMBS.Cli -- batch \
  --types nhambs \
  --pools-dir pools \
  --pools-out pools_out \
  --download \
  --process \
  --pools-urls pool_urls.txt
```

Without `--download`, only the PDFs already in `pools/` are parsed.

Batch mode writes one JSON file per circular plus a resumable
`batch-report.json`. It prints progress as each PDF finishes. Files modified
within the last 10 seconds (assumed to still be downloading) are marked
`pending`, and a second run resumes by skipping files already marked `parsed` or
`skipped` while retrying `pending` and `failed` files. PDFs are processed in
parallel; set `NHAMBS_PARALLELISM` to limit how many run at once (defaults to the
processor count).

The batch report is a JSON object with `command`, `startedAt`, `updatedAt`,
`total`, `completed` and an `entries` array. `total` and `completed` describe
the current run; `entries` retains the latest status for every file across runs.
Each entry has a `file`, a `status` (`parsed`, `skipped`, `pending` or `failed`),
an optional `detail`, and optional command-specific `metadata` such as the pool
number. Reports are written atomically and checkpointed while the batch runs, so
an interrupted run can be started again with the same arguments. If an output
directory only has a report from an older CLI version (`batch-report.csv` or
`circulars-report.csv`), it is imported once so the first run does not re-parse
everything.

### Canadian registered covered bonds

Parse the covered bonds registry:

```bash
dotnet run --project src/NHAMBS.Cli -- \
  covered-bonds-registry \
  https://www.cmhc-schl.gc.ca/professionals/project-funding-and-mortgage-financing/securitization/canadian-registered-covered-bonds/canadian-covered-bonds-registry \
  --output covered_bonds_out/registry.json
```

Parse one program page:

```bash
dotnet run --project src/NHAMBS.Cli -- \
  covered-bonds-program \
  https://www.cmhc-schl.gc.ca/professionals/project-funding-and-mortgage-financing/securitization/canadian-registered-covered-bonds/canadian-covered-bonds-registry/canadian-covered-bond-program-bank-of-nova-scotia \
  --output covered_bonds_out/program-bank-of-nova-scotia.json
```

Convert the registry and every linked program page, downloading the HTML into a
cache directory and writing a manifest:

```bash
dotnet run --project src/NHAMBS.Cli -- batch \
  --types covered-bonds \
  --covered-bonds-registry \
  'https://www.cmhc-schl.gc.ca/professionals/project-funding-and-mortgage-financing/securitization/canadian-registered-covered-bonds/canadian-covered-bonds-registry' \
  --covered-bonds-out covered_bonds_out \
  --download \
  --process \
  --html-dir covered_bonds_html \
  --covered-bonds-urls covered_bonds_html/program_urls.txt
```

`--download` fetches missing registry/program pages into `--html-dir` (default
`covered_bonds_html`), `--process` parses them, and `--covered-bonds-urls`
writes the manifest. `--covered-bonds-registry` accepts a registry URL, an HTML
file, or a registry JSON file. Local JSON files use CMHC's host to resolve
site-relative program links.

Parse from the downloaded pages instead of re-fetching them:

```bash
dotnet run --project src/NHAMBS.Cli -- batch \
  --types covered-bonds \
  --covered-bonds-registry covered_bonds_html/registry.html \
  --covered-bonds-out covered_bonds_out \
  --process \
  --html-dir covered_bonds_html
```

`--html-dir` falls back to the network for any page that is not present in the
directory. Program pages are processed in registry order, progress is printed,
and `batch-report.json` makes the batch resumable; the registry is reused from
`registry.json` when it is already complete.

### Canada Housing Trust debt issues

Parse the outstanding debt issues page:

```bash
dotnet run --project src/NHAMBS.Cli -- \
  cht-debt-issues \
  https://www.cmhc-schl.gc.ca/professionals/project-funding-and-mortgage-financing/securitization/canada-mortgage-bonds/list-of-outstanding-cht-debt-issues \
  --output out/debt-issues.json
```

The debt-issues snapshot is written as a single `out/debt-issues.json` file; no
intermediate `cht/` directory is needed.

Parse one offering circular PDF:

```bash
dotnet run --project src/NHAMBS.Cli -- \
  cht-circular \
  cht_circulars/series-77-initial-issue.pdf \
  --output out/cht_circulars/series-77-initial-issue.json
```

Parse one history of bond purchases for cancellation workbook, writing one
JSON file per series:

```bash
dotnet run --project src/NHAMBS.Cli -- \
  cht-history \
  cht_circulars/history-bond-purchases-cancellation-en.xlsx \
  --output out/cht_history
```

Parse every offering circular linked from a debt-issues snapshot, downloading
missing PDFs and optionally writing a manifest:

```bash
dotnet run --project src/NHAMBS.Cli -- batch \
  --types cht-circulars \
  --cht-debt-issues out/debt-issues.json \
  --pdf-dir cht_circulars \
  --cht-out out/cht_circulars \
  --cht-history-out out/cht_history \
  --download \
  --process \
  --cht-urls cht_circular_urls.txt
```

Without `--download`, `cht-circulars` only uses PDFs already present in the PDF
directory and reports missing ones. `--download` also fetches the history of
bond purchases and cancellations workbook linked from the outstanding amounts
on the debt-issues page (currently `history-bond-purchases-cancellation-en.xlsx`)
into the same directory. `--process` splits each linked history workbook into
one JSON document per series and writes them to `--cht-history-out`; the file
name is the series, for example `series-77-history.json`. It validates each
parsed result, prints progress, and writes a resumable `batch-report.json` plus
a `manifest.csv` mapping series/designation to file name and source URL. That
`manifest.csv` has the same shape as a `pool_urls.txt` manifest and is
compatible with `--cht-urls`. A second run skips circulars already marked
`parsed` or `skipped` when their JSON output still exists.

## Validation

Every command validates its output against the real Draft 2020-12 JSON Schema
before writing it:

- `cmhc-2834-i-v2-extended-en.schema.json`
- `cmhc-covered-bonds-registry.schema.json`
- `cmhc-covered-bond-program.schema.json`
- `cmhc-cht-debt-issues.schema.json`
- `cmhc-cht-offering-circular.schema.json`
- `cmhc-cht-outstanding-history.schema.json`

Parsing modules live in the corresponding libraries (`NHAMBS.Circular`,
`NHAMBS.CoveredBonds`, `NHAMBS.Cht`); this CLI is the single entry point for
producing and validating the JSON datasets.

## Requirements

- .NET 10 SDK.
- `pdftotext`, `pdfinfo` and `pdftoppm` (Poppler) for NHA MBS circulars.
- `pdftotext` for CHT offering circulars.
- `tesseract` for scanned/image-only NHA MBS circulars. The CLI looks for
  `NHAMBS_TESSERACT` first, then `tesseract` on `PATH`. `TESSDATA_PREFIX` must
  point at a directory containing `eng.traineddata` and Tesseract's `configs`
  directory.
- Network access when a source is a URL.

Set `NHAMBS_DUMP_TEXT=/path/to/file.txt` to dump the combined NHA MBS text that
the parser sees; this is useful when diagnosing OCR output.

# NHAMBS.Mbs

Library for scraping CMHC's NHA MBS information circular search pages. The
command-line interface lives in `NHAMBS.Cli`.

## Usage

The `nhambs` batch type scrapes CMHC for the current pool URLs, writes
`pool_urls.txt` (`PoolName,CUSIP,Url`) when the manifest does not already exist,
and downloads missing PDFs into `pools/`:

```bash
dotnet run --project src/NHAMBS.Cli -- batch --types nhambs --download
```

Process the downloaded PDFs:

```bash
dotnet run --project src/NHAMBS.Cli -- batch --types nhambs --process
```

Use `--pools-dir`, `--pools-out` and `--pools-urls` to override the default PDF
directory, output directory and manifest path. See
`dotnet run --project src/NHAMBS.Cli -- batch --help` for the complete batch
options.

Downloads skip files that already exist and are not empty, run in parallel
(`NHAMBS_PARALLELISM` controls the degree), and return a non-zero exit code if
any download fails.

## API

- `getIssuers` — read the issuer list from the CMHC search page.
- `getPools` — read every pool page for an issuer code.
- `parsePoolPage`, `parsePoolPages`, `parseIssuers` — parse already-loaded HTML
  (used by the tests).

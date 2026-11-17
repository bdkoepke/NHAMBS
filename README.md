# NHA MBS Securitization Dashboard

A SAFE Stack dashboard for CMHC's NHA mortgage-backed securities, Canada
Mortgage Bonds and Canadian registered covered bonds. It turns the parsed JSON
extracts in `out/` into a PostgreSQL analytics database, exposes a typed
Saturn / Fable.Remoting API, and renders the client with Fable, Elmish, Feliz
and AG Grid.

The experience follows the Computershare NHA MBS Data Site securitization
dashboard: market KPIs, issuance and maturity charts, issuer/province/insurer
breakdowns, and filterable, sortable pool and security tables with CSV export.

## Screenshots

| | |
| --- | --- |
| ![Market overview](docs/dashboard-overview.png) | ![NHA MBS pool explorer](docs/dashboard-pools.png) |
| Market overview | NHA MBS pool explorer with AG Grid |
| ![Pool detail](docs/dashboard-pool-detail.png) | ![Canada Mortgage Bonds](docs/dashboard-cht.png) |
| Pool detail drawer | Canada Mortgage Bonds |

## Architecture

```text
CMHC source PDFs / pages
        │
        ▼
out/pools/*.json  out/debt-issues.json  out/cht_circulars/  out/cht_history/  out/covered_bonds/
        │
        ▼
NHAMBS.Etl (Elsa CLI)      NHAMBS.ElsaServer (Elsa Server + Studio)
        │                         │
        ▼                         ▼
NHAMBS.Workflows (Elsa workflows + activities)
        │
        ▼
NHAMBS.Etl.Core ── bulk COPY ──▶ PostgreSQL (schemas: raw, nhambs, elsa)
        │                         │
        │                         ├── FluentMigrator migrations
        │                         ├── typed reads via SQLProvider LINQ
        │                         └── tables and analytics views in nhambs
        ▼
NHAMBS.Server (Saturn + Fable.Remoting)
        │
        ▼
NHAMBS.Client (Fable + Elmish + Feliz + AG Grid)
```

The ETL is orchestrated by **Elsa Workflows**. `NHAMBS.Etl` is a thin CLI host
that resolves `IWorkflowRunner` and runs one of the workflows defined in
`NHAMBS.Workflows`; the resumable database loaders and bulk COPY code live in
`NHAMBS.Etl.Core`. `NHAMBS.ElsaServer` exposes the same workflows through Elsa's
REST API and hosts **Elsa Studio** (Blazor WebAssembly) for visual workflow
management and run inspection. Elsa persists its definitions, runtime instances
and identity data in the `elsa` PostgreSQL schema.

Infrastructure is managed with **Pulumi** (`src/NHAMBS.Pulumi/`). The local
stack uses the Pulumi Docker provider to run PostgreSQL 18 with a named volume
and health check. There is no hand-rolled `docker-compose.yml`.

## Features

- **Market overview** — NHA MBS principal, pool/issuer/loan counts, average
  coupon, CHT outstanding, covered bond coverage, monthly issuance, rate-type
  mix, province and maturity profiles, a current/prior-month/last-year period
  pivot with charts, top issuers with share bars and 12-month sparklines, and
  ETL freshness.
- **NHA MBS pool explorer** — filter 50,000+ circulars by search, issuer, rate
  type, pool type, province, insurer, issue date and maturity date. Results
  include 5 KPI cards, 4 charts, loaded-row issuer/coupon charts and an AG Grid
  table.
- **Pool detail drawer** — key facts, loan type profile, provincial and insurer
  allocations, scheduled maturity buckets, interest adjustment profile, rate
  bands, prepayment provisions and a direct link to the source PDF.
- **Canada Mortgage Bonds** — outstanding CHT series, issuance, coupon and
  maturity analytics, plus the parsed offering-circular library with ratings,
  ISIN/CUSIP identifiers and source links. A third tab shows the per-series
  history of bond purchases for cancellation.
- **Covered bonds** — registered programme and bond analytics, currency and
  maturity charts, programme/bond/registry tabs and a CMHC registry view.
- **Forward curves** — forward rate curves loaded from `forward_curves.json`,
  with snapshot/horizon KPIs, an SVG curve chart, tenor observations and series
  metadata.
- **CSV export** — export the currently loaded pool result set from the grid.

## Project layout

| Project | Purpose |
| --- | --- |
| `src/NHAMBS.Shared` | Transport-safe DTOs and the Fable.Remoting `DashboardApi` contract. |
| `src/NHAMBS.Data` | SQLProvider context, typed repository reads, reporting queries and the forward-curve JSON loader. |
| `src/NHAMBS.Migrations` | FluentMigrator schema and dashboard view migrations. |
| `src/NHAMBS.Etl.Core` | Resumable, bulk-loading loaders for MBS, CHT, covered bond and mortgage-arrears data. |
| `src/NHAMBS.Workflows` | Elsa activities and workflow definitions for each dataset. |
| `src/NHAMBS.Etl` | Elsa workflow CLI host and command-line entry point. |
| `src/NHAMBS.ElsaServer` | Elsa Server APIs, persistence, identity and hosted Elsa Studio. |
| `src/NHAMBS.Studio` | Elsa Studio Blazor WebAssembly host for workflow management. |
| `src/NHAMBS.Server` | Saturn API, Fable.Remoting handler and SPA hosting. |
| `src/NHAMBS.Client` | Fable/Elmish/Feliz front end, AG Grid integration and charts. |
| `src/NHAMBS.Pulumi` | Pulumi infrastructure (PostgreSQL container, volume, outputs). |
| `build.fsx` | Local orchestration: infra, migrations, ETL, build, server and client. |
| `tools` | SQLProvider offline-schema generation helper. |

The pre-existing `NHAMBS.Mbs`, `NHAMBS.Cli`, `NHAMBS.Cht`,
`NHAMBS.Circular`, `NHAMBS.CoveredBonds` and `NHAMBS.Sources` projects are left
untouched and continue to produce the JSON extracts consumed by the ETL.

## One-command local runner

`build.fsx` orchestrates the local stack: Pulumi, PostgreSQL readiness,
FluentMigrator, the ETL, npm dependencies, the Saturn API, Elsa Server + Studio
and the Vite/Fable client.

```bash
./build.fsx
```

or, if you prefer invoking F# Interactive explicitly:

```bash
dotnet fsi build.fsx -- --target dev
```

That is the `dev` target. It:

1. Checks in with Pulumi and brings up the local PostgreSQL container.
2. Waits for PostgreSQL to accept connections.
3. Applies migrations.
4. Loads `out/` through the Elsa ETL workflows.
5. Installs client dependencies if needed.
6. Starts the API, Elsa Server + Studio and Vite client together.

Press `Ctrl+C` to stop the child processes. If a process outlives a terminal
session, run `./build.fsx --target stop`.

If `NHAMBS_CONNECTION` is already set, the runner uses that database and skips
Pulumi unless `--with-infra` is passed.

For a local Pulumi backend (`pulumi login --local` or `file://...`), the runner
supplies `PULUMI_CONFIG_PASSPHRASE=nhambs-local-dev` when no passphrase is
configured. Set `PULUMI_CONFIG_PASSPHRASE` or `PULUMI_CONFIG_PASSPHRASE_FILE`
yourself when using shared state or an existing encrypted stack.

If you switch to a different passphrase for a local stack, remove the
`encryptionsalt` line from `src/NHAMBS.Pulumi/Pulumi.dev.yaml` before initializing the
stack, or recreate the stack with `pulumi stack rm dev` first.

```bash
./build.fsx --target run       # use an already-loaded database
./build.fsx --target build     # compile .NET and the production client
./build.fsx --target ci        # build-only, npm ci when possible
./build.fsx --skip-etl         # launch without reloading data
./build.fsx --skip-infra       # use NHAMBS_CONNECTION instead of Pulumi
./build.fsx --target help
```

When invoking through `dotnet fsi` directly, F# Interactive parses leading
options itself, so put script options after `--`:

```bash
dotnet fsi build.fsx -- --target run
```

Available targets are `dev`, `run`, `build`, `ci`, `infra`, `migrate`, `etl`,
`server`, `elsa`, `client`, `stop` and `help`. The runner records child PIDs under
`.build/` so `--target stop` can clean up processes that outlive a terminal
session. Pass `--no-elsa` to skip Elsa Server + Studio.

## Prerequisites

- .NET SDK 10
- Node.js 20+ and npm
- Docker (used by the Pulumi Docker provider)
- Pulumi CLI
- PostgreSQL client tools are useful, but not required

## Quick start

### 1. Start PostgreSQL with Pulumi

```bash
cd src/NHAMBS.Pulumi
pulumi login --local          # or use your Pulumi Cloud organization
pulumi stack init dev
pulumi up
pulumi stack output connectionString --show-secrets
```

The default stack configuration is in `src/NHAMBS.Pulumi/Pulumi.dev.yaml`:

```yaml
config:
  nhambs:image: postgres:18-alpine
  nhambs:port: "54322"
  nhambs:database: nhambs
  nhambs:username: nhambs
  nhambs:password: nhambs
```

PostgreSQL 18 stores its cluster under `/var/lib/postgresql/<major>/docker`, so the
stack mounts the named volume at `/var/lib/postgresql`. A volume created by an
older image (for example PostgreSQL 17) cannot be reused directly; migrate it with
`pg_dump`/`pg_restore` or `pg_upgrade` instead.

Change the password for anything beyond local development:

```bash
pulumi config set --secret nhambs:password 'a-strong-password'
pulumi up
```

### 2. Configure the connection

```bash
export NHAMBS_CONNECTION='Host=localhost;Port=54322;Database=nhambs;Username=nhambs;Password=nhambs'
```

All .NET projects read `NHAMBS_CONNECTION`. The default already points at the
local Pulumi stack, so this is only required if you change it.

Forward curves are file-backed. The API looks for `out/forward_curves.json`,
then `forward_curves.json` in the repository root, then the application
directory; set `NHAMBS_FORWARD_CURVES` to override the path. The puller project
can write to `out/forward_curves.json` without a schema migration.

### 3. Create the schema and load the extracts

```bash
dotnet tool restore
dotnet run --project src/NHAMBS.Migrations
dotnet run --project src/NHAMBS.Etl -- --dataset all
```

The ETL host runs an Elsa workflow from `NHAMBS.Workflows`. `--dataset all`
runs `NhambsEtlWorkflow`, which composes the MBS, CHT, covered bond,
mortgage-arrears and government-bond activities in order; the per-dataset
options select the matching workflow instead. The workflows load from `out/` and the pool manifest from
`src/NHAMBS.Mbs/pool_urls.txt` by default. CHT reads the debt-issues snapshot
directly from `out/debt-issues.json`, its offering circulars from
`out/cht_circulars/` and the per-series cancellation history from
`out/cht_history/`. Useful options:

```bash
dotnet run --project src/NHAMBS.Etl -- --dataset mbs
dotnet run --project src/NHAMBS.Etl -- --dataset cht
dotnet run --project src/NHAMBS.Etl -- --dataset covered
dotnet run --project src/NHAMBS.Etl -- --dataset arrears
dotnet run --project src/NHAMBS.Etl -- --dataset government-bonds
dotnet run --project src/NHAMBS.Etl -- --data-root out --pools-dir out/pools
```

Every run is recorded in `nhambs.etl_runs`. A failed run rolls back its data
transaction while preserving the audit row and error message. Elsa reports
activity failures as workflow incidents; the host prints those incidents and
exits with status `1`.

The workflow definitions are intentionally isolated from the database loaders:

- `NHAMBS.Workflows` contains `MbsEtlWorkflow`, `ChtEtlWorkflow`,
  `CoveredBondEtlWorkflow` and `NhambsEtlWorkflow`, plus the three
  `CodeActivity` wrappers and the `Registration.register` helper. The workflows
  also expose parameterless constructors using `NHAMBS_DATA_ROOT`,
  `NHAMBS_POOLS_DIR` and `NHAMBS_MANIFEST`, so Elsa Server can host and run them.
- `NHAMBS.Etl.Core` contains the reusable MBS, CHT, covered bond and
  mortgage-arrears loaders,
  JSON helpers and the binary `COPY` writer.
- `NHAMBS.Etl` only parses arguments, builds the Elsa service provider, selects
  a workflow and surfaces its result. The same four workflows are registered in
  `NHAMBS.ElsaServer` and published to Elsa Studio.

### 4. Run the API

```bash
dotnet run --project src/NHAMBS.Server
```

The API listens on `http://localhost:8085`. `GET /health` returns `ok`.

### 5. Run Elsa Server + Studio

```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/NHAMBS.ElsaServer
```

Elsa Studio is served at `http://localhost:8090`; the API lives under
`http://localhost:8090/elsa/api`. The server applies Elsa's EF Core migrations to
the `elsa` schema on startup. In Development it bootstraps an administrator from
`NHAMBS_ELSA_ADMIN_USER` (default `admin`) and
`NHAMBS_ELSA_ADMIN_PASSWORD` (default `nhambs-elsa-admin`). Set both, plus
`NHAMBS_ELSA_SIGNING_KEY`, outside Development. Elsa Studio is available
directly at `http://localhost:8090`; the dashboard does not link to or embed it.

### 6. Run the client in development

```bash
cd src/NHAMBS.Client
npm install
npm run dev
```

Vite serves the UI at `http://localhost:5173` and proxies `/api` to the Saturn
server. The Fable watcher compiles `.fs` files to `.fable/src/*.js`.

### 7. Build the production client into the server

```bash
cd src/NHAMBS.Client
npm run build
```

Vite writes the client bundle to `src/NHAMBS.Server/wwwroot/`. The server serves
that directory and falls back to `index.html` for client-side routes. Restart
the server and open `http://localhost:8085`.

## Database model

The warehouse keeps raw source documents in `raw` and all conformed tables and
analytics views in `nhambs`. See
[`docs/data-lineage.md`](docs/data-lineage.md) for the layer boundaries,
retention policy and full lineage diagram.

**Raw (`raw`)**

- `source_files` — one raw JSON document per source file with path, filename,
  SHA-256 checksum, size, source timestamp, ETL run id and load timestamp.
- `source_documents` — binary source workbooks (currently the CBA and CMHC
  arrears XLSX files) with the same lineage metadata.

**Conformed tables (`nhambs`)**

- `mbs_pools` — one row per NHA MBS pool keyed by CUSIP, with coupon, principal,
  dates, loan counts and JSONB series. `issuer_id` links to the canonical
  issuer; `source_file_id` points back to the raw document. Scheduled maturity
  totals are read from the `total` bucket instead of being duplicated here.
- `mbs_pool_insurers` — insurer allocations keyed by `(pool_cusip, insurer_id)`.
- `mbs_pool_locations` — provincial allocations keyed by
  `(pool_cusip, province)`.
- `mbs_pool_scheduled_maturity` — 1/2/3/4/5-month, at-maturity and total
  buckets keyed by `(pool_cusip, bucket)`.
- `mbs_pool_rate_bands` — canonical rate-band allocations and remaining face
  values by date.
- `issuers` / `insurers` — canonical entity reference tables with stable
  surrogate ids; facts link through `issuer_id` / `insurer_id`.
- `issuer_aliases` — normalized source-name key to canonical issuer key/name.
  The ETL and `nhambs` views join through this table rather than a hard-coded
  application map.
- `provinces` / `rate_types` / `pool_types` / `rate_bands` /
  `maturity_buckets` — reference tables for repeated MBS labels. Base tables
  store the stable code or key, and the views join for display text. Rate-band
  labels are snapped to canonical 25 basis-point buckets; source text remains in
  `raw`.
- `cht_issues` / `cht_offering_circulars` — outstanding CHT series and circular
  links.
- `cht_circulars` / `cht_circular_ratings` / `cht_circular_payment_dates` —
  parsed offering circular library.
- `cht_outstanding_history` / `cht_outstanding_history_cancellations` —
  per-series history of bond purchases for cancellation and its periods.
- `covered_bond_programs` / `covered_bond_parties` / `covered_bonds` /
  `covered_bond_isins` / `covered_bond_registry` — covered bond registry.
- `arrears_sources` / `arrears_geographies` — provider definitions, cadence,
  attribution and the country/province/region/CMA geography keys.
- `arrears_observations` — one row per source, period, geography and metric
  (90+ day arrears rate, arrears count or mortgage count). See
  `docs/arrears.md` for the definitional reconciliation.
- `government_bond_programs` / `government_bond_issues` — issue-level
  Government of Canada bonds and treasury bills from Bank of Canada Valet,
  including ISIN, coupon, issue/maturity dates, outstanding amounts and
  inflation adjustments.
- `provincial_bond_transactions` — issue-level Provincial Bond Purchase Program
  purchases (issuer, ISIN, coupon, maturity, trade dates, amount purchased and
  purchase yield). See `docs/government-bonds.md`.

**Analytics views (`nhambs`)**

Dashboard views are built from the `nhambs` tables only: `v_mbs_pool_grid`, `v_mbs_monthly`,
`v_mbs_issuer_summary`, `v_mbs_province_summary`, `v_mbs_rate_type_summary`,
`v_mbs_pool_type_summary`, `v_mbs_insurer_summary`, `v_mbs_maturity_summary`,
`v_mbs_unresolved_entities`, `v_covered_bond_program_summary`,
`v_covered_bond_registry_summary`, `v_cht_summary`,
`v_cht_history_summary`, `v_arrears_series`, `v_arrears_latest`,
`v_arrears_provider_comparison`, `v_arrears_freshness`,
`v_government_bond_latest`, `v_government_bond_program_summary`,
`v_government_bond_freshness`, `v_provincial_bond_transactions`,
`v_provincial_bond_issue_summary` and `v_provincial_bond_summary`.

**Operations (`nhambs` / `elsa`)**

- `etl_runs` — loader audit trail.
- `etl_anomalies` — detected data-quality issues, including corrected values.
- `elsa.*` — Elsa workflow definitions, runtime instances, triggers, users and
  operational history maintained by `NHAMBS.ElsaServer` in the `elsa` schema.

The ETL cleans issuer names and resolves them before the bulk COPY, so
`mbs_pools` stores only `issuer_id`. Canonical names are resolved from
`nhambs.issuer_aliases`; add a row there for a new spelling or OCR variant.
Exact aliases win, and near matches use PostgreSQL's built-in
`fuzzystrmatch` functions (`levenshtein_less_equal` plus a `dmetaphone` guard)
rather than a custom edit-distance implementation. Insurer labels are
normalized across case, spacing, legal suffixes and common boilerplate;
combined labels such as `CMHC and Sagen` are expanded to the canonical names.
`nhambs.v_mbs_unresolved_entities` lists issuers without an alias mapping and
insurers that remain `Unknown insurer`; it is the review queue for adding
aliases or fixing a source scan.

## SQLProvider offline schema

`src/NHAMBS.Data/Provider.fs` uses SQLProvider's `ContextSchemaPath` so the
solution compiles without a live database. The committed schema file is
`src/NHAMBS.Data/sqlprovider.schema`.

After changing migrations or views, regenerate it with the local database
running:

```bash
./tools/generate-provider-schema.sh
```

The helper opens the local database, touches every table and view, and writes
the schema used at compile time. Runtime data access still uses
`NHAMBS_CONNECTION`.

SQLProvider is used for all repository reads: pool search and detail, filter
options, analytics inputs, CHT, covered bonds and ETL metadata. The data layer
uses LINQ queries against the `nhambs` tables and analytics views; filtering and
presentation aggregation that SQLProvider cannot express cleanly are done in F#
over those typed results.

## API contract

`NHAMBS.Shared/Api.fs` defines the Fable.Remoting contract:

- `GetOverview`
- `GetMbsAnalytics`
- `GetMbsPools`
- `GetMbsPool`
- `GetMbsFilterOptions`
- `GetCht`
- `GetCoveredBonds`
- `GetArrears`
- `GetGovernmentBonds`
- `GetFreshness`

The server route builder maps these to `/api/DashboardApi/{method}`. Fable
clients call the same functions directly through `Fable.Remoting.Client`.
The transport is Fable.Remoting's MessagePack binary serialization rather than
JSON: Fable.SimpleJson 3.24 under Fable 5 misclassifies `FSharpOption` as a
union and cannot decode the nullable date/text fields that the API preserves.
`/health` remains a plain-text endpoint, and the API routes are intended for the
generated Fable client or another MessagePack-capable Fable.Remoting client.

## Dependency versions

All direct NuGet packages are on the latest stable versions available for
`net10.0`, with three compatibility pins:

- **FSharp.Core 10.1.404** — `Fable.Remoting.Server` 6.1.0 requires
  `FSharp.Core >= 10.1.202 < 10.2.0`.
- **Npgsql 9.0.5** — `SQLProvider.PostgreSql` 1.5.32 reads PostgreSQL `date`
  columns through `DateTime`; Npgsql 10 switches those columns to `DateOnly`
  by default and breaks the provider. 9.0.5 is the latest compatible release.
- **Microsoft.AspNetCore.WebUtilities 10.0.15** — version 11.0.0 only targets
  `net11.0`, while this solution targets `net10.0`.

The ETL workflows use **Elsa 3.10.1**, which targets `net10.0` directly. The CLI
hosts them in-process for batch runs, and `NHAMBS.ElsaServer` hosts the same
definitions behind Elsa's REST API with the Elsa Studio dashboard.

Transitive security advisories were cleared by adding explicit newer versions
of `System.Security.Cryptography.Pkcs`, `Microsoft.IdentityModel.*`,
`System.IdentityModel.Tokens.Jwt`, `Microsoft.AspNetCore.WebUtilities` and the
OpenTelemetry packages. `dotnet list package --vulnerable` reports no
vulnerable packages.

## Notes and limitations

- `out/` is intentionally ignored by git; it contains the parsed source
  extracts. The ETL expects the existing directory layout.
- The pool grid loads all matching rows for client-side AG Grid
  sorting/filtering; filters and CSV exports always cover the full result set.
- Nominal covered bond amounts are grouped by issuance currency and are not
  converted across currencies.
- CHT amounts are stored as published (CAD millions) and formatted as dollars
  in the UI.
- SQLProvider is compiled against the committed offline schema; if the schema
  is out of date relative to the database, regenerate the schema file and
  rebuild.
- Elsa Studio requires authentication. Local development bootstraps the admin
  from `NHAMBS_ELSA_ADMIN_USER` / `NHAMBS_ELSA_ADMIN_PASSWORD`; set those and
  `NHAMBS_ELSA_SIGNING_KEY` explicitly outside Development. The Studio default
  backend URL is `http://localhost:8090/elsa/api`; change
  `src/NHAMBS.Studio/wwwroot/appsettings.json` if you move the server.
- Forward curve values with `dataType: percent` are read as decimal fractions
  (for example `0.02525` is shown as `2.525%`). The API rereads the JSON on each
  request, so updates appear after a Refresh without a database reload.

## Verification

```bash
./build.fsx --target build
./build.fsx --target migrate
./build.fsx --target etl
./build.fsx --target server
curl http://localhost:8085/health
```

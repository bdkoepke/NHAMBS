# Data lineage and layer policy

The warehouse has two data schemas: `raw` for landed source documents and
`nhambs` for the conformed tables, analytics views and ETL audit tables.

## Layer boundaries

| Layer | Schema | Contents | Retention | Reload behaviour |
| --- | --- | --- | --- | --- |
| Raw | `raw` | One row per source file: path, filename, SHA-256, size, source timestamp, ETL run id, and the unmodified document. Parsed extracts use `raw.source_files` (JSONB); binary source workbooks use `raw.source_documents` (bytea). | Current file for each dataset. A dataset is replaced on the next successful load; the previous copy is not kept. | `DELETE` by dataset, then bulk COPY. The SHA-256 makes a changed source file visible. |
| Conformed tables | `nhambs` | Typed tables with natural keys, unique constraints and foreign keys (`mbs_pools` keyed by CUSIP, CHT by series, covered bonds by program/bond id). `source_file_id` points back to raw for the top-level facts. | Current snapshot of each dataset. Child rows follow their parent by `ON DELETE CASCADE`. | The MBS, CHT, covered-bond and arrears loaders truncate their datasets and reload one transaction at a time. A failed load rolls back the data changes. |
| Analytics views | `nhambs` | Dashboard and API views over the conformed tables. No ETL writes and no raw source JSON. | Recreated by migrations; always derived. | `CREATE OR REPLACE VIEW` on migration; no independent reload. |
| Operations | `nhambs` | `etl_runs`, `etl_anomalies`, analytics views. | Audit history is retained. Views are derived. | Created and maintained by the ETL and migrations. |

## Lineage

```text
CMHC PDF / HTML / XLSX
        │
        ▼
raw.source_files (dataset, source_path, checksum_sha256, run_id, document)
        │
        ├── nhambs.mbs_pools.source_file_id
        │       ├── nhambs.mbs_pool_insurers.pool_cusip
        │       ├── nhambs.mbs_pool_locations.pool_cusip
        │       ├── nhambs.mbs_pool_scheduled_maturity.pool_cusip
        │       └── nhambs.mbs_pool_rate_bands.pool_cusip
        │
        ├── nhambs.cht_issues.source_file_id
        ├── nhambs.cht_circulars.source_file_id
        ├── nhambs.cht_outstanding_history.source_file_id
        ├── nhambs.covered_bond_programs.source_file_id
        └── nhambs.covered_bond_registry.source_file_id
                │
                ▼
        nhambs.v_mbs_* / nhambs.v_cht_* / nhambs.v_covered_bond_*
        nhambs.v_arrears_*
                │
                ▼
        NHAMBS.Server → NHAMBS.Client
```

The arrears workbooks use a parallel binary lineage path:

```text
CBA / CMHC XLSX
        │
        ▼
raw.source_documents (dataset, source_path, checksum_sha256, run_id, bytea document)
        │
        ▼
nhambs.arrears_observations.source_document_id
        │
        ▼
nhambs.v_arrears_series / v_arrears_latest / v_arrears_provider_comparison / v_arrears_freshness

Bank of Canada Valet GOC_OUTSTANDING JSON
        │
        ▼
raw.source_documents (dataset = government-bonds-goc)
        │
        ▼
nhambs.government_bond_issues.source_document_id
        │
        ▼
nhambs.v_government_bond_latest / v_government_bond_program_summary / v_government_bond_freshness
```

Every conformed top-level fact records the raw file that produced it. Child
tables inherit lineage through their parent pool, issue, circular, program or
bond key. `nhambs.etl_anomalies` records corrections and warnings with the
`run_id` that detected them.

## Natural keys

| Entity | Natural key |
| --- | --- |
| NHA MBS pool | `cusip` |
| CHT issue | `series` |
| CHT offering circular | `(series, designation)` |
| CHT outstanding history | `series` |
| Covered bond program | `(issuer_name, program_name)` |
| Covered bond registry | `name` |
| Arrears observation | `(source_code, period_end, geography_code, lender_type, insured_status, product_type, arrears_stage, metric)` |
| Government bond issue | `(program_code, isin, as_of_date)` |

## Maturity year decoding

The circular date fields are printed as four-digit years, but low-quality scans
produce codes such as `2610` (for 2010) and `0201` (for 2011). The decoder uses
the other dates on the same circular as anchors:

* If a year is below 1970 or above 2200, reuse the year from the final payment
  date (for a maturity date) or from the maturity date (for a final payment).
* A weighted-average maturity more than a year after the final payment is reset
  to the final-payment year.
* A weighted-average interest adjustment date outside ten years before to one
  year after the issue date is reset to the issue year.
* A first payment before the issue date is moved to the issue year, or the
  following year if the month/day has already passed.
* A maturity date at or before the issue date is moved forward five years, the
  common term for the older forms.

The corrected value is recorded in `nhambs.etl_anomalies` as a `corrected`
anomaly, so the original OCR value remains visible for review. The CLI applies
the same repairs before writing JSON; the ETL re-applies them when loading older
JSON files.

The `2610` example in the original TODO mapped to 2016, but the corresponding
circular (`62918ZUW1` / pool `97516363`) prints a final payment date of
2010-11-15 and a weighted-average maturity of 2010-09-28, so the decoder uses
2010.

## Entity reference tables

`nhambs.issuers` and `nhambs.insurers` hold stable surrogate ids and the
canonical display name for each normalized key. `nhambs.issuer_aliases` maps a
normalized source-name key to the canonical key and display name; add a row
there for a new spelling or OCR variant instead of changing application code.
Before the COPY starts, the ETL resolves each source issuer through
`nhambs.resolve_issuer_alias` and writes only `issuer_id` on the pool. Exact
aliases are preferred; near matches use PostgreSQL's `fuzzystrmatch` extension
(`levenshtein_less_equal` with a `dmetaphone` guard). Insurer allocations carry
`insurer_id`.

The remaining repeated MBS labels live in reference tables:

| Reference table | Key | Used by |
| --- | --- | --- |
| `provinces` | `code` | `mbs_pool_locations.province` |
| `rate_types` | `code` | `mbs_pools.rate_type` |
| `pool_types` | `code` | `mbs_pools.pool_type` |
| `rate_bands` | `band_key` | `mbs_pool_rate_bands.rate_band` |
| `maturity_buckets` | `code` | `mbs_pool_scheduled_maturity.bucket` |

`rate_bands` uses canonical 25 basis-point buckets. Source labels such as
`3.751 - 4.000` and `3.76 - 4.00` both map to `3.7500|4.0000` / `3.75 - 4.00`.
The original label remains in `raw.source_files.document`; an unparseable label
uses a `raw:` key and null bounds rather than being dropped.

`nhambs.v_mbs_unresolved_entities` lists issuers without an alias mapping and
insurers that remain `Unknown insurer`; it is the review queue for adding
aliases or fixing a source scan.

## Normalization validation and rollback

Migration `M013_ReferenceDimensions` moves the MBS dimensions into the
reference tables and drops the duplicated columns. Validate a migrated
database with:

```sql
-- Every fact row must resolve through its reference key.
SELECT count(*) FROM nhambs.mbs_pools p
LEFT JOIN nhambs.issuers i ON i.issuer_id = p.issuer_id
WHERE i.issuer_id IS NULL;

SELECT count(*) FROM nhambs.mbs_pool_insurers x
LEFT JOIN nhambs.insurers i ON i.insurer_id = x.insurer_id
WHERE i.insurer_id IS NULL;

SELECT count(*) FROM nhambs.mbs_pool_locations x
LEFT JOIN nhambs.provinces p ON p.code = x.province
WHERE p.code IS NULL;

SELECT count(*) FROM nhambs.mbs_pool_scheduled_maturity x
LEFT JOIN nhambs.maturity_buckets b ON b.code = x.bucket
WHERE b.code IS NULL;

SELECT count(*) FROM nhambs.mbs_pool_rate_bands x
LEFT JOIN nhambs.rate_bands b ON b.band_key = x.rate_band
WHERE b.band_key IS NULL;
```

All five queries must return zero. Confirm that the grid view still returns one
row per pool and that the scheduled totals match the `total` bucket:

```sql
SELECT count(*) FROM nhambs.mbs_pools;
SELECT count(*) FROM nhambs.v_mbs_pool_grid;

SELECT p.cusip
FROM nhambs.mbs_pools p
LEFT JOIN nhambs.mbs_pool_scheduled_maturity s
    ON s.pool_cusip = p.cusip AND s.bucket = 'total'
WHERE s.id IS NULL;
```

`mbs_pool_rate_bands` intentionally has no unique constraint on
`(pool_cusip, rate_band)`: a handful of malformed source labels (for example
`6.01 - 5.25`) canonicalize onto the same bucket as a valid row. Both source
rows remain separate facts and the original labels are visible in `raw`.

Inspect those cases with:

```sql
SELECT pool_cusip, rate_band, count(*)
FROM nhambs.mbs_pool_rate_bands
GROUP BY 1, 2
HAVING count(*) > 1;
```

The rollback path is `dotnet run --project src/NHAMBS.Migrations -- --down`.
`M013.Down` restores the duplicated columns from the reference tables,
recreates the legacy indexes and views, and drops the reference tables. Run a
fresh MBS ETL afterwards so the reloaded facts use the restored layout.

## Idempotency

* Raw deletes a dataset before copying it, so a rerun replaces rather than
  duplicates the raw files.
* The conformed tables are truncated inside the transaction that loads it.
* Anomalies are written with the current `run_id`, so a failed run can be
  retried without mixing correction history.
* A conformed load with a bad file records the file as skipped/failed and either
  completes with a non-zero count or rolls back; it does not leave a partial
  pool set.

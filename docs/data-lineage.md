# Data lineage and layer policy

The warehouse has two data schemas: `raw` for landed source documents and
`nhambs` for the conformed tables, analytics views and ETL audit tables.

## Layer boundaries

| Layer | Schema | Contents | Retention | Reload behaviour |
| --- | --- | --- | --- | --- |
| Raw | `raw` | One row per source file: path, filename, SHA-256, size, source timestamp, ETL run id, and the unmodified JSON document. | Current file for each dataset. A dataset is replaced on the next successful load; the previous copy is not kept. | `DELETE` by dataset, then bulk COPY. The SHA-256 makes a changed source file visible. |
| Conformed tables | `nhambs` | Typed tables with natural keys, unique constraints and foreign keys (`mbs_pools` keyed by CUSIP, CHT by series, covered bonds by program/bond id). `source_file_id` points back to raw for the top-level facts. | Current snapshot of each dataset. Child rows follow their parent by `ON DELETE CASCADE`. | The MBS, CHT and covered-bond loaders truncate their datasets and reload one transaction at a time. A failed load rolls back the data changes. |
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
                │
                ▼
        NHAMBS.Server → NHAMBS.Client
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
The ETL writes the cleaned source name and normalized key, then resolves it
through `nhambs.resolve_issuer_alias`. Exact aliases are preferred; near
matches use PostgreSQL's `fuzzystrmatch` extension
(`levenshtein_less_equal` with a `dmetaphone` guard). Fact rows carry
`issuer_id` / `insurer_id`.

`nhambs.v_mbs_unresolved_entities` lists names that could not be resolved and is
the review queue for adding aliases or fixing a source scan.

## Idempotency

* Raw deletes a dataset before copying it, so a rerun replaces rather than
  duplicates the raw files.
* The conformed tables are truncated inside the transaction that loads it.
* Anomalies are written with the current `run_id`, so a failed run can be
  retried without mixing correction history.
* A conformed load with a bad file records the file as skipped/failed and either
  completes with a non-zero count or rolls back; it does not leave a partial
  pool set.

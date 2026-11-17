# Government bonds

The dashboard adds issue-level Government of Canada domestic marketable
securities to the MBS, CHT and covered-bond datasets.

## Scope

| Program | Level | Status | Source |
| --- | --- | --- | --- |
| Government of Canada domestic marketable bonds and treasury bills | Federal | Implemented | Bank of Canada Valet `GOC_OUTSTANDING` |
| Provincial direct and guaranteed bonds | Provincial | Assessed, not implemented | Statistics Canada Table 10-10-0130 provides monthly aggregate outstanding amounts by currency and issuer type, but not issue-level coupons or maturities. |
| CMHC Canada Mortgage Bonds | Agency | Already covered | `nhambs.cht_issues` and the CHT dashboard page. |
| Other agency bonds (EDC, FCC, BDC, etc.) | Agency | Assessed, not implemented | No single public consolidated feed with ISIN, coupon, maturity and outstanding amount. Individual issuer reports would need separate parsers. |

The federal program was selected first because it has a stable public API with
every field the domain model needs: issuer, program, ISIN, coupon, issue date,
maturity date, outstanding amount, inflation adjustment and instrument type.

## Source and cadence

Bank of Canada Valet group:

```text
https://www.bankofcanada.ca/valet/observations/group/GOC_OUTSTANDING/json
```

The group returns daily snapshots. Each observation is keyed by an as-of date
and either an aggregate/subtotal id or `{as_of}_{security_type}_{ISIN}`. The
ETL downloads the full response, lands it in `raw.source_documents` as
`government-bonds-goc`, and loads the latest as-of snapshot into
`nhambs.government_bond_issues`. Older snapshots remain in the raw JSON and can
be backfilled later without re-downloading.

Run:

```bash
dotnet run --project src/NHAMBS.Etl -- --dataset government-bonds
```

## Storage model

`nhambs.government_bond_programs`:

- `program_code` — `goc`.
- `name`, `issuer_name`, `government_level`, `country_code`.
- `source_name`, `source_url`, `cadence`, `notes`.
- `latest_as_of_date`, `last_loaded_at`.

`nhambs.government_bond_issues`:

- `program_code`, `isin`.
- `security_type` — `BOND` or `MM` (treasury bill).
- `instrument_type` — `BD-FIX`, `BD-REAL`, `MM-TBILL`, etc.
- `coupon_rate_percent`, `issue_date`, `maturity_date`.
- `outstanding_amount`, `inflation_adjustment`,
  `outstanding_inflation_adjusted`.
- `issuance_term_bucket`, `maturity_term_bucket`.
- `as_of_date`, `source_document_id`, `ingested_at`.

The unique key is `(program_code, isin, as_of_date)`, so a rerun replaces the
same daily snapshot without duplicating rows.

Views:

- `v_government_bond_latest` — issues at each program's latest as-of date.
- `v_government_bond_program_summary` — issue count, bond/T-bill/total
  outstanding and weighted average coupon.
- `v_government_bond_freshness` — latest as-of date, days since, stale after
  10 days.

## API and dashboard

`GetGovernmentBonds` returns the latest issues plus:

- total bond, T-bill and combined outstanding,
- issue and bond counts,
- weighted average coupon,
- outstanding by maturity year,
- outstanding by coupon bucket,
- outstanding by security type.

The `#/government-bonds` page shows the KPIs, maturity profile, coupon profile,
issue table and source card.

## Definitions and limitations

- Outstanding amounts are par value and exclude securities that have auctioned
  but not settled.
- Real-return bonds include an inflation adjustment; the dashboard shows the
  inflation-adjusted value in the issue table and uses the nominal coupon in the
  coupon profile.
- Treasury bills are included because the Valet group reports them alongside
  bonds. The page separates them in the KPIs and issue type column.
- Provincial and agency coverage remains a source-selection task rather than a
  parser gap. The candidate provincial aggregate table and agency limitations
  are listed above.

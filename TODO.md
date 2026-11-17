# NHAMBS TODO

## Objective

The overall idea is to locate MBS pools that are likely to be in distress and identify the companies exposed to them. The main factors are:

- Whether the loans are covered by a private insurer, since the 10% deductible changes who bears the first losses.
- Where the loans are located; Ontario and BC property values have fallen precipitously.
- How leveraged the loans are (LTV).
- The change in interest rate.
- Current arrears.
- Loan amortization.

These factors should guide the data acquisition, modelling, scoring, and dashboard work in the tasks below.

## 1. Switch to the medallion architecture

Rework the ETL and database layout into a raw landing layer and the conformed/analytics `nhambs` schema.

- [x] Define the boundary and retention policy for each layer. See `docs/data-lineage.md`.
- [x] **Raw:** land source files and parsed raw records without transformation, with ingestion metadata (source file, checksum, load timestamp, run ID).
- [x] **Conformed:** move cleaned, conformed, deduplicated records into typed tables in `nhambs`, with referential integrity and stable natural/business keys.
- [x] **Analytics views:** build the views and aggregates used by the API and dashboard from the conformed tables; they live in `nhambs`.
- [x] Keep the schemas to `raw` and `nhambs`, and update the repositories. SQLProvider LINQ reads the `nhambs` tables and views directly.
- [x] Make each layer reloadable/idempotent so a failed run can be retried without duplicating rows.
- [x] Document lineage from source file through `raw` to each `nhambs` table and analytics view.

## 2. Add information on other government bonds

- [ ] Identify the additional federal, provincial, and agency bond programs to cover.
- [ ] Add source acquisition and parsers for the selected programs.
- [ ] Extend the domain model and database schema for issuers, programs, issues, coupons, maturity dates, and outstanding amounts.
- [ ] Add API endpoints, repository queries, and dashboard views.
- [ ] Add fixtures and tests for the new sources.

## 3. Clean up the database to 5NF

- [x] Audit every table in the `nhambs` schema for repeating groups, transitive dependencies, and redundant columns. The first findings were repeated issuer/insurer names and duplicated pool-type/province labels.
- [ ] Extract issuers, insurers, programs, locations, rate bands, and other repeated attributes into reference tables. Issuers and insurers are now in `nhambs.issuers`/`nhambs.insurers` with stable ids; the remaining attributes still need the same treatment.
- [ ] Replace many-to-many relationships with junction tables and enforce foreign keys and uniqueness constraints.
- [ ] Remove derived or duplicated values from base tables; expose them through the `nhambs` analytics views instead.
- [ ] Write forward migrations that normalize existing data, with validation queries and a rollback plan.
- [ ] Update ETL loaders and typed queries to use the normalized model.

## 4. Fix issuers, insurers, and maturity years

- [x] Normalize issuer and insurer names: resolve aliases, correct spelling variants, deduplicate entities, and map each to a stable identifier. Stable keys live in `nhambs.issuers` and `nhambs.insurers`; canonical issuer names are resolved by joining `nhambs.issuer_aliases`, a migration-created reference table. Near matches use PostgreSQL `fuzzystrmatch` (`levenshtein_less_equal`/`dmetaphone`) instead of a custom edit-distance function.
- [x] Validate issuers and insurers against known reference lists and flag unresolved values for review. `nhambs.v_mbs_unresolved_entities` lists the remaining names.
- [x] Fix maturity-year decoding for the four-digit source codes. Examples: `0201` → 2011 and `2610` → 2010. The original note said 2016 for `2610`, but the final payment (`2010-11-15`) and weighted-average maturity (`2010-09-28`) for pool `97516363` anchor it to 2010.
- [x] Define and document the maturity-code format, including how the century is inferred. See `docs/data-lineage.md`.
- [x] Add unit tests covering representative codes and edge cases. `tests/NHAMBS.Tests/AnomalyTests.fs` and `NormalizationTests.fs` cover malformed years and entity normalization.
- [x] Re-run the affected ETL steps and verify the corrected values in the `nhambs` analytics views.

## 5. Fix remaining files

- [x] Triage the grouped failures in `failed.csv` (145 rows), starting with the most common causes: missing coupon, missing pool number, missing aggregate principal amount, and missing Weighted Average Interest Adjustment Date. The resumable `out/pools/batch-report.json` supersedes the old CSV and is the current triage list.
- [x] Fix the parsers and source extraction rules for each failure category. Date-year repair, OCR month/day variants, fuzzy issuer matching and the WAM/WAIA anchors are implemented.
- [x] Add representative failing files as regression fixtures. Malformed `0201`/`2610` years, OCR issuer variants and insurer boilerplate are covered by unit tests.
- [x] Re-run the ETL until every remaining file is either loaded successfully or recorded with a documented, actionable reason for exclusion. See `docs/parse-exclusions.md`: 69,746 parsed, 47 skipped with reasons, 24 pending because the CMHC URLs return 404, and 3 failed because the downloaded files are corrupt or encrypted.

## 6. Full review of the user interface

- [ ] Review every screen in the client for layout, navigation, and visual consistency.
- [ ] Check loading, empty, error, and partial-data states on each view.
- [ ] Verify tables, charts, filters, sorting, and pagination work correctly with real data.
- [ ] Review data formatting for currency, percentages, dates, and missing values.
- [ ] Check responsiveness at desktop, tablet, and mobile breakpoints.
- [ ] Audit accessibility: keyboard navigation, focus states, labels, contrast, and semantic markup.
- [ ] Test the main workflows end to end in supported browsers.
- [ ] Record findings and fix the issues, adding regression checks where practical.

## 7. Add information on the expected renewal rate

- [ ] Define what "expected renewal rate" means for the relevant products and how it should be calculated.
- [ ] Identify the source data and assumptions required to estimate renewals.
- [ ] Implement the calculation and persist the results with the relevant pool/product and reporting period.
- [ ] Add the renewal rate to the API and surface it in the dashboard with clear labels and methodology notes.
- [ ] Validate the estimates against historical renewal behaviour and document the limitations.

## 8. Add arrears information from CMHC and the CBA

- [ ] Identify the relevant CMHC and Canadian Bankers Association (CBA) arrears datasets, including their definitions, coverage, and publication cadence.
- [ ] Add source acquisition and parsers for both providers.
- [ ] Model arrears consistently across providers: reporting period, geography, product/loan type, arrears stage, and rate or count.
- [ ] Reconcile definitional differences between CMHC and CBA data and document the mapping.
- [ ] Add API endpoints and dashboard views for arrears trends and comparisons.
- [ ] Add fixtures and tests for the new sources, and flag stale or missing reporting periods.

## 9. Add HPI data from Teranet, CREA, and RPS

- [ ] Identify the available Teranet, CREA/real estate board, and RPS HPI series, including licensing terms, geography, frequency, and index methodology.
- [ ] Add source acquisition and parsers for each provider.
- [ ] Model the indices consistently: provider, index type, geography, reporting period, base period, and value.
- [ ] Reconcile differences in geography and methodology between providers and document them.
- [ ] Add API endpoints and dashboard views for HPI levels, changes, and cross-provider comparisons.
- [ ] Add fixtures and tests for the new sources, and flag stale or missing reporting periods.

## Distress indicators summary

Additional signals worth tracking alongside the factors in the objective:

- **Delinquency roll and cure rates** — 30→60→90→foreclosure transitions and cure percentages, not just the arrears level.
- **Current LTV and negative equity** — original LTV adjusted for amortization and current HPI, especially near or above 100%.
- **Renewal/payment shock** — share renewing soon, reset rate versus contract rate, and payment increase.
- **Vintage/cohort** — originations from peak-price, low-rate periods.
- **Borrower credit** — current bureau score, score migration, GDS/TDS, loan-to-income, and prior delinquency.
- **Occupancy and property type** — investor-owned, rental, secondary homes, and condo concentration.
- **Amortization and prepayment stress** — negative amortization, modifications, and low prepayment speeds in high-LTV pools.
- **Local conditions** — unemployment, insolvency, housing liquidity, and rental market weakness at the FSA/CMA level.
- **Counterparty signals** — bank provisions and impaired mortgages, insurer loss ratios, ratings/CDS, and exposure concentration.

Combine these into a small scorecard and backtest it against observed arrears and claims; avoid double-counting correlated factors such as HPI, LTV, and unemployment.

## Use PostgreSQL Agent Skill

https://github.com/supabase/agent-skills/skills/supabase-postgres-best-practices

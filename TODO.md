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

- [x] Identify the additional federal, provincial, and agency bond programs to cover. The federal program is issue-level Government of Canada domestic marketable bonds and treasury bills; provincial coverage uses issue-level Provincial Bond Purchase Program purchases, and agency candidates and data limitations are documented in `docs/government-bonds.md`.
- [x] Add source acquisition and parsers for the selected programs. The Bank of Canada Valet `GOC_OUTSTANDING` and `PBPP_TRANSACTION_DATA` feeds are downloaded, landed in `raw.source_documents`, and parsed into federal issue and provincial transaction rows.
- [x] Extend the domain model and database schema for issuers, programs, issues, coupons, maturity dates, and outstanding amounts. `government_bond_programs`, `government_bond_issues` and `provincial_bond_transactions` carry program, ISIN, instrument type, coupon, issue/maturity dates, outstanding or purchased amount, inflation adjustment, purchase yield and as-of/trade date.
- [x] Add API endpoints, repository queries, and dashboard views. `GetGovernmentBonds` and the `#/government-bonds` page show federal totals, weighted coupon, maturity/coupon profiles, the federal issue table, and PBPP provincial purchase summaries and issues.
- [x] Add fixtures and tests for the new sources. `tests/NHAMBS.Tests/GovernmentBondTests.fs` covers latest-snapshot selection, field mapping and fixed-rate inflation handling.

## 3. Clean up the database to 5NF

- [x] Audit every table in the `nhambs` schema for repeating groups, transitive dependencies, and redundant columns. The findings were repeated issuer/insurer names, duplicated pool-type/province labels, free-text rate bands, and scheduled-maturity totals copied onto `mbs_pools`.
- [x] Extract issuers, insurers, programs, locations, rate bands, and other repeated attributes into reference tables. Issuers and insurers live in `nhambs.issuers`/`nhambs.insurers`; `nhambs.provinces`, `nhambs.rate_types`, `nhambs.pool_types`, `nhambs.rate_bands` and `nhambs.maturity_buckets` hold the remaining MBS dimensions.
- [x] Replace many-to-many relationships with junction tables and enforce foreign keys and uniqueness constraints. `mbs_pool_insurers`, `mbs_pool_locations` and `mbs_pool_scheduled_maturity` are keyed by pool plus dimension and have unique constraints; all dimension keys are foreign keys.
- [x] Remove derived or duplicated values from base tables; expose them through the `nhambs` analytics views instead. `mbs_pools` no longer stores issuer text, mortgage-insurer text, or scheduled-maturity totals; the views join `issuers`, `insurers` and the `total` maturity bucket.
- [x] Write forward migrations that normalize existing data, with validation queries and a rollback plan. See `M013_ReferenceDimensions` and `docs/data-lineage.md`.
- [x] Update ETL loaders and typed queries to use the normalized model. The MBS loader resolves references before COPY and the repositories join the reference tables.

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

- [x] Review every screen in the client for layout, navigation, and visual consistency. All five routes were reviewed at desktop, tablet and mobile widths.
- [x] Check loading, empty, error, and partial-data states on each view. Loading and error rendering were verified; an impossible pool search shows the grid empty state.
- [x] Verify tables, charts, filters, sorting, and pagination work correctly with real data. Search, issuer filter, sort, page navigation, reset and the pool-detail drawer were exercised against 69,746 pools.
- [x] Review data formatting for currency, percentages, dates, and missing values. Missing dates and text render as empty or `—`; currency and percentage formatting was spot-checked.
- [x] Check responsiveness at desktop, tablet, and mobile breakpoints. No document-level horizontal overflow at 1440, 768 or 390 pixels.
- [x] Audit accessibility: keyboard navigation, focus states, labels, contrast, and semantic markup. Added missing names, dialog semantics, live regions and a mobile close control; no unnamed interactive nodes remain in the Chrome accessibility tree.
- [x] Test the main workflows end to end in supported browsers. Playwright against Chromium exercised every route and the main pool workflows.
- [x] Record findings and fix the issues, adding regression checks where practical. See `docs/ui-review.md`; the nullable-option API failure was fixed by switching the Fable.Remoting transport to MessagePack.

## 7. Add information on the expected renewal rate

- [ ] Define what "expected renewal rate" means for the relevant products and how it should be calculated.
- [ ] Identify the source data and assumptions required to estimate renewals.
- [ ] Implement the calculation and persist the results with the relevant pool/product and reporting period.
- [ ] Add the renewal rate to the API and surface it in the dashboard with clear labels and methodology notes.
- [ ] Validate the estimates against historical renewal behaviour and document the limitations.

## 8. Add arrears information from CMHC and the CBA

- [x] Identify the relevant CMHC and Canadian Bankers Association (CBA) arrears datasets, including their definitions, coverage, and publication cadence. See `docs/arrears.md`: CBA chartered-bank monthly counts/rates and CMHC/Equifax quarterly all-lender rates.
- [x] Add source acquisition and parsers for both providers. The arrears ETL resolves the current CBA XLSX link and CMHC report-download API URL, lands the workbooks in `raw.source_documents`, and parses them with ExcelDataReader.
- [x] Model arrears consistently across providers: reporting period, geography, product/loan type, arrears stage, and rate or count. `nhambs.arrears_observations` stores source, period, geography, lender type, insured status, product, 90+ day stage and metric.
- [x] Reconcile definitional differences between CMHC and CBA data and document the mapping. See `docs/arrears.md`.
- [x] Add API endpoints and dashboard views for arrears trends and comparisons. `GetArrears` and the `#/arrears` page show the national comparison, latest geography rates and source definitions.
- [x] Add fixtures and tests for the new sources, and flag stale or missing reporting periods. `tests/NHAMBS.Arrears.Tests` covers both parsers; `v_arrears_freshness` marks sources stale after 120 monthly or 180 quarterly days.

## 9. Add HPI data from Teranet, CREA, and RPS

- [ ] Identify the available Teranet, CREA/real estate board, and RPS HPI series, including licensing terms, geography, frequency, and index methodology.
- [ ] Add source acquisition and parsers for each provider.
- [ ] Model the indices consistently: provider, index type, geography, reporting period, base period, and value.
- [ ] Reconcile differences in geography and methodology between providers and document them.
- [ ] Add API endpoints and dashboard views for HPI levels, changes, and cross-provider comparisons.
- [ ] Add fixtures and tests for the new sources, and flag stale or missing reporting periods.

## 10. Correct the market-data and securities taxonomy

- [x] Document the classification rule: **securities** are instruments with an issuance identity such as a CUSIP/ISIN (NHA MBS pools, Canada Mortgage Bonds, covered bonds, government bonds); **market data** is a rate, price, curve or arrears series used to monitor or value those securities (overview, forward curves, mortgage arrears).
- [x] Add a `PageSection` type and a single `pageSection` mapping in `src/NHAMBS.Client/src/Components.fs` (`MarketData` vs `Securities`) so classification is defined once instead of hard-coded in the sidebar. `navigationSections` drives the rendered order.
- [x] In `src/NHAMBS.Client/src/App.fs`, drive the sidebar from that mapping:
  - **Market data:** Overview, Forward curves, Mortgage arrears
  - **Securities:** NHA MBS pools, Canada Mortgage Bonds, Covered bonds, Government bonds
- [x] Move **NHA MBS pools** from Market to Securities and **Mortgage arrears** from Securities to Market data. Keep **Forward curves** under Market data; no move is otherwise required.
- [x] Rename the sidebar label from `Market` to `Market data` for clarity.
- [x] Update `README.md`, `docs/ui-review.md`, `docs/arrears.md`, and any feature lists or page descriptions that blur market data and securities.
- [x] Add regression checks asserting the sidebar section contents and order. `tests/NHAMBS.Client.Tests` covers the shared navigation model used by desktop and mobile; the mobile menu was also exercised in Chromium.

## 11. Add geography selection to the mortgage-arrears panel

- [x] Expose **every loaded geography** from `nhambs.arrears_geographies` in the selector, not a hard-coded province list. Group them by level: Canada, provinces or territories, regions (Atlantic/Territories), and CMAs, so newly ingested geographies appear automatically.
- [x] Extend the shared arrears DTOs in `src/NHAMBS.Shared/Domain.fs`:
  - added `ArrearsGeographyInfo` with code, name, level, province code, sort order and available source codes;
  - added `GeographyCode` to `ArrearsSeriesPoint` and a `Series` collection to `ArrearsData`, so the client can render a selected geography’s history.
- [x] Replace the national-only `v_arrears_provider_comparison` query in `src/NHAMBS.Data/ArrearsRepository.fs` with a query over `v_arrears_series` filtered to `metric = 'arrears_rate_percent'`. No database migration was needed because the view already carries geography.
- [x] Return all geography series in one `GetArrears` call and filter in the client, so switching is instant. The API contract and database schema are unchanged.
- [x] Add a labelled geography selector above the KPIs in `src/NHAMBS.Client/src/Arrears.fs`:
  - defaults to Canada;
  - lists every loaded geography in a stable order, using grouped options for country, provinces or territories, regions and CMAs;
  - uses explicit level labels so a CMA such as Toronto is not mistaken for a province.
- [x] Add Elmish state for the selected geography (`ArrearsGeography` and `ArrearsGeographyChanged`) so the choice survives refresh and navigation. Hash-route persistence was left optional and is not implemented.
- [x] Make the whole panel geography-aware: KPI cards, chart title and accessibility label, coverage note, and latest-rate table all reflect the selected geography rather than showing national values under a provincial or CMA heading.
- [x] Handle provider coverage gaps explicitly:
  - CBA has no separate New Brunswick, Nova Scotia, Prince Edward Island or Newfoundland and Labrador series;
  - CBA has no CMA series, while CMHC does;
  - CMHC has no Atlantic or Territories aggregate.
  - Only available provider series are charted, with a short note explaining each gap. Geographies with no observations render an empty state rather than national values.
- [x] Keep the “latest rates by geography” table as an all-geography comparison, highlighting the selected rows so users can jump between geographies.
- [x] Add tests for geography grouping and ordering, the provider-gap notes and coverage labels, plus navigation taxonomy. The repository result was verified against the loaded database; the all-series loading model makes per-geography repository filtering unnecessary.
- [x] Add a UI check that switching geographies updates the KPIs, chart and table, and that an unavailable-provider case renders cleanly. Chromium checks covered Canada, Ontario, Toronto, New Brunswick, Atlantic and the mobile menu.
- [x] Update `docs/arrears.md` and `README.md` with the selector coverage and provider-gap behaviour.

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

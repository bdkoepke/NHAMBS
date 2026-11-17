# Mortgage arrears data

The dashboard combines two public 90+ day mortgage-arrears series:

| Provider | Dataset | Cadence | Coverage | Lender population | Metric |
| --- | --- | --- | --- | --- | --- |
| Canadian Bankers Association (CBA) | Number of Residential Mortgages in Arrears | Monthly | Canada, Atlantic, Quebec, Ontario, Manitoba, Saskatchewan, Alberta, British Columbia, Territories; 1995 to present | Chartered-bank residential mortgages | Total mortgages, mortgages in arrears, arrears rate |
| Canada Mortgage and Housing Corporation (CMHC) | Mortgage Delinquency Rate: Canada, Provinces and CMAs | Quarterly | Canada, provinces, selected CMAs; 2012 Q3 to 2025 Q4 | All lender types, sourced from Equifax Canada | Arrears rate |

Both series define arrears as payments overdue by 90 days or more (CBA's
published definition is "three or more months"). They are not directly
interchangeable:

- CBA reports **counts and rates** for the chartered-bank population. The
  national rate is the number of mortgages in arrears divided by the total
  number of residential mortgages at the reporting banks.
- CMHC reports a **rate only**, sourced from Equifax, covering all lender types.
  It includes non-bank lenders that are outside the CBA series.
- CBA publishes monthly, CMHC quarterly. The dashboard shows both on a
  calendar-time axis rather than forcing them onto a shared period index.
- CBA aggregates the Atlantic provinces and the territories; CMHC publishes
  individual provinces and selected CMAs.

## Storage model

`nhambs.arrears_observations` is a long, provider-neutral fact table. The key is
`(source_code, period_end, geography_code, lender_type, insured_status,
product_type, arrears_stage, metric)`. A row stores one metric value:

- `arrears_rate_percent` — 90+ day arrears rate, in percent.
- `arrears_count` — number of mortgages in arrears.
- `mortgage_count` — total number of residential mortgages in the denominator.

The fact columns make the definitional differences explicit:

- `source_code` — `cba` or `cmhc`.
- `lender_type` — `chartered_banks` for CBA, `all_lenders` for CMHC.
- `insured_status` — currently `all` for both.
- `product_type` — `residential_mortgage`.
- `arrears_stage` — `90_plus_days`.

Reference tables:

- `nhambs.arrears_sources` — provider, dataset, definition, cadence, source URL,
  attribution, latest period and last load time.
- `nhambs.arrears_geographies` — country, province, region and CMA keys. CMA
  keys use a `cma_` prefix (for example `cma_toronto`).

Views:

- `v_arrears_series` — joined observations with provider and geography names.
- `v_arrears_latest` — latest observation per source, geography and metric.
- `v_arrears_provider_comparison` — national rate comparison.
- `v_arrears_freshness` — latest period, days since period end and a stale flag.

## Acquisition and parsing

Run:

```bash
dotnet run --project src/NHAMBS.Etl -- --dataset arrears
```

The ETL resolves the current workbook URLs at runtime:

- CBA: scrapes the current XLSX link from
  <https://cba.ca/article/mortgages-in-arrears>.
- CMHC: reads the `ReportDocumentId` from the CMHC data-table page and calls the
  CMHC report-download API for the current asset URL.

The workbooks are landed as binary documents in `raw.source_documents` under the
`arrears-cba` and `arrears-cmhc` datasets, parsed with ExcelDataReader, and
loaded in one transaction. The CBA parser reads both side-by-side date blocks
(1995–2010 and 2011 onward), normalises dates to month ends, treats `*` cells as
suppressed, and converts the published fraction to percent. The CMHC parser
reads the quarterly matrix and maps province and CMA names to stable keys.

## Freshness

`v_arrears_freshness` marks a source stale when:

- it has no observations;
- it is monthly and the latest period ended more than 120 days ago; or
- it is quarterly and the latest period ended more than 180 days ago.

The 120/180-day windows allow for normal publication lag. The current CMHC file
ends at 2025 Q4 and is flagged stale until CMHC publishes a newer table; CBA is
current through August 2026. The UI shows the stale flag on the source card.

## Known limitations

- The CBA article reports the latest national rate in its narrative. The ETL
  reads the workbook instead, which is the authoritative series and includes
  history and provinces.
- The CMHC 2002–2018 Q1 arrears-and-foreclosure workbook is archived and is not
  ingested; the current CMHC delinquency table starts in 2012 Q3.
- Counts are not available from CMHC, so the comparison view uses rates only.
- CMA boundaries follow Equifax/Statistics Canada definitions and are not
  reconciled to the province keys in `mbs_pool_locations`.

# NHA MBS parse exclusions

The resumable report at `out/pools/batch-report.json` is the source of truth for
what was loaded. After the parser/OCR changes, the last full run produced:

| Outcome | Files | Meaning |
| --- | ---: | --- |
| Parsed | 69,746 | JSON written to `out/pools/` and loaded into `nhambs`. |
| Skipped | 47 | Source is not a usable MBS circular; documented below. |
| Pending | 24 | Manifest URL is not present on the CMHC host (HTTP 404) or the file was never downloaded. |
| Failed | 3 | Downloaded files are corrupt, encrypted or not PDFs. |

## Skipped files

### Not an NHA MBS information circular (16)

These are attachment pages, schedules or portfolio listings, not a circular
with a pool number and coupon. The parser rejects them before OCR:

`62941ZDA9`, `62954ZFM5`, `62955Z3Q8`, `62959ZQR7`, `62963ZEU7`, `62964ZMB9`,
`62964ZTE6`, `62964ZXN1`, `62966ZGJ7`, `62968ZUB6`, `62973ZB53`, `62976ZQW5`,
`62980ZAY2`, `62985ZC58`, `62985ZR45`, `62994ZKU3`.

### Coupon field is blank in the source (12)

The printed coupon line has no rate, so loading the pool would require
inventing data:

`62918ZLS0`, `62918ZLT8`, `62918ZLU5`, `62918ZLV3`, `62930ZNR4`, `62936Z5Z0`,
`62936Z6A4`, `62936Z6B2`, `62936Z6C0`, `62938ZFX2`, `62938ZFY0`, `62938ZFZ7`.

### OCR cannot read a required weighted-average date (3)

The addendum is a low-quality scan and even 300/150 dpi retries do not produce
the `Weighted Average Maturity Date` label or value:

`62918ZBB8`, `62918ZBC6`, `62918ZBF9`.

### OCR cannot read the loan count (1)

`62918ZAY9` — the total-number-of-mortgage-loans value is not legible in the
scan and no retry recovers it.

### Dates remain internally inconsistent (15)

The source scan has dates that cannot be reconciled even after year repair
(for example a maturity date equal to the issue date and a final payment before
it). These files previously loaded with a guessed final-payment year; they are
now excluded instead of publishing a wrong maturity:

`62917ZB52`, `62917ZUK8`, `62917ZXY5`, `62918ZUX9`, `62919ZK84`,
`62921ZSB5`, `62924ZQH1`, `62933ZSQ8`, `62936ZT27`, `62937ZBU3`,
`62937ZGP9`, `62937ZX96`, `62937ZY20`, `62937ZY38`, `62938ZKU2`.

### Source file is not a usable PDF (3)

- `62922ZS24.pdf` — encrypted PDF (password required).
- `62955ZFL6.pdf` and `62937ZP46.pdf` — the downloaded bytes are not a PDF
  document. Re-download from the manifest URL when CMHC republishes them.

## Pending files

The manifest lists 24 PDFs that are not on the CMHC asset host (HTTP 404). They
remain `pending` so a future download can retry them if CMHC republishes the
files. Re-run:

```bash
dotnet run --project src/NHAMBS.Cli -- batch --types nhambs --download \
  --pools-directory downloads/pools --pools-urls src/NHAMBS.Mbs/pool_urls.txt
dotnet run --project src/NHAMBS.Cli -- batch --types nhambs --process \
  --pools-directory downloads/pools --pools-out out/pools \
  --pools-urls src/NHAMBS.Mbs/pool_urls.txt
```

Skipped entries are intentionally not retried by the batch command; remove the
entry from `batch-report.json` if a source file is replaced with a better scan.

# User-interface review

The review covered every route in the Fable client at desktop (1440x900),
tablet (768x1024) and mobile (390x844) widths:

| Route | Screen |
| --- | --- |
| `#/overview` | Market overview |
| `#/pools` | Pool explorer and pool-detail drawer |
| `#/forward-curves` | Forward curves |
| `#/cht` | Canada Mortgage Bonds |
| `#/covered-bonds` | Covered bond registry |
| `#/arrears` | Mortgage arrears comparison |

## Findings fixed

- **Pool, CHT and covered-bond data failed to decode after nullable fields
  became F# options.** Fable.SimpleJson under Fable 5 classifies
  `FSharpOption` as a union and expects `None`/`Some` objects, while the JSON
  server emitted bare values and `null`. Fable.Remoting now uses MessagePack
  binary serialization on both the client and server, which preserves the
  option values and the nullable API contract.
- **The mobile navigation could be opened but not closed.** Added a close
  button inside the sidebar and a click-away backdrop; the menu toggle now
  reports `aria-expanded`.
- **Several custom controls had no accessible name.** Added labels to the grid
  quick filter, grouping and pivot selects, the primary navigation, the refresh
  icon, the mobile menu toggle and the pool-detail drawer.
- **Loading and error states were silent to assistive technology.** The loading
  block is now a live `status` region and the error block is an `alert`.
- **The pool-detail drawer had no dialog semantics.** It now exposes
  `role="dialog"`, `aria-modal` and an accessible close control.
- **The new mortgage-arrears view needed source context.** It shows both
  provider definitions, cadence, latest period and a stale badge next to the
  national comparison chart and latest-rate table.

## Checks performed

- All six routes render without console errors or document-level horizontal
  overflow at the three viewport widths.
- Pool search, issuer filtering, coupon sorting, pagination, reset, CSV export
  and the detail drawer were exercised against the full 69,746-pool data set.
- An impossible search returns the AG Grid empty-state overlay (`No records
  match the current filters.`) and a zero-row count rather than an error.
- The mobile menu opens, closes from its close button and closes from the
  backdrop.
- The Chrome accessibility tree contains no unnamed text boxes, comboboxes,
  buttons or checkboxes on any route.
- The arrears page was checked against the loaded CBA and CMHC data: the
  national comparison chart, latest geography table, source cards and stale
  flag all render correctly.
- Chart and table values were spot-checked for currency, percentage, date and
  missing-value formatting.

## Known limitations

- The MBS analytics endpoint takes roughly 7–9 seconds for the full,
  unfiltered data set because the view aggregation runs in F# over 69,746
  typed rows. The loading state is explicit, but server-side aggregates or a
  short-lived cache would be the next performance step.
- AG Grid renders its own internal checkbox controls; their accessible names
  come from the grid library rather than this application.

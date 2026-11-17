module NHAMBS.Etl.GovernmentBondParser

open System
open System.Globalization
open System.Text.Json
open NHAMBS.Etl.GovernmentBondTypes

let private tryProperty (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Object ->
        match value.TryGetProperty "v" with
        | true, inner -> Some inner
        | _ -> None
    | _ -> None

let private textValue (name: string) (element: JsonElement) =
    tryProperty name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.String -> Option.ofObj (value.GetString())
        | JsonValueKind.Number -> Some(value.GetRawText())
        | _ -> None)

let private decimalValue (name: string) (element: JsonElement) =
    textValue name element
    |> Option.bind (fun value ->
        match Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture) with
        | true, parsed -> Some parsed
        | _ -> None)

let private dateValue (name: string) (element: JsonElement) =
    textValue name element
    |> Option.bind (fun value ->
        match DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
        | true, parsed -> Some parsed.Date
        | _ -> None)

let private observationDate (element: JsonElement) = dateValue "DOM_DBT_AS_OF_DATE" element

let private issue (asOf: DateTime) (element: JsonElement) =
    match
        textValue "DOM_DBT_ISIN" element,
        textValue "DOM_DBT_SECURITY_TYPE" element,
        textValue "DOM_DBT_INSTRUMENT_TYPE" element,
        decimalValue "DOM_DBT_COUPON_RATE" element,
        dateValue "DOM_DBT_ISSUE_DATE" element,
        dateValue "DOM_DBT_MATURITY_DATE" element,
        decimalValue "DOM_DBT_OUTSTANDING_AMOUNT" element,
        decimalValue "DOM_DBT_OUTSTANDING_AMOUNT_INF_ADJ" element
    with
    | Some isin,
      Some securityType,
      Some instrumentType,
      Some coupon,
      Some issueDate,
      Some maturityDate,
      Some outstanding,
      Some outstandingInflationAdjusted ->
        Some
            {
                ProgramCode = "goc"
                Isin = isin
                SecurityType = securityType
                InstrumentType = instrumentType
                CouponRatePercent = coupon
                IssueDate = issueDate
                MaturityDate = maturityDate
                OutstandingAmount = outstanding
                InflationAdjustment = decimalValue "DOM_DBT_INFLATION_ADJUSTMENT" element
                OutstandingInflationAdjusted = outstandingInflationAdjusted
                IssuanceTermBucket = textValue "DOM_DBT_ISSUANCE_TERM_BUCKET" element
                MaturityTermBucket = textValue "DOM_DBT_MATURITY_TERM_BUCKET" element
                AsOfDate = asOf
            }
    | _ -> None

/// Parse the Bank of Canada Valet `GOC_OUTSTANDING` group response. The group
/// returns daily snapshots; only the latest as-of date is loaded as the current
/// issue set. Older snapshots remain available in the raw source document.
let parseGocOutstanding (json: string) : GovernmentBondParseResult =
    use document = JsonDocument.Parse json

    match document.RootElement.TryGetProperty "observations" with
    | false, _ ->
        {
            Issues = [||]
            AsOfDate = None
            Warnings = [| "The Valet response has no observations array." |]
        }
    | true, observations ->
        let rows = observations.EnumerateArray() |> Seq.toArray

        let latestAsOf =
            rows |> Array.choose observationDate |> Array.sortDescending |> Array.tryHead

        match latestAsOf with
        | None ->
            {
                Issues = [||]
                AsOfDate = None
                Warnings = [| "No observation has an as-of date." |]
            }
        | Some asOf ->
            let parsed =
                rows
                |> Array.filter (fun row -> observationDate row = Some asOf)
                |> Array.choose (issue asOf)

            let warnings =
                if parsed.Length = 0 then
                    [|
                        sprintf
                            "No issue-level rows found for %s."
                            (asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    |]
                else
                    [||]

            {
                Issues =
                    parsed
                    |> Array.distinctBy (fun item -> item.Isin)
                    |> Array.sortBy (fun item -> item.MaturityDate, item.Isin)
                AsOfDate = Some asOf
                Warnings = warnings
            }

let private provinceKey (issuerName: string) =
    let normalized =
        issuerName.Normalize(System.Text.NormalizationForm.FormD)
        |> Seq.filter (fun character ->
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory character
            <> System.Globalization.UnicodeCategory.NonSpacingMark)
        |> Array.ofSeq
        |> String
        |> fun value -> value.ToLowerInvariant()
        |> fun value -> System.Text.RegularExpressions.Regex.Replace(value, @"[^a-z]+", " ")
        |> fun value -> value.Trim()

    if normalized.Contains "alberta" then
        Some "alberta"
    elif normalized.Contains "british columbia" then
        Some "britishColumbia"
    elif normalized.Contains "manitoba" then
        Some "manitoba"
    elif normalized.Contains "new brunswick" then
        Some "newBrunswick"
    elif normalized.Contains "newfoundland" then
        Some "newfoundland"
    elif normalized.Contains "nova scotia" then
        Some "novaScotia"
    elif normalized.Contains "ontario" then
        Some "ontario"
    elif normalized.Contains "prince edward island" then
        Some "princeEdwardIsland"
    elif normalized.Contains "quebec" then
        Some "quebec"
    elif normalized.Contains "saskatchewan" then
        Some "saskatchewan"
    elif normalized.Contains "yukon" then
        Some "yukon"
    elif normalized.Contains "northwest territories" then
        Some "northwestTerritories"
    elif normalized.Contains "nunavut" then
        Some "nunavut"
    else
        None

/// Parse the Bank of Canada Provincial Bond Purchase Program transaction
/// observations. Each row is one purchase; multiple rows can refer to the same
/// ISIN on different trade dates.
let parsePbppTransactions (json: string) : ProvincialBondParseResult =
    use document = JsonDocument.Parse json

    match document.RootElement.TryGetProperty "observations" with
    | false, _ ->
        {
            Transactions = [||]
            LatestTradeDate = None
            Warnings = [| "The PBPP response has no observations array." |]
        }
    | true, observations ->
        let rows = observations.EnumerateArray() |> Seq.toArray

        let transactions =
            rows
            |> Array.choose (fun row ->
                match
                    textValue "PBPP_TRANSACTION_ISIN" row,
                    textValue "PBPP_TRANSACTION_ISSUER_NAME" row,
                    decimalValue "PBPP_TRANSACTION_COUPON" row,
                    dateValue "PBPP_TRANSACTION_SECURITY_MATURITY_DATE" row,
                    dateValue "PBPP_TRANSACTION_TRADE_DATE" row,
                    decimalValue "PBPP_TRANSACTION_AMOUNT_PURCHASED" row
                with
                | Some isin, Some issuerName, Some coupon, Some maturityDate, Some tradeDate, Some amountPurchased ->
                    Some
                        {
                            IssuerName = issuerName
                            ProvinceCode = provinceKey issuerName
                            Isin = isin
                            CouponRatePercent = coupon
                            MaturityDate = maturityDate
                            TradeDate = tradeDate
                            SettlementDate = dateValue "PBPP_TRANSACTION_SETTLEMENT_DATE" row
                            AmountPurchased = amountPurchased
                            PurchasePrice = decimalValue "PBPP_TRANSACTION_PURCHASE_PRICE" row
                            PurchaseYieldPercent = decimalValue "PBPP_TRANSACTION_PURCHASE_YIELD" row
                        }
                | _ -> None)
            |> Array.distinctBy (fun item -> item.Isin, item.TradeDate, item.AmountPurchased, item.PurchasePrice)
            |> Array.sortBy (fun item -> item.TradeDate, item.Isin)

        let latestTradeDate =
            transactions
            |> Array.map (fun item -> item.TradeDate)
            |> Array.sortDescending
            |> Array.tryHead

        let unknownIssuers =
            transactions
            |> Array.filter (fun item -> item.ProvinceCode.IsNone)
            |> Array.map (fun item -> item.IssuerName)
            |> Array.distinct

        let warnings =
            if transactions.Length = 0 then
                [| "No provincial bond transactions were parsed." |]
            elif unknownIssuers.Length > 0 then
                [|
                    sprintf "Unmapped provincial issuers: %s" (String.concat ", " unknownIssuers)
                |]
            else
                [||]

        {
            Transactions = transactions
            LatestTradeDate = latestTradeDate
            Warnings = warnings
        }

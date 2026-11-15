module NHAMBS.Client.PoolTypes

open System

/// Plain-language descriptions of the three-digit CMHC NHA MBS pool types.
/// Source: CMHC NHA MBS Guide, "Pool Types" chapter.
type PoolTypeInfo =
    {
        Code: string
        Name: string
        Detail: string
        Legacy: bool
    }

let all =
    [|
        {
            Code = "867"
            Name = "Homeowner – collateral fixed"
            Detail =
                "Fixed-rate loans secured by collateral mortgages on properties with up to four self-contained units. Prepayment penalties and indemnities are retained by the issuer."
            Legacy = false
        }
        {
            Code = "880"
            Name = "Homeowner – adjustable CDOR"
            Detail =
                "Loans on properties with up to four self-contained units. The coupon resets monthly off one-month CDOR and payments adjust so the amortization schedule is preserved. Prepayment penalties and indemnities are retained by the issuer."
            Legacy = true
        }
        {
            Code = "881"
            Name = "Homeowner – adjustable CORRA"
            Detail =
                "Loans on properties with up to four self-contained units. The coupon resets monthly off one-month daily compounded CORRA and payments adjust so the amortization schedule is preserved. Prepayment penalties and indemnities are retained by the issuer."
            Legacy = false
        }
        {
            Code = "885"
            Name = "Homeowner – variable CDOR"
            Detail =
                "Loans on properties with up to four self-contained units. The coupon resets monthly off one-month CDOR while loan payments stay fixed, so the amortization schedule can change. Prepayment penalties and indemnities are retained by the issuer."
            Legacy = true
        }
        {
            Code = "886"
            Name = "Homeowner – variable CORRA"
            Detail =
                "Loans on properties with up to four self-contained units. The coupon resets monthly off one-month daily compounded CORRA while loan payments stay fixed, so the amortization schedule can change. Prepayment penalties and indemnities are retained by the issuer."
            Legacy = false
        }
        {
            Code = "964"
            Name = "Homeowner – fixed"
            Detail =
                "Fixed-rate loans on properties with up to four self-contained units. Prepayment penalties and indemnities are paid to investors."
            Legacy = false
        }
        {
            Code = "965"
            Name = "Multiple-family – prepayable with indemnity"
            Detail =
                "Fixed-rate loans on residential properties with more than four units, including social housing. May include loans that can be fractured or prepaid with a penalty; investors receive an indemnity calculated under the CMHC 2834 methodology."
            Legacy = false
        }
        {
            Code = "966"
            Name = "Multiple-family – closed"
            Detail =
                "Fixed-rate loans on insured multiple-family, small rental, and social housing projects. Loans are closed to prepayment and cannot be liquidated without CMHC approval; early repayments pay investors an indemnity based on the NHA MBS coupon."
            Legacy = false
        }
        {
            Code = "967"
            Name = "Homeowner – fixed"
            Detail =
                "Fixed-rate loans on properties with up to four self-contained units. Prepayment penalties and indemnities are retained by the issuer."
            Legacy = false
        }
        {
            Code = "970"
            Name = "Homeowner – fixed with indemnity"
            Detail =
                "Fixed-rate loans on properties with up to four self-contained units. Indemnities are passed to investors for prepayments within the first 36 months after the loan's interest adjustment date, except for disclosed permitted prepayments, bona fide sales, or enforcement actions."
            Legacy = false
        }
        {
            Code = "975"
            Name = "Homeowner – fixed with indemnity"
            Detail =
                "Fixed-rate loans on properties with up to four self-contained units. Indemnities are passed to investors for prepayments within the first 60 months after the loan's interest adjustment date, except for disclosed permitted prepayments, bona fide sales, or enforcement actions."
            Legacy = false
        }
        {
            Code = "980"
            Name = "Homeowner – adjustable CDOR"
            Detail =
                "Loans on properties with up to four self-contained units. The coupon resets monthly off one-month CDOR and payments adjust so the amortization schedule is preserved. No prepayment penalties or indemnities are passed to investors."
            Legacy = true
        }
        {
            Code = "981"
            Name = "Homeowner – adjustable CORRA"
            Detail =
                "Loans on properties with up to four self-contained units. The coupon resets monthly off one-month daily compounded CORRA and payments adjust so the amortization schedule is preserved. No prepayment penalties or indemnities are passed to investors."
            Legacy = false
        }
        {
            Code = "985"
            Name = "Homeowner – variable CDOR"
            Detail =
                "Loans on properties with up to four self-contained units. The coupon resets monthly off one-month CDOR while loan payments stay fixed, so the amortization schedule can change. No prepayment penalties or indemnities are passed to investors."
            Legacy = true
        }
        {
            Code = "986"
            Name = "Homeowner – variable CORRA"
            Detail =
                "Loans on properties with up to four self-contained units. The coupon resets monthly off one-month daily compounded CORRA while loan payments stay fixed, so the amortization schedule can change. No prepayment penalties or indemnities are passed to investors."
            Legacy = false
        }
        {
            Code = "987"
            Name = "Homeowner – WAC floating"
            Detail =
                "Floating-rate loans on properties with up to four self-contained units. The coupon is set as a spread to the weighted average mortgage rate of the loans in the pool. No prepayment penalties or indemnities are passed to investors."
            Legacy = false
        }
        {
            Code = "990"
            Name = "Social housing"
            Detail =
                "Loans for social housing projects under Section 95 of the National Housing Act and approved provincial social housing programs, as described in the Social Housing Loans section of the CMHC guide."
            Legacy = false
        }
    |]

let tryFind (code: string) =
    if String.IsNullOrWhiteSpace code then
        None
    else
        let trimmed = code.Trim()
        all |> Array.tryFind (fun info -> info.Code = trimmed)

let tooltip (info: PoolTypeInfo) =
    if info.Legacy then
        sprintf "%s · %s — %s No new issuances since January 1, 2023." info.Code info.Name info.Detail
    else
        sprintf "%s · %s — %s" info.Code info.Name info.Detail

let tooltipFor (code: string) =
    match tryFind code with
    | Some info -> tooltip info
    | None -> ""

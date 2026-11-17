module NHAMBS.Etl.GovernmentBondTypes

open System

type GovernmentBondIssue =
    {
        ProgramCode: string
        Isin: string
        SecurityType: string
        InstrumentType: string
        CouponRatePercent: decimal
        IssueDate: DateTime
        MaturityDate: DateTime
        OutstandingAmount: decimal
        InflationAdjustment: decimal option
        OutstandingInflationAdjusted: decimal
        IssuanceTermBucket: string option
        MaturityTermBucket: string option
        AsOfDate: DateTime
    }

type GovernmentBondParseResult =
    {
        Issues: GovernmentBondIssue[]
        AsOfDate: DateTime option
        Warnings: string[]
    }

type ProvincialBondTransaction =
    {
        IssuerName: string
        ProvinceCode: string option
        Isin: string
        CouponRatePercent: decimal
        MaturityDate: DateTime
        TradeDate: DateTime
        SettlementDate: DateTime option
        AmountPurchased: decimal
        PurchasePrice: decimal option
        PurchaseYieldPercent: decimal option
    }

type ProvincialBondParseResult =
    {
        Transactions: ProvincialBondTransaction[]
        LatestTradeDate: DateTime option
        Warnings: string[]
    }

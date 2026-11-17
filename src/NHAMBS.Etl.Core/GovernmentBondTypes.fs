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

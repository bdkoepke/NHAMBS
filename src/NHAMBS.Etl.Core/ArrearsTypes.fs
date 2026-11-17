module NHAMBS.Etl.ArrearsTypes

open System

/// One geography referenced by an arrears observation. The same table holds
/// countries, provinces, aggregate regions (Atlantic/Territories) and CMAs.
type ArrearsGeography =
    {
        Code: string
        Name: string
        Level: string
        ProvinceCode: string option
        SortOrder: int
    }

/// A single provider/period/geography/metric observation.
type ArrearsObservation =
    {
        SourceCode: string
        PeriodType: string
        PeriodStart: DateTime
        PeriodEnd: DateTime
        GeographyCode: string
        LenderType: string
        InsuredStatus: string
        ProductType: string
        ArrearsStage: string
        Metric: string
        Value: decimal
        IsSuppressed: bool
    }

type ArrearsWarning =
    {
        SourceCode: string
        Geography: string
        Period: string
        Message: string
    }

type ArrearsParseResult =
    {
        Observations: ArrearsObservation[]
        Geographies: ArrearsGeography[]
        Warnings: ArrearsWarning[]
        Suppressed: int
        LatestPeriodEnd: DateTime option
    }

module ArrearsParseResult =

    let empty =
        {
            Observations = [||]
            Geographies = [||]
            Warnings = [||]
            Suppressed = 0
            LatestPeriodEnd = None
        }

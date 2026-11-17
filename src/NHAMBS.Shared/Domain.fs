namespace NHAMBS.Shared

/// Shared, transport-safe contracts used by the Saturn API and the Fable client.
/// Dates are ISO-8601 strings and monetary values are in CAD unless stated otherwise.
module Domain =

    type MbsPoolFilter =
        {
            Search: string
            Issuer: string
            RateType: string
            PoolType: string
            Insurer: string
            Province: string
            DateFrom: string
            DateTo: string
            MaturityFrom: string
            MaturityTo: string
            /// "", "active" or "inactive".
            Status: string
            SortBy: string
            SortDir: string
        }

    let emptyMbsFilter =
        {
            Search = ""
            Issuer = ""
            RateType = ""
            PoolType = ""
            Insurer = ""
            Province = ""
            DateFrom = ""
            DateTo = ""
            MaturityFrom = ""
            MaturityTo = ""
            Status = ""
            SortBy = "issue_date"
            SortDir = "desc"
        }

    type MbsPoolQuery = { Filter: MbsPoolFilter }

    type MbsPoolRow =
        {
            PoolId: string
            Cusip: string
            PoolNumber: string
            IssuerName: string
            RateType: string
            PoolType: string
            PoolTypeDescription: string
            CouponDisplay: string
            CouponValue: decimal
            Principal: decimal
            IssueDate: string option
            SettlementDate: string option
            MaturityDate: string option
            Loans: int
            Wac: decimal
            Wam: decimal
            Provinces: string
            Insurers: string
            SourceUrl: string
        }

    type MbsPoolPage = { Rows: MbsPoolRow[]; Total: int }

    type CategoryPoint =
        {
            Label: string
            Value: decimal
            Count: int
        }

    type SeriesPoint =
        {
            Label: string
            Date: string
            Value: decimal
            Secondary: decimal
            Count: int
        }

    /// Snapshot of the market for a reporting month. Cumulative counts and
    /// balances include every pool issued up to the end of that month.
    type PeriodSummary =
        {
            Label: string
            Month: string
            Issuers: int
            Pools: int
            NewPrincipal: decimal
            PoolBalance: decimal
            AverageCoupon: decimal
            Loans: int
        }

    type IssuerSummary =
        {
            Issuer: string
            Pools: int
            Principal: decimal
            AverageCoupon: decimal
            Loans: int
            Share: decimal
            /// New principal issued in each month of MbsAnalytics.TrendMonths.
            Trend: decimal[]
        }

    type ProvinceSummary =
        {
            Province: string
            Pools: int
            Principal: decimal
            Share: decimal
        }

    type MbsAnalytics =
        {
            TotalPrincipal: decimal
            PoolCount: int
            IssuerCount: int
            LoanCount: int
            AverageCoupon: decimal
            AverageWam: decimal
            NewestIssueDate: string option
            Monthly: SeriesPoint[]
            ByProvince: ProvinceSummary[]
            ByRateType: CategoryPoint[]
            ByPoolType: CategoryPoint[]
            ByInsurer: CategoryPoint[]
            ByMaturityYear: SeriesPoint[]
            TopIssuers: IssuerSummary[]
            Periods: PeriodSummary[]
            /// Month labels (YYYY-MM) shared by every issuer trend sparkline.
            TrendMonths: string[]
        }

    type MbsFilterOptions =
        {
            Issuers: string[]
            RateTypes: string[]
            PoolTypes: CodeLabel[]
            Insurers: string[]
            Provinces: string[]
            MinIssueDate: string option
            MaxIssueDate: string option
        }

    and CodeLabel = { Code: string; Label: string }

    type MaturityBucket =
        {
            Bucket: string
            RemainingFaceValue: decimal
            WeightedAverageRate: decimal
            WeightedAverageAmortization: decimal
            IndexSpreadTotal: decimal
            IndexSpreadFullTerm: decimal
            MonthlyPayment: decimal
        }

    type InsurerAllocation =
        {
            Name: string
            DollarValue: decimal
            PercentOfLoans: decimal
        }

    type LocationAllocation =
        {
            Province: string
            LoansPercent: decimal
            PrincipalAmountPercent: decimal
        }

    type RateBand =
        {
            RateBand: string
            Total: decimal
            PercentOfTotal: decimal
            RemainingFaceValueByDate: decimal[]
        }

    type InterestSeries = { Label: string; Values: decimal[] }

    type MbsPoolDetail =
        {
            Row: MbsPoolRow
            Custodian: string option
            MortgageInsurer: string option
            FormVersion: string option
            CouponKind: string option
            CouponIndex: string option
            CouponSpread: decimal
            CouponFixedRate: decimal
            FirstPaymentDue: string option
            FinalPaymentDue: string option
            WeightedAverageInterestAdjustmentDate: string option
            WeightedAverageMaturityDate: string option
            ScheduledRemainingFaceValue: decimal
            ScheduledWac: decimal
            ScheduledWam: decimal
            ScheduledIndexSpreadTotal: decimal
            ScheduledMonthlyPayment: decimal
            HomeownerPercent: decimal
            MultipleFamilyPercent: decimal
            SocialHousingPercent: decimal
            RamByAmortization: decimal[]
            RamByTerm: decimal[]
            InterestAdjustmentDates: string[]
            InterestTotals: InterestSeries[]
            InterestPercent: InterestSeries[]
            InterestWac: decimal[]
            MaturityBuckets: MaturityBucket[]
            Insurers: InsurerAllocation[]
            Locations: LocationAllocation[]
            RateBands: RateBand[]
            PrepaymentBonaFideExempt: bool
            PrepaymentSectionA: string option
            PrepaymentSectionB: string option
            PrepaymentOther: string option
            SourceFile: string option
            IngestedAt: string
        }

    type ChtIssueRow =
        {
            IssueId: int
            Series: string
            SettlementDate: string option
            CouponDisplay: string option
            CouponKind: string option
            CouponValue: decimal
            MaturityDate: string option
            Currency: string option
            InterestPaymentFrequency: string option
            IssuedToInvestors: decimal
            IssuedToGovernment: decimal
            TotalIssuance: decimal
            Outstanding: decimal
            Identifier: string option
            CircularCount: int
            HistoryAvailable: bool
            CancellationCount: int
            CancelledMillions: decimal
            SourceUrl: string option
        }

    type ChtCancellationRow =
        {
            FromDate: string option
            ToDate: string option
            CancelledAmount: decimal
        }

    type ChtHistoryRow =
        {
            HistoryId: int
            Series: string
            AsOfDate: string option
            Title: string option
            CouponDisplay: string option
            CouponKind: string option
            CouponValue: decimal
            MaturityDate: string option
            Identifier: string option
            CancellationCount: int
            CancelledMillions: decimal
            FirstCancellationDate: string option
            LastCancellationDate: string option
            Cancellations: ChtCancellationRow[]
            SourceUrl: string option
        }

    type ChtCircularRow =
        {
            CircularId: int
            Series: string
            Designation: string
            DocumentDate: string option
            Currency: string option
            AggregatePrincipalAmount: decimal
            CouponDisplay: string option
            CouponKind: string option
            CouponValue: decimal
            MaturityDate: string option
            IssuePricePercent: decimal
            SettlementDate: string option
            FirstInterestPaymentDate: string option
            InterestPaymentFrequency: string option
            MinimumDenomination: decimal
            IssuerName: string option
            Isin: string option
            Cusip: string option
            CommonCode: string option
            LegalEntityIdentifier: string option
            ListingExchange: string option
            Ratings: string
            YieldPercent: decimal
            SourceUrl: string option
        }

    type ChtAnalytics =
        {
            OutstandingMillions: decimal
            TotalIssuanceMillions: decimal
            IssueCount: int
            CircularCount: int
            AverageCoupon: decimal
            NextMaturityDate: string option
            HistoryCount: int
            CancellationCount: int
            CancelledMillions: decimal
            HistoryAsOfDate: string option
            ByMaturityYear: SeriesPoint[]
            ByCouponBucket: CategoryPoint[]
            ByCurrency: CategoryPoint[]
        }

    type ChtData =
        {
            Analytics: ChtAnalytics
            Issues: ChtIssueRow[]
            Circulars: ChtCircularRow[]
            History: ChtHistoryRow[]
        }

    type CoveredBondRegistryRow =
        {
            Name: string
            Status: string option
            RegistrationDate: string option
            BusinessAddress: string option
            WebsiteUrl: string option
            ProgramPage: string option
            ProgramName: string option
            MinimumAssetPercentage: decimal
            MaximumAssetPercentage: decimal
            BondCount: int
        }

    type CoveredBondProgramRow =
        {
            ProgramId: int
            IssuerName: string
            ProgramName: string
            MinimumAssetPercentage: decimal
            MaximumAssetPercentage: decimal
            BondCount: int
            Currencies: string
            EarliestMaturity: string option
            LatestMaturity: string option
            SourceUrl: string option
        }

    type CoveredBondRow =
        {
            BondId: int
            ProgramId: int
            IssuerName: string
            ProgramName: string
            SeriesDesignation: string option
            IssueCurrency: string option
            NominalAmount: decimal
            Isin: string option
            CouponDisplay: string option
            CouponKind: string option
            CouponValue: decimal
            IssueDate: string option
            RegistrationDate: string option
            MaturityDate: string option
            ExtendedMaturityDate: string option
            BulletType: string option
            InterestPaymentFrequency: string option
            ListingAuthority: string option
        }

    type CoveredBondAnalytics =
        {
            ProgramCount: int
            BondCount: int
            CurrencyCount: int
            IssuerCount: int
            EarliestMaturity: string option
            LatestMaturity: string option
            ByCurrency: CategoryPoint[]
            ByMaturityYear: SeriesPoint[]
            ByProgram: CategoryPoint[]
        }

    type CoveredBondData =
        {
            Analytics: CoveredBondAnalytics
            Programs: CoveredBondProgramRow[]
            Bonds: CoveredBondRow[]
            Registry: CoveredBondRegistryRow[]
        }

    type ArrearsObservationRow =
        {
            SourceCode: string
            Provider: string
            PeriodType: string
            PeriodEnd: string
            GeographyCode: string
            GeographyName: string
            GeographyLevel: string
            LenderType: string
            Metric: string
            Value: decimal
        }

    type ArrearsSourceInfo =
        {
            SourceCode: string
            Provider: string
            DatasetName: string
            Definition: string
            Cadence: string
            Attribution: string
            LatestPeriodEnd: string option
            IsStale: bool
        }

    /// One national 90+ day arrears-rate point used by the provider comparison
    /// chart. CBA is monthly and CMHC is quarterly, so points are not aligned.
    type ArrearsSeriesPoint =
        {
            Label: string
            Date: string
            Value: decimal
            Source: string
        }

    type ArrearsData =
        {
            Comparison: ArrearsSeriesPoint[]
            Latest: ArrearsObservationRow[]
            Sources: ArrearsSourceInfo[]
        }

    type GovernmentBondIssueRow =
        {
            ProgramCode: string
            ProgramName: string
            Isin: string
            SecurityType: string
            InstrumentType: string
            CouponRatePercent: decimal
            IssueDate: string
            MaturityDate: string
            OutstandingAmount: decimal
            InflationAdjustment: decimal option
            OutstandingInflationAdjusted: decimal
            AsOfDate: string
        }

    type GovernmentBondProgramInfo =
        {
            ProgramCode: string
            Name: string
            IssuerName: string
            GovernmentLevel: string
            SourceName: string
            SourceUrl: string
            Cadence: string
            Notes: string
            LatestAsOfDate: string option
            IsStale: bool
            IssueCount: int
            BondOutstanding: decimal
            BillOutstanding: decimal
            TotalOutstanding: decimal
            WeightedAverageCoupon: decimal option
            LastLoadedAt: string option
        }

    type GovernmentBondAnalytics =
        {
            AsOfDate: string option
            BondOutstanding: decimal
            BillOutstanding: decimal
            TotalOutstanding: decimal
            IssueCount: int
            BondIssueCount: int
            WeightedAverageCoupon: decimal
            ByMaturityYear: SeriesPoint[]
            ByCouponBucket: CategoryPoint[]
            BySecurityType: CategoryPoint[]
        }

    type ProvincialBondSummaryRow =
        {
            ProvinceCode: string option
            ProvinceName: string
            TransactionCount: int
            IssueCount: int
            IssuerCount: int
            AmountPurchased: decimal
            WeightedAverageYield: decimal option
            FirstTradeDate: string option
            LastTradeDate: string option
        }

    type ProvincialBondIssueRow =
        {
            IssuerName: string
            ProvinceCode: string option
            ProvinceName: string
            Isin: string
            CouponRatePercent: decimal
            MaturityDate: string
            TransactionCount: int
            AmountPurchased: decimal
            WeightedAverageYield: decimal option
            FirstTradeDate: string option
            LastTradeDate: string option
        }

    type GovernmentBondData =
        {
            Analytics: GovernmentBondAnalytics
            Issues: GovernmentBondIssueRow[]
            Programs: GovernmentBondProgramInfo[]
            ProvincialSummary: ProvincialBondSummaryRow[]
            ProvincialIssues: ProvincialBondIssueRow[]
        }

    type DatasetFreshness =
        {
            Dataset: string
            DisplayName: string
            LastLoaded: string
            FilesProcessed: int
            RowsLoaded: int
            Status: string
            DurationSeconds: decimal
        }

    type OverviewData =
        {
            Mbs: MbsAnalytics
            Cht: ChtAnalytics
            CoveredBonds: CoveredBondAnalytics
            Freshness: DatasetFreshness[]
        }

    /// One observed or projected point on a forward curve. Values use the
    /// source's native units; `percent` series are decimal fractions.
    type ForwardCurvePoint = { Date: string; Value: decimal }

    type ForwardCurve =
        {
            Id: string
            Name: string
            ShortName: string
            DataType: string
            Stale: bool
            DecimalPrecision: int
            AsOfDate: string
            UpdatedOn: string
            Points: ForwardCurvePoint[]
        }

    type ForwardCurveSet =
        {
            Name: string
            AsOfDate: string
            Curves: ForwardCurve[]
            SourceFile: string
        }

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
            SortBy = "issue_date"
            SortDir = "desc"
        }

    type MbsPoolQuery =
        {
            Filter: MbsPoolFilter
            Offset: int
            Limit: int
        }

    let defaultPoolQuery =
        {
            Filter = emptyMbsFilter
            Offset = 0
            Limit = 20000
        }

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
            IssueDate: string
            SettlementDate: string
            MaturityDate: string
            Loans: int
            Wac: decimal
            Wam: decimal
            Provinces: string
            Insurers: string
            SourceUrl: string
        }

    type MbsPoolPage =
        {
            Rows: MbsPoolRow[]
            Total: int
            Offset: int
            Limit: int
            Capped: bool
        }

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
            NewestIssueDate: string
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
            MinIssueDate: string
            MaxIssueDate: string
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
            Custodian: string
            MortgageInsurer: string
            FormVersion: string
            CouponKind: string
            CouponIndex: string
            CouponSpread: decimal
            CouponFixedRate: decimal
            FirstPaymentDue: string
            FinalPaymentDue: string
            WeightedAverageInterestAdjustmentDate: string
            WeightedAverageMaturityDate: string
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
            PrepaymentSectionA: string
            PrepaymentSectionB: string
            PrepaymentOther: string
            SourceFile: string
            IngestedAt: string
        }

    type ChtIssueRow =
        {
            IssueId: int
            Series: string
            SettlementDate: string
            CouponDisplay: string
            CouponKind: string
            CouponValue: decimal
            MaturityDate: string
            Currency: string
            InterestPaymentFrequency: string
            IssuedToInvestors: decimal
            IssuedToGovernment: decimal
            TotalIssuance: decimal
            Outstanding: decimal
            Identifier: string
            CircularCount: int
            HistoryAvailable: bool
            CancellationCount: int
            CancelledMillions: decimal
            SourceUrl: string
        }

    type ChtCancellationRow =
        {
            FromDate: string
            ToDate: string
            CancelledAmount: decimal
        }

    type ChtHistoryRow =
        {
            HistoryId: int
            Series: string
            AsOfDate: string
            Title: string
            CouponDisplay: string
            CouponKind: string
            CouponValue: decimal
            MaturityDate: string
            Identifier: string
            CancellationCount: int
            CancelledMillions: decimal
            FirstCancellationDate: string
            LastCancellationDate: string
            Cancellations: ChtCancellationRow[]
            SourceUrl: string
        }

    type ChtCircularRow =
        {
            CircularId: int
            Series: string
            Designation: string
            DocumentDate: string
            Currency: string
            AggregatePrincipalAmount: decimal
            CouponDisplay: string
            CouponKind: string
            CouponValue: decimal
            MaturityDate: string
            IssuePricePercent: decimal
            SettlementDate: string
            FirstInterestPaymentDate: string
            InterestPaymentFrequency: string
            MinimumDenomination: decimal
            IssuerName: string
            Isin: string
            Cusip: string
            CommonCode: string
            LegalEntityIdentifier: string
            ListingExchange: string
            Ratings: string
            YieldPercent: decimal
            SourceUrl: string
        }

    type ChtAnalytics =
        {
            OutstandingMillions: decimal
            TotalIssuanceMillions: decimal
            IssueCount: int
            CircularCount: int
            AverageCoupon: decimal
            NextMaturityDate: string
            HistoryCount: int
            CancellationCount: int
            CancelledMillions: decimal
            HistoryAsOfDate: string
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
            Status: string
            RegistrationDate: string
            BusinessAddress: string
            WebsiteUrl: string
            ProgramPage: string
            ProgramName: string
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
            EarliestMaturity: string
            LatestMaturity: string
            SourceUrl: string
        }

    type CoveredBondRow =
        {
            BondId: int
            ProgramId: int
            IssuerName: string
            ProgramName: string
            SeriesDesignation: string
            IssueCurrency: string
            NominalAmount: decimal
            Isin: string
            CouponDisplay: string
            CouponKind: string
            CouponValue: decimal
            IssueDate: string
            RegistrationDate: string
            MaturityDate: string
            ExtendedMaturityDate: string
            BulletType: string
            InterestPaymentFrequency: string
            ListingAuthority: string
        }

    type CoveredBondAnalytics =
        {
            ProgramCount: int
            BondCount: int
            CurrencyCount: int
            IssuerCount: int
            EarliestMaturity: string
            LatestMaturity: string
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

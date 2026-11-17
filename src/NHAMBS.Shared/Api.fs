namespace NHAMBS.Shared

open NHAMBS.Shared.Domain

/// Fable.Remoting contract. Every function below is exposed by the Saturn API
/// and consumed directly by the Elmish client.
type DashboardApi =
    {
        GetOverview: unit -> Async<OverviewData>
        GetMbsAnalytics: MbsPoolFilter -> Async<MbsAnalytics>
        GetMbsPools: MbsPoolQuery -> Async<MbsPoolPage>
        GetMbsPool: string -> Async<MbsPoolDetail[]>
        GetMbsFilterOptions: unit -> Async<MbsFilterOptions>
        GetCht: unit -> Async<ChtData>
        GetCoveredBonds: unit -> Async<CoveredBondData>
        GetArrears: unit -> Async<ArrearsData>
        GetGovernmentBonds: unit -> Async<GovernmentBondData>
        GetFreshness: unit -> Async<DatasetFreshness[]>
        GetForwardCurves: unit -> Async<ForwardCurveSet>
    }

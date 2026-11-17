module NHAMBS.Data.ArrearsRepository

open System
open System.Globalization
open NHAMBS.Shared.Domain

let private dateText (value: DateTime option) =
    value
    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let private sourceLabel (sourceCode: string) (provider: string) =
    if sourceCode = "cba" then "CBA"
    elif sourceCode = "cmhc" then "CMHC"
    else provider

let get (context: Provider.Sql.dataContext) =
    let latest =
        query {
            for row in context.Nhambs.VArrearsLatest do
                select row
        }
        |> Seq.map (fun row ->
            let sourceCode = Option.get row.SourceCode
            let provider = Option.get row.Provider
            let periodType = Option.get row.PeriodType

            let periodEnd =
                row.PeriodEnd
                |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                |> Option.defaultValue ""

            let geographyCode = Option.get row.GeographyCode
            let geographyName = Option.get row.GeographyName
            let geographyLevel = Option.get row.GeographyLevel
            let lenderType = Option.get row.LenderType
            let metric = Option.get row.Metric
            let value = defaultArg row.Value 0m

            ({
                SourceCode = sourceCode
                Provider = provider
                PeriodType = periodType
                PeriodEnd = periodEnd
                GeographyCode = geographyCode
                GeographyName = geographyName
                GeographyLevel = geographyLevel
                LenderType = lenderType
                Metric = metric
                Value = value
            }
            : ArrearsObservationRow))
        |> Seq.sortBy (fun row ->
            let levelOrder =
                match row.GeographyLevel with
                | "country" -> 0
                | "province" -> 1
                | "region" -> 2
                | "cma" -> 3
                | _ -> 4

            levelOrder, row.GeographyName, row.SourceCode)
        |> Seq.toArray

    let sourcesByGeography =
        latest
        |> Array.groupBy (fun row -> row.GeographyCode)
        |> Array.map (fun (geographyCode, rows) ->
            geographyCode, rows |> Array.map (fun row -> row.SourceCode) |> Array.distinct |> Array.sort)
        |> dict

    let geographies =
        query {
            for row in context.Nhambs.ArrearsGeographies do
                select row
        }
        |> Seq.map (fun row ->
            ({
                Code = row.GeographyCode
                Name = row.Name
                Level = row.Level
                ProvinceCode = row.ProvinceCode
                SortOrder = row.SortOrder
                Sources =
                    match sourcesByGeography.TryGetValue row.GeographyCode with
                    | true, sources -> sources
                    | _ -> [||]
            }
            : ArrearsGeographyInfo))
        |> Seq.sortBy (fun geography -> geography.SortOrder, geography.Name)
        |> Seq.toArray

    let series =
        query {
            for row in context.Nhambs.VArrearsSeries do
                where (row.Metric = Some "arrears_rate_percent")
                select row
        }
        |> Seq.choose (fun row ->
            match row.PeriodEnd, row.Value, row.Provider, row.SourceCode, row.GeographyCode with
            | Some period, Some value, Some provider, Some source, Some geography ->
                Some(
                    {
                        GeographyCode = geography
                        Label = period.ToString("MMM yyyy", CultureInfo.InvariantCulture)
                        Date = period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        Value = value
                        Source = sourceLabel source provider
                    }
                    : ArrearsSeriesPoint
                )
            | _ -> None)
        |> Seq.sortBy (fun point -> point.Date, point.Source)
        |> Seq.toArray

    let freshness =
        query {
            for fresh in context.Nhambs.VArrearsFreshness do
                select fresh
        }
        |> Seq.map (fun fresh -> defaultArg fresh.SourceCode "", fresh)
        |> dict

    let sources =
        query {
            for source in context.Nhambs.ArrearsSources do
                select source
        }
        |> Seq.map (fun source ->
            let fresh = freshness.[source.SourceCode]

            ({
                SourceCode = source.SourceCode
                Provider = source.Provider
                DatasetName = source.DatasetName
                Definition = source.Definition
                Cadence = source.Cadence
                Attribution = source.Attribution
                LatestPeriodEnd = dateText fresh.LatestPeriodEnd
                IsStale = defaultArg fresh.IsStale false
            }
            : ArrearsSourceInfo))
        |> Seq.sortBy (fun source -> source.SourceCode)
        |> Seq.toArray

    {
        Series = series
        Geographies = geographies
        Latest = latest
        Sources = sources
    }

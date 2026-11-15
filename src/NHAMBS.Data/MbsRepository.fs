module NHAMBS.Data.MbsRepository

open System
open System.Globalization
open System.Text.Json
open System.Data.Common
open Npgsql
open NHAMBS.Shared.Domain
open NHAMBS.Data

let private dateText (value: DateTime option) =
    value
    |> Option.map (fun date -> date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
    |> Option.defaultValue ""

let private parseDate (value: string) =
    if String.IsNullOrWhiteSpace value then
        None
    else
        match DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
        | true, date -> Some(date.Date)
        | _ -> None

let private readString (reader: DbDataReader) index =
    if reader.IsDBNull index then "" else reader.GetString index

let private readDecimalOpt (reader: DbDataReader) index =
    if reader.IsDBNull index then
        None
    else
        Some(reader.GetDecimal index)

let private readInt (reader: DbDataReader) index =
    if reader.IsDBNull index then
        0
    else
        Convert.ToInt32(reader.GetValue index, CultureInfo.InvariantCulture)

let private readDateText (reader: DbDataReader) index =
    if reader.IsDBNull index then
        ""
    else
        reader.GetDateTime(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let private readDecimalOptFromScalar (command: NpgsqlCommand) =
    match command.ExecuteScalar() with
    | :? decimal as value -> Some value
    | :? double as value -> Some(decimal value)
    | :? int as value -> Some(decimal value)
    | _ -> None

let private sortColumn =
    function
    | "pool_number" -> "g.pool_number"
    | "cusip" -> "g.cusip"
    | "issuer_name" -> "g.issuer_name"
    | "rate_type" -> "g.rate_type"
    | "pool_type" -> "g.pool_type"
    | "coupon_value" -> "g.coupon_value"
    | "principal" -> "g.principal"
    | "settlement_date" -> "g.settlement_date"
    | "maturity_date" -> "g.maturity_date"
    | "loans" -> "g.loans"
    | "wac" -> "g.wac"
    | "wam" -> "g.wam"
    | _ -> "g.issue_date"

let private whereClause (filter: MbsPoolFilter) =
    let clauses = ResizeArray<string>()
    let parameters = ResizeArray<string * obj>()

    let search = filter.Search.Trim()

    if not (String.IsNullOrWhiteSpace search) then
        clauses.Add("(g.pool_number ILIKE @search OR g.cusip ILIKE @search OR g.issuer_name ILIKE @search)")
        parameters.Add("search", box (sprintf "%%%s%%" search))

    if not (String.IsNullOrWhiteSpace filter.Issuer) then
        clauses.Add("g.issuer_name = @issuer")
        parameters.Add("issuer", box filter.Issuer)

    if not (String.IsNullOrWhiteSpace filter.RateType) then
        clauses.Add("g.rate_type = @rate_type")
        parameters.Add("rate_type", box filter.RateType)

    if not (String.IsNullOrWhiteSpace filter.PoolType) then
        clauses.Add("g.pool_type = @pool_type")
        parameters.Add("pool_type", box filter.PoolType)

    if not (String.IsNullOrWhiteSpace filter.Insurer) then
        clauses.Add(
            "EXISTS (SELECT 1 FROM nhambs.mbs_pool_insurers i WHERE i.pool_cusip = g.pool_id AND i.name = @insurer)"
        )

        parameters.Add("insurer", box filter.Insurer)

    if not (String.IsNullOrWhiteSpace filter.Province) then
        clauses.Add(
            "EXISTS (SELECT 1 FROM nhambs.mbs_pool_locations l WHERE l.pool_cusip = g.pool_id AND l.province_name = @province AND COALESCE(l.principal_amount_percent, 0) > 0)"
        )

        parameters.Add("province", box filter.Province)

    match parseDate filter.DateFrom with
    | Some date ->
        clauses.Add("g.issue_date >= @date_from")
        parameters.Add("date_from", box date)
    | None -> ()

    match parseDate filter.DateTo with
    | Some date ->
        clauses.Add("g.issue_date <= @date_to")
        parameters.Add("date_to", box date)
    | None -> ()

    match parseDate filter.MaturityFrom with
    | Some date ->
        clauses.Add("g.maturity_date >= @maturity_from")
        parameters.Add("maturity_from", box date)
    | None -> ()

    match parseDate filter.MaturityTo with
    | Some date ->
        clauses.Add("g.maturity_date <= @maturity_to")
        parameters.Add("maturity_to", box date)
    | None -> ()

    let sql =
        if clauses.Count = 0 then
            ""
        else
            " WHERE " + String.concat " AND " clauses

    sql, parameters

let private addParameters (command: NpgsqlCommand) (parameters: ResizeArray<string * obj>) =
    for name, value in parameters do
        command.Parameters.AddWithValue(name, value) |> ignore

let private mapRow (reader: DbDataReader) =
    {
        PoolId = readString reader 0
        Cusip = readString reader 1
        PoolNumber = readString reader 2
        IssuerName = readString reader 3
        RateType = readString reader 4
        PoolType = readString reader 5
        PoolTypeDescription = readString reader 6
        CouponDisplay = readString reader 7
        CouponValue = defaultArg (readDecimalOpt reader 8) 0m
        Principal = if reader.IsDBNull 9 then 0m else reader.GetDecimal 9
        IssueDate = readDateText reader 10
        SettlementDate = readDateText reader 11
        MaturityDate = readDateText reader 12
        Loans = readInt reader 13
        Wac = defaultArg (readDecimalOpt reader 14) 0m
        Wam = defaultArg (readDecimalOpt reader 15) 0m
        Provinces = readString reader 16
        Insurers = readString reader 17
        SourceUrl = readString reader 18
    }

let private rowColumns =
    """
    g.pool_id, g.cusip, g.pool_number, g.issuer_name, g.rate_type, g.pool_type,
    g.pool_type_description, g.coupon_display, g.coupon_value, g.principal,
    g.issue_date, g.settlement_date, g.maturity_date, g.loans, g.wac, g.wam,
    g.provinces, g.insurers, g.source_url
    """

let search (query: MbsPoolQuery) =
    let whereSql, parameters = whereClause query.Filter
    let order = sortColumn query.Filter.SortBy

    let direction =
        if query.Filter.SortDir.ToLowerInvariant() = "asc" then
            "ASC"
        else
            "DESC"

    let limit = max 1 (min query.Limit 50000)
    let offset = max 0 query.Offset

    Database.withConnection (fun connection ->
        use countCommand =
            new NpgsqlCommand($"SELECT count(*) FROM nhambs.v_mbs_pool_grid g{whereSql}", connection)

        addParameters countCommand parameters

        let total =
            Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture)

        use command =
            new NpgsqlCommand(
                $"""
                SELECT {rowColumns}
                FROM nhambs.v_mbs_pool_grid g
                {whereSql}
                ORDER BY {order} {direction} NULLS LAST, g.pool_id
                LIMIT @limit OFFSET @offset
                """,
                connection
            )

        addParameters command parameters
        command.Parameters.AddWithValue("limit", limit) |> ignore
        command.Parameters.AddWithValue("offset", offset) |> ignore

        use reader = command.ExecuteReader()
        let rows = ResizeArray<MbsPoolRow>()

        while reader.Read() do
            rows.Add(mapRow reader)

        {
            Rows = rows.ToArray()
            Total = total
            Offset = offset
            Limit = limit
            Capped = total > offset + rows.Count
        })

let analytics (filter: MbsPoolFilter) =
    let whereSql, parameters = whereClause filter

    Database.withConnection (fun connection ->
        let scalar query =
            use command = new NpgsqlCommand(query, connection)
            addParameters command parameters
            command.ExecuteScalar()

        let totalPrincipal =
            Convert.ToDecimal(
                scalar $"SELECT COALESCE(SUM(g.principal), 0) FROM nhambs.v_mbs_pool_grid g{whereSql}",
                CultureInfo.InvariantCulture
            )

        let poolCount =
            Convert.ToInt32(
                scalar $"SELECT COUNT(*) FROM nhambs.v_mbs_pool_grid g{whereSql}",
                CultureInfo.InvariantCulture
            )

        let issuerCount =
            Convert.ToInt32(
                scalar $"SELECT COUNT(DISTINCT g.issuer_name) FROM nhambs.v_mbs_pool_grid g{whereSql}",
                CultureInfo.InvariantCulture
            )

        let loanCount =
            Convert.ToInt32(
                scalar $"SELECT COALESCE(SUM(g.loans), 0) FROM nhambs.v_mbs_pool_grid g{whereSql}",
                CultureInfo.InvariantCulture
            )

        let averageCoupon =
            use command =
                new NpgsqlCommand(
                    $"""
                    SELECT SUM(COALESCE(g.coupon_value, 0) * g.principal)
                           / NULLIF(SUM(CASE WHEN g.coupon_value IS NULL THEN 0 ELSE g.principal END), 0)
                    FROM nhambs.v_mbs_pool_grid g{whereSql}
                    """,
                    connection
                )

            addParameters command parameters
            readDecimalOptFromScalar command

        let averageWam =
            use command =
                new NpgsqlCommand(
                    $"""
                    SELECT SUM(COALESCE(g.wam, 0) * g.principal)
                           / NULLIF(SUM(CASE WHEN g.wam IS NULL THEN 0 ELSE g.principal END), 0)
                    FROM nhambs.v_mbs_pool_grid g{whereSql}
                    """,
                    connection
                )

            addParameters command parameters
            readDecimalOptFromScalar command

        let newestIssue =
            use command =
                new NpgsqlCommand($"SELECT MAX(g.issue_date) FROM nhambs.v_mbs_pool_grid g{whereSql}", connection)

            addParameters command parameters

            match command.ExecuteScalar() with
            | :? DateTime as date -> dateText (Some date)
            | _ -> ""

        let monthly =
            use command =
                new NpgsqlCommand(
                    $"""
                    SELECT to_char(date_trunc('month', g.issue_date), 'YYYY-MM') AS label,
                           date_trunc('month', g.issue_date)::date AS month_start,
                           COUNT(*) AS pools,
                           COALESCE(SUM(g.principal), 0) AS principal
                    FROM nhambs.v_mbs_pool_grid g
                    {whereSql}
                      {(if whereSql = "" then
                            "WHERE g.issue_date IS NOT NULL"
                        else
                            "AND g.issue_date IS NOT NULL")}
                    GROUP BY 1, 2
                    ORDER BY 2
                    """,
                    connection
                )

            addParameters command parameters
            use reader = command.ExecuteReader()
            let values = ResizeArray<SeriesPoint>()

            while reader.Read() do
                values.Add(
                    {
                        Label = readString reader 0
                        Date = readDateText reader 1
                        Value = if reader.IsDBNull 3 then 0m else reader.GetDecimal 3
                        Secondary = 0m
                        Count = readInt reader 2
                    }
                )

            values.ToArray()

        let provinces =
            use command =
                new NpgsqlCommand(
                    $"""
                    SELECT l.province_name,
                           COUNT(DISTINCT g.pool_id) AS pools,
                           COALESCE(SUM(g.principal * COALESCE(l.principal_amount_percent, 0) / 100.0), 0) AS principal
                    FROM nhambs.v_mbs_pool_grid g
                    JOIN nhambs.mbs_pool_locations l ON l.pool_cusip = g.pool_id
                    {whereSql}
                      {(if whereSql = "" then "WHERE" else "AND")} COALESCE(l.principal_amount_percent, 0) > 0
                    GROUP BY l.province_name
                    ORDER BY principal DESC
                    """,
                    connection
                )

            addParameters command parameters
            use reader = command.ExecuteReader()
            let values = ResizeArray<ProvinceSummary>()

            while reader.Read() do
                let principal = if reader.IsDBNull 2 then 0m else reader.GetDecimal 2

                let share =
                    if totalPrincipal = 0m then
                        0m
                    else
                        principal / totalPrincipal * 100m

                values.Add(
                    {
                        Province = readString reader 0
                        Pools = readInt reader 1
                        Principal = principal
                        Share = share
                    }
                )

            values.ToArray()

        let categoryQuery
            (labelExpression: string)
            (valueExpression: string)
            (fromClause: string)
            (groupBy: string)
            (orderBy: string)
            =
            use command =
                new NpgsqlCommand(
                    $"""
                    SELECT {labelExpression}, {valueExpression}, COUNT(*) AS items
                    FROM {fromClause}
                    {whereSql}
                    GROUP BY {groupBy}
                    ORDER BY {orderBy}
                    """,
                    connection
                )

            addParameters command parameters
            use reader = command.ExecuteReader()
            let values = ResizeArray<CategoryPoint>()

            while reader.Read() do
                values.Add(
                    {
                        Label = readString reader 0
                        Value = if reader.IsDBNull 1 then 0m else reader.GetDecimal 1
                        Count = readInt reader 2
                    }
                )

            values.ToArray()

        let byRateType =
            categoryQuery
                "COALESCE(g.rate_type, 'unknown')"
                "COALESCE(SUM(g.principal), 0)"
                "nhambs.v_mbs_pool_grid g"
                "1"
                "2 DESC"

        let byPoolType =
            categoryQuery
                "g.pool_type || ' · ' || g.pool_type_description"
                "COALESCE(SUM(g.principal), 0)"
                "nhambs.v_mbs_pool_grid g"
                "1"
                "2 DESC"

        let byInsurer =
            use command =
                new NpgsqlCommand(
                    $"""
                    SELECT i.name,
                           COALESCE(SUM(COALESCE(i.dollar_value, g.principal * COALESCE(i.percent_of_loans, 0) / 100.0)), 0) AS principal,
                           COUNT(DISTINCT g.pool_id) AS pools
                    FROM nhambs.v_mbs_pool_grid g
                    JOIN nhambs.mbs_pool_insurers i ON i.pool_cusip = g.pool_id
                    {whereSql}
                    GROUP BY i.name
                    ORDER BY principal DESC
                    LIMIT 12
                    """,
                    connection
                )

            addParameters command parameters
            use reader = command.ExecuteReader()
            let values = ResizeArray<CategoryPoint>()

            while reader.Read() do
                values.Add(
                    {
                        Label = readString reader 0
                        Value = if reader.IsDBNull 1 then 0m else reader.GetDecimal 1
                        Count = readInt reader 2
                    }
                )

            values.ToArray()

        let byMaturityYear =
            use command =
                new NpgsqlCommand(
                    $"""
                    SELECT to_char(date_trunc('year', g.maturity_date), 'YYYY') AS label,
                           date_trunc('year', g.maturity_date)::date AS year_start,
                           COUNT(*) AS pools,
                           COALESCE(SUM(g.principal), 0) AS principal
                    FROM nhambs.v_mbs_pool_grid g
                    {whereSql}
                      {(if whereSql = "" then
                            "WHERE g.maturity_date IS NOT NULL"
                        else
                            "AND g.maturity_date IS NOT NULL")}
                    GROUP BY 1, 2
                    ORDER BY 2
                    """,
                    connection
                )

            addParameters command parameters
            use reader = command.ExecuteReader()
            let values = ResizeArray<SeriesPoint>()

            while reader.Read() do
                values.Add(
                    {
                        Label = readString reader 0
                        Date = readDateText reader 1
                        Value = if reader.IsDBNull 3 then 0m else reader.GetDecimal 3
                        Secondary = 0m
                        Count = readInt reader 2
                    }
                )

            values.ToArray()

        let trendMonths =
            monthly
            |> Array.map (fun point -> point.Label)
            |> Array.rev
            |> Array.truncate 12
            |> Array.rev

        let latestMonth =
            match parseDate newestIssue with
            | Some date -> Some(new DateTime(date.Year, date.Month, 1))
            | None ->
                monthly
                |> Array.tryLast
                |> Option.bind (fun point -> parseDate point.Date)
                |> Option.map (fun date -> new DateTime(date.Year, date.Month, 1))

        let periods =
            match latestMonth with
            | None -> [||]
            | Some current ->
                [|
                    "Current", current
                    "Previous Month", current.AddMonths(-1)
                    "Last Year", current.AddYears(-1)
                |]
                |> Array.map (fun (label, month) ->
                    use command =
                        new NpgsqlCommand(
                            $"""
                            WITH period AS (
                                SELECT @period_month::date AS month_start,
                                       (@period_month::date + interval '1 month - 1 day')::date AS month_end
                            )
                            SELECT
                                COUNT(DISTINCT g.issuer_name) FILTER (WHERE g.issue_date <= p.month_end) AS issuers,
                                COUNT(*) FILTER (WHERE g.issue_date <= p.month_end) AS pools,
                                COALESCE(SUM(g.principal) FILTER (WHERE g.issue_date >= p.month_start AND g.issue_date <= p.month_end), 0) AS new_principal,
                                COALESCE(SUM(g.principal) FILTER (WHERE g.issue_date <= p.month_end), 0) AS pool_balance,
                                SUM(COALESCE(g.coupon_value, 0) * g.principal) FILTER (WHERE g.issue_date <= p.month_end)
                                    / NULLIF(SUM(CASE WHEN g.coupon_value IS NULL THEN 0 ELSE g.principal END) FILTER (WHERE g.issue_date <= p.month_end), 0) AS weighted_coupon,
                                COALESCE(SUM(g.loans) FILTER (WHERE g.issue_date <= p.month_end), 0) AS loans
                            FROM nhambs.v_mbs_pool_grid g
                            CROSS JOIN period p
                            {whereSql}
                            """,
                            connection
                        )

                    addParameters command parameters
                    command.Parameters.AddWithValue("period_month", month) |> ignore
                    use reader = command.ExecuteReader()
                    let monthText = month.ToString("yyyy-MM", CultureInfo.InvariantCulture)

                    if reader.Read() then
                        {
                            Label = label
                            Month = monthText
                            Issuers = readInt reader 0
                            Pools = readInt reader 1
                            NewPrincipal = if reader.IsDBNull 2 then 0m else reader.GetDecimal 2
                            PoolBalance = if reader.IsDBNull 3 then 0m else reader.GetDecimal 3
                            AverageCoupon = defaultArg (readDecimalOpt reader 4) 0m
                            Loans = readInt reader 5
                        }
                    else
                        {
                            Label = label
                            Month = monthText
                            Issuers = 0
                            Pools = 0
                            NewPrincipal = 0m
                            PoolBalance = 0m
                            AverageCoupon = 0m
                            Loans = 0
                        })

        let topIssuers =
            use command =
                new NpgsqlCommand(
                    $"""
                    SELECT g.issuer_name,
                           COUNT(*) AS pools,
                           COALESCE(SUM(g.principal), 0) AS principal,
                           SUM(COALESCE(g.coupon_value, 0) * g.principal)
                               / NULLIF(SUM(CASE WHEN g.coupon_value IS NULL THEN 0 ELSE g.principal END), 0) AS weighted_coupon,
                           COALESCE(SUM(g.loans), 0) AS loans
                    FROM nhambs.v_mbs_pool_grid g
                    {whereSql}
                    GROUP BY g.issuer_name
                    ORDER BY principal DESC
                    LIMIT 15
                    """,
                    connection
                )

            addParameters command parameters
            use reader = command.ExecuteReader()
            let values = ResizeArray<IssuerSummary>()

            while reader.Read() do
                values.Add(
                    {
                        Issuer = readString reader 0
                        Pools = readInt reader 1
                        Principal = if reader.IsDBNull 2 then 0m else reader.GetDecimal 2
                        AverageCoupon = defaultArg (readDecimalOpt reader 3) 0m
                        Loans = readInt reader 4
                        Share = 0m
                        Trend = [||]
                    }
                )

            values.ToArray()

        let topIssuerNames = topIssuers |> Array.map (fun issuer -> issuer.Issuer)

        let topIssuersWithTrend =
            let shareFor (issuer: IssuerSummary) =
                if totalPrincipal = 0m then
                    0m
                else
                    issuer.Principal / totalPrincipal * 100m

            if topIssuerNames.Length = 0 then
                topIssuers |> Array.map (fun issuer -> { issuer with Share = shareFor issuer })
            else
                let clauses = ResizeArray<string>()

                if not (String.IsNullOrWhiteSpace whereSql) then
                    clauses.Add(whereSql.Trim().Substring(6))

                clauses.Add("g.issuer_name = ANY(@issuer_names)")

                let trendStart, trendEnd =
                    match trendMonths |> Array.tryHead, trendMonths |> Array.tryLast with
                    | Some first, Some last ->
                        match parseDate (first + "-01"), parseDate (last + "-01") with
                        | Some startDate, Some endDate -> Some startDate, Some(endDate.AddMonths(1))
                        | _ -> None, None
                    | _ -> None, None

                match trendStart, trendEnd with
                | Some _, Some _ ->
                    clauses.Add("g.issue_date >= @trend_start")
                    clauses.Add("g.issue_date < @trend_end")
                | _ -> ()

                let trendWhere = " WHERE " + String.concat " AND " clauses

                use command =
                    new NpgsqlCommand(
                        $"""
                        SELECT g.issuer_name,
                               to_char(date_trunc('month', g.issue_date), 'YYYY-MM') AS label,
                               COALESCE(SUM(g.principal), 0) AS principal
                        FROM nhambs.v_mbs_pool_grid g
                        {trendWhere}
                        GROUP BY 1, 2
                        """,
                        connection
                    )

                addParameters command parameters
                command.Parameters.AddWithValue("issuer_names", topIssuerNames) |> ignore

                match trendStart, trendEnd with
                | Some startDate, Some endDate ->
                    command.Parameters.AddWithValue("trend_start", startDate) |> ignore
                    command.Parameters.AddWithValue("trend_end", endDate) |> ignore
                | _ -> ()

                use reader = command.ExecuteReader()
                let trends = System.Collections.Generic.Dictionary<string, decimal[]>()

                for name in topIssuerNames do
                    trends.[name] <- Array.zeroCreate trendMonths.Length

                while reader.Read() do
                    let issuer = readString reader 0
                    let label = readString reader 1
                    let value = if reader.IsDBNull 2 then 0m else reader.GetDecimal 2

                    match trends.TryGetValue issuer with
                    | true, values ->
                        match Array.IndexOf(trendMonths, label) with
                        | -1 -> ()
                        | index -> values.[index] <- value
                    | _ -> ()

                topIssuers
                |> Array.map (fun issuer ->
                    let trend =
                        match trends.TryGetValue issuer.Issuer with
                        | true, values -> values
                        | _ -> [||]

                    { issuer with
                        Share = shareFor issuer
                        Trend = trend
                    })

        {
            TotalPrincipal = totalPrincipal
            PoolCount = poolCount
            IssuerCount = issuerCount
            LoanCount = loanCount
            AverageCoupon = defaultArg averageCoupon 0m
            AverageWam = defaultArg averageWam 0m
            NewestIssueDate = newestIssue
            Monthly = monthly
            ByProvince = provinces
            ByRateType = byRateType
            ByPoolType = byPoolType
            ByInsurer = byInsurer
            ByMaturityYear = byMaturityYear
            TopIssuers = topIssuersWithTrend
            Periods = periods
            TrendMonths = trendMonths
        })

let private parseDecimalArray (json: string option) =
    match json with
    | Some value when not (String.IsNullOrWhiteSpace value) ->
        try
            JsonSerializer.Deserialize<decimal[]>(value)
        with _ ->
            [||]
    | _ -> [||]

let private parseStringArray (json: string option) =
    match json with
    | Some value when not (String.IsNullOrWhiteSpace value) ->
        try
            JsonSerializer.Deserialize<string[]>(value)
        with _ ->
            [||]
    | _ -> [||]

let private parseByDate (json: string option) (label: string) =
    match json with
    | Some value when not (String.IsNullOrWhiteSpace value) ->
        try
            use document = JsonDocument.Parse value
            let mutable byDate = [||]

            match document.RootElement.TryGetProperty "byDate" with
            | true, element ->
                byDate <-
                    element.EnumerateArray()
                    |> Seq.map (fun item ->
                        if item.ValueKind = JsonValueKind.Number then
                            item.GetDecimal()
                        else
                            0m)
                    |> Seq.toArray
            | _ -> ()

            { Label = label; Values = byDate }
        with _ ->
            { Label = label; Values = [||] }
    | _ -> { Label = label; Values = [||] }

let filterOptions () =
    let minIssueDate, maxIssueDate =
        Database.withConnection (fun connection ->
            use command =
                new NpgsqlCommand("SELECT MIN(issue_date), MAX(issue_date) FROM nhambs.v_mbs_pool_grid;", connection)

            use reader = command.ExecuteReader()

            if reader.Read() then
                let minimum =
                    if reader.IsDBNull 0 then
                        ""
                    else
                        reader.GetDateTime(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

                let maximum =
                    if reader.IsDBNull 1 then
                        ""
                    else
                        reader.GetDateTime(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

                minimum, maximum
            else
                "", "")

    Database.withContext (fun context ->
        let issuers =
            query {
                for row in context.Nhambs.VMbsPoolGrid do
                    select row.IssuerName
            }
            |> Seq.choose id
            |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray

        let rateTypes =
            query {
                for row in context.Nhambs.VMbsPoolGrid do
                    select row.RateType
            }
            |> Seq.choose id
            |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray

        let poolTypes =
            query {
                for row in context.Nhambs.VMbsPoolGrid do
                    select (row.PoolType, row.PoolTypeDescription)
            }
            |> Seq.distinct
            |> Seq.sortBy fst
            |> Seq.map (fun (code, label) ->
                {
                    Code = defaultArg code ""
                    Label = defaultArg label ""
                })
            |> Seq.toArray

        let insurers =
            query {
                for row in context.Nhambs.MbsPoolInsurers do
                    select row.Name
            }
            |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray

        let provinces =
            query {
                for row in context.Nhambs.MbsPoolLocations do
                    select row.ProvinceName
            }
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray

        {
            Issuers = issuers
            RateTypes = rateTypes
            PoolTypes = poolTypes
            Insurers = insurers
            Provinces = provinces
            MinIssueDate = minIssueDate
            MaxIssueDate = maxIssueDate
        })

let getDetail (cusip: string) =
    Database.withContext (fun context ->
        let viewRow =
            query {
                for row in context.Nhambs.VMbsPoolGrid do
                    where (row.Cusip = Some cusip)
                    select row
            }
            |> Seq.tryHead

        match viewRow with
        | None -> None
        | Some view ->
            let text (value: string option) = defaultArg value ""

            let poolRow =
                {
                    PoolId = text view.PoolId
                    Cusip = text view.Cusip
                    PoolNumber = text view.PoolNumber
                    IssuerName = text view.IssuerName
                    RateType = text view.RateType
                    PoolType = text view.PoolType
                    PoolTypeDescription = text view.PoolTypeDescription
                    CouponDisplay = text view.CouponDisplay
                    CouponValue = defaultArg view.CouponValue 0m
                    Principal = defaultArg view.Principal 0m
                    IssueDate = dateText view.IssueDate
                    SettlementDate = dateText view.SettlementDate
                    MaturityDate = dateText view.MaturityDate
                    Loans = defaultArg view.Loans 0
                    Wac = defaultArg view.Wac 0m
                    Wam = defaultArg view.Wam 0m
                    Provinces = text view.Provinces
                    Insurers = text view.Insurers
                    SourceUrl = text view.SourceUrl
                }

            let pool =
                query {
                    for row in context.Nhambs.MbsPools do
                        where (row.Cusip = cusip)
                        select row
                }
                |> Seq.tryHead

            match pool with
            | None -> None
            | Some p ->
                let insurers =
                    query {
                        for row in context.Nhambs.MbsPoolInsurers do
                            where (row.PoolCusip = cusip)
                            select row
                    }
                    |> Seq.sortByDescending (fun row -> row.DollarValue |> Option.defaultValue 0m)
                    |> Seq.map (fun row ->
                        {
                            Name = row.Name
                            DollarValue = defaultArg row.DollarValue 0m
                            PercentOfLoans = defaultArg row.PercentOfLoans 0m
                        })
                    |> Seq.toArray

                let locations =
                    query {
                        for row in context.Nhambs.MbsPoolLocations do
                            where (row.PoolCusip = cusip)
                            select row
                    }
                    |> Seq.sortByDescending (fun row -> row.PrincipalAmountPercent |> Option.defaultValue 0m)
                    |> Seq.map (fun row ->
                        {
                            Province = row.ProvinceName
                            LoansPercent = defaultArg row.LoansPercent 0m
                            PrincipalAmountPercent = defaultArg row.PrincipalAmountPercent 0m
                        })
                    |> Seq.toArray

                let bucketOrder =
                    dict
                        [
                            "5 months", 0
                            "4 months", 1
                            "3 months", 2
                            "2 months", 3
                            "1 month", 4
                            "At maturity", 5
                            "Total", 6
                        ]

                let buckets =
                    query {
                        for row in context.Nhambs.MbsPoolScheduledMaturity do
                            where (row.PoolCusip = cusip)
                            select row
                    }
                    |> Seq.sortBy (fun row ->
                        match bucketOrder.TryGetValue row.Bucket with
                        | true, value -> value
                        | _ -> 99)
                    |> Seq.map (fun row ->
                        {
                            Bucket = row.Bucket
                            RemainingFaceValue = defaultArg row.RemainingFaceValue 0m
                            WeightedAverageRate = defaultArg row.WeightedAverageRate 0m
                            WeightedAverageAmortization = defaultArg row.WeightedAverageAmortization 0m
                            IndexSpreadTotal = defaultArg row.IndexSpreadTotal 0m
                            IndexSpreadFullTerm = defaultArg row.IndexSpreadFullTerm 0m
                            MonthlyPayment = defaultArg row.MonthlyPayment 0m
                        })
                    |> Seq.toArray

                let bands =
                    query {
                        for row in context.Nhambs.MbsPoolRateBands do
                            where (row.PoolCusip = cusip)
                            select row
                    }
                    |> Seq.sortBy (fun row -> row.Id)
                    |> Seq.map (fun row ->
                        {
                            RateBand = row.RateBand
                            Total = defaultArg row.Total 0m
                            PercentOfTotal = defaultArg row.PercentOfTotal 0m
                            RemainingFaceValueByDate = parseDecimalArray row.RemainingFaceValueByDate
                        })
                    |> Seq.toArray

                Some
                    {
                        Row = poolRow
                        Custodian = p.Custodian |> Option.defaultValue ""
                        MortgageInsurer = p.MortgageInsurer |> Option.defaultValue ""
                        FormVersion = p.FormVersion |> Option.defaultValue ""
                        CouponKind = p.CouponKind |> Option.defaultValue ""
                        CouponIndex = p.CouponIndex |> Option.defaultValue ""
                        CouponSpread = defaultArg p.CouponSpreadPercent 0m
                        CouponFixedRate = defaultArg p.CouponFixedRatePercent 0m
                        FirstPaymentDue = dateText p.FirstPaymentDue
                        FinalPaymentDue = dateText p.FinalPaymentDue
                        WeightedAverageInterestAdjustmentDate = dateText p.WeightedAverageInterestAdjustmentDate
                        WeightedAverageMaturityDate = dateText p.WeightedAverageMaturityDate
                        ScheduledRemainingFaceValue = defaultArg p.ScheduledRemainingFaceValue 0m
                        ScheduledWac = defaultArg p.ScheduledWac 0m
                        ScheduledWam = defaultArg p.ScheduledWam 0m
                        ScheduledIndexSpreadTotal = defaultArg p.ScheduledIndexSpreadTotal 0m
                        ScheduledMonthlyPayment = defaultArg p.ScheduledMonthlyPayment 0m
                        HomeownerPercent = defaultArg p.HomeownerPercent 0m
                        MultipleFamilyPercent = defaultArg p.MultipleFamilyPercent 0m
                        SocialHousingPercent = defaultArg p.SocialHousingPercent 0m
                        RamByAmortization = parseDecimalArray p.RamByAmortization
                        RamByTerm = parseDecimalArray p.RamByTerm
                        InterestAdjustmentDates = parseStringArray p.InterestAdjustmentDates
                        InterestTotals = [| parseByDate p.InterestAdjustmentTotals "Remaining face value" |]
                        InterestPercent = [| parseByDate p.InterestAdjustmentPercent "Percent of total" |]
                        InterestWac = parseDecimalArray p.InterestAdjustmentWac
                        MaturityBuckets = buckets
                        Insurers = insurers
                        Locations = locations
                        RateBands = bands
                        PrepaymentBonaFideExempt = p.PrepaymentBonaFideExempt |> Option.defaultValue false
                        PrepaymentSectionA = p.PrepaymentSectionA |> Option.defaultValue ""
                        PrepaymentSectionB = p.PrepaymentSectionB |> Option.defaultValue ""
                        PrepaymentOther = p.PrepaymentOtherDisclosures |> Option.defaultValue ""
                        SourceFile = p.SourceFile |> Option.defaultValue ""
                        IngestedAt = p.IngestedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                    })

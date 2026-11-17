namespace NHAMBS.Migrations

open FluentMigrator

/// Issuer aliases are reference data, not application constants. The ETL
/// writes the cleaned source name and a normalized key; reference-table
/// refreshes and the dashboard views join through this table to resolve the
/// canonical name.
[<Migration(202611140008L)>]
type M008_IssuerAliases() =
    inherit Migration()

    let createAliases =
        """
        CREATE TABLE silver.issuer_aliases (
            alias_key     varchar(256) PRIMARY KEY,
            canonical_key varchar(256) NOT NULL,
            canonical_name text NOT NULL
        );

        INSERT INTO silver.issuer_aliases (alias_key, canonical_key, canonical_name) VALUES
            ('rbc', 'royal bank of canada', 'Royal Bank of Canada'),
            ('rbc dominion securities', 'royal bank of canada', 'Royal Bank of Canada'),
            ('royal bank of canada', 'royal bank of canada', 'Royal Bank of Canada'),
            ('td', 'toronto dominion bank', 'The Toronto-Dominion Bank'),
            ('td securities', 'toronto dominion bank', 'The Toronto-Dominion Bank'),
            ('toronto dominion bank', 'toronto dominion bank', 'The Toronto-Dominion Bank'),
            ('scotia capital', 'bank of nova scotia', 'The Bank of Nova Scotia'),
            ('scotiabank', 'bank of nova scotia', 'The Bank of Nova Scotia'),
            ('bank of nova scotia', 'bank of nova scotia', 'The Bank of Nova Scotia'),
            ('bmo', 'bank of montreal', 'Bank of Montreal'),
            ('bmo nesbitt burns', 'bank of montreal', 'Bank of Montreal'),
            ('bank of montreal', 'bank of montreal', 'Bank of Montreal'),
            ('cibc', 'canadian imperial bank of commerce', 'Canadian Imperial Bank of Commerce'),
            ('cibc world markets', 'canadian imperial bank of commerce', 'Canadian Imperial Bank of Commerce'),
            ('canadian imperial bank of commerce', 'canadian imperial bank of commerce', 'Canadian Imperial Bank of Commerce'),
            ('national bank financial', 'national bank of canada', 'National Bank of Canada'),
            ('national bank of canada', 'national bank of canada', 'National Bank of Canada'),
            ('merrill lynch', 'merrill lynch canada', 'Merrill Lynch Canada Inc.'),
            ('merrill lynch canada', 'merrill lynch canada', 'Merrill Lynch Canada Inc.'),
            ('merwl lynch canada', 'merrill lynch canada', 'Merrill Lynch Canada Inc.'),
            ('mcap', 'mcap', 'MCAP'),
            ('mcap financial', 'mcap', 'MCAP'),
            ('mcap mortgage', 'mcap', 'MCAP'),
            ('mcap service', 'mcap', 'MCAP'),
            ('mcap servicing', 'mcap', 'MCAP'),
            ('alberta treasury branches', 'atb financial', 'ATB Financial'),
            ('atb financial', 'atb financial', 'ATB Financial'),
            ('first national financial', 'first national financial', 'First National Financial'),
            ('home trust', 'home trust', 'Home Trust Company'),
            ('home trust company', 'home trust', 'Home Trust Company'),
            ('peoples trust', 'peoples trust', 'Peoples Trust Company'),
            ('caisse centrale desjardins', 'desjardins group', 'Desjardins Group'),
            ('desjardins', 'desjardins group', 'Desjardins Group'),
            ('federation des caisses desjardins du quebec', 'desjardins group', 'Desjardins Group'),
            ('caisse populaire acadienne', 'caisse populaire acadienne', 'Caisse Populaire Acadienne Ltée'),
            ('equitable bank', 'equitable bank', 'Equitable Bank'),
            ('equitable trust', 'equitable bank', 'Equitable Bank'),
            ('banque de montreal', 'bank of montreal', 'Bank of Montreal'),
            ('banque royale du canada', 'royal bank of canada', 'Royal Bank of Canada'),
            ('banque toronto dominion', 'toronto dominion bank', 'The Toronto-Dominion Bank'),
            ('banque de la nouvelle ecosse', 'bank of nova scotia', 'The Bank of Nova Scotia'),
            ('banque nationale du canada', 'national bank of canada', 'National Bank of Canada'),
            ('banque canadienne imperiale de commerce', 'canadian imperial bank of commerce', 'Canadian Imperial Bank of Commerce'),
            ('ta industrial alliance', 'industrial alliance insurance and financial services', 'Industrial Alliance, Insurance and Financial Services'),
            ('industrial alliance', 'industrial alliance insurance and financial services', 'Industrial Alliance, Insurance and Financial Services'),
            ('industrial alliance insurance and financial servi', 'industrial alliance insurance and financial services', 'Industrial Alliance, Insurance and Financial Services'),
            ('bank of montrea', 'bank of montreal', 'Bank of Montreal'),
            ('tha bank of nova scotia', 'bank of nova scotia', 'The Bank of Nova Scotia'),
            ('bank af nova scotia', 'bank of nova scotia', 'The Bank of Nova Scotia'),
            ('bank of nova scalia', 'bank of nova scotia', 'The Bank of Nova Scotia');
        """

    let rebuildIssuers =
        """
        UPDATE silver.mbs_pools SET issuer_id = NULL;

        DELETE FROM silver.issuers;

        INSERT INTO silver.issuers (issuer_key, canonical_name)
        SELECT DISTINCT
               COALESCE(a.canonical_key, p.issuer_key),
               COALESCE(a.canonical_name, p.issuer_name)
        FROM silver.mbs_pools p
        LEFT JOIN silver.issuer_aliases a ON a.alias_key = p.issuer_key;

        UPDATE silver.mbs_pools p
        SET issuer_id = i.issuer_id
        FROM silver.issuer_aliases a
        JOIN silver.issuers i ON i.issuer_key = a.canonical_key
        WHERE p.issuer_key = a.alias_key;

        UPDATE silver.mbs_pools p
        SET issuer_id = i.issuer_id
        FROM silver.issuers i
        WHERE p.issuer_id IS NULL AND i.issuer_key = p.issuer_key;
        """

    let poolTypeCase =
        """
        CASE p.pool_type
            WHEN '867' THEN 'Homeowner - collateral fixed'
            WHEN '880' THEN 'Homeowner - adjustable CDOR (legacy)'
            WHEN '881' THEN 'Homeowner - adjustable CORRA'
            WHEN '885' THEN 'Homeowner - variable CDOR (legacy)'
            WHEN '886' THEN 'Homeowner - variable CORRA'
            WHEN '964' THEN 'Homeowner - fixed'
            WHEN '965' THEN 'Multiple-family - prepayable with indemnity'
            WHEN '966' THEN 'Multiple-family - closed'
            WHEN '967' THEN 'Homeowner - fixed'
            WHEN '970' THEN 'Homeowner - fixed with indemnity'
            WHEN '975' THEN 'Homeowner - fixed with indemnity'
            WHEN '980' THEN 'Homeowner - adjustable CDOR (legacy)'
            WHEN '981' THEN 'Homeowner - adjustable CORRA'
            WHEN '985' THEN 'Homeowner - variable CDOR (legacy)'
            WHEN '986' THEN 'Homeowner - variable CORRA'
            WHEN '987' THEN 'Homeowner - WAC floating'
            WHEN '990' THEN 'Social housing'
            ELSE COALESCE(p.pool_type, 'Other')
        END
        """

    let recreateViews =
        $"""
        CREATE OR REPLACE VIEW nhambs.v_mbs_pool_grid AS
        SELECT
            p.cusip                                   AS pool_id,
            p.cusip,
            p.pool_number,
            COALESCE(i.canonical_name, p.issuer_name) AS issuer_name,
            COALESCE(p.rate_type, '')                 AS rate_type,
            COALESCE(p.pool_type, '')                 AS pool_type,
            {poolTypeCase}                            AS pool_type_description,
            COALESCE(p.coupon_display, '')            AS coupon_display,
            COALESCE(p.coupon_fixed_rate_percent, p.coupon_spread_percent) AS coupon_value,
            p.aggregate_principal_amount              AS principal,
            p.issue_date,
            p.settlement_date,
            p.maturity_date,
            COALESCE(p.total_number_of_mortgage_loans, 0) AS loans,
            NULLIF(p.scheduled_wac, 0)                AS wac,
            NULLIF(p.scheduled_wam, 0)                AS wam,
            COALESCE((
                SELECT string_agg(l.province_name, ', ' ORDER BY l.principal_amount_percent DESC)
                FROM silver.mbs_pool_locations l
                WHERE l.pool_cusip = p.cusip AND COALESCE(l.principal_amount_percent, 0) > 0
            ), '')                                    AS provinces,
            COALESCE((
                SELECT string_agg(x.name, ', ' ORDER BY x.dollar_value DESC)
                FROM silver.mbs_pool_insurers x
                WHERE x.pool_cusip = p.cusip
            ), COALESCE(p.mortgage_insurer, ''))      AS insurers,
            COALESCE(p.source_url, '')                AS source_url
        FROM silver.mbs_pools p
        LEFT JOIN silver.issuers i ON i.issuer_id = p.issuer_id;

        CREATE OR REPLACE VIEW nhambs.v_mbs_issuer_summary AS
        SELECT
            i.issuer_key,
            i.canonical_name                                    AS issuer_name,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(p.aggregate_principal_amount), 0)      AS principal,
            COALESCE(SUM(p.total_number_of_mortgage_loans), 0)  AS loans,
            SUM(COALESCE(p.coupon_fixed_rate_percent, p.coupon_spread_percent, 0) * p.aggregate_principal_amount)
                / NULLIF(SUM(CASE WHEN COALESCE(p.coupon_fixed_rate_percent, p.coupon_spread_percent) IS NULL THEN 0 ELSE p.aggregate_principal_amount END), 0) AS weighted_coupon,
            MIN(p.issue_date)                                   AS first_issue,
            MAX(p.issue_date)                                   AS last_issue
        FROM silver.mbs_pools p
        JOIN silver.issuers i ON i.issuer_id = p.issuer_id
        GROUP BY i.issuer_key, i.canonical_name;

        CREATE OR REPLACE VIEW nhambs.v_mbs_unresolved_entities AS
        SELECT
            'issuer'::text AS entity_type,
            p.issuer_name AS name,
            COUNT(*)      AS records
        FROM silver.mbs_pools p
        WHERE p.issuer_id IS NULL OR p.issuer_name = 'Unknown issuer'
        GROUP BY p.issuer_name
        UNION ALL
        SELECT
            'insurer'::text AS entity_type,
            i.name         AS name,
            COUNT(*)       AS records
        FROM silver.mbs_pool_insurers i
        WHERE i.insurer_id IS NULL OR i.name = 'Unknown insurer'
        GROUP BY i.name;
        """

    let restorePreAliasViews =
        $"""
        CREATE OR REPLACE VIEW nhambs.v_mbs_pool_grid AS
        SELECT
            p.cusip                                   AS pool_id,
            p.cusip,
            p.pool_number,
            p.issuer_name,
            COALESCE(p.rate_type, '')                 AS rate_type,
            COALESCE(p.pool_type, '')                 AS pool_type,
            {poolTypeCase}                            AS pool_type_description,
            COALESCE(p.coupon_display, '')            AS coupon_display,
            COALESCE(p.coupon_fixed_rate_percent, p.coupon_spread_percent) AS coupon_value,
            p.aggregate_principal_amount              AS principal,
            p.issue_date,
            p.settlement_date,
            p.maturity_date,
            COALESCE(p.total_number_of_mortgage_loans, 0) AS loans,
            NULLIF(p.scheduled_wac, 0)                AS wac,
            NULLIF(p.scheduled_wam, 0)                AS wam,
            COALESCE((
                SELECT string_agg(l.province_name, ', ' ORDER BY l.principal_amount_percent DESC)
                FROM silver.mbs_pool_locations l
                WHERE l.pool_cusip = p.cusip AND COALESCE(l.principal_amount_percent, 0) > 0
            ), '')                                    AS provinces,
            COALESCE((
                SELECT string_agg(x.name, ', ' ORDER BY x.dollar_value DESC)
                FROM silver.mbs_pool_insurers x
                WHERE x.pool_cusip = p.cusip
            ), COALESCE(p.mortgage_insurer, ''))      AS insurers,
            COALESCE(p.source_url, '')                AS source_url
        FROM silver.mbs_pools p;

        CREATE OR REPLACE VIEW nhambs.v_mbs_issuer_summary AS
        SELECT
            issuer_key,
            MIN(issuer_name)                                    AS issuer_name,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(aggregate_principal_amount), 0)        AS principal,
            COALESCE(SUM(total_number_of_mortgage_loans), 0)    AS loans,
            SUM(COALESCE(coupon_fixed_rate_percent, coupon_spread_percent, 0) * aggregate_principal_amount)
                / NULLIF(SUM(CASE WHEN COALESCE(coupon_fixed_rate_percent, coupon_spread_percent) IS NULL THEN 0 ELSE aggregate_principal_amount END), 0) AS weighted_coupon,
            MIN(issue_date)                                     AS first_issue,
            MAX(issue_date)                                     AS last_issue
        FROM silver.mbs_pools
        GROUP BY issuer_key;
        """

    override this.Up() =
        this.Execute.Sql(createAliases)
        this.Execute.Sql(rebuildIssuers)
        this.Execute.Sql(recreateViews)

    override this.Down() =
        this.Execute.Sql("DROP TABLE IF EXISTS silver.issuer_aliases;")
        this.Execute.Sql(restorePreAliasViews)

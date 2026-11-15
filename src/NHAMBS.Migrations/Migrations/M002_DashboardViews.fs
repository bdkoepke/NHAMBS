namespace NHAMBS.Migrations

open FluentMigrator

[<Migration(202611140002L)>]
type M002_DashboardViews() =
    inherit Migration()

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

    override this.Up() =
        this.Execute.Sql(
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
                    FROM nhambs.mbs_pool_locations l
                    WHERE l.pool_cusip = p.cusip AND COALESCE(l.principal_amount_percent, 0) > 0
                ), '')                                    AS provinces,
                COALESCE((
                    SELECT string_agg(i.name, ', ' ORDER BY i.dollar_value DESC)
                    FROM nhambs.mbs_pool_insurers i
                    WHERE i.pool_cusip = p.cusip
                ), COALESCE(p.mortgage_insurer, ''))      AS insurers,
                COALESCE(p.source_url, '')                AS source_url
            FROM nhambs.mbs_pools p;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_mbs_monthly AS
            SELECT
                to_char(date_trunc('month', issue_date), 'YYYY-MM') AS label,
                date_trunc('month', issue_date)::date               AS month_start,
                COUNT(*)                                            AS pools,
                COALESCE(SUM(aggregate_principal_amount), 0)        AS principal,
                COALESCE(SUM(total_number_of_mortgage_loans), 0)    AS loans
            FROM nhambs.mbs_pools
            WHERE issue_date IS NOT NULL
            GROUP BY 1, 2
            ORDER BY 2;
            """
        )

        this.Execute.Sql(
            """
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
            FROM nhambs.mbs_pools
            GROUP BY issuer_key;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_mbs_province_summary AS
            SELECT
                l.province_name,
                COUNT(DISTINCT p.cusip)                             AS pools,
                COALESCE(SUM(p.aggregate_principal_amount * COALESCE(l.principal_amount_percent, 0) / 100.0), 0) AS principal
            FROM nhambs.mbs_pools p
            JOIN nhambs.mbs_pool_locations l ON l.pool_cusip = p.cusip
            WHERE COALESCE(l.principal_amount_percent, 0) > 0
            GROUP BY l.province_name;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_mbs_rate_type_summary AS
            SELECT
                COALESCE(rate_type, 'unknown')                      AS rate_type,
                COUNT(*)                                            AS pools,
                COALESCE(SUM(aggregate_principal_amount), 0)        AS principal
            FROM nhambs.mbs_pools
            GROUP BY 1;
            """
        )

        this.Execute.Sql(
            $"""
            CREATE OR REPLACE VIEW nhambs.v_mbs_pool_type_summary AS
            SELECT
                COALESCE(p.pool_type, '')                           AS pool_type,
                {poolTypeCase}                                      AS pool_type_description,
                COUNT(*)                                            AS pools,
                COALESCE(SUM(p.aggregate_principal_amount), 0)      AS principal
            FROM nhambs.mbs_pools p
            GROUP BY 1, 2;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_mbs_insurer_summary AS
            SELECT
                i.name                                              AS insurer,
                COUNT(DISTINCT i.pool_cusip)                        AS pools,
                COALESCE(SUM(COALESCE(i.dollar_value,
                    p.aggregate_principal_amount * COALESCE(i.percent_of_loans, 0) / 100.0)), 0) AS principal
            FROM nhambs.mbs_pool_insurers i
            JOIN nhambs.mbs_pools p ON p.cusip = i.pool_cusip
            GROUP BY i.name;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_mbs_maturity_summary AS
            SELECT
                to_char(date_trunc('year', maturity_date), 'YYYY')  AS label,
                date_trunc('year', maturity_date)::date             AS year_start,
                COUNT(*)                                            AS pools,
                COALESCE(SUM(aggregate_principal_amount), 0)        AS principal
            FROM nhambs.mbs_pools
            WHERE maturity_date IS NOT NULL
            GROUP BY 1, 2
            ORDER BY 2;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_covered_bond_program_summary AS
            SELECT
                pr.id                                               AS program_id,
                pr.issuer_name,
                pr.program_name,
                pr.minimum_asset_percentage,
                pr.maximum_asset_percentage,
                pr.source_url,
                COUNT(b.id)                                         AS bond_count,
                COALESCE(string_agg(DISTINCT b.issue_currency, ', ' ORDER BY b.issue_currency), '') AS currencies,
                MIN(b.maturity_date)                                AS earliest_maturity,
                MAX(b.maturity_date)                                AS latest_maturity
            FROM nhambs.covered_bond_programs pr
            LEFT JOIN nhambs.covered_bonds b ON b.program_id = pr.id
            GROUP BY pr.id, pr.issuer_name, pr.program_name, pr.minimum_asset_percentage, pr.maximum_asset_percentage, pr.source_url;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_covered_bond_registry_summary AS
            SELECT
                r.id,
                r.name,
                r.status,
                r.registration_date,
                r.business_address,
                r.website_url,
                r.program_page,
                COALESCE(pr.program_name, '')                       AS program_name,
                pr.minimum_asset_percentage,
                pr.maximum_asset_percentage,
                (SELECT COUNT(*) FROM nhambs.covered_bonds b WHERE b.program_id = pr.id) AS bond_count
            FROM nhambs.covered_bond_registry r
            LEFT JOIN nhambs.covered_bond_programs pr ON pr.issuer_name = r.name;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_cht_summary AS
            SELECT
                COUNT(*)                                            AS issue_count,
                COALESCE(SUM(outstanding), 0)                       AS outstanding_millions,
                COALESCE(SUM(total_issuance), 0)                    AS total_issuance_millions,
                MIN(maturity_date)                                  AS next_maturity_date
            FROM nhambs.cht_issues;
            """
        )

    override this.Down() =
        this.Execute.Sql(
            """
            DROP VIEW IF EXISTS nhambs.v_cht_summary;
            DROP VIEW IF EXISTS nhambs.v_covered_bond_registry_summary;
            DROP VIEW IF EXISTS nhambs.v_covered_bond_program_summary;
            DROP VIEW IF EXISTS nhambs.v_mbs_maturity_summary;
            DROP VIEW IF EXISTS nhambs.v_mbs_insurer_summary;
            DROP VIEW IF EXISTS nhambs.v_mbs_pool_type_summary;
            DROP VIEW IF EXISTS nhambs.v_mbs_rate_type_summary;
            DROP VIEW IF EXISTS nhambs.v_mbs_province_summary;
            DROP VIEW IF EXISTS nhambs.v_mbs_issuer_summary;
            DROP VIEW IF EXISTS nhambs.v_mbs_monthly;
            DROP VIEW IF EXISTS nhambs.v_mbs_pool_grid;
            """
        )

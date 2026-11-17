namespace NHAMBS.Migrations

open FluentMigrator

/// Move the conformed data into a silver schema, expose the dashboard views
/// from a gold schema, and add a bronze landing table for the raw source
/// documents. The existing `nhambs` objects become thin compatibility views so
/// the SQLProvider context and older clients keep working during the change.
///
/// Operational metadata (`etl_runs`, `etl_anomalies`) stays in `nhambs`; it is
/// about the pipeline rather than any one layer.
[<Migration(202611140005L)>]
type M005_MedallionArchitecture() =
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

    let dropExistingViews =
        """
        DROP VIEW IF EXISTS nhambs.v_cht_history_summary;
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

    let createBronze =
        """
        CREATE SCHEMA IF NOT EXISTS bronze;

        CREATE TABLE IF NOT EXISTS bronze.source_files (
            source_file_id     bigserial PRIMARY KEY,
            dataset            varchar(32) NOT NULL,
            source_path        text NOT NULL,
            file_name          text NOT NULL,
            checksum_sha256    char(64) NOT NULL,
            size_bytes         bigint NOT NULL,
            source_modified_at timestamptz NULL,
            loaded_at          timestamptz NOT NULL DEFAULT now(),
            run_id             bigint NULL REFERENCES nhambs.etl_runs (run_id) ON DELETE SET NULL,
            document           jsonb NOT NULL,
            CONSTRAINT uq_bronze_source_files UNIQUE (dataset, source_path)
        );

        CREATE INDEX IF NOT EXISTS ix_bronze_source_files_dataset
            ON bronze.source_files (dataset, loaded_at DESC);

        CREATE INDEX IF NOT EXISTS ix_bronze_source_files_checksum
            ON bronze.source_files (checksum_sha256);
        """

    let moveToSilver =
        """
        CREATE SCHEMA IF NOT EXISTS silver;

        ALTER TABLE nhambs.mbs_pools SET SCHEMA silver;
        ALTER TABLE nhambs.mbs_pool_insurers SET SCHEMA silver;
        ALTER TABLE nhambs.mbs_pool_locations SET SCHEMA silver;
        ALTER TABLE nhambs.mbs_pool_scheduled_maturity SET SCHEMA silver;
        ALTER TABLE nhambs.mbs_pool_rate_bands SET SCHEMA silver;
        ALTER TABLE nhambs.cht_issues SET SCHEMA silver;
        ALTER TABLE nhambs.cht_offering_circulars SET SCHEMA silver;
        ALTER TABLE nhambs.cht_circulars SET SCHEMA silver;
        ALTER TABLE nhambs.cht_circular_ratings SET SCHEMA silver;
        ALTER TABLE nhambs.cht_circular_payment_dates SET SCHEMA silver;
        ALTER TABLE nhambs.cht_outstanding_history SET SCHEMA silver;
        ALTER TABLE nhambs.cht_outstanding_history_cancellations SET SCHEMA silver;
        ALTER TABLE nhambs.covered_bond_programs SET SCHEMA silver;
        ALTER TABLE nhambs.covered_bond_parties SET SCHEMA silver;
        ALTER TABLE nhambs.covered_bonds SET SCHEMA silver;
        ALTER TABLE nhambs.covered_bond_isins SET SCHEMA silver;
        ALTER TABLE nhambs.covered_bond_registry SET SCHEMA silver;
        """

    let addLineage =
        """
        ALTER TABLE silver.mbs_pools
            ADD COLUMN source_file_id bigint NULL;
        ALTER TABLE silver.mbs_pools
            ADD CONSTRAINT fk_mbs_pools_bronze
            FOREIGN KEY (source_file_id) REFERENCES bronze.source_files (source_file_id)
            ON DELETE SET NULL;
        CREATE INDEX ix_mbs_pools_source_file ON silver.mbs_pools (source_file_id);

        ALTER TABLE silver.cht_issues
            ADD COLUMN source_file_id bigint NULL;
        ALTER TABLE silver.cht_issues
            ADD CONSTRAINT fk_cht_issues_bronze
            FOREIGN KEY (source_file_id) REFERENCES bronze.source_files (source_file_id)
            ON DELETE SET NULL;

        ALTER TABLE silver.cht_circulars
            ADD COLUMN source_file_id bigint NULL;
        ALTER TABLE silver.cht_circulars
            ADD CONSTRAINT fk_cht_circulars_bronze
            FOREIGN KEY (source_file_id) REFERENCES bronze.source_files (source_file_id)
            ON DELETE SET NULL;

        ALTER TABLE silver.cht_outstanding_history
            ADD COLUMN source_file_id bigint NULL;
        ALTER TABLE silver.cht_outstanding_history
            ADD CONSTRAINT fk_cht_history_bronze
            FOREIGN KEY (source_file_id) REFERENCES bronze.source_files (source_file_id)
            ON DELETE SET NULL;

        ALTER TABLE silver.covered_bond_programs
            ADD COLUMN source_file_id bigint NULL;
        ALTER TABLE silver.covered_bond_programs
            ADD CONSTRAINT fk_covered_programs_bronze
            FOREIGN KEY (source_file_id) REFERENCES bronze.source_files (source_file_id)
            ON DELETE SET NULL;

        ALTER TABLE silver.covered_bond_registry
            ADD COLUMN source_file_id bigint NULL;
        ALTER TABLE silver.covered_bond_registry
            ADD CONSTRAINT fk_covered_registry_bronze
            FOREIGN KEY (source_file_id) REFERENCES bronze.source_files (source_file_id)
            ON DELETE SET NULL;
        """

    let createGold =
        $"""
        CREATE SCHEMA IF NOT EXISTS gold;

        CREATE OR REPLACE VIEW gold.v_mbs_pool_grid AS
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
                SELECT string_agg(i.name, ', ' ORDER BY i.dollar_value DESC)
                FROM silver.mbs_pool_insurers i
                WHERE i.pool_cusip = p.cusip
            ), COALESCE(p.mortgage_insurer, ''))      AS insurers,
            COALESCE(p.source_url, '')                AS source_url
        FROM silver.mbs_pools p;

        CREATE OR REPLACE VIEW gold.v_mbs_monthly AS
        SELECT
            to_char(date_trunc('month', issue_date), 'YYYY-MM') AS label,
            date_trunc('month', issue_date)::date               AS month_start,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(aggregate_principal_amount), 0)        AS principal,
            COALESCE(SUM(total_number_of_mortgage_loans), 0)    AS loans
        FROM silver.mbs_pools
        WHERE issue_date IS NOT NULL
        GROUP BY 1, 2
        ORDER BY 2;

        CREATE OR REPLACE VIEW gold.v_mbs_issuer_summary AS
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

        CREATE OR REPLACE VIEW gold.v_mbs_province_summary AS
        SELECT
            l.province_name,
            COUNT(DISTINCT p.cusip)                             AS pools,
            COALESCE(SUM(p.aggregate_principal_amount * COALESCE(l.principal_amount_percent, 0) / 100.0), 0) AS principal
        FROM silver.mbs_pools p
        JOIN silver.mbs_pool_locations l ON l.pool_cusip = p.cusip
        WHERE COALESCE(l.principal_amount_percent, 0) > 0
        GROUP BY l.province_name;

        CREATE OR REPLACE VIEW gold.v_mbs_rate_type_summary AS
        SELECT
            COALESCE(rate_type, 'unknown')                      AS rate_type,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(aggregate_principal_amount), 0)        AS principal
        FROM silver.mbs_pools
        GROUP BY 1;

        CREATE OR REPLACE VIEW gold.v_mbs_pool_type_summary AS
        SELECT
            COALESCE(p.pool_type, '')                           AS pool_type,
            {poolTypeCase}                                      AS pool_type_description,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(p.aggregate_principal_amount), 0)      AS principal
        FROM silver.mbs_pools p
        GROUP BY 1, 2;

        CREATE OR REPLACE VIEW gold.v_mbs_insurer_summary AS
        SELECT
            i.name                                              AS insurer,
            COUNT(DISTINCT i.pool_cusip)                        AS pools,
            COALESCE(SUM(COALESCE(i.dollar_value,
                p.aggregate_principal_amount * COALESCE(i.percent_of_loans, 0) / 100.0)), 0) AS principal
        FROM silver.mbs_pool_insurers i
        JOIN silver.mbs_pools p ON p.cusip = i.pool_cusip
        GROUP BY i.name;

        CREATE OR REPLACE VIEW gold.v_mbs_maturity_summary AS
        SELECT
            to_char(date_trunc('year', maturity_date), 'YYYY')  AS label,
            date_trunc('year', maturity_date)::date             AS year_start,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(aggregate_principal_amount), 0)        AS principal
        FROM silver.mbs_pools
        WHERE maturity_date IS NOT NULL
        GROUP BY 1, 2
        ORDER BY 2;

        CREATE OR REPLACE VIEW gold.v_covered_bond_program_summary AS
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
        FROM silver.covered_bond_programs pr
        LEFT JOIN silver.covered_bonds b ON b.program_id = pr.id
        GROUP BY pr.id, pr.issuer_name, pr.program_name, pr.minimum_asset_percentage, pr.maximum_asset_percentage, pr.source_url;

        CREATE OR REPLACE VIEW gold.v_covered_bond_registry_summary AS
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
            (SELECT COUNT(*) FROM silver.covered_bonds b WHERE b.program_id = pr.id) AS bond_count
        FROM silver.covered_bond_registry r
        LEFT JOIN silver.covered_bond_programs pr ON pr.issuer_name = r.name;

        CREATE OR REPLACE VIEW gold.v_cht_summary AS
        SELECT
            COUNT(*)                                            AS issue_count,
            COALESCE(SUM(outstanding), 0)                       AS outstanding_millions,
            COALESCE(SUM(total_issuance), 0)                    AS total_issuance_millions,
            MIN(maturity_date)                                  AS next_maturity_date
        FROM silver.cht_issues;

        CREATE OR REPLACE VIEW gold.v_cht_history_summary AS
        SELECT
            h.id,
            h.series,
            h.as_of_date,
            COALESCE(h.title, '')                                   AS title,
            COALESCE(h.coupon_display, '')                          AS coupon_display,
            COALESCE(h.coupon_kind, '')                             AS coupon_kind,
            COALESCE(h.coupon_fixed_rate_percent, h.coupon_spread_percent) AS coupon_value,
            h.maturity_date,
            COALESCE(h.identifier, '')                              AS identifier,
            COUNT(c.id)                                             AS cancellation_count,
            COALESCE(SUM(c.cancelled_amount), 0)                    AS cancelled_millions,
            MIN(c.from_date)                                        AS first_cancellation_date,
            MAX(c.to_date)                                          AS last_cancellation_date,
            COALESCE(h.source_url, '')                              AS source_url
        FROM silver.cht_outstanding_history h
        LEFT JOIN silver.cht_outstanding_history_cancellations c ON c.history_id = h.id
        GROUP BY
            h.id,
            h.series,
            h.as_of_date,
            h.title,
            h.coupon_display,
            h.coupon_kind,
            h.coupon_fixed_rate_percent,
            h.coupon_spread_percent,
            h.maturity_date,
            h.identifier,
            h.source_url;
        """

    let createCompatibilityViews =
        """
        CREATE OR REPLACE VIEW nhambs.v_mbs_pool_grid AS SELECT * FROM gold.v_mbs_pool_grid;
        CREATE OR REPLACE VIEW nhambs.v_mbs_monthly AS SELECT * FROM gold.v_mbs_monthly;
        CREATE OR REPLACE VIEW nhambs.v_mbs_issuer_summary AS SELECT * FROM gold.v_mbs_issuer_summary;
        CREATE OR REPLACE VIEW nhambs.v_mbs_province_summary AS SELECT * FROM gold.v_mbs_province_summary;
        CREATE OR REPLACE VIEW nhambs.v_mbs_rate_type_summary AS SELECT * FROM gold.v_mbs_rate_type_summary;
        CREATE OR REPLACE VIEW nhambs.v_mbs_pool_type_summary AS SELECT * FROM gold.v_mbs_pool_type_summary;
        CREATE OR REPLACE VIEW nhambs.v_mbs_insurer_summary AS SELECT * FROM gold.v_mbs_insurer_summary;
        CREATE OR REPLACE VIEW nhambs.v_mbs_maturity_summary AS SELECT * FROM gold.v_mbs_maturity_summary;
        CREATE OR REPLACE VIEW nhambs.v_covered_bond_program_summary AS SELECT * FROM gold.v_covered_bond_program_summary;
        CREATE OR REPLACE VIEW nhambs.v_covered_bond_registry_summary AS SELECT * FROM gold.v_covered_bond_registry_summary;
        CREATE OR REPLACE VIEW nhambs.v_cht_summary AS SELECT * FROM gold.v_cht_summary;
        CREATE OR REPLACE VIEW nhambs.v_cht_history_summary AS SELECT * FROM gold.v_cht_history_summary;

        CREATE OR REPLACE VIEW nhambs.mbs_pools AS SELECT * FROM silver.mbs_pools;
        CREATE OR REPLACE VIEW nhambs.mbs_pool_insurers AS SELECT * FROM silver.mbs_pool_insurers;
        CREATE OR REPLACE VIEW nhambs.mbs_pool_locations AS SELECT * FROM silver.mbs_pool_locations;
        CREATE OR REPLACE VIEW nhambs.mbs_pool_scheduled_maturity AS SELECT * FROM silver.mbs_pool_scheduled_maturity;
        CREATE OR REPLACE VIEW nhambs.mbs_pool_rate_bands AS SELECT * FROM silver.mbs_pool_rate_bands;
        """

    override this.Up() =
        this.Execute.Sql(dropExistingViews)
        this.Execute.Sql(createBronze)
        this.Execute.Sql(moveToSilver)
        this.Execute.Sql(addLineage)
        this.Execute.Sql(createGold)
        this.Execute.Sql(createCompatibilityViews)

    override this.Down() =
        this.Execute.Sql(
            """
            DROP VIEW IF EXISTS nhambs.mbs_pool_rate_bands;
            DROP VIEW IF EXISTS nhambs.mbs_pool_scheduled_maturity;
            DROP VIEW IF EXISTS nhambs.mbs_pool_locations;
            DROP VIEW IF EXISTS nhambs.mbs_pool_insurers;
            DROP VIEW IF EXISTS nhambs.mbs_pools;
            """
        )

        this.Execute.Sql(dropExistingViews)

        this.Execute.Sql(
            """
            ALTER TABLE silver.mbs_pools DROP CONSTRAINT IF EXISTS fk_mbs_pools_bronze;
            ALTER TABLE silver.cht_issues DROP CONSTRAINT IF EXISTS fk_cht_issues_bronze;
            ALTER TABLE silver.cht_circulars DROP CONSTRAINT IF EXISTS fk_cht_circulars_bronze;
            ALTER TABLE silver.cht_outstanding_history DROP CONSTRAINT IF EXISTS fk_cht_history_bronze;
            ALTER TABLE silver.covered_bond_programs DROP CONSTRAINT IF EXISTS fk_covered_programs_bronze;
            ALTER TABLE silver.covered_bond_registry DROP CONSTRAINT IF EXISTS fk_covered_registry_bronze;

            ALTER TABLE silver.mbs_pools DROP COLUMN IF EXISTS source_file_id;
            ALTER TABLE silver.cht_issues DROP COLUMN IF EXISTS source_file_id;
            ALTER TABLE silver.cht_circulars DROP COLUMN IF EXISTS source_file_id;
            ALTER TABLE silver.cht_outstanding_history DROP COLUMN IF EXISTS source_file_id;
            ALTER TABLE silver.covered_bond_programs DROP COLUMN IF EXISTS source_file_id;
            ALTER TABLE silver.covered_bond_registry DROP COLUMN IF EXISTS source_file_id;

            ALTER TABLE silver.mbs_pools SET SCHEMA nhambs;
            ALTER TABLE silver.mbs_pool_insurers SET SCHEMA nhambs;
            ALTER TABLE silver.mbs_pool_locations SET SCHEMA nhambs;
            ALTER TABLE silver.mbs_pool_scheduled_maturity SET SCHEMA nhambs;
            ALTER TABLE silver.mbs_pool_rate_bands SET SCHEMA nhambs;
            ALTER TABLE silver.cht_issues SET SCHEMA nhambs;
            ALTER TABLE silver.cht_offering_circulars SET SCHEMA nhambs;
            ALTER TABLE silver.cht_circulars SET SCHEMA nhambs;
            ALTER TABLE silver.cht_circular_ratings SET SCHEMA nhambs;
            ALTER TABLE silver.cht_circular_payment_dates SET SCHEMA nhambs;
            ALTER TABLE silver.cht_outstanding_history SET SCHEMA nhambs;
            ALTER TABLE silver.cht_outstanding_history_cancellations SET SCHEMA nhambs;
            ALTER TABLE silver.covered_bond_programs SET SCHEMA nhambs;
            ALTER TABLE silver.covered_bond_parties SET SCHEMA nhambs;
            ALTER TABLE silver.covered_bonds SET SCHEMA nhambs;
            ALTER TABLE silver.covered_bond_isins SET SCHEMA nhambs;
            ALTER TABLE silver.covered_bond_registry SET SCHEMA nhambs;

            DROP SCHEMA IF EXISTS gold CASCADE;
            DROP SCHEMA IF EXISTS silver CASCADE;
            DROP SCHEMA IF EXISTS bronze CASCADE;
            """
        )

        // Restore the pre-medallion views from M002/M003.
        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_cht_history_summary AS
            SELECT
                h.id,
                h.series,
                h.as_of_date,
                COALESCE(h.title, '')                                   AS title,
                COALESCE(h.coupon_display, '')                          AS coupon_display,
                COALESCE(h.coupon_kind, '')                             AS coupon_kind,
                COALESCE(h.coupon_fixed_rate_percent, h.coupon_spread_percent) AS coupon_value,
                h.maturity_date,
                COALESCE(h.identifier, '')                              AS identifier,
                COUNT(c.id)                                             AS cancellation_count,
                COALESCE(SUM(c.cancelled_amount), 0)                    AS cancelled_millions,
                MIN(c.from_date)                                        AS first_cancellation_date,
                MAX(c.to_date)                                          AS last_cancellation_date,
                COALESCE(h.source_url, '')                              AS source_url
            FROM nhambs.cht_outstanding_history h
            LEFT JOIN nhambs.cht_outstanding_history_cancellations c ON c.history_id = h.id
            GROUP BY
                h.id,
                h.series,
                h.as_of_date,
                h.title,
                h.coupon_display,
                h.coupon_kind,
                h.coupon_fixed_rate_percent,
                h.coupon_spread_percent,
                h.maturity_date,
                h.identifier,
                h.source_url;
            """
        )

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

            CREATE OR REPLACE VIEW nhambs.v_mbs_province_summary AS
            SELECT
                l.province_name,
                COUNT(DISTINCT p.cusip)                             AS pools,
                COALESCE(SUM(p.aggregate_principal_amount * COALESCE(l.principal_amount_percent, 0) / 100.0), 0) AS principal
            FROM nhambs.mbs_pools p
            JOIN nhambs.mbs_pool_locations l ON l.pool_cusip = p.cusip
            WHERE COALESCE(l.principal_amount_percent, 0) > 0
            GROUP BY l.province_name;

            CREATE OR REPLACE VIEW nhambs.v_mbs_rate_type_summary AS
            SELECT
                COALESCE(rate_type, 'unknown')                      AS rate_type,
                COUNT(*)                                            AS pools,
                COALESCE(SUM(aggregate_principal_amount), 0)        AS principal
            FROM nhambs.mbs_pools
            GROUP BY 1;

            CREATE OR REPLACE VIEW nhambs.v_mbs_pool_type_summary AS
            SELECT
                COALESCE(p.pool_type, '')                           AS pool_type,
                {poolTypeCase}                                      AS pool_type_description,
                COUNT(*)                                            AS pools,
                COALESCE(SUM(p.aggregate_principal_amount), 0)      AS principal
            FROM nhambs.mbs_pools p
            GROUP BY 1, 2;

            CREATE OR REPLACE VIEW nhambs.v_mbs_insurer_summary AS
            SELECT
                i.name                                              AS insurer,
                COUNT(DISTINCT i.pool_cusip)                        AS pools,
                COALESCE(SUM(COALESCE(i.dollar_value,
                    p.aggregate_principal_amount * COALESCE(i.percent_of_loans, 0) / 100.0)), 0) AS principal
            FROM nhambs.mbs_pool_insurers i
            JOIN nhambs.mbs_pools p ON p.cusip = i.pool_cusip
            GROUP BY i.name;

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

            CREATE OR REPLACE VIEW nhambs.v_cht_summary AS
            SELECT
                COUNT(*)                                            AS issue_count,
                COALESCE(SUM(outstanding), 0)                       AS outstanding_millions,
                COALESCE(SUM(total_issuance), 0)                    AS total_issuance_millions,
                MIN(maturity_date)                                  AS next_maturity_date
            FROM nhambs.cht_issues;
            """
        )

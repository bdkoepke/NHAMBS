namespace NHAMBS.Migrations

open FluentMigrator

/// Finish the 5NF clean-up for the MBS warehouse:
/// - move the remaining repeated labels (province, pool type, rate type,
///   rate band and scheduled-maturity bucket) into reference tables;
/// - keep only stable keys on the fact and junction tables;
/// - remove the derived scheduled totals from `mbs_pools` and expose them from
///   the scheduled-maturity bucket views instead.
///
/// The foreign keys are deliberately added after the existing data has been
/// mapped, so this migration can be applied to a populated warehouse.
[<Migration(202611140013L)>]
type M013_ReferenceDimensions() =
    inherit Migration()

    let dropViews =
        """
        DROP VIEW IF EXISTS nhambs.v_mbs_unresolved_entities;
        DROP VIEW IF EXISTS nhambs.v_mbs_maturity_summary;
        DROP VIEW IF EXISTS nhambs.v_mbs_insurer_summary;
        DROP VIEW IF EXISTS nhambs.v_mbs_pool_type_summary;
        DROP VIEW IF EXISTS nhambs.v_mbs_rate_type_summary;
        DROP VIEW IF EXISTS nhambs.v_mbs_province_summary;
        DROP VIEW IF EXISTS nhambs.v_mbs_issuer_summary;
        DROP VIEW IF EXISTS nhambs.v_mbs_monthly;
        DROP VIEW IF EXISTS nhambs.v_mbs_pool_grid;
        """

    let createReferenceTables =
        """
        CREATE TABLE nhambs.provinces (
            code        varchar(64) PRIMARY KEY,
            name        varchar(96) NOT NULL,
            sort_order  integer NOT NULL
        );

        INSERT INTO nhambs.provinces (code, name, sort_order)
        VALUES
            ('newfoundland', 'Newfoundland and Labrador', 0),
            ('novaScotia', 'Nova Scotia', 1),
            ('newBrunswick', 'New Brunswick', 2),
            ('princeEdwardIsland', 'Prince Edward Island', 3),
            ('quebec', 'Quebec', 4),
            ('ontario', 'Ontario', 5),
            ('manitoba', 'Manitoba', 6),
            ('saskatchewan', 'Saskatchewan', 7),
            ('alberta', 'Alberta', 8),
            ('britishColumbia', 'British Columbia', 9),
            ('northwestTerritories', 'Northwest Territories', 10),
            ('yukon', 'Yukon', 11),
            ('nunavut', 'Nunavut', 12);

        CREATE TABLE nhambs.rate_types (
            code   varchar(32) PRIMARY KEY,
            label  varchar(64) NOT NULL
        );

        INSERT INTO nhambs.rate_types (code, label)
        VALUES
            ('fixed', 'Fixed'),
            ('adjustable', 'Adjustable'),
            ('variable', 'Variable'),
            ('wac', 'WAC');

        CREATE TABLE nhambs.pool_types (
            code         varchar(16) PRIMARY KEY,
            description  varchar(160) NOT NULL
        );

        INSERT INTO nhambs.pool_types (code, description)
        VALUES
            ('867', 'Homeowner - collateral fixed'),
            ('880', 'Homeowner - adjustable CDOR (legacy)'),
            ('881', 'Homeowner - adjustable CORRA'),
            ('885', 'Homeowner - variable CDOR (legacy)'),
            ('886', 'Homeowner - variable CORRA'),
            ('964', 'Homeowner - fixed'),
            ('965', 'Multiple-family - prepayable with indemnity'),
            ('966', 'Multiple-family - closed'),
            ('967', 'Homeowner - fixed'),
            ('970', 'Homeowner - fixed with indemnity'),
            ('975', 'Homeowner - fixed with indemnity'),
            ('980', 'Homeowner - adjustable CDOR (legacy)'),
            ('981', 'Homeowner - adjustable CORRA'),
            ('985', 'Homeowner - variable CDOR (legacy)'),
            ('986', 'Homeowner - variable CORRA'),
            ('987', 'Homeowner - WAC floating'),
            ('990', 'Social housing');

        CREATE TABLE nhambs.maturity_buckets (
            code      varchar(32) PRIMARY KEY,
            label     varchar(64) NOT NULL,
            sequence  integer NOT NULL
        );

        INSERT INTO nhambs.maturity_buckets (code, label, sequence)
        VALUES
            ('fiveMonths', '5 months', 0),
            ('fourMonths', '4 months', 1),
            ('threeMonths', '3 months', 2),
            ('twoMonths', '2 months', 3),
            ('oneMonth', '1 month', 4),
            ('maturity', 'At maturity', 5),
            ('total', 'Total', 6);

        CREATE TABLE nhambs.rate_bands (
            band_key       varchar(128) PRIMARY KEY,
            label          text NOT NULL,
            lower_percent  numeric(8, 4) NULL,
            upper_percent  numeric(8, 4) NULL
        );

        COMMENT ON TABLE nhambs.rate_bands IS
            'Canonical 25 basis-point interest-rate buckets. The source label is kept in raw; malformed labels use a raw: key with null bounds.';
        COMMENT ON TABLE nhambs.maturity_buckets IS
            'Scheduled-maturity buckets in the order printed on the NHA MBS circular.';
        COMMENT ON TABLE nhambs.pool_types IS
            'NHA MBS pool-type codes and their dashboard descriptions.';
        COMMENT ON TABLE nhambs.rate_types IS
            'NHA MBS coupon rate-type codes.';
        COMMENT ON TABLE nhambs.provinces IS
            'Province and territory codes used by the location allocation table.';
        """

    let buildRateBandMap =
        """
        CREATE TABLE nhambs.rate_band_map (
            source_label  text PRIMARY KEY,
            band_key      varchar(128) NOT NULL,
            label         text NOT NULL,
            lower_percent numeric(8, 4) NOT NULL,
            upper_percent numeric(8, 4) NOT NULL
        );

        INSERT INTO nhambs.rate_band_map (source_label, band_key, label, lower_percent, upper_percent)
        WITH labels AS (
            SELECT DISTINCT rate_band AS source_label
            FROM nhambs.mbs_pool_rate_bands
        ),
        parsed AS (
            SELECT
                source_label,
                (regexp_match(trim(source_label), '^([0-9]+(?:\.[0-9]+)?)[[:space:]]*-[[:space:]]*([0-9]+(?:\.[0-9]+)?)$')) AS match
            FROM labels
        ),
        bounds AS (
            SELECT
                source_label,
                (match[1])::numeric AS lower_raw,
                (match[2])::numeric AS upper_raw
            FROM parsed
            WHERE match IS NOT NULL
        ),
        snapped AS (
            SELECT
                source_label,
                floor(lower_raw / 0.25) * 0.25 AS lower_percent,
                CASE
                    WHEN upper_raw > floor(lower_raw / 0.25) * 0.25 + 0.25
                        THEN ceil(upper_raw / 0.25) * 0.25
                    ELSE floor(lower_raw / 0.25) * 0.25 + 0.25
                END AS upper_percent
            FROM bounds
        )
        SELECT
            source_label,
            to_char(lower_percent, 'FM999990.0000') || '|' || to_char(upper_percent, 'FM999990.0000') AS band_key,
            to_char(lower_percent, 'FM999990.00') || ' - ' || to_char(upper_percent, 'FM999990.00') AS label,
            lower_percent,
            upper_percent
        FROM snapped;

        INSERT INTO nhambs.rate_bands (band_key, label, lower_percent, upper_percent)
        SELECT DISTINCT band_key, label, lower_percent, upper_percent
        FROM nhambs.rate_band_map;

        UPDATE nhambs.mbs_pool_rate_bands
        SET rate_band = map.band_key
        FROM nhambs.rate_band_map map
        WHERE map.source_label = nhambs.mbs_pool_rate_bands.rate_band;

        DROP TABLE nhambs.rate_band_map;
        """

    let attachReferenceTables =
        """
        ALTER TABLE nhambs.mbs_pools
            ALTER COLUMN pool_type SET NOT NULL,
            ALTER COLUMN rate_type SET NOT NULL,
            ALTER COLUMN issuer_id SET NOT NULL;

        ALTER TABLE nhambs.mbs_pools
            DROP CONSTRAINT IF EXISTS fk_mbs_pools_issuer;
        ALTER TABLE nhambs.mbs_pools
            ADD CONSTRAINT fk_mbs_pools_issuer
            FOREIGN KEY (issuer_id) REFERENCES nhambs.issuers (issuer_id);
        ALTER TABLE nhambs.mbs_pool_insurers
            DROP CONSTRAINT IF EXISTS fk_mbs_insurers_insurer;
        ALTER TABLE nhambs.mbs_pool_insurers
            ADD CONSTRAINT fk_mbs_insurers_insurer
            FOREIGN KEY (insurer_id) REFERENCES nhambs.insurers (insurer_id);

        ALTER TABLE nhambs.mbs_pools
            ADD CONSTRAINT fk_mbs_pools_pool_type
            FOREIGN KEY (pool_type) REFERENCES nhambs.pool_types (code);
        ALTER TABLE nhambs.mbs_pools
            ADD CONSTRAINT fk_mbs_pools_rate_type
            FOREIGN KEY (rate_type) REFERENCES nhambs.rate_types (code);

        ALTER TABLE nhambs.mbs_pool_insurers
            ALTER COLUMN insurer_id SET NOT NULL;
        ALTER TABLE nhambs.mbs_pool_insurers
            ADD CONSTRAINT uq_mbs_pool_insurers_pool_insurer
            UNIQUE (pool_cusip, insurer_id);

        ALTER TABLE nhambs.mbs_pool_locations
            ADD CONSTRAINT fk_mbs_pool_locations_province
            FOREIGN KEY (province) REFERENCES nhambs.provinces (code);
        ALTER TABLE nhambs.mbs_pool_locations
            ADD CONSTRAINT uq_mbs_pool_locations_pool_province
            UNIQUE (pool_cusip, province);

        ALTER TABLE nhambs.mbs_pool_scheduled_maturity
            ADD CONSTRAINT fk_mbs_pool_scheduled_maturity_bucket
            FOREIGN KEY (bucket) REFERENCES nhambs.maturity_buckets (code);
        ALTER TABLE nhambs.mbs_pool_scheduled_maturity
            ADD CONSTRAINT uq_mbs_pool_scheduled_maturity_pool_bucket
            UNIQUE (pool_cusip, bucket);

        ALTER TABLE nhambs.mbs_pool_rate_bands
            ALTER COLUMN rate_band TYPE varchar(128);
        ALTER TABLE nhambs.mbs_pool_rate_bands
            ADD CONSTRAINT fk_mbs_pool_rate_bands_band
            FOREIGN KEY (rate_band) REFERENCES nhambs.rate_bands (band_key);
        """

    let normalizeBuckets =
        """
        UPDATE nhambs.mbs_pool_scheduled_maturity
        SET bucket = CASE bucket
            WHEN '5 months' THEN 'fiveMonths'
            WHEN '4 months' THEN 'fourMonths'
            WHEN '3 months' THEN 'threeMonths'
            WHEN '2 months' THEN 'twoMonths'
            WHEN '1 month' THEN 'oneMonth'
            WHEN 'At maturity' THEN 'maturity'
            WHEN 'Total' THEN 'total'
            ELSE bucket
        END;
        """

    let dropDuplicatedColumns =
        """
        ALTER TABLE nhambs.mbs_pools
            DROP COLUMN issuer_name,
            DROP COLUMN issuer_key,
            DROP COLUMN mortgage_insurer,
            DROP COLUMN scheduled_remaining_face_value,
            DROP COLUMN scheduled_wac,
            DROP COLUMN scheduled_wam,
            DROP COLUMN scheduled_index_spread_total,
            DROP COLUMN scheduled_index_spread_full_term,
            DROP COLUMN scheduled_monthly_payment,
            DROP COLUMN scheduled_introductory_months;

        ALTER TABLE nhambs.mbs_pool_insurers
            DROP COLUMN name;

        ALTER TABLE nhambs.mbs_pool_locations
            DROP COLUMN province_name;
        """

    let createViews =
        """
        CREATE OR REPLACE VIEW nhambs.v_mbs_pool_grid AS
        SELECT
            p.cusip                                   AS pool_id,
            p.cusip,
            p.pool_number,
            i.canonical_name                          AS issuer_name,
            p.rate_type,
            p.pool_type,
            pt.description                            AS pool_type_description,
            COALESCE(p.coupon_display, '')            AS coupon_display,
            COALESCE(p.coupon_fixed_rate_percent, p.coupon_spread_percent) AS coupon_value,
            p.aggregate_principal_amount              AS principal,
            p.issue_date,
            p.settlement_date,
            p.maturity_date,
            COALESCE(p.total_number_of_mortgage_loans, 0) AS loans,
            NULLIF(total_bucket.weighted_average_rate, 0) AS wac,
            NULLIF(total_bucket.weighted_average_amortization, 0) AS wam,
            COALESCE((
                SELECT string_agg(pr.name, ', ' ORDER BY l.principal_amount_percent DESC)
                FROM nhambs.mbs_pool_locations l
                JOIN nhambs.provinces pr ON pr.code = l.province
                WHERE l.pool_cusip = p.cusip AND COALESCE(l.principal_amount_percent, 0) > 0
            ), '')                                    AS provinces,
            COALESCE((
                SELECT string_agg(ins.canonical_name, ', ' ORDER BY mi.dollar_value DESC)
                FROM nhambs.mbs_pool_insurers mi
                JOIN nhambs.insurers ins ON ins.insurer_id = mi.insurer_id
                WHERE mi.pool_cusip = p.cusip
            ), '')                                    AS insurers,
            COALESCE(p.source_url, '')                AS source_url
        FROM nhambs.mbs_pools p
        JOIN nhambs.issuers i ON i.issuer_id = p.issuer_id
        JOIN nhambs.pool_types pt ON pt.code = p.pool_type
        LEFT JOIN nhambs.mbs_pool_scheduled_maturity total_bucket
            ON total_bucket.pool_cusip = p.cusip AND total_bucket.bucket = 'total';

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
            i.issuer_key,
            i.canonical_name                                    AS issuer_name,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(p.aggregate_principal_amount), 0)      AS principal,
            COALESCE(SUM(p.total_number_of_mortgage_loans), 0)  AS loans,
            SUM(COALESCE(p.coupon_fixed_rate_percent, p.coupon_spread_percent, 0) * p.aggregate_principal_amount)
                / NULLIF(SUM(CASE WHEN COALESCE(p.coupon_fixed_rate_percent, p.coupon_spread_percent) IS NULL THEN 0 ELSE p.aggregate_principal_amount END), 0) AS weighted_coupon,
            MIN(p.issue_date)                                   AS first_issue,
            MAX(p.issue_date)                                   AS last_issue
        FROM nhambs.mbs_pools p
        JOIN nhambs.issuers i ON i.issuer_id = p.issuer_id
        GROUP BY i.issuer_id, i.issuer_key, i.canonical_name;

        CREATE OR REPLACE VIEW nhambs.v_mbs_province_summary AS
        SELECT
            pr.name                                             AS province_name,
            COUNT(DISTINCT p.cusip)                             AS pools,
            COALESCE(SUM(p.aggregate_principal_amount * COALESCE(l.principal_amount_percent, 0) / 100.0), 0) AS principal
        FROM nhambs.mbs_pools p
        JOIN nhambs.mbs_pool_locations l ON l.pool_cusip = p.cusip
        JOIN nhambs.provinces pr ON pr.code = l.province
        WHERE COALESCE(l.principal_amount_percent, 0) > 0
        GROUP BY pr.code, pr.name;

        CREATE OR REPLACE VIEW nhambs.v_mbs_rate_type_summary AS
        SELECT
            rt.code                                             AS rate_type,
            rt.label                                            AS rate_type_label,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(p.aggregate_principal_amount), 0)      AS principal
        FROM nhambs.mbs_pools p
        JOIN nhambs.rate_types rt ON rt.code = p.rate_type
        GROUP BY rt.code, rt.label;

        CREATE OR REPLACE VIEW nhambs.v_mbs_pool_type_summary AS
        SELECT
            pt.code                                             AS pool_type,
            pt.description                                      AS pool_type_description,
            COUNT(*)                                            AS pools,
            COALESCE(SUM(p.aggregate_principal_amount), 0)      AS principal
        FROM nhambs.mbs_pools p
        JOIN nhambs.pool_types pt ON pt.code = p.pool_type
        GROUP BY pt.code, pt.description;

        CREATE OR REPLACE VIEW nhambs.v_mbs_insurer_summary AS
        SELECT
            ins.canonical_name                                  AS insurer,
            COUNT(DISTINCT mi.pool_cusip)                       AS pools,
            COALESCE(SUM(COALESCE(mi.dollar_value,
                p.aggregate_principal_amount * COALESCE(mi.percent_of_loans, 0) / 100.0)), 0) AS principal
        FROM nhambs.mbs_pool_insurers mi
        JOIN nhambs.mbs_pools p ON p.cusip = mi.pool_cusip
        JOIN nhambs.insurers ins ON ins.insurer_id = mi.insurer_id
        GROUP BY ins.insurer_id, ins.canonical_name;

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

        CREATE OR REPLACE VIEW nhambs.v_mbs_unresolved_entities AS
        SELECT
            'issuer'::text                                      AS entity_type,
            i.canonical_name                                    AS name,
            COUNT(*)                                            AS records
        FROM nhambs.issuers i
        JOIN nhambs.mbs_pools p ON p.issuer_id = i.issuer_id
        WHERE NOT EXISTS (
            SELECT 1
            FROM nhambs.issuer_aliases a
            WHERE a.alias_key = i.issuer_key OR a.canonical_key = i.issuer_key
        )
        GROUP BY i.canonical_name
        UNION ALL
        SELECT
            'insurer'::text                                     AS entity_type,
            i.canonical_name                                    AS name,
            COUNT(*)                                            AS records
        FROM nhambs.insurers i
        JOIN nhambs.mbs_pool_insurers mi ON mi.insurer_id = i.insurer_id
        WHERE i.canonical_name = 'Unknown insurer'
        GROUP BY i.canonical_name;
        """

    override this.Up() =
        this.Execute.Sql(dropViews)
        this.Execute.Sql(createReferenceTables)
        this.Execute.Sql(buildRateBandMap)
        this.Execute.Sql(normalizeBuckets)
        this.Execute.Sql(attachReferenceTables)
        this.Execute.Sql(dropDuplicatedColumns)
        this.Execute.Sql(createViews)

    override this.Down() =
        this.Execute.Sql(dropViews)

        this.Execute.Sql(
            """
            ALTER TABLE nhambs.mbs_pools
                ADD COLUMN issuer_name text NULL,
                ADD COLUMN issuer_key varchar(256) NULL,
                ADD COLUMN mortgage_insurer text NULL,
                ADD COLUMN scheduled_remaining_face_value numeric(18, 2) NULL,
                ADD COLUMN scheduled_wac numeric(10, 5) NULL,
                ADD COLUMN scheduled_wam numeric(12, 4) NULL,
                ADD COLUMN scheduled_index_spread_total numeric(10, 5) NULL,
                ADD COLUMN scheduled_index_spread_full_term numeric(10, 5) NULL,
                ADD COLUMN scheduled_monthly_payment numeric(18, 2) NULL,
                ADD COLUMN scheduled_introductory_months numeric(10, 5) NULL;

            UPDATE nhambs.mbs_pools p
            SET issuer_name = i.canonical_name,
                issuer_key = i.issuer_key,
                mortgage_insurer = (
                    SELECT string_agg(ins.canonical_name, ' and ' ORDER BY mi.dollar_value DESC)
                    FROM nhambs.mbs_pool_insurers mi
                    JOIN nhambs.insurers ins ON ins.insurer_id = mi.insurer_id
                    WHERE mi.pool_cusip = p.cusip
                ),
                scheduled_remaining_face_value = (
                    SELECT total.remaining_face_value
                    FROM nhambs.mbs_pool_scheduled_maturity total
                    WHERE total.pool_cusip = p.cusip AND total.bucket = 'total'
                ),
                scheduled_wac = (
                    SELECT total.weighted_average_rate
                    FROM nhambs.mbs_pool_scheduled_maturity total
                    WHERE total.pool_cusip = p.cusip AND total.bucket = 'total'
                ),
                scheduled_wam = (
                    SELECT total.weighted_average_amortization
                    FROM nhambs.mbs_pool_scheduled_maturity total
                    WHERE total.pool_cusip = p.cusip AND total.bucket = 'total'
                ),
                scheduled_index_spread_total = (
                    SELECT total.index_spread_total
                    FROM nhambs.mbs_pool_scheduled_maturity total
                    WHERE total.pool_cusip = p.cusip AND total.bucket = 'total'
                ),
                scheduled_index_spread_full_term = (
                    SELECT total.index_spread_full_term
                    FROM nhambs.mbs_pool_scheduled_maturity total
                    WHERE total.pool_cusip = p.cusip AND total.bucket = 'total'
                ),
                scheduled_monthly_payment = (
                    SELECT total.monthly_payment
                    FROM nhambs.mbs_pool_scheduled_maturity total
                    WHERE total.pool_cusip = p.cusip AND total.bucket = 'total'
                ),
                scheduled_introductory_months = (
                    SELECT total.introductory_months
                    FROM nhambs.mbs_pool_scheduled_maturity total
                    WHERE total.pool_cusip = p.cusip AND total.bucket = 'total'
                )
            FROM nhambs.issuers i
            WHERE i.issuer_id = p.issuer_id;

            ALTER TABLE nhambs.mbs_pools
                ALTER COLUMN issuer_name SET NOT NULL,
                ALTER COLUMN issuer_key SET NOT NULL;

            ALTER TABLE nhambs.mbs_pool_insurers
                ADD COLUMN name text NULL;
            UPDATE nhambs.mbs_pool_insurers mi
            SET name = i.canonical_name
            FROM nhambs.insurers i
            WHERE i.insurer_id = mi.insurer_id;
            ALTER TABLE nhambs.mbs_pool_insurers
                ALTER COLUMN name SET NOT NULL;

            ALTER TABLE nhambs.mbs_pool_locations
                ADD COLUMN province_name varchar(96) NULL;
            UPDATE nhambs.mbs_pool_locations l
            SET province_name = p.name
            FROM nhambs.provinces p
            WHERE p.code = l.province;
            ALTER TABLE nhambs.mbs_pool_locations
                ALTER COLUMN province_name SET NOT NULL;

            ALTER TABLE nhambs.mbs_pool_rate_bands
                DROP CONSTRAINT IF EXISTS fk_mbs_pool_rate_bands_band;
            UPDATE nhambs.mbs_pool_rate_bands b
            SET rate_band = rb.label
            FROM nhambs.rate_bands rb
            WHERE rb.band_key = b.rate_band;

            ALTER TABLE nhambs.mbs_pool_scheduled_maturity
                DROP CONSTRAINT IF EXISTS fk_mbs_pool_scheduled_maturity_bucket;
            UPDATE nhambs.mbs_pool_scheduled_maturity s
            SET bucket = mb.label
            FROM nhambs.maturity_buckets mb
            WHERE mb.code = s.bucket;

            ALTER TABLE nhambs.mbs_pool_insurers
                DROP CONSTRAINT IF EXISTS uq_mbs_pool_insurers_pool_insurer;
            ALTER TABLE nhambs.mbs_pool_locations
                DROP CONSTRAINT IF EXISTS uq_mbs_pool_locations_pool_province;
            ALTER TABLE nhambs.mbs_pool_locations
                DROP CONSTRAINT IF EXISTS fk_mbs_pool_locations_province;
            ALTER TABLE nhambs.mbs_pool_scheduled_maturity
                DROP CONSTRAINT IF EXISTS uq_mbs_pool_scheduled_maturity_pool_bucket;
            ALTER TABLE nhambs.mbs_pools
                DROP CONSTRAINT IF EXISTS fk_mbs_pools_pool_type;
            ALTER TABLE nhambs.mbs_pools
                DROP CONSTRAINT IF EXISTS fk_mbs_pools_rate_type;
            ALTER TABLE nhambs.mbs_pools
                ALTER COLUMN pool_type DROP NOT NULL,
                ALTER COLUMN rate_type DROP NOT NULL,
                ALTER COLUMN issuer_id DROP NOT NULL;

            ALTER TABLE nhambs.mbs_pools
                DROP CONSTRAINT IF EXISTS fk_mbs_pools_issuer;
            ALTER TABLE nhambs.mbs_pools
                ADD CONSTRAINT fk_mbs_pools_issuer
                FOREIGN KEY (issuer_id) REFERENCES nhambs.issuers (issuer_id) ON DELETE SET NULL;
            ALTER TABLE nhambs.mbs_pool_insurers
                DROP CONSTRAINT IF EXISTS fk_mbs_insurers_insurer;
            ALTER TABLE nhambs.mbs_pool_insurers
                ADD CONSTRAINT fk_mbs_insurers_insurer
                FOREIGN KEY (insurer_id) REFERENCES nhambs.insurers (insurer_id) ON DELETE SET NULL;

            CREATE INDEX ix_mbs_pools_issuer ON nhambs.mbs_pools (issuer_key);
            CREATE INDEX ix_mbs_pools_search ON nhambs.mbs_pools (lower(pool_number), lower(issuer_name), cusip);
            CREATE INDEX ix_mbs_insurers_name ON nhambs.mbs_pool_insurers (name);

            DROP TABLE IF EXISTS nhambs.rate_bands;
            DROP TABLE IF EXISTS nhambs.maturity_buckets;
            DROP TABLE IF EXISTS nhambs.pool_types;
            DROP TABLE IF EXISTS nhambs.rate_types;
            DROP TABLE IF EXISTS nhambs.provinces;
            """
        )

        this.Execute.Sql(
            """
            CREATE OR REPLACE VIEW nhambs.v_mbs_pool_grid AS
            SELECT
                p.cusip                                   AS pool_id,
                p.cusip,
                p.pool_number,
                p.issuer_name,
                COALESCE(p.rate_type, '')                 AS rate_type,
                COALESCE(p.pool_type, '')                 AS pool_type,
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
                END                                       AS pool_type_description,
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
                END                                                   AS pool_type_description,
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

            CREATE OR REPLACE VIEW nhambs.v_mbs_unresolved_entities AS
            SELECT
                'issuer'::text AS entity_type,
                p.issuer_name AS name,
                COUNT(*)      AS records
            FROM nhambs.mbs_pools p
            WHERE p.issuer_id IS NULL OR p.issuer_name = 'Unknown issuer'
            GROUP BY p.issuer_name
            UNION ALL
            SELECT
                'insurer'::text AS entity_type,
                i.name         AS name,
                COUNT(*)       AS records
            FROM nhambs.mbs_pool_insurers i
            WHERE i.insurer_id IS NULL OR i.name = 'Unknown insurer'
            GROUP BY i.name;
            """
        )

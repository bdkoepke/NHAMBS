namespace NHAMBS.Migrations

open FluentMigrator

/// Mortgage-arrears data from the Canadian Bankers Association and CMHC.
///
/// Both providers report mortgages that are 90+ days past due, but they cover
/// different lender populations: CBA is a monthly chartered-bank series, while
/// CMHC publishes a quarterly Equifax series for all lender types. The
/// observation table keeps provider, lender type, geography, arrears stage and
/// metric explicit so the two series can be compared without collapsing their
/// definitions.
[<Migration(202611140014L)>]
type M014_Arrears() =
    inherit Migration()

    let dropViews =
        """
        DROP VIEW IF EXISTS nhambs.v_arrears_freshness;
        DROP VIEW IF EXISTS nhambs.v_arrears_provider_comparison;
        DROP VIEW IF EXISTS nhambs.v_arrears_latest;
        DROP VIEW IF EXISTS nhambs.v_arrears_series;
        """

    let createRawDocuments =
        """
        CREATE TABLE raw.source_documents (
            source_document_id  bigserial PRIMARY KEY,
            dataset             varchar(32) NOT NULL,
            source_path         text NOT NULL,
            file_name           text NOT NULL,
            content_type        varchar(128) NOT NULL,
            checksum_sha256     char(64) NOT NULL,
            size_bytes          bigint NOT NULL,
            source_modified_at  timestamptz NULL,
            loaded_at           timestamptz NOT NULL DEFAULT now(),
            run_id              bigint NULL REFERENCES nhambs.etl_runs (run_id) ON DELETE SET NULL,
            document            bytea NOT NULL,
            CONSTRAINT uq_source_documents UNIQUE (dataset, source_path)
        );

        CREATE INDEX ix_source_documents_dataset
            ON raw.source_documents (dataset, loaded_at DESC);
        CREATE INDEX ix_source_documents_checksum
            ON raw.source_documents (checksum_sha256);
        """

    let createSources =
        """
        CREATE TABLE nhambs.arrears_sources (
            source_code       varchar(32) PRIMARY KEY,
            provider          text NOT NULL,
            dataset_name      text NOT NULL,
            definition        text NOT NULL,
            cadence           varchar(16) NOT NULL,
            lender_type       varchar(32) NOT NULL,
            product_type      varchar(64) NOT NULL,
            insured_status    varchar(16) NOT NULL,
            source_url        text NOT NULL,
            attribution       text NOT NULL,
            latest_period_end date NULL,
            last_loaded_at    timestamptz NULL
        );

        INSERT INTO nhambs.arrears_sources
            (source_code, provider, dataset_name, definition, cadence, lender_type,
             product_type, insured_status, source_url, attribution)
        VALUES
            (
                'cba',
                'Canadian Bankers Association',
                'Number of Residential Mortgages in Arrears',
                'Residential mortgage payments overdue by three or more months (90+ days). Chartered-bank mortgages only.',
                'monthly',
                'chartered_banks',
                'residential_mortgage',
                'all',
                'https://cba.ca/article/mortgages-in-arrears',
                'Source: Canadian Bankers Association, Number of Residential Mortgages in Arrears.'
            ),
            (
                'cmhc',
                'Canada Mortgage and Housing Corporation',
                'Mortgage Delinquency Rate: Canada, Provinces and CMAs',
                'Share of mortgage loans past due 90 days or more. Equifax Canada data covering all lender types.',
                'quarterly',
                'all_lenders',
                'residential_mortgage',
                'all',
                'https://www.cmhc-schl.gc.ca/professionals/housing-markets-data-and-research/housing-data/data-tables/mortgage-and-debt/mortgage-delinquency-rate-canada-provinces-cmas',
                'Source: Canada Mortgage and Housing Corporation, Mortgage Delinquency Rate: Canada, Provinces and CMAs. Equifax Canada data reproduced with permission.'
            );
        """

    let createGeographies =
        """
        CREATE TABLE nhambs.arrears_geographies (
            geography_code varchar(64) PRIMARY KEY,
            name           text NOT NULL,
            level          varchar(16) NOT NULL,
            province_code  varchar(64) NULL REFERENCES nhambs.provinces (code),
            sort_order     integer NOT NULL,
            CONSTRAINT ck_arrears_geography_level
                CHECK (level IN ('country', 'province', 'region', 'cma'))
        );

        INSERT INTO nhambs.arrears_geographies (geography_code, name, level, province_code, sort_order)
        SELECT 'canada', 'Canada', 'country', NULL, 0
        UNION ALL
        SELECT code, name, 'province', code, 10 + sort_order FROM nhambs.provinces
        UNION ALL
        SELECT 'atlantic', 'Atlantic', 'region', NULL, 50
        UNION ALL
        SELECT 'territories', 'Territories', 'region', NULL, 51;
        """

    let createObservations =
        """
        CREATE TABLE nhambs.arrears_observations (
            id                  bigserial PRIMARY KEY,
            source_code         varchar(32) NOT NULL REFERENCES nhambs.arrears_sources (source_code),
            period_type         varchar(16) NOT NULL,
            period_start        date NOT NULL,
            period_end          date NOT NULL,
            geography_code      varchar(64) NOT NULL REFERENCES nhambs.arrears_geographies (geography_code),
            lender_type         varchar(32) NOT NULL,
            insured_status      varchar(16) NOT NULL,
            product_type        varchar(64) NOT NULL,
            arrears_stage       varchar(32) NOT NULL,
            metric              varchar(32) NOT NULL,
            value               numeric(18, 6) NOT NULL,
            is_suppressed       boolean NOT NULL DEFAULT false,
            source_document_id  bigint NULL REFERENCES raw.source_documents (source_document_id) ON DELETE SET NULL,
            ingested_at         timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_arrears_period_type CHECK (period_type IN ('monthly', 'quarterly')),
            CONSTRAINT ck_arrears_metric
                CHECK (metric IN ('arrears_rate_percent', 'arrears_count', 'mortgage_count')),
            CONSTRAINT uq_arrears_observation
                UNIQUE (source_code, period_end, geography_code, lender_type,
                        insured_status, product_type, arrears_stage, metric)
        );

        CREATE INDEX ix_arrears_observations_period
            ON nhambs.arrears_observations (period_end DESC);
        CREATE INDEX ix_arrears_observations_geography
            ON nhambs.arrears_observations (geography_code, period_end DESC);
        CREATE INDEX ix_arrears_observations_source
            ON nhambs.arrears_observations (source_code, period_end DESC);
        """

    let createViews =
        """
        CREATE OR REPLACE VIEW nhambs.v_arrears_series AS
        SELECT
            o.id,
            o.source_code,
            s.provider,
            s.dataset_name,
            s.cadence,
            o.period_type,
            o.period_start,
            o.period_end,
            o.geography_code,
            g.name AS geography_name,
            g.level AS geography_level,
            o.lender_type,
            o.insured_status,
            o.product_type,
            o.arrears_stage,
            o.metric,
            o.value,
            o.is_suppressed,
            o.source_document_id,
            o.ingested_at
        FROM nhambs.arrears_observations o
        JOIN nhambs.arrears_sources s ON s.source_code = o.source_code
        JOIN nhambs.arrears_geographies g ON g.geography_code = o.geography_code;

        CREATE OR REPLACE VIEW nhambs.v_arrears_latest AS
        SELECT DISTINCT ON (o.source_code, o.geography_code, o.metric)
            o.source_code,
            s.provider,
            s.cadence,
            o.period_type,
            o.period_start,
            o.period_end,
            o.geography_code,
            g.name AS geography_name,
            g.level AS geography_level,
            o.lender_type,
            o.insured_status,
            o.product_type,
            o.arrears_stage,
            o.metric,
            o.value,
            o.is_suppressed,
            o.ingested_at
        FROM nhambs.arrears_observations o
        JOIN nhambs.arrears_sources s ON s.source_code = o.source_code
        JOIN nhambs.arrears_geographies g ON g.geography_code = o.geography_code
        ORDER BY o.source_code, o.geography_code, o.metric, o.period_end DESC;

        CREATE OR REPLACE VIEW nhambs.v_arrears_provider_comparison AS
        SELECT
            o.period_end,
            o.source_code,
            s.provider,
            o.value AS arrears_rate_percent
        FROM nhambs.arrears_observations o
        JOIN nhambs.arrears_sources s ON s.source_code = o.source_code
        WHERE o.geography_code = 'canada'
          AND o.metric = 'arrears_rate_percent'
          AND o.arrears_stage = '90_plus_days'
        ORDER BY o.period_end, o.source_code;

        CREATE OR REPLACE VIEW nhambs.v_arrears_freshness AS
        SELECT
            s.source_code,
            s.provider,
            s.cadence,
            max(o.period_end) AS latest_period_end,
            CASE
                WHEN max(o.period_end) IS NULL THEN NULL
                ELSE current_date - max(o.period_end)
            END AS days_since_period_end,
            CASE
                WHEN max(o.period_end) IS NULL THEN true
                WHEN s.cadence = 'monthly' AND current_date - max(o.period_end) > 120 THEN true
                WHEN s.cadence = 'quarterly' AND current_date - max(o.period_end) > 180 THEN true
                ELSE false
            END AS is_stale,
            max(o.ingested_at) AS last_loaded_at
        FROM nhambs.arrears_sources s
        LEFT JOIN nhambs.arrears_observations o ON o.source_code = s.source_code
        GROUP BY s.source_code, s.provider, s.cadence;
        """

    override this.Up() =
        this.Execute.Sql(createRawDocuments)
        this.Execute.Sql(createSources)
        this.Execute.Sql(createGeographies)
        this.Execute.Sql(createObservations)
        this.Execute.Sql(createViews)

    override this.Down() =
        this.Execute.Sql(dropViews)
        this.Execute.Sql("DROP TABLE IF EXISTS nhambs.arrears_observations;")
        this.Execute.Sql("DROP TABLE IF EXISTS nhambs.arrears_geographies;")
        this.Execute.Sql("DROP TABLE IF EXISTS nhambs.arrears_sources;")
        this.Execute.Sql("DROP TABLE IF EXISTS raw.source_documents;")

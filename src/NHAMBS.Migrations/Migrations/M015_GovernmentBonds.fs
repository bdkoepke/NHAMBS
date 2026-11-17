namespace NHAMBS.Migrations

open FluentMigrator

/// Issue-level Government of Canada domestic marketable bonds and treasury
/// bills. The Bank of Canada Valet `GOC_OUTSTANDING` group publishes a daily
/// snapshot with ISIN, coupon, issue/maturity dates and outstanding amounts.
///
/// Provincial and agency issue-level feeds are not available from a single
/// public source; `docs/government-bonds.md` records the candidates and their
/// limitations.
[<Migration(202611140015L)>]
type M015_GovernmentBonds() =
    inherit Migration()

    let dropViews =
        """
        DROP VIEW IF EXISTS nhambs.v_government_bond_freshness;
        DROP VIEW IF EXISTS nhambs.v_government_bond_program_summary;
        DROP VIEW IF EXISTS nhambs.v_government_bond_latest;
        """

    let createPrograms =
        """
        CREATE TABLE nhambs.government_bond_programs (
            program_code       varchar(32) PRIMARY KEY,
            name               text NOT NULL,
            issuer_name        text NOT NULL,
            government_level   varchar(16) NOT NULL,
            country_code       char(2) NOT NULL DEFAULT 'CA',
            source_name        text NOT NULL,
            source_url         text NOT NULL,
            cadence            varchar(16) NOT NULL,
            notes              text NOT NULL,
            latest_as_of_date  date NULL,
            last_loaded_at     timestamptz NULL,
            CONSTRAINT ck_government_bond_level
                CHECK (government_level IN ('federal', 'provincial', 'agency'))
        );

        INSERT INTO nhambs.government_bond_programs
            (program_code, name, issuer_name, government_level, source_name, source_url, cadence, notes)
        VALUES
            (
                'goc',
                'Government of Canada domestic marketable securities',
                'Government of Canada',
                'federal',
                'Bank of Canada Valet — Government of Canada Treasury Bills and Domestic Marketable Bonds Outstanding',
                'https://www.bankofcanada.ca/markets/government-securities-auctions/goc-t-bills-bonds-outstanding/',
                'daily',
                'Issue-level domestic marketable bonds and treasury bills. Outstanding amounts are par value and exclude securities that have auctioned but not settled.'
            );
        """

    let createIssues =
        """
        CREATE TABLE nhambs.government_bond_issues (
            id                              bigserial PRIMARY KEY,
            program_code                    varchar(32) NOT NULL REFERENCES nhambs.government_bond_programs (program_code),
            isin                            varchar(32) NOT NULL,
            security_type                   varchar(16) NOT NULL,
            instrument_type                 varchar(32) NOT NULL,
            coupon_rate_percent             numeric(10, 5) NOT NULL,
            issue_date                      date NOT NULL,
            maturity_date                   date NOT NULL,
            outstanding_amount              numeric(20, 2) NOT NULL,
            inflation_adjustment            numeric(20, 2) NULL,
            outstanding_inflation_adjusted  numeric(20, 2) NOT NULL,
            issuance_term_bucket            varchar(16) NULL,
            maturity_term_bucket            varchar(16) NULL,
            as_of_date                      date NOT NULL,
            source_document_id              bigint NULL REFERENCES raw.source_documents (source_document_id) ON DELETE SET NULL,
            ingested_at                     timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT uq_government_bond_issue
                UNIQUE (program_code, isin, as_of_date)
        );

        CREATE INDEX ix_government_bond_issues_as_of
            ON nhambs.government_bond_issues (as_of_date DESC);
        CREATE INDEX ix_government_bond_issues_maturity
            ON nhambs.government_bond_issues (maturity_date);
        CREATE INDEX ix_government_bond_issues_security
            ON nhambs.government_bond_issues (security_type, as_of_date DESC);
        """

    let createViews =
        """
        CREATE OR REPLACE VIEW nhambs.v_government_bond_latest AS
        SELECT i.*
        FROM nhambs.government_bond_issues i
        JOIN (
            SELECT program_code, max(as_of_date) AS as_of_date
            FROM nhambs.government_bond_issues
            GROUP BY program_code
        ) latest
            ON latest.program_code = i.program_code
           AND latest.as_of_date = i.as_of_date;

        CREATE OR REPLACE VIEW nhambs.v_government_bond_program_summary AS
        SELECT
            p.program_code,
            p.name,
            p.issuer_name,
            p.government_level,
            p.source_name,
            p.source_url,
            p.cadence,
            p.notes,
            max(i.as_of_date) AS latest_as_of_date,
            count(DISTINCT i.isin) AS issue_count,
            COALESCE(sum(i.outstanding_amount) FILTER (WHERE i.security_type = 'BOND'), 0) AS bond_outstanding,
            COALESCE(sum(i.outstanding_amount) FILTER (WHERE i.security_type IN ('TBILL', 'MM')), 0) AS bill_outstanding,
            COALESCE(sum(i.outstanding_amount), 0) AS total_outstanding,
            sum(i.coupon_rate_percent * i.outstanding_amount) FILTER (WHERE i.security_type = 'BOND')
                / NULLIF(sum(i.outstanding_amount) FILTER (WHERE i.security_type = 'BOND'), 0) AS weighted_average_coupon,
            max(i.ingested_at) AS last_loaded_at
        FROM nhambs.government_bond_programs p
        LEFT JOIN nhambs.government_bond_issues i ON i.program_code = p.program_code
        GROUP BY p.program_code, p.name, p.issuer_name, p.government_level,
                 p.source_name, p.source_url, p.cadence, p.notes;

        CREATE OR REPLACE VIEW nhambs.v_government_bond_freshness AS
        SELECT
            program_code,
            name,
            cadence,
            latest_as_of_date,
            CASE
                WHEN latest_as_of_date IS NULL THEN NULL
                ELSE current_date - latest_as_of_date
            END AS days_since_as_of,
            CASE
                WHEN latest_as_of_date IS NULL THEN true
                ELSE current_date - latest_as_of_date > 10
            END AS is_stale,
            last_loaded_at
        FROM nhambs.v_government_bond_program_summary;
        """

    override this.Up() =
        this.Execute.Sql(createPrograms)
        this.Execute.Sql(createIssues)
        this.Execute.Sql(createViews)

    override this.Down() =
        this.Execute.Sql(dropViews)
        this.Execute.Sql("DROP TABLE IF EXISTS nhambs.government_bond_issues;")
        this.Execute.Sql("DROP TABLE IF EXISTS nhambs.government_bond_programs;")

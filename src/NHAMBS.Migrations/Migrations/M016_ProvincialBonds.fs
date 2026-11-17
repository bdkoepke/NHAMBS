namespace NHAMBS.Migrations

open FluentMigrator

/// Provincial bond transactions from the Bank of Canada Provincial Bond
/// Purchase Program (PBPP). This is the public issue-level provincial feed:
/// it carries issuer, ISIN, coupon, maturity, trade/settlement dates, amount
/// purchased and purchase yield. It is a historical purchase programme, not a
/// current outstanding snapshot.
[<Migration(202611140016L)>]
type M016_ProvincialBonds() =
    inherit Migration()

    let dropViews =
        """
        DROP VIEW IF EXISTS nhambs.v_provincial_bond_issue_summary;
        DROP VIEW IF EXISTS nhambs.v_provincial_bond_summary;
        DROP VIEW IF EXISTS nhambs.v_provincial_bond_transactions;
        """

    let createProgram =
        """
        INSERT INTO nhambs.government_bond_programs
            (program_code, name, issuer_name, government_level, source_name, source_url, cadence, notes)
        VALUES
            (
                'pbpp',
                'Provincial Bond Purchase Program transactions',
                'Participating provincial issuers',
                'provincial',
                'Bank of Canada Valet — Provincial Bond Purchase Program transaction data',
                'https://www.bankofcanada.ca/markets/market-operations-liquidity-provision/market-operations-programs-and-facilities/provincial-bond-purchase-program/',
                'historical',
                'Issue-level purchases made under the Bank of Canada Provincial Bond Purchase Program in 2020-2021. Amounts purchased are not current outstanding balances.'
            )
        ON CONFLICT (program_code) DO NOTHING;
        """

    let createTransactions =
        """
        CREATE TABLE nhambs.provincial_bond_transactions (
            id                      bigserial PRIMARY KEY,
            program_code            varchar(32) NOT NULL REFERENCES nhambs.government_bond_programs (program_code),
            issuer_name             text NOT NULL,
            province_code           varchar(64) NULL REFERENCES nhambs.provinces (code),
            isin                    varchar(32) NOT NULL,
            coupon_rate_percent     numeric(10, 5) NOT NULL,
            maturity_date           date NOT NULL,
            trade_date              date NOT NULL,
            settlement_date         date NULL,
            amount_purchased        numeric(20, 2) NOT NULL,
            purchase_price          numeric(12, 4) NULL,
            purchase_yield_percent  numeric(10, 5) NULL,
            source_document_id      bigint NULL REFERENCES raw.source_documents (source_document_id) ON DELETE SET NULL,
            ingested_at             timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT uq_provincial_bond_transaction
                UNIQUE (program_code, isin, trade_date, amount_purchased, purchase_price)
        );

        CREATE INDEX ix_provincial_bond_transactions_province
            ON nhambs.provincial_bond_transactions (province_code, trade_date DESC);
        CREATE INDEX ix_provincial_bond_transactions_trade
            ON nhambs.provincial_bond_transactions (trade_date DESC);
        CREATE INDEX ix_provincial_bond_transactions_maturity
            ON nhambs.provincial_bond_transactions (maturity_date);
        """

    let createViews =
        """
        CREATE OR REPLACE VIEW nhambs.v_provincial_bond_transactions AS
        SELECT
            t.id,
            t.program_code,
            t.issuer_name,
            t.province_code,
            p.name AS province_name,
            t.isin,
            t.coupon_rate_percent,
            t.maturity_date,
            t.trade_date,
            t.settlement_date,
            t.amount_purchased,
            t.purchase_price,
            t.purchase_yield_percent,
            t.source_document_id,
            t.ingested_at
        FROM nhambs.provincial_bond_transactions t
        LEFT JOIN nhambs.provinces p ON p.code = t.province_code;

        CREATE OR REPLACE VIEW nhambs.v_provincial_bond_issue_summary AS
        SELECT
            issuer_name,
            province_code,
            COALESCE(province_name, 'Other provincial issuers') AS province_name,
            isin,
            coupon_rate_percent,
            maturity_date,
            count(*) AS transaction_count,
            sum(amount_purchased) AS amount_purchased,
            sum(purchase_yield_percent * amount_purchased)
                / NULLIF(sum(amount_purchased) FILTER (WHERE purchase_yield_percent IS NOT NULL), 0) AS weighted_average_yield,
            min(trade_date) AS first_trade_date,
            max(trade_date) AS last_trade_date
        FROM nhambs.v_provincial_bond_transactions
        GROUP BY issuer_name, province_code, province_name, isin, coupon_rate_percent, maturity_date;

        CREATE OR REPLACE VIEW nhambs.v_provincial_bond_summary AS
        SELECT
            province_code,
            COALESCE(province_name, 'Other provincial issuers') AS province_name,
            count(*) AS transaction_count,
            count(DISTINCT isin) AS issue_count,
            count(DISTINCT issuer_name) AS issuer_count,
            sum(amount_purchased) AS amount_purchased,
            sum(purchase_yield_percent * amount_purchased)
                / NULLIF(sum(amount_purchased) FILTER (WHERE purchase_yield_percent IS NOT NULL), 0) AS weighted_average_yield,
            min(trade_date) AS first_trade_date,
            max(trade_date) AS last_trade_date
        FROM nhambs.v_provincial_bond_transactions
        GROUP BY province_code, province_name;
        """

    override this.Up() =
        this.Execute.Sql(createProgram)
        this.Execute.Sql(createTransactions)
        this.Execute.Sql(createViews)

    override this.Down() =
        this.Execute.Sql(dropViews)
        this.Execute.Sql("DROP TABLE IF EXISTS nhambs.provincial_bond_transactions;")
        this.Execute.Sql("DELETE FROM nhambs.government_bond_programs WHERE program_code = 'pbpp';")

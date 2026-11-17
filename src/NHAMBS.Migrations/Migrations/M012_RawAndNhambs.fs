namespace NHAMBS.Migrations

open FluentMigrator

/// Keep only two schemas: `raw` for landed source documents and `nhambs` for
/// the conformed tables and analytics views. The former bronze schema becomes
/// raw; the silver tables move into nhambs and the temporary compatibility
/// views are no longer needed.
[<Migration(202611140012L)>]
type M012_RawAndNhambs() =
    inherit Migration()

    let dropCompatibilityViews =
        """
        DROP VIEW IF EXISTS nhambs.mbs_pools;
        DROP VIEW IF EXISTS nhambs.mbs_pool_insurers;
        DROP VIEW IF EXISTS nhambs.mbs_pool_locations;
        DROP VIEW IF EXISTS nhambs.mbs_pool_scheduled_maturity;
        DROP VIEW IF EXISTS nhambs.mbs_pool_rate_bands;
        DROP VIEW IF EXISTS nhambs.cht_issues;
        DROP VIEW IF EXISTS nhambs.cht_offering_circulars;
        DROP VIEW IF EXISTS nhambs.cht_circulars;
        DROP VIEW IF EXISTS nhambs.cht_circular_ratings;
        DROP VIEW IF EXISTS nhambs.cht_circular_payment_dates;
        DROP VIEW IF EXISTS nhambs.cht_outstanding_history;
        DROP VIEW IF EXISTS nhambs.cht_outstanding_history_cancellations;
        DROP VIEW IF EXISTS nhambs.covered_bond_programs;
        DROP VIEW IF EXISTS nhambs.covered_bond_parties;
        DROP VIEW IF EXISTS nhambs.covered_bonds;
        DROP VIEW IF EXISTS nhambs.covered_bond_isins;
        DROP VIEW IF EXISTS nhambs.covered_bond_registry;
        """

    let moveTablesToNhambs =
        """
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
        ALTER TABLE silver.issuers SET SCHEMA nhambs;
        ALTER TABLE silver.insurers SET SCHEMA nhambs;
        ALTER TABLE silver.issuer_aliases SET SCHEMA nhambs;
        """

    let createResolver =
        """
        CREATE OR REPLACE FUNCTION nhambs.resolve_issuer_alias(input_key text)
        RETURNS TABLE (canonical_key text, canonical_name text)
        LANGUAGE sql
        STABLE
        PARALLEL SAFE
        AS $$
            SELECT a.canonical_key, a.canonical_name
            FROM nhambs.issuer_aliases a
            WHERE a.alias_key = input_key
               OR (
                   length(input_key) >= 6
                   AND public.levenshtein_less_equal(input_key, a.alias_key, 4) <= 4
                   AND public.dmetaphone(input_key) = public.dmetaphone(a.alias_key)
               )
            ORDER BY
                CASE WHEN a.alias_key = input_key THEN 0 ELSE 1 END,
                public.levenshtein_less_equal(input_key, a.alias_key, 4),
                a.alias_key
            LIMIT 1
        $$;
        """

    let moveTablesToSilver =
        """
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
        ALTER TABLE nhambs.issuers SET SCHEMA silver;
        ALTER TABLE nhambs.insurers SET SCHEMA silver;
        ALTER TABLE nhambs.issuer_aliases SET SCHEMA silver;
        """

    let restoreCompatibilityViews =
        """
        CREATE OR REPLACE VIEW nhambs.mbs_pools AS SELECT * FROM silver.mbs_pools;
        CREATE OR REPLACE VIEW nhambs.mbs_pool_insurers AS SELECT * FROM silver.mbs_pool_insurers;
        CREATE OR REPLACE VIEW nhambs.mbs_pool_locations AS SELECT * FROM silver.mbs_pool_locations;
        CREATE OR REPLACE VIEW nhambs.mbs_pool_scheduled_maturity AS SELECT * FROM silver.mbs_pool_scheduled_maturity;
        CREATE OR REPLACE VIEW nhambs.mbs_pool_rate_bands AS SELECT * FROM silver.mbs_pool_rate_bands;
        CREATE OR REPLACE VIEW nhambs.cht_issues AS SELECT * FROM silver.cht_issues;
        CREATE OR REPLACE VIEW nhambs.cht_offering_circulars AS SELECT * FROM silver.cht_offering_circulars;
        CREATE OR REPLACE VIEW nhambs.cht_circulars AS SELECT * FROM silver.cht_circulars;
        CREATE OR REPLACE VIEW nhambs.cht_circular_ratings AS SELECT * FROM silver.cht_circular_ratings;
        CREATE OR REPLACE VIEW nhambs.cht_circular_payment_dates AS SELECT * FROM silver.cht_circular_payment_dates;
        CREATE OR REPLACE VIEW nhambs.cht_outstanding_history AS SELECT * FROM silver.cht_outstanding_history;
        CREATE OR REPLACE VIEW nhambs.cht_outstanding_history_cancellations AS SELECT * FROM silver.cht_outstanding_history_cancellations;
        CREATE OR REPLACE VIEW nhambs.covered_bond_programs AS SELECT * FROM silver.covered_bond_programs;
        CREATE OR REPLACE VIEW nhambs.covered_bond_parties AS SELECT * FROM silver.covered_bond_parties;
        CREATE OR REPLACE VIEW nhambs.covered_bonds AS SELECT * FROM silver.covered_bonds;
        CREATE OR REPLACE VIEW nhambs.covered_bond_isins AS SELECT * FROM silver.covered_bond_isins;
        CREATE OR REPLACE VIEW nhambs.covered_bond_registry AS SELECT * FROM silver.covered_bond_registry;
        """

    override this.Up() =
        this.Execute.Sql(dropCompatibilityViews)
        this.Execute.Sql("DROP FUNCTION IF EXISTS silver.resolve_issuer_alias(text);")
        this.Execute.Sql(moveTablesToNhambs)
        this.Execute.Sql("ALTER EXTENSION fuzzystrmatch SET SCHEMA public;")
        this.Execute.Sql(createResolver)
        this.Execute.Sql("ALTER SCHEMA bronze RENAME TO raw;")
        this.Execute.Sql("DROP SCHEMA silver;")

    override this.Down() =
        this.Execute.Sql("ALTER SCHEMA raw RENAME TO bronze;")
        this.Execute.Sql("DROP FUNCTION IF EXISTS nhambs.resolve_issuer_alias(text);")
        this.Execute.Sql("CREATE SCHEMA silver;")
        this.Execute.Sql(moveTablesToSilver)
        this.Execute.Sql(restoreCompatibilityViews)

        this.Execute.Sql(
            """
            CREATE OR REPLACE FUNCTION silver.resolve_issuer_alias(input_key text)
            RETURNS TABLE (canonical_key text, canonical_name text)
            LANGUAGE sql
            STABLE
            PARALLEL SAFE
            AS $$
                SELECT a.canonical_key, a.canonical_name
                FROM silver.issuer_aliases a
                WHERE a.alias_key = input_key
                   OR (
                       length(input_key) >= 6
                       AND public.levenshtein_less_equal(input_key, a.alias_key, 4) <= 4
                       AND public.dmetaphone(input_key) = public.dmetaphone(a.alias_key)
                   )
                ORDER BY
                    CASE WHEN a.alias_key = input_key THEN 0 ELSE 1 END,
                    public.levenshtein_less_equal(input_key, a.alias_key, 4),
                    a.alias_key
                LIMIT 1
            $$;
            """
        )

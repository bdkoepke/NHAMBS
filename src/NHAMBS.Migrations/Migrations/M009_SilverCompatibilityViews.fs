namespace NHAMBS.Migrations

open FluentMigrator

/// SQLProvider is compiled against the historical `nhambs` object names. Keep
/// read-only compatibility views over the silver tables so typed LINQ queries
/// continue to work while the physical tables live in `silver`.
[<Migration(202611140009L)>]
type M009_SilverCompatibilityViews() =
    inherit Migration()

    let createViews =
        """
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

    override this.Up() = this.Execute.Sql(createViews)

    override this.Down() =
        this.Execute.Sql(
            """
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
        )

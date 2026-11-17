namespace NHAMBS.Migrations

open FluentMigrator

/// The analytics views are part of the public `nhambs` surface, not a separate
/// schema. Move the gold views back into `nhambs` and drop the temporary gold
/// schema. Silver remains the conformed layer and bronze remains the landing
/// layer.
[<Migration(202611140007L)>]
type M007_MoveGoldViewsToNhambs() =
    inherit Migration()

    let dropCompatibilityViews =
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

    let moveViewsToNhambs =
        """
        ALTER VIEW gold.v_cht_history_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_cht_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_covered_bond_registry_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_covered_bond_program_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_maturity_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_unresolved_entities SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_insurer_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_pool_type_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_rate_type_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_province_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_issuer_summary SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_monthly SET SCHEMA nhambs;
        ALTER VIEW gold.v_mbs_pool_grid SET SCHEMA nhambs;

        DROP SCHEMA gold;
        """

    let moveViewsToGold =
        """
        CREATE SCHEMA IF NOT EXISTS gold;
        ALTER VIEW nhambs.v_cht_history_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_cht_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_covered_bond_registry_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_covered_bond_program_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_maturity_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_unresolved_entities SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_insurer_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_pool_type_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_rate_type_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_province_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_issuer_summary SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_monthly SET SCHEMA gold;
        ALTER VIEW nhambs.v_mbs_pool_grid SET SCHEMA gold;
        """

    let restoreCompatibilityViews =
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
        """

    override this.Up() =
        this.Execute.Sql(dropCompatibilityViews)
        this.Execute.Sql(moveViewsToNhambs)

    override this.Down() =
        // The `nhambs.v_*` views are the moved analytics views at this point,
        // not compatibility views over gold, so move them back directly.
        this.Execute.Sql(moveViewsToGold)
        this.Execute.Sql(restoreCompatibilityViews)

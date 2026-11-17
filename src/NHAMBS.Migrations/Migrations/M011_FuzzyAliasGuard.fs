namespace NHAMBS.Migrations

open FluentMigrator

/// Levenshtein alone can merge distinct short issuer names (for example
/// "MCAN Mortgage" and "MCAP Mortgage", or "CMLS Financial" and "ATB
/// Financial"). Require the built-in double-metaphone code to agree as well;
/// exact aliases are still matched first.
[<Migration(202611140011L)>]
type M011_FuzzyAliasGuard() =
    inherit Migration()

    override this.Up() =
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
                       AND levenshtein_less_equal(input_key, a.alias_key, 4) <= 4
                       AND dmetaphone(input_key) = dmetaphone(a.alias_key)
                   )
                ORDER BY
                    CASE WHEN a.alias_key = input_key THEN 0 ELSE 1 END,
                    levenshtein_less_equal(input_key, a.alias_key, 4),
                    a.alias_key
                LIMIT 1
            $$;
            """
        )

    override this.Down() =
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
                       AND levenshtein_less_equal(input_key, a.alias_key, 4) <= 4
                   )
                ORDER BY
                    CASE WHEN a.alias_key = input_key THEN 0 ELSE 1 END,
                    levenshtein_less_equal(input_key, a.alias_key, 4),
                    a.alias_key
                LIMIT 1
            $$;
            """
        )

namespace NHAMBS.Migrations

open FluentMigrator

/// Use PostgreSQL's built-in fuzzy string functions instead of carrying an
/// edit-distance implementation in F#. `fuzzystrmatch` provides
/// levenshtein/levenshtein_less_equal; the alias resolver prefers an exact
/// alias key and only then falls back to the nearest known alias.
[<Migration(202611140010L)>]
type M010_PostgresFuzzyAliases() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
            CREATE EXTENSION IF NOT EXISTS fuzzystrmatch;

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

    override this.Down() =
        this.Execute.Sql(
            """
            DROP FUNCTION IF EXISTS silver.resolve_issuer_alias(text);
            DROP EXTENSION IF EXISTS fuzzystrmatch;
            """
        )

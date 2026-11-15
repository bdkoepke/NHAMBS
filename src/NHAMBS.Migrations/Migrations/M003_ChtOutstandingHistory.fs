namespace NHAMBS.Migrations

open FluentMigrator

[<Migration(202611140003L)>]
type M003_ChtOutstandingHistory() =
    inherit Migration()

    override this.Up() =
        this.Create
            .Table("cht_outstanding_history")
            .InSchema("nhambs")
            .WithColumn("id")
            .AsInt32()
            .PrimaryKey()
            .Identity()
            .WithColumn("series")
            .AsString(32)
            .NotNullable()
            .WithColumn("title")
            .AsCustom("text")
            .Nullable()
            .WithColumn("as_of_date")
            .AsDate()
            .Nullable()
            .WithColumn("coupon_display")
            .AsCustom("text")
            .Nullable()
            .WithColumn("coupon_kind")
            .AsString(24)
            .Nullable()
            .WithColumn("coupon_fixed_rate_percent")
            .AsDecimal(10, 5)
            .Nullable()
            .WithColumn("coupon_index")
            .AsString(128)
            .Nullable()
            .WithColumn("coupon_spread_percent")
            .AsDecimal(10, 5)
            .Nullable()
            .WithColumn("maturity_date")
            .AsDate()
            .Nullable()
            .WithColumn("identifier")
            .AsString(32)
            .Nullable()
            .WithColumn("source_url")
            .AsCustom("text")
            .Nullable()
            .WithColumn("raw")
            .AsCustom("jsonb")
            .Nullable()

        this.Create
            .UniqueConstraint("uq_cht_outstanding_history_series")
            .OnTable("cht_outstanding_history")
            .WithSchema("nhambs")
            .Columns("series")

        this.Create
            .Index("ix_cht_outstanding_history_maturity")
            .OnTable("cht_outstanding_history")
            .InSchema("nhambs")
            .OnColumn("maturity_date")
            .Ascending()

        this.Create
            .Index("ix_cht_outstanding_history_identifier")
            .OnTable("cht_outstanding_history")
            .InSchema("nhambs")
            .OnColumn("identifier")
            .Ascending()

        this.Create
            .Table("cht_outstanding_history_cancellations")
            .InSchema("nhambs")
            .WithColumn("id")
            .AsInt64()
            .PrimaryKey()
            .Identity()
            .WithColumn("history_id")
            .AsInt32()
            .NotNullable()
            .WithColumn("sequence")
            .AsInt32()
            .NotNullable()
            .WithColumn("from_date")
            .AsDate()
            .Nullable()
            .WithColumn("to_date")
            .AsDate()
            .Nullable()
            .WithColumn("cancelled_amount")
            .AsDecimal(18, 4)
            .Nullable()

        this.Create
            .Index("ix_cht_history_cancellations_history")
            .OnTable("cht_outstanding_history_cancellations")
            .InSchema("nhambs")
            .OnColumn("history_id")
            .Ascending()

        this.Execute.Sql(
            """
            ALTER TABLE nhambs.cht_outstanding_history_cancellations
                ADD CONSTRAINT fk_cht_history_cancellations_history
                FOREIGN KEY (history_id)
                REFERENCES nhambs.cht_outstanding_history (id)
                ON DELETE CASCADE;
            """
        )

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

    override this.Down() =
        this.Execute.Sql("DROP VIEW IF EXISTS nhambs.v_cht_history_summary;")
        this.Delete.Table("cht_outstanding_history_cancellations").InSchema("nhambs")
        this.Delete.Table("cht_outstanding_history").InSchema("nhambs")

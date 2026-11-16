namespace NHAMBS.Migrations

open FluentMigrator

[<Migration(202611140004L)>]
type M004_DataAnomalies() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
            CREATE TABLE IF NOT EXISTS nhambs.etl_anomalies (
                anomaly_id       bigserial PRIMARY KEY,
                run_id           bigint NOT NULL REFERENCES nhambs.etl_runs (run_id) ON DELETE CASCADE,
                dataset          varchar(32) NOT NULL,
                record_key       varchar(64) NOT NULL,
                field            varchar(64) NOT NULL,
                raw_value        text NULL,
                corrected_value  text NULL,
                severity         varchar(16) NOT NULL,
                message          text NOT NULL,
                detected_at      timestamptz NOT NULL DEFAULT now()
            );

            CREATE INDEX IF NOT EXISTS ix_etl_anomalies_run
                ON nhambs.etl_anomalies (run_id);

            CREATE INDEX IF NOT EXISTS ix_etl_anomalies_record
                ON nhambs.etl_anomalies (dataset, record_key);
            """
        )

    override this.Down() =
        this.Execute.Sql("DROP TABLE IF EXISTS nhambs.etl_anomalies;")

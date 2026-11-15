# NHAMBS.ElsaServer

Elsa Server host for the NHAMBS ETL. It exposes the workflows from
`NHAMBS.Workflows` through Elsa's REST API and hosts the Elsa Studio Blazor
WebAssembly app from `NHAMBS.Studio`.

## Run

```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run
```

- Elsa Studio: `http://localhost:8090`
- Elsa API: `http://localhost:8090/elsa/api`
- Health: `http://localhost:8090/health`

The server applies Elsa's EF Core migrations to the `elsa` schema on startup.
Workflow definitions, runtime instances, triggers, users and roles are persisted
in the same PostgreSQL database as the dashboard.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `NHAMBS_CONNECTION` | local PostgreSQL connection | Dashboard and Elsa database. |
| `NHAMBS_ELSA_SCHEMA` | `elsa` | PostgreSQL schema for Elsa tables. |
| `NHAMBS_ELSA_ADMIN_USER` | `admin` in Development | Bootstrap administrator. |
| `NHAMBS_ELSA_ADMIN_PASSWORD` | `nhambs-elsa-admin` in Development | Bootstrap password. |
| `NHAMBS_ELSA_SIGNING_KEY` | local development key | Identity token signing key. |
| `NHAMBS_DATA_ROOT` | `<repo>/out` | Parsed JSON extracts used by workflows. |
| `NHAMBS_POOLS_DIR` | `<NHAMBS_DATA_ROOT>/pools` | NHA MBS pool JSON directory. |
| `NHAMBS_MANIFEST` | `<repo>/src/NHAMBS.Mbs/pool_urls.txt` | Source URL manifest. |

The administrator and signing-key defaults are Development-only. Configure all
three explicitly in other environments.

## Studio backend URL

`NHAMBS.Studio/wwwroot/appsettings.json` points Studio at
`http://localhost:8090/elsa/api`. Update it if the server runs on another origin.

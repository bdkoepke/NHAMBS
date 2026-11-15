# NHAMBS.Studio

Elsa Studio Blazor WebAssembly host. It is served by `NHAMBS.ElsaServer` and
provides the visual workflow designer, workflow definitions, runtime instances
and the Elsa dashboard.

## Configuration

`wwwroot/appsettings.json`:

```json
{
  "Backend": {
    "Url": "http://localhost:8090/elsa/api"
  },
  "Authentication": {
    "Provider": "ElsaIdentity"
  }
}
```

The backend URL must point at a running `NHAMBS.ElsaServer`. Sign in with the
Elsa administrator configured by `NHAMBS_ELSA_ADMIN_USER` and
`NHAMBS_ELSA_ADMIN_PASSWORD`.

## Build

`NHAMBS.ElsaServer` references this project, so building the server also builds
Studio. Running the Studio project directly starts the standalone Blazor
development server:

```bash
dotnet run
```

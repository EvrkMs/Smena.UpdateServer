# Smena Update Server

Minimal standalone server for client updates.

## Endpoints

- `GET /healthz`
- `GET /manifest.json`
- `GET /updater.plan.json`
- `GET /updates/client/*` (static files, including packages)

## Configuration

`Smena.UpdateServer/appsettings.json`:

```json
{
  "UpdateServer": {
    "UpdatesPath": "updates/client",
    "PublishedClientPath": "updates/published-client",
    "ManifestFileName": "manifest.json",
    "UpdaterPlanFileName": "updater.plan.json",
    "EntryExe": "Smena.Client.exe",
    "ProcessName": "Smena.Client"
  }
}
```

Override with env vars:

- `UpdateServer__UpdatesPath`
- `UpdateServer__PublishedClientPath`
- `UpdateServer__ManifestFileName`
- `UpdateServer__UpdaterPlanFileName`
- `UpdateServer__EntryExe`
- `UpdateServer__ProcessName`

## Docker

From `Smena.UpdateServer`:

```powershell
git submodule update --init --recursive
docker compose up -d --build
```

Compose runs without bind mounts.
During image build:

1. `Smena.Client` is published into `updates/published-client`.
2. `Smena.UpdateServer` is published as the runtime service.

## Publish flow

1. Update `Smena.Client` submodule commit if needed.
2. Rebuild/restart update-server container (`docker compose up -d --build`).
3. On startup server rebuilds:
   - `updates/client/packages/client-<version>.zip`
   - `updates/client/manifest.json`
   - `updates/client/updater.plan.json`

## Generated Files

`manifest.json` now includes:

- `entryExe`
- `processName`
- `updaterPlanUrl`

`updater.plan.json` is generated from `UpdateServer:UpdaterEnv` and app options (`entryExe`, `processName`, app-dir policy).

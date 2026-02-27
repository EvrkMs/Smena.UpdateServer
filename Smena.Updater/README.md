# Smena Updater

Standalone updater executable for `Smena.Client`.

## Usage

```powershell
Smena.Updater.exe update --server-url https://smena.ava-kk.ru:5100 --app-dir "C:\Apps\SmenaClient" --grpc-address https://smena.ava-kk.ru:5001
Smena.Updater.exe reconfig
Smena.Updater.exe uninstall --app-dir "C:\Apps\SmenaClient"
```

Optional args:

- `--entry-exe Smena.Client.exe`
- `--api-key <key>`
- `--grpc-address <url>`
- `--no-launch` (apply update but do not start client)
- `--yes` (for uninstall without confirmation dialog)

Modes:

1. `update` - default update flow (manifest, package download, apply, launch).
2. `reconfig` - update only saved `AVA_SMENA_API_KEY` and `AVA_SMENA_GRPC_ADDRESS`.
3. `uninstall` - stop client, remove app directory, clear saved env vars.

`update` mode can work without `--app-dir`: updater reads `updater.plan.json` and resolves app directory by plan policy (default `relativeToUpdater/client`).

You can also use executable aliases instead of explicit mode:

- `Smena.Reconfig.exe`
- `Smena.Uninstall.exe`

Updater now runs as a small WinForms app with visible stages:

1. Try server connection (`/healthz`)
2. Read remote `manifest.json`
3. Compare with local `update.local.json`
4. If needed, ask permission to close running client
5. Download package zip and validate SHA256
6. Replace files in app directory
7. Write new `update.local.json`
8. Start client (unless `--no-launch`)

If `AVA_SMENA_API_KEY` is missing, updater asks for it once and saves it to user environment variables.
If `AVA_SMENA_GRPC_ADDRESS` is missing, updater asks for it once and saves it to user environment variables.

When `manifest.json` includes `updaterPlanUrl`, updater downloads `updater.plan.json` and applies:

- `app.entryExe` / `app.processName`
- app directory policy (`appDirPolicy`, `appDirRelativePath`, `createAppDirIfMissing`)
- `env[]` variable definitions (prompt/required/secret/validation/defaultValue)

Manual input is required only for `required` variables that are missing and have no default value.

`manifest.json` and zip package are built by `Smena.UpdateServer` on startup.

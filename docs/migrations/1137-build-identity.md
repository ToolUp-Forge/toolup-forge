# Phase 1137 — a production-safe build identity (`/version`)

**Class:** additive. Nothing changes until a deployment opts in (GP 11).

## What changes

`/dev/version` answers "which build is this?" but lists every loaded assembly and file timestamps, so it
exists only under `ServerConfig.EnableDevEndpoints`. A production deployment therefore had no way to say
which commit it runs. `ServerApp.withBuildIdentity` mounts `GET /version`, independent of
`EnableDevEndpoints`:

```json
{ "version": "1.4.0", "commit": "9f04f29b…" }
```

- `version` is the entry assembly's informational version without its `+<metadata>` suffix; `commit` is that
  suffix (the sha Source Link stamps onto a git build). `commit` is **omitted**, never guessed, when the build
  carries none.
- `Cache-Control: no-store`. No assembly list, no path, no timestamp.
- Off by default: no deployment discloses its commit unless it asks.
- The path is registered with the SPA fallback, so an app that ships a client bundle answers `/version` from the
  server rather than with the shell.

The informational-version read is now one function (`DevDiagnosticsHandler.BuildIdentity.ofAssembly`) shared by
`/dev/version` and `/version`; `/dev/version`'s output is unchanged.

## Opting in

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withBuildIdentity   // the one line
```

The application must stamp the commit into its entry assembly, e.g. `dotnet publish -p:SourceRevisionId=$GITHUB_SHA`
(or Source Link in a git checkout), which yields `1.0.0+<sha>`.

## Deploy-step check

Poll until the running commit equals the one deployed:

```powershell
$expected = $env:GITHUB_SHA
$deadline = (Get-Date).AddMinutes(5)
do {
    try { $running = (Invoke-RestMethod "https://<host>/version").commit } catch { $running = $null }
    if ($running -and $expected.StartsWith($running)) { return }
    Start-Sleep -Seconds 5
} while ((Get-Date) -lt $deadline)
throw "deployment did not reach commit $expected"
```

(`SourceRevisionId` may be abbreviated by the build; compare by prefix as above.)

## Verification

`dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Phase 1137"` — eight cases: the shared
reader, off by default, the two-field `no-store` payload that names no assembly or path, independence from the dev
gate, and the SPA shell leaving the route alone.

## Rollback

Remove the `withBuildIdentity` line; the route stops existing. The new public members are additive.

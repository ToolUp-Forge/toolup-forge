# Consumer codemod for the v0.1 breaking renames (Phases 73 / 66 / 11.C.5)

**Status:** additive; **opt-in tooling for three migrations that already shipped**. The SDK runtime
surface is untouched. `ToolUp.Platform.Build` gains one FAKE target and one module (`Codemod`,
`SDK.Codemod.fs`); a consumer who never runs it is byte-for-byte unaffected, and a consumer who
hand-migrated already gets an empty diff and the same review list (GP 11 / GP 13).

## What changes

The three Epoch-1 rename clusters shipped as prose migration docs, and every consumer hand-applied
the same `open` / symbol edits. This target applies the ones that are **deterministic** and
**reports** the ones that are not:

```pwsh
dotnet run -- Codemod <consumer-source-dir>            # rewrite in place, list review sites, Fantomas the rewritten .fs/.fsx
dotnet run -- Codemod <consumer-source-dir> --check    # print the diff + review list, write NOTHING; exit 1 while rewrites are pending
```

Run it from the consumer's Build project (the one that calls `registerTargets`), the same way you
run `dotnet run -- Format`. `<dir>` is walked recursively for `.fs` / `.fsx` (source rules) and
`.fsproj` / `.props` / `.targets` (package-id rules); `bin`, `obj`, `node_modules`, `.git`, `.fable`,
`.vs`, `.idea` and `packages` are pruned. Files are rewritten line-locally, preserving a UTF-8 BOM
and CRLF / LF terminators, so the diff is exactly the renamed lines.

### Rewritten (deterministic, 1:1, context-free)

| Rule | Before → after | Doc |
|---|---|---|
| `73-remoting-namespace` | `Fable.Remoting.{Server,Client,Json,Giraffe,MsgPack}` → `ToolUp.Remoting.*` (opens and fully-qualified) | [Phase 73](73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md) |
| `73-elmish-open` | `open Elmish` / `Elmish.React` / `Elmish.HMR` → `open ToolUp.Elmish` / `.React` / `.HMR` | Phase 73 |
| `73-elmish-qualified` | `Elmish.{Program,Cmd,Sub,React,HMR}.X` → `ToolUp.Elmish.…` | Phase 73 |
| `66-accept-flags` | the five `Accept*InAuthenticatedMode` → `Accept*WhenAuthRequired` | [Phase 66 §3](0.X.0-platform-mode-to-surfaces.md) |
| `66-platform-mode-env` | `TOOLUP_PLATFORM_MODE` → `TOOLUP_PLATFORM_SURFACES` (and the `__…__` Vite define) | Phase 66 |
| `66-bundle-constant` | `BundleConstants.platformMode` → `.platformSurfaces` | Phase 66 |
| `66-user-session-get-mode` | `UserSession.getMode ()` → `UserSession.getSubjectKind ()` | Phase 66 §8 |
| `66-mode-field` | `Mode = Anonymous / AuthenticatedEphemeral / Individual / Team / MultiTeam` in a `ServerConfig` / `ClientConfig` record → `Surfaces = Surfaces.anonymous / trial / individual / team / multiTeam` | Phase 66 §1–2 |
| `11c5-audit-retry-policy` | `AuditReplicatorRetryPolicy` → `RetryPolicy` (same field shape) | [Phase 11.C.5 T3.2](11-C-5-public-api-stability-cluster.md) |
| `11c5-sink-kind` | `NotificationKind.SinkKind.Email` / `.Sms` → `SinkKind.Email` / `.Sms` | 11.C.5 T3.3 |
| `11c5-package-*` (five) | `ToolUp.Platform.AuditSinks.*`, `ToolUp.Platform.NotificationChannels.*`, `ToolUp.Platform.Metrics.OpenTelemetry`, `ToolUp.Storage.Azure`, `ToolUp.AuthProviders.OidcClient` → the Tier 2 ids — **project files only**: the F# namespaces inside those packages did not move | 11.C.5 Tier 2 |

### Reported for human review (never rewritten)

Each site is listed per file with its line, rule id and the migration section to read; the bytes are
left alone. A codemod that guessed here would compile and be wrong.

- **Phase 66:** `match ctx.Mode with` (§4 — the arm mapping onto `Subject` is per site), any other
  `.Mode` read (a `ServerConfig` / `ClientConfig` `.Surfaces` or an `AccessContext` `.Subject` —
  a consumer-owned `Mode` field is a false positive), a `PlatformMode` type reference (§6 parameter
  drop), `withAnonymousRoute` / `AnonymousRoutePrefixes` (§5 per-route `SurfaceRequirement`), an
  `IAuditSink` implementation (§7 `SchemaVersion` + `AuditEnvelope list`), `UserSession.configure`
  (§8 takes `SubjectKind`), `DevDefaultUserId` (deleted), `AuthEnforcementMiddleware` (retired),
  `RateLimit = Some | None` (no longer an option).
- **Phase 11.C.5:** an `IAuthProvider` implementation and any `:?> HttpContext` cast (T3.1
  `RequestContext`), `MaxRetries` / `BackoffMs` (T3.2 — `MaxAttempts` includes the first attempt),
  `NotificationKind.SinkKind.Push` or a `Kind = "…"` string (T3.3 — choose the `PushVariant`),
  `ClientConfig.create` (T3.4 — trailing `ClientHandlerRegistry`), `createWithModel` /
  `createWithBatchSize` (T3.5 — `secretStore` first on the OpenAI embedding factories).
- **Phase 73:** a `Fable.Remoting.X` the fork does not carry (AspNetCore, DotnetClient, …), an
  `Elmish.X` sub-namespace the runtime does not carry (Navigation, UrlParser, Debug, …), and
  `FableJsonConverter` (retired in the STJ migration).

The review list is the same on every run — findings are computed over the rewritten text — and
never fails the run; `--check` exits 1 only while deterministic rewrites are pending.

## Diff to apply (per consumer)

```pwsh
git status --short                                    # start clean, so the codemod's diff is reviewable on its own
dotnet run -- Codemod src --check                     # read the diff and the review list first
dotnet run -- Codemod src                             # apply; the rewritten .fs/.fsx are Fantomas-formatted
git diff                                              # the deterministic renames, format-clean
```

Then work the review list by hand against the three migration docs it cites, `dotnet build`, and
`dotnet fable -o output --noCache` for every client project. The build is the gate: an `open` the
codemod could not classify surfaces as an unresolved namespace, exactly as the Phase 73 doc
describes.

## Verification

1. `dotnet run -- Codemod src --check` exits 0 and prints `no rewrites pending` — the deterministic
   half is complete.
2. The review list is empty, or every remaining site is one you have judged and left (a
   consumer-owned `Mode` field, an upstream `Elmish.Navigation` you keep).
3. `dotnet build <consumer>.sln` clean; `dotnet fable -o output --noCache` clean per client project.
4. `dotnet fantomas --check src` clean — the codemod formatted what it rewrote, and only that.

## Rollback

`git checkout -- <the rewritten files>` (or revert the commit). The codemod holds no state and
writes nothing but the files it lists under `rewrote`; running it again after a rollback reproduces
the same diff. Reverting the SDK version is the rollback for the underlying renames, per each
migration doc.

## Adoption

Additive tooling: the row in the workspace adoption matrix records which consumers ran it. It is
the tooling *for* the Phase 66 / 73 / 11.C.5 rows, not a fourth migration — a consumer whose three
rows already read adopted has nothing to run.

## See also

- The rule tables are data: `src/ToolUp.Platform.Build/Build/SDK.Codemod.fs` (`Codemod.rewriteRules`
  / `Codemod.reviewRules`), each with its one-line account.
- The golden-file pack that decides every rule: `src/ToolUp.Platform.Build.Tests/CodemodTests.fs`
  over `fixtures/codemod/` (`before/` → `after/`, `findings.txt`, `diff.txt`).

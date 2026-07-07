# Phase 495 — Module API-factory helper (`ModuleApiFactory`)

**Ships in:** `ToolUp.Platform.Server` (`ModuleApiFactory` / `ModuleApiContext` /
`NarrativeSettings`, `Server/ModuleApiFactory.fs`). Purely additive — no existing surface changed.

## What changes

Consumer composition roots repeat the same per-module API-factory boilerplate for every module:
scope-aware `FileManagement.getFileContents` plumbing on each endpoint, hand-`sprintf`'d
`key=value|key=value` settings keys, and the 7-argument
`NarrativePublisher.publishWithProvenance` call. The helper captures that shape once:

- **`ModuleApiFactory.create "MyModule" ctx`** binds a per-request `ModuleApiContext` at the top
  of the factory.
- **`m.GetFileContents fileName`** — `getFileContents` with the context pre-applied (same
  `FileNotFoundInSessionException` contract).
- **`m.FromFile(_.FileName, fun contents r -> routine contents …)`** — collapses the
  4–5-line `fun request -> async { let contents = … ; return routine … }` endpoint wrapper to one line.
- **`NarrativeSettings.ofPairs [ "k", v ]`** / **`NarrativeSettings.create keyPairs displayPairs`**
  — the canonical settings-key convention (`"k=v|k2=v2"`), replacing per-module `sprintf` format strings.
- **`m.PublishNarrative(pageRoute, settings, doc)`** — provenance publish under the bound module
  id; subtitle key defaults to `doc.Subtitle` (explicit-subtitle overload available). Replace-latest
  semantics and the no-`INarrativeStore` no-op are unchanged (it delegates to `publishWithProvenance`).
- **`m.TryPublishNarrative(…)`** — the best-effort variant (publish failure degrades to the
  unstamped document instead of failing the endpoint) for dispatcher-anonymous data paths.

**Owning package (495.A decision):** `ToolUp.Platform.Server` — both dependencies the shape needs
(`FileManagement.getFileContents` and `NarrativePublisher` / `INarrativeStore`) already live there;
there is no separate narrative companion to own it. Extracted module packages already consume
`Platform.Server` for their AI executors, so the helper is equally available to module-side and
composition-root-side factories.

## Diff to apply (per module factory)

```diff
 let myModuleApi (ctx: HttpContext) : MyModuleApi =
+    let m = ModuleApiFactory.create "MyModule" ctx
     {
-        GetPreview =
-            fun request -> async {
-                let contents = getFileContents ctx request.FileName
-                return MyModule.Server.previewRoutine contents request.MaxRows
-            }
+        GetPreview = m.FromFile(_.FileName, fun contents r -> MyModule.Server.previewRoutine contents r.MaxRows)
         RunInsight =
             fun request -> async {
-                let contents = getFileContents ctx request.FileName
-                let result = MyModule.Server.insightRoutine contents request
+                let result = MyModule.Server.insightRoutine (m.GetFileContents request.FileName) request
                 match result.Narrative with
                 | Some doc ->
-                    let settingsKey =
-                        sprintf "channel=%s|audience=%s|refGRP=%g" request.Channel request.Audience request.RefGRP
-                    let settingsDisplay = [ "Channel", request.Channel; "Audience", request.Audience ]
-                    let! stamped =
-                        NarrativePublisher.publishWithProvenance
-                            ctx "MyModule" (Some "/insight") settingsKey settingsDisplay doc.Subtitle doc
+                    let settings =
+                        NarrativeSettings.create
+                            [ "channel", request.Channel; "audience", request.Audience; "refGRP", sprintf "%g" request.RefGRP ]
+                            [ "Channel", request.Channel; "Audience", request.Audience ]
+                    let! stamped = m.PublishNarrative(Some "/insight", settings, doc)
                     return { result with Narrative = Some stamped }
                 | None -> return result
             }
     }
```

Best-effort paths (`try … publishWithProvenance … with _ -> return doc`) collapse to
`m.TryPublishNarrative(…)` — no `try`/`with` needed.

**Settings-key caution:** `NarrativeSettings.key` joins pairs as `k=v|k=v` verbatim. Where a
module's existing `sprintf` produced a different value rendering (e.g. `%g`, `%b`, `%.2f`),
pre-format the value string in the pair (as `refGRP` above) so the canonical `SettingsKey` —
the KB deduplication key — stays byte-identical across the migration.

**Stale-rationale retirement:** consumer composition roots that carry the in-file justification
"the factories live in the composition root because `getFileContents` is only available to
`ToolUp.Platform.Server` consumers" (the reference consumer's `Wiring.fs:166-171`) should delete
that comment block in the same adoption PR — the extracted module packages consume
`Platform.Server` already, so the rationale no longer holds; the factory-location choice is now a
convention, not a constraint.

## Reference migration (495.C)

`src/ToolUp.Platform.Tests/InProcess/ModuleApiFactoryTests.fs` carries one consumer-shaped factory
written both ways (`demoApiBefore` / `demoApiAfter`) plus a test proving identical runtime
behaviour (results, provenance stamps, stored entries). Non-comment code lines (miniature block:
one plain + one narrative endpoint, 3 settings pairs, Fantomas-formatted both ways): **32 → 24**.
The per-shape collapses that
compound in a real multi-module composition root: plain file-backed endpoint 4–5 lines → 1
(`FromFile`); provenance publish call 8 lines → 1; settings-key `sprintf` format string →
pair list; `try`/`with` best-effort wrapper → `TryPublishNarrative`.

## Verification

1. `dotnet build` the composition root — signature-compatible by construction.
2. Regenerate one narrative per migrated module and confirm the stored `SettingsKey` matches the
   pre-migration value (KB dedup continuity).
3. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 495"`
   (SDK-side contract suite).

## Rollback

Additive-only: revert the consumer commit — the hand-rolled `getFileContents` /
`publishWithProvenance` wiring keeps working unchanged. No data migration in either direction
(the helper emits byte-identical provenance and store writes).

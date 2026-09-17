# Migration — Phase 347: shared-types monolith split

**What changed.** Two of `ToolUp.Platform.Core`'s largest compilation units were carved into
cohesive files. **Nothing moved namespace, type name or module name** — only file boundaries.

| Before | After (compile order, top to bottom) |
|---|---|
| `Shared/SDK.Shared.fs` (5,082 lines) | `Shared/ModuleEvents.fs` — `ModuleEvent`, `IEventStore`, `Events`, `EventRetentionPolicy`, `EventReplay` |
| | `Shared/Config/StoreModes.fs` — the store / scheduler / ingestion / session-registry / backup / model / telemetry / tenant `*Mode` DUs |
| | `Shared/Config/RuntimeModes.fs` — metrics / webhook / audit-log / notification / rate-limit / CORS / SSE / hardening / readiness / rendering / media / deploy-plane modes |
| | `Shared/Config/InboundRateLimitTypes.fs` — the Phase 56 inbound rate-limit types |
| | `Shared/Config/ConsentAdTypes.fs` — consent, ad-panel, premium, platform-admin profile, serverless-host, process-profile modes |
| | `Shared/SDK.Shared.fs` — **the `ServerConfig` record only** (1,519 lines; the documented irreducible core) |
| | `Shared/Config/ServerConfigOverrides.fs` — `ServerConfigOverrides` + its module |
| | `Shared/Config/DeploymentConfig.fs` — the `DeploymentConfig` predicates over `Surfaces` |
| | `Shared/Config/ServerConfigFromEnv.fs` — `module ServerConfig` (`defaults` + the server-only `fromEnv`) |
| `Shared/AuditTypes.fs` (6,146 lines) | `Shared/Audit/AuditSubject.fs` — `AuditSourceModule`, `AuditSubject` |
| | `Shared/Audit/{Platform,Knowledge,Session,Access,Governance,Peer,Tenant,Model,Provenance,Evidence}AuditPayloads.fs` — the payload records, one subsystem lane each, in their original declaration order |
| | `Shared/AuditTypes.fs` — **the `AuditEvent` union + `AuditEvent.eventTypeName` only** (998 lines; the documented irreducible core, still append-only) |
| | `Shared/Audit/AuditEnvelope.fs` — `AuditEnvelope`, `AuditSchemaVersion`, `IAuditLog`, `IAuthAuditHook`, `BlobStorageAuthAudit` |

Every carved body is byte-identical to the range it came from. The `ServerConfig` companion
module carries an explicit `[<CompilationRepresentation(ModuleSuffix)>]` so its compiled name
stays `ServerConfigModule` now that the record and the module sit in different files.

`Client/SDK.Client.fs` was **not** split — see "What did not move" below.

## Do I need to do anything?

**No.** Expected consumer diff: none.

- **Binary consumers** (anything that references the `ToolUp.Platform.Core` DLL): every type,
  module and member keeps its fully-qualified name and its compiled name. The public-API
  approval baselines are byte-identical (they are sorted by type and member, so a file move is
  invisible to them), and no `open` changes.
- **Source-in-nupkg Fable consumers** (the client tier compiles `Core`'s source from the
  nupkg's `fable/` folder through the packed `.fsproj`): the packed file list **grows by 20
  files** — the seven `Shared/Config/*.fs`, `Shared/ModuleEvents.fs` and the twelve
  `Shared/Audit/*.fs` above — and the packed `.fsproj`'s `<Compile>` order already carries
  them. The `fable/` packing glob (`**\*.fs`) picks them up with no packaging change. A
  consumer that Fable-compiles the SDK sees the same set of declarations in the same order; it
  just arrives in more, smaller compilation units (which is the point — each file is now a
  smaller unit on the consumer's critical path).
- **Tooling that greps the source by file name** — an in-repo test that read `SDK.Shared.fs`
  for the `fromEnv` region now reads `Shared/Config/ServerConfigFromEnv.fs`. If you carry an
  out-of-tree script pinned to either old path, repoint it using the table above.

## Where new declarations go

- A new `*Mode` DU or validation-adjacent record: the `Shared/Config/` file that owns its
  subsystem (store-shaped in `StoreModes.fs`, runtime-shaped in `RuntimeModes.fs`); it must
  sit before `SDK.Shared.fs` if `ServerConfig` carries it.
- A new `ServerConfig` field: `SDK.Shared.fs` (the record), its default in
  `Config/ServerConfigFromEnv.fs`.
- A new audit payload record: the `Shared/Audit/*AuditPayloads.fs` lane that owns its
  subsystem; the union case goes at the **end** of `AuditEvent` in `AuditTypes.fs` and the
  `eventTypeName` arm beside it, exactly as before. An in-flight branch that appended a payload
  record immediately above `type AuditEvent` still rebases cleanly — that context is unchanged.

## What did not move

`Client/SDK.Client.fs` (4,827 lines) is one F# module, `Client`, whose 25 public members —
`Model`, `Msg`, `init`, `update`, `view`, `prepareModules`, `boot`, `program`, `run`, … —
are the shell's public surface. F# has no partial modules, so any member that leaves the file
leaves `Client.*` and changes the public API, which this phase forbids; and the 57 private
helpers cannot leave either, because all but ~300 lines of them read `Model` / `Msg`, which
are declared in the same module. The units the phase named as separable ("update branches
already delegated", the sidebar and palette rules, boot degradation) were already delegated
to sibling files before this phase; what remains is the irreducible MVU core. A split of the
shell is therefore a deliberate public-surface change for a later, versioned phase — not a
file move.

## Verification

- `dotnet build ToolUp.Forge.sln` and `VerifyAll` green on the folded tree (the phase's gate).
- `api-baselines/ToolUp.Platform.Core.approved.txt` unchanged.
- Each carved file's body diffed byte-for-byte against its source range.

## Rollback

Revert the two `refactor(core): Phase 347 — carve …` commits. They touch only
`src/ToolUp.Platform.Core/Shared/**`, `ToolUp.Platform.Core.fsproj` and one test's file path;
no consumer needs to change in either direction.

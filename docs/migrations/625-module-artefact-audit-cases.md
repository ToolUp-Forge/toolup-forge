# Migration — Phase 625: `ModuleArtefact*` audit cases

**Status:** breaking **source** change; **zero** wire change. A consumer
that never pattern-matches the Phase 30a module-artefact audit events
has nothing to do. A consumer that does gets a compile error naming
every site.

## What changes

`AuditEvent` carried a one-letter homograph pair — the Phase 30a
module-distribution family (`ArtifactSigned` / `ArtifactVerified` /
`ArtifactRejected`, `SourceModule = "_platform.artefacts"`) and the
unrelated Phase 40 detached-JWS event (`ArtefactSigned`,
`SourceModule = "_platform.signing"`). Two different security events,
one vowel apart, both public API.

The Phase 30a family is renamed to carry a `Module` qualifier and the
estate's `artefact` house spelling. The Phase 40 case is **unchanged**.

| Before | After |
|---|---|
| `AuditEvent.ArtifactSigned` | `AuditEvent.ModuleArtefactSigned` |
| `AuditEvent.ArtifactVerified` | `AuditEvent.ModuleArtefactVerified` |
| `AuditEvent.ArtifactRejected` | `AuditEvent.ModuleArtefactRejected` |
| `ArtifactSignedPayload` | `ModuleArtefactSignedPayload` |
| `ArtifactVerifiedPayload` | `ModuleArtefactVerifiedPayload` |
| `ArtifactRejectedPayload` | `ModuleArtefactRejectedPayload` |
| `AuditEvent.ArtefactSigned` (Phase 40) | *unchanged* |
| `ArtefactSignedPayload` (Phase 40) | *unchanged* |

## What does NOT change — read this before touching any query

**The wire is untouched.** `AuditEvent.eventTypeName` still emits the
historical discriminators, and the codec registry is still keyed by
them:

| Case | Emitted `EventType` |
|---|---|
| `ModuleArtefactSigned` | `"ArtifactSigned"` |
| `ModuleArtefactVerified` | `"ArtifactVerified"` |
| `ModuleArtefactRejected` | `"ArtifactRejected"` |

So **none** of the following need editing, and editing them would break
things:

- Archived events in the platform event store — still decode.
- Records already replicated to S3 / GCS / Azure Blob archives, Splunk
  HEC, Datadog Logs, or a CEF collector — the `EventType` /
  `eventTypeName` / `cat` field is unchanged.
- Operator-owned SIEM saved searches, dashboards, correlation rules and
  retention policies keyed on `ArtifactSigned` / `ArtifactVerified` /
  `ArtifactRejected`.
- Alert thresholds on the `toolup.audit.write_failures_total` metric's
  `event_type` tag.

Record **field** names (`Actor`, `PublisherKeyId`, `ModuleId`,
`ArtifactVersion`, `Reason`) are serialised and are likewise unchanged.

The full reasoning — including why the emitted strings were deliberately
NOT moved to match the new case names — is recorded at the registry in
`src/ToolUp.Platform.Server/Server/AuditLog.fs`, above
`auditEventCodecs`.

## Diff to apply

Pattern matches — rename the case:

```fsharp
// before
match evt with
| AuditEvent.ArtifactRejected p -> alertOnRefusal p.Reason
| AuditEvent.ArtefactSigned p -> recordJwsSignature p.KeyId
| _ -> ()

// after
match evt with
| AuditEvent.ModuleArtefactRejected p -> alertOnRefusal p.Reason
| AuditEvent.ArtefactSigned p -> recordJwsSignature p.KeyId   // Phase 40 — unchanged
| _ -> ()
```

Payload construction / type annotations — rename the type:

```fsharp
// before
let payload: ArtifactSignedPayload = { Actor = actor; ... }

// after
let payload: ModuleArtefactSignedPayload = { Actor = actor; ... }
```

Field names inside the record are unchanged, so only the type name and
the case name move.

A safe mechanical sweep, since the new names share no prefix with the
subsystem identifiers (`IArtifactSigner`, `ArtifactManifest`,
`ArtifactValidation`, `ArtifactVersion`) which must NOT be renamed:

```
ArtifactSigned   -> ModuleArtefactSigned
ArtifactVerified -> ModuleArtefactVerified
ArtifactRejected -> ModuleArtefactRejected
```

(The trailing `d` is what keeps `ArtifactSigner` / `ArtifactVerifier`
out of the match.) **Do not apply it to quoted strings** — a string
literal `"ArtifactRejected"` is a wire discriminator and must stay.

## Verification

1. `dotnet build` — every remaining call site is a compile error; there
   is no silent-drift path.
2. Confirm no string literal moved:
   `grep -rn '"Artifact\(Signed\|Verified\|Rejected\)"' src` should show
   only wire-discriminator sites (the codec registry, `eventTypeName`,
   and any severity/classification set such as `CefFormatter`'s
   `highEvents`).
3. If you replicate audit events, spot-check one archived record after
   upgrading: its `EventType` must still read `ArtifactSigned` etc.

## Rollback

Revert the rename in your own source. No data migration is involved in
either direction, because no persisted or replicated value changed.

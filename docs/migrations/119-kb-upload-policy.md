# Phase 119 — Knowledge Base upload policy (`withUploadPolicy`) + filename sanitisation (consumer migration)

**What changes.** The KB upload boundary gains an opt-in policy (size cap + extension allowlist + unsupported-type handling) and always-on filename sanitisation. Pre-119, `uploadDocument` accepted arbitrary `bytes` + `fileName` with no KB-level size cap or type allowlist, composed the client-supplied filename unsanitised into the blob key (`../../index.json` could normalise to the container-root `knowledge/index.json`), and stored an unrecognised type as `Complete 0` — successfully ingested but permanently unsearchable, indistinguishable from a real empty file.

**Scope.** `ToolUp.KnowledgeBase.Core` (`IngestionStatus` gains `UploadRejected` / `UnsupportedFormat`; new `KnowledgeUploadPolicy` + `UnsupportedUploadHandling`), `ToolUp.KnowledgeBase.Server` (boundary enforcement, `withUploadPolicy`, preflight validator), `ToolUp.KnowledgeBase.Client` (status badges). No wire-shape change beyond the additive `IngestionStatus` cases.

**Backward compatibility.**

- **Stock consumers:** nothing to do. A deployment that never composes `withUploadPolicy` gets `KnowledgeUploadPolicy.permissive` — no size cap, no allowlist, unrecognised types stored-but-unsearchable. Behaviour is byte-identical to pre-119 **except** (a) filename sanitisation is now always applied (a name that survives `Path.GetFileName` + `FileNameSanitiser.validate` is unchanged; a traversal/control-char name is sanitised or rejected), and (b) an unrecognised type now lands `UnsupportedFormat` instead of `Complete 0`.
- **`IngestionStatus` consumers** (custom KB clients, anything pattern-matching the status DU): two new cases — `UploadRejected of reason` and `UnsupportedFormat of detail`. Both are terminal. The in-tree KB client handles them (badge + poll-loop termination); external matchers gain two arms (or a `| _`).
- **`KnowledgeApiDeps` construction sites** (test fixtures): the record gains `UploadPolicy: KnowledgeUploadPolicy` — add `UploadPolicy = KnowledgeUploadPolicy.permissive`.
- **`UploadDocument` contract** (`byte[] -> string -> Async<KnowledgeDocument>`) is unchanged. A rejected upload returns a non-persisted `KnowledgeDocument` carrying `Status = UploadRejected reason`; it never appears in `GetDocuments`.

## Diff to apply

Stock consumers: none. To enforce a policy:

```fsharp
app
|> KnowledgeBase.Server.withUploadPolicy {
    KnowledgeUploadPolicy.permissive with
        MaxUploadBytes = Some (25L * 1024L * 1024L)
        AllowedExtensions = Some (Set.ofList [ "pdf"; "docx"; "csv" ])
        OnUnsupportedType = Reject
}
```

**Serving originals:** any endpoint serving `OriginalDocument.Content` must set `Content-Disposition: attachment` and pin `Content-Type` from `OriginalDocument.ContentType` (csv/md/html/svg served inline is a stored-XSS vector). See `docs/knowledge-base/concepts.md` → "Serving originals safely".

## Verification

1. `dotnet build ToolUp.Forge.sln` — green.
2. Upload `../../index.json` → stored under `knowledge/{docId}/index.json`; the KB index is intact.
3. With `MaxUploadBytes = Some N`, an `N+1`-byte upload returns `UploadRejected` and writes nothing.
4. Upload an unrecognised type with the default policy → `UnsupportedFormat`, not `Complete 0`.
5. Compose KB in `Team` / `MultiTeam` mode with no `MaxUploadBytes` → startup emits the upload-policy `Warning` (non-fatal); a cap or `AcceptUnboundedUploads = true` silences it.

## Rollback

Revert the Phase 119 commit. No data migration — `UnsupportedFormat` / `UploadRejected` are forward-only status values that a reverted server simply never writes; any already-persisted `UnsupportedFormat` index entry deserialises on an older server only if its `IngestionStatus` matcher tolerates the case (the in-tree client does via `| _`).

namespace ToolUp.Platform

/// Scope-indexed persistent store for feature flags. One JSON document
/// per `FlagScope`; each document is a `Map<string, FlagValue>` keyed
/// by `FeatureFlag.Key`.
///
/// Mirrors `IConfigStore`'s shape deliberately — `GetFlag` / `ListFlags`
/// / `SetFlag` / `ClearFlag` — so the admin-UI machinery built for
/// config carries over with minimal surface difference.
///
/// Lives in the server layer because implementations depend on
/// server-only infrastructure (`IBlobStorage`, STJ + the shared
/// `FableConverters`). A Fable.Remoting admin API exposed to the
/// client never holds this interface — handlers resolve the caller's
/// `AccessContext`, translate to `FlagScope`, and call through.
///
/// Scope isolation is the caller's responsibility: handlers must
/// resolve the authenticated caller's scope, gate writes (team flags:
/// Owner/Admin only; platform flags: deployment-level admin), and hand
/// the resolved `FlagScope` to this store. A misrouted `FlagScope`
/// would read or write another team's document — the same trust
/// boundary as every other blob-backed store (`PermissionStore`,
/// `BlobConfigStore`, `BlobProviderProfile`, `PersistentEventStore`).
type IFeatureFlagStore =
    /// Read one flag at one scope. `None` when the scope has no entry
    /// for this key — callers fall through to the next layer
    /// (`FlagEvaluator`) or to the declared default.
    abstract GetFlag: scope: FlagScope * key: string -> Async<FlagValue option>

    /// Read every flag set at one scope. Empty map when no document
    /// exists. Used by the admin UI to pre-populate the per-scope form
    /// and by the shell's client-side prefetch at boot.
    abstract ListFlags: scope: FlagScope -> Async<Map<string, FlagValue>>

    /// Upsert one flag at one scope. Persists immediately. Validation
    /// (variant choice is in the declared options; value type matches
    /// the declared default) is the handler's responsibility — this
    /// interface trusts its caller. Surface the underlying
    /// `IBlobStorage.Upload` error on persistence failure.
    abstract SetFlag: scope: FlagScope * key: string * value: FlagValue -> Async<Result<unit, string>>

    /// Remove one flag's entry at one scope — subsequent reads fall
    /// through to the next layer. Idempotent: clearing a missing key
    /// succeeds without I/O.
    abstract ClearFlag: scope: FlagScope * key: string -> Async<unit>

    /// Phase 9h — GDPR Article 17 erasure surface. Erase (or redact)
    /// flag entries in the `scopeId` document whose `FlagValue` names
    /// `subjectUserId`, per `policy`:
    ///
    ///  - `HardDelete` — drop the matching flag entries (subsequent
    ///    reads fall through to the next layer). If the document
    ///    empties, its blob is removed.
    ///  - `Tombstone` / `RetainPerCompliance` — keep the flag key but
    ///    replace its value with `Variant([], Erasure.TombstoneMarker)`.
    ///    Flags are operational config, not the audit fabric — neither
    ///    policy refuses; both redact.
    ///
    /// **Subject match.** Only `Variant(options, value)` flags can
    /// name a subject (a `Bool` cannot). A flag matches when its
    /// `value` or any `option` contains `subjectUserId`. Blank
    /// subject is a zero-count no-op.
    ///
    /// **Scope isolation (GP 4).** `scopeId` is the `FlagScope.slug`
    /// of the request scope; only `flags/{scopeId}.json` is read or
    /// mutated. A subject's own per-user flag document is erased when
    /// the DSR runs with that user scope — never reached from another
    /// scope's request.
    ///
    /// `dryRun = true` counts affected flags without mutating.
    /// Portability audit (GP 12): identity by value, async at
    /// boundary, failure as `ErasureError`, stateless, single-scope,
    /// precision declared above.
    abstract Erase:
        scopeId: string * subjectUserId: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>
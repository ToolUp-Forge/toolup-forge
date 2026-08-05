namespace ToolUp.Platform

/// Scope-indexed persistent store for module-visibility profiles
/// (Phase 637). One JSON document per `FlagScope`, each holding a single
/// `ModuleVisibilityProfile`.
///
/// Mirrors `IFeatureFlagStore`'s shape deliberately — get / set / clear
/// keyed by `FlagScope` — because the two are resolved along the same
/// scope walk and an operator reasoning about one should not have to
/// learn a second vocabulary for the other. The difference is arity: a
/// flag document is a MAP of keys, a visibility document is one profile,
/// so there is no `List` method and no per-key clear.
///
/// Lives in the server layer because implementations depend on
/// server-only infrastructure (`IBlobStorage`, STJ + the shared
/// `FableConverters`). A Fable.Remoting admin API exposed to the client
/// never holds this interface — handlers resolve the caller's
/// `AccessContext`, translate to `FlagScope`, and call through.
///
/// Scope isolation is the caller's responsibility (GP 4): handlers must
/// resolve the authenticated caller's scope, gate writes (team profiles:
/// Owner/Admin only), and hand the resolved `FlagScope` here. A
/// misrouted `FlagScope` would read or write another team's document —
/// the same trust boundary every other blob-backed store carries.
///
/// Portability audit (GP 12): identity by value (`FlagScope` /
/// `ModuleVisibilityProfile` are plain data, never a handle); async at
/// every boundary; failure as data (`Result<unit, string>` on the write
/// path); stateless between calls; single-scope, so no cross-shard
/// ordering is promised.
type IModuleVisibilityStore =
    /// Read the profile declared at one scope. `None` when that scope
    /// declares none — the caller falls through to the next layer of the
    /// walk (see `ModuleVisibilityResolver`).
    abstract GetProfile: scope: FlagScope -> Async<ModuleVisibilityProfile option>

    /// Upsert the profile at one scope. Persists immediately. The
    /// profile's own `Scope` is authoritative for the document path only
    /// insofar as the CALLER passes them consistently; implementations
    /// key off the `scope` parameter, so a mismatched
    /// `profile.Scope` can never redirect the write. Validation (ids
    /// naming registered modules, write authority) is the handler's
    /// responsibility — this interface trusts its caller.
    abstract SetProfile: scope: FlagScope * profile: ModuleVisibilityProfile -> Async<Result<unit, string>>

    /// Remove the profile at one scope — that layer stops contributing
    /// to the walk. Idempotent: clearing a scope with no profile
    /// succeeds without I/O.
    abstract ClearProfile: scope: FlagScope -> Async<unit>
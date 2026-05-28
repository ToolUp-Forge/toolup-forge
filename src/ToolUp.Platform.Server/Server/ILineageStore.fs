namespace ToolUp.Platform

// ─── ILineageStore ───────────────────────────────────────────────
//
// Server-side query surface for lineage links (Phase 8a). No
// dedicated persistence — `Record` writes a `ModuleEvent` to
// `IEventStore` with `EventType = LineageEventType.Link` and
// `SourceModule = "_platform.lineage"`; queries fan out from
// `ReadByType` and walk the resulting edge list in memory.
//
// **Optional registration.** `compose` only registers
// `ILineageStore` when `ServerConfig.Lineage = EnabledLineageStore`.
// `PersistentResultStore` checks DI for `ILineageStore` at
// construction time and either auto-emits links on `SaveResult`
// (when present) or silently ignores the `inputs` parameter (when
// absent).
//
// **Phase 9c portability.** Identity by value (`Guid` link ids,
// string object ids). Async at every method. No callbacks.
// Stateless — graphs are computed per-call from `IEventStore`.
// Within-scope ordering: query results are bounded by `scopeId` at
// the underlying read path; cross-scope leakage is structurally
// impossible. No timing primitives.

/// Reserved `SourceModule` for lineage events written by
/// `ILineageStore.Record`. Distinguishes platform-emitted links
/// from any module-emitted events that happen to use the same
/// `EventType`.
module LineageSourceModule =
    [<Literal>]
    let Value = "_platform.lineage"

/// Server-side interface for recording and querying lineage links.
/// Registered in DI only when
/// `ServerConfig.Lineage = EnabledLineageStore`.
type ILineageStore =
    /// Persist a single `LineageLink` as a `ModuleEvent` in the
    /// underlying `IEventStore`. Best-effort: returns `Error` when
    /// the event-store write fails, but callers (typically
    /// `PersistentResultStore`) treat the error as non-fatal so a
    /// failed lineage write doesn't roll back a successful
    /// `SaveResult`.
    abstract Record: scopeId: string * link: LineageLink -> Async<Result<unit, string>>

    /// Walk lineage backwards from `objectId`. Returns a graph
    /// whose root is `objectId`, whose edges connect each ancestor
    /// to its consumer, and whose nodes carry the producer
    /// `ModuleName` for objects with at least one outgoing edge.
    /// Empty graph (zero nodes, zero edges) when `objectId` has no
    /// recorded lineage.
    abstract GetAncestors: scopeId: string * objectId: string -> Async<LineageGraph>

    /// Walk lineage forwards from `objectId`. Returns the graph of
    /// every object derived from `objectId` (and their descendants
    /// transitively). Symmetric counterpart to `GetAncestors`.
    abstract GetDescendants: scopeId: string * objectId: string -> Async<LineageGraph>

    /// Find any path of links from `fromId` to `toId`. Returns
    /// `None` when no path exists; `Some links` returns the
    /// shortest path found (the in-memory traversal does BFS, so
    /// "shortest" is by edge count, not by `Timestamp` order).
    abstract GetPath: scopeId: string * fromId: string * toId: string -> Async<LineageLink list option>

    /// Phase 9h — GDPR Article 17 erasure surface for lineage links.
    ///
    /// **Architecture.** `ILineageStore` has no independent
    /// persistence — every `LineageLink` is a `ModuleEvent`
    /// (`SourceModule = "_platform.lineage"`, `EventType =
    /// LineageEventType.Link`). Byte-level erasure of those events is
    /// therefore owned by the *event-store* erasure handler under the
    /// same `policy` (a lineage link IS an event). This surface is the
    /// lineage-scoped, structurally-aware participant: it reports how
    /// many lineage links in `scopeId` name the subject and refuses
    /// under `RetainPerCompliance`, but does NOT itself byte-mutate
    /// the shared event store — doing so would double-mutate (the
    /// event-store handler already covers lineage-typed events) and
    /// would over-reach (the lineage handler must not touch non-
    /// lineage events). The lineage-graph consequence of the chosen
    /// policy:
    ///
    ///  - `HardDelete` — the link events are removed by the event
    ///    store; the corresponding edges disappear from
    ///    `GetAncestors` / `GetDescendants`.
    ///  - `Tombstone` — the link events keep their envelope (LinkId
    ///    via `Id`, Timestamp via `OccurredAt`) but the serialised
    ///    `LineageLink` payload is replaced with the marker, so the
    ///    edge drops out of graph traversal (payload no longer
    ///    deserialises). Field-level "edge survives, participant ids
    ///    tombstoned" structural redaction needs an event-payload
    ///    rewrite primitive and is a tracked follow-up.
    ///  - `RetainPerCompliance` — refuse: lineage is part of the
    ///    provenance/audit fabric.
    ///
    /// **Subject match.** The serialised `LineageLink` carries no
    /// user id; "names the subject" is a substring match of
    /// `subjectUserId` within the link payload (typically its
    /// `FromObjectId` / `ToObjectId` / `ModuleName`). Blank subject
    /// is a zero-count no-op. Scope-isolated (GP 4) via the
    /// per-scope `ReadByType`. GP 12 audit: pass.
    abstract Erase:
        scopeId: string * subjectUserId: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>
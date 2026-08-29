// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Hosts.DeliveredEgress.CallbackLogSource

open System
open ToolUp.Platform
open ToolUp.MediaLibrary
open ToolUp.MediaLibrary.DeliveredEgress

// ─── Phase 742 — reference seam implementations ───────────────────────
//
// `IDeliveredLogSource` and `IDeliveredScopeResolver` implemented over
// plain callbacks, the same shape `CallbackUrlSigner` takes to
// `IDelegatedUrlSigner` and for the same reason: fetching a log batch
// means reaching a specific object store or push destination with a
// specific credential, and that belongs to the deployment. What the SDK
// can own is everything around the callback — the declared delivery lag,
// the failure-as-data boundary, and the guarantee that a throwing
// callback becomes a typed error rather than an escaped exception.

/// An `IDeliveredLogSource` over a caller-supplied fetch callback.
///
/// The callback returns whole batches — typically one per delivered log
/// file, which is the unit an edge redelivers. It may legitimately
/// re-offer batches it has offered before: ingestion is idempotent by
/// `DeliveredEgress.dedupKey`, so a forgetful source is correct, merely
/// slightly wasteful.
///
/// `deliveryLag` is the declaration portability rule 6 asks for, and it
/// is the caller's to make because only the caller knows which delivery
/// it wired. The two edge classes surveyed for this phase differ by
/// roughly three orders of magnitude — sub-minute pushes against
/// typically-within-the-hour deliveries that can lag up to a day — so a
/// default here would be actively misleading and there is none.
///
/// `byteSemantics` (Phase 743) is the second declaration of the same
/// kind and for the same reason: what the source's byte counts MEAN is
/// per-deployment configuration, only the caller knows which field it
/// selected, and there is no default because a wrong one would be
/// invisible in every number downstream. A caller who genuinely does not
/// know passes `UnknownByteSemantics`, which is an answer rather than a
/// silence — those bytes then count toward the delivered total and
/// toward no semantics-specific figure.
type CallbackLogSource
    (
        name: string,
        deliveryLag: TimeSpan,
        byteSemantics: ByteSemantics,
        fetch: unit -> Async<Result<DeliveredBatch list, string>>
    ) =

    /// Convenience for a source whose callback cannot fail in a way the
    /// caller wishes to distinguish — a throw still becomes an `Error`.
    ///
    /// A named factory rather than a second CONSTRUCTOR overload: the
    /// two differ only in the callback's return type, and F# cannot pick
    /// between them for a lambda whose type is not yet known — including
    /// the entirely reasonable `fun () -> async { ... }` that ends in a
    /// `failwith`. An overload a caller cannot invoke without a type
    /// annotation is not a convenience.
    static member ofBatches
        (name: string, deliveryLag: TimeSpan, byteSemantics: ByteSemantics, fetch: unit -> Async<DeliveredBatch list>)
        =
        CallbackLogSource(
            name,
            deliveryLag,
            byteSemantics,
            fun () -> async {
                let! batches = fetch ()
                return Ok batches
            }
        )

    interface IDeliveredLogSource with
        member _.Name = name

        member _.DeliveryLag = deliveryLag

        member _.ByteSemantics = byteSemantics

        /// A throwing callback becomes `Error`, never an escaped
        /// exception. The job handler turns that into a
        /// `TransientFailure` and the scheduler retries it — which is
        /// the right disposition for the overwhelmingly likely cause (a
        /// credential expiry, a throttle, an unreachable store), and is
        /// a disposition an exception escaping into the scheduler would
        /// also reach but without a message naming the source.
        member _.FetchBatches() = async {
            try
                return! fetch ()
            with ex ->
                return Error(sprintf "%s: %s" (ex.GetType().Name) ex.Message)
        }

/// An `IDeliveredScopeResolver` over a caller-supplied lookup.
///
/// This is how the ambient-scope routes (`/api/media/stream/{id}` and
/// `/api/media/hls/{id}/{file}`) become attributable at all. Their URLs
/// never carried a scope — the origin resolved it from request context
/// that no access log captured — and `IMediaLibrary` cannot supply one
/// either, because every member takes the scope as a parameter precisely
/// so that a cross-scope read cannot be expressed (GP 4). The deployment
/// already holds the mapping in its own catalogue; this is it saying so.
type CallbackScopeResolver(name: string, resolve: MediaId -> Async<StorageScope option>) =

    /// Convenience for a deployment holding the mapping in memory — a
    /// small library, or a cache in front of a larger one.
    new(name: string, table: Map<string, StorageScope>) =
        CallbackScopeResolver(name, (fun id -> async.Return(table.TryFind(MediaId.value id))))

    interface IDeliveredScopeResolver with
        member _.Name = name

        /// A throwing lookup resolves to `None`, which the ingestion
        /// counts as `ScopeUnresolved`. Deliberate: a resolver that
        /// fails for one item must cost that item's attribution and
        /// nothing more. Letting the exception escape would abandon the
        /// whole batch — every OTHER record in it — over one lookup, and
        /// the drop counter is exactly the instrument that makes the
        /// resulting shortfall visible.
        member _.ResolveScope(id: MediaId) = async {
            try
                return! resolve id
            with _ ->
                return None
        }
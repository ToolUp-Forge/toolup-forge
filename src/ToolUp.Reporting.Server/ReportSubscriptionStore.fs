namespace ToolUp.Reporting

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 534.A — the subscription store ────────────────────────────
//
// Storage layout, following the `_platform/{feature}/{scopeId}/...`
// shape every other platform-owned store uses (`_platform/jobs/`,
// `_platform/webhooks/`, `_platform/data-sources/`) so an operator sees
// one consistent tree:
//
//   container : `_platform`
//   blob      : `report-subscriptions/{scopeId}/{subscriptionId}.json`
//
// Cross-scope reads are structurally impossible: every path is built
// from the `scopeId` the caller was resolved to, and no operation ever
// widens the prefix (GP 4). A `Get` for an id belonging to another
// scope looks in this scope's prefix, finds nothing, and returns
// `None` — the caller cannot distinguish "does not exist" from "is not
// yours", which is the property that makes id enumeration useless.
//
// Concurrency: read-modify-write is non-atomic, the same trade-off
// `BlobWebhookRegistry` and `BlobDataSourceConfigStore` take.
// Subscriptions change on an admin action and are written once per run
// to record an outcome; the last-write-wins window is acceptable, and
// an ETag-based CAS implementation drops in behind this interface
// without changing it.

/// Per-scope persistence for `ReportSubscription`. An interface rather
/// than a concrete type so a deployment already holding its
/// subscriptions elsewhere can bind its own, and so the job handler and
/// API handler can be tested against an in-memory one.
///
/// Portability audit (GP 12): identity by value (`string` scope +
/// `SubscriptionId`); async at every boundary; failures as
/// `Result`/`option` data, no callbacks; stateless between calls;
/// single-scope, so no cross-shard ordering is promised; no time
/// precision surface.
type IReportSubscriptionStore =
    /// Every subscription at the scope, ordered by display name.
    abstract List: scopeId: string -> Async<ReportSubscription list>

    /// One subscription. `None` when it does not exist at this scope.
    abstract Get: scopeId: string * id: SubscriptionId -> Async<ReportSubscription option>

    /// Create or overwrite. The record's own `ScopeId` is ignored in
    /// favour of the `scopeId` argument — the store cannot be talked
    /// into writing into a scope the caller was not resolved to.
    abstract Save: scopeId: string * subscription: ReportSubscription -> Async<Result<ReportSubscription, string>>

    /// Delete. Idempotent — deleting an unknown id is a no-op. Raises
    /// when storage REFUSES the delete (Phase 971): the record is still
    /// there, and no failure channel exists to say so.
    abstract Delete: scopeId: string * id: SubscriptionId -> Async<unit>

/// Phase 1005 — where standing subscriptions were seeded: each row's
/// declaration key and the scope its principal resolved to then. Seeding
/// reads it to retire a row whose declaration was removed — or whose
/// principal now resolves elsewhere — because the scope such a row lives in
/// is no longer derivable from the current declarations. Request-created
/// subscriptions never appear in it.
type IStandingSubscriptionLedger =
    /// The seeded rows, as (key, scope id) pairs. Empty before the first
    /// seeding; `Error` when the ledger exists but cannot be read, which a
    /// seeding refuses on rather than reading as "nothing to retire". A key
    /// can stand in two scopes for as long as retiring its old row fails.
    abstract Read: unit -> Async<Result<Set<string * string>, string>>

    /// Replace the seeded rows.
    abstract Write: seeded: Set<string * string> -> Async<Result<unit, string>>

module ReportSubscriptionStore =

    [<Literal>]
    let private PlatformContainer = "_platform"

    let private scopePrefix (scopeId: string) =
        $"{ReportSubscription.StorePrefix}/{scopeId}/"

    let private subscriptionBlob (scopeId: string) (id: SubscriptionId) = $"{scopePrefix scopeId}{id}.json"

    // `ReportSubscription` round-trips to a management surface, so the
    // Fable-compatible converter set is the wire — the same choice
    // `BlobDataSourceConfigStore` / `ConfigStore` / `BlobJobStore` make.
    // A record persisted before a field existed deserialises with that
    // field `null`, which for the two collection fields would NRE on
    // first use; `coerce` below backfills them.
    module private Json =
        let private options = FableConverters.create ()

        let serialize (value: 'T) : byte[] =
            JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

        let tryDeserialize<'T> (bytes: byte[]) : 'T option =
            try
                Some(JsonSerializer.Deserialize<'T>(Encoding.UTF8.GetString bytes, options))
            with _ ->
                None

    /// Backfill reference-typed collection fields a pre-existing blob
    /// may not carry. `[]` is the `Empty` singleton, never null, so an
    /// absent list deserialises to `null` and NREs on the first list
    /// operation — the documented additive-field hazard on the STJ path.
    let private coerce (subscription: ReportSubscription) =
        let recipients =
            if isNull (box subscription.RecipientUserIds) then
                []
            else
                subscription.RecipientUserIds

        let parameters =
            if isNull (box subscription.Parameters) then
                Map.empty
            else
                subscription.Parameters

        {
            subscription with
                RecipientUserIds = recipients
                Parameters = parameters
        }

    /// Validate a create/update request against the producer registry
    /// and the cron parser, and stamp the server-owned fields.
    ///
    /// This is the one place a string becomes a schedule, and it runs
    /// BEFORE anything is persisted: a subscription that would fail at
    /// its first tick — an unparseable cron, a missing required
    /// parameter, a format the producer does not serve, no recipients
    /// at all — is refused while the caller is still present to be told
    /// why. Discovering any of those at 06:00 on a Monday, in a log, is
    /// the failure mode this exists to prevent.
    let validate
        (registry: ReportProducerRegistry)
        (scopeId: string)
        (id: SubscriptionId)
        (createdBy: string)
        (createdAt: DateTimeOffset)
        (lastRun: SubscriptionRunOutcome)
        (request: NewReportSubscription)
        : Result<ReportSubscription, SubscriptionError> =
        match registry.TryResolve request.ProducerKey with
        | None -> Error(UnknownProducer request.ProducerKey)
        | Some producer ->
            // `isNull (box …)` rather than `Option.ofObj`: an F# list /
            // Map does not satisfy the null constraint, yet a record
            // deserialised from a blob that predates a field really can
            // hold null there.
            let recipients =
                if isNull (box request.RecipientUserIds) then
                    []
                else
                    request.RecipientUserIds
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                    |> List.distinct

            let parameters =
                if isNull (box request.Parameters) then
                    Map.empty
                else
                    request.Parameters

            if List.isEmpty recipients then
                Error NoRecipients
            else
                match CronExpression.tryParse request.Schedule with
                | Error reason -> Error(InvalidSchedule(request.Schedule, reason))
                | Ok _ ->
                    ReportSubscription.validateParameters producer.Descriptor.Parameters parameters
                    |> Result.bind (fun () ->
                        ReportSubscription.validateFormat producer.Descriptor.Formats request.Format)
                    |> Result.map (fun () -> {
                        Id = id
                        ScopeId = scopeId
                        DisplayName = request.DisplayName
                        ProducerKey = request.ProducerKey
                        Parameters = parameters
                        Schedule = request.Schedule
                        RecipientUserIds = recipients
                        Format = request.Format
                        Enabled = request.Enabled
                        LastRun = lastRun
                        CreatedBy = createdBy
                        CreatedAt = createdAt
                    })

    /// Blob-backed store. One JSON blob per subscription.
    type BlobReportSubscriptionStore(storage: IBlobStorage) =

        interface IReportSubscriptionStore with

            member _.List scopeId = async {
                let! names = storage.List(PlatformContainer, scopePrefix scopeId)

                let! loaded =
                    names
                    |> List.map (fun name -> async {
                        let! result = storage.Download(PlatformContainer, name)

                        return
                            match result with
                            | Ok bytes -> Json.tryDeserialize<ReportSubscription> bytes |> Option.map coerce
                            | Error _ -> None
                    })
                    |> Async.Parallel

                return loaded |> Array.choose id |> Array.toList |> List.sortBy _.DisplayName
            }

            member _.Get(scopeId, id) = async {
                let! result = storage.Download(PlatformContainer, subscriptionBlob scopeId id)

                return
                    match result with
                    | Ok bytes -> Json.tryDeserialize<ReportSubscription> bytes |> Option.map coerce
                    | Error _ -> None
            }

            member _.Save(scopeId, subscription) = async {
                // The argument scope wins over the record's own field,
                // always. A handler stamps it correctly; this makes it
                // impossible for any other caller not to.
                let stamped = { subscription with ScopeId = scopeId }

                let! result =
                    storage.Upload(PlatformContainer, subscriptionBlob scopeId stamped.Id, Json.serialize stamped)

                return
                    match result with
                    | Ok _ -> Ok stamped
                    | Error e -> Error e
            }

            member _.Delete(scopeId, id) = async {
                // A missing blob answers Ok, so an Error is a refusal: the
                // subscription is still stored and still listed. Raise
                // rather than read it as deleted (Phase 971).
                match! storage.Delete(PlatformContainer, subscriptionBlob scopeId id) with
                | Ok() -> return ()
                | Error storageError ->
                    return
                        failwithf "report subscription %s in scope %s could not be deleted: %s" id scopeId storageError
            }

    /// Build the blob-backed store over the deployment's `IBlobStorage`.
    let create (storage: IBlobStorage) : IReportSubscriptionStore =
        BlobReportSubscriptionStore(storage) :> IReportSubscriptionStore

    /// The ledger's blob: beside the per-scope prefixes, never under one.
    let private standingLedgerBlob =
        $"{ReportSubscription.StorePrefix}-standing/ledger.json"

    /// Blob-backed standing-subscription ledger: one JSON blob in `_platform`.
    type BlobStandingSubscriptionLedger(storage: IBlobStorage) =

        interface IStandingSubscriptionLedger with

            member _.Read() = async {
                let! exists = storage.Exists(PlatformContainer, standingLedgerBlob)

                if not exists then
                    return Ok Set.empty
                else
                    let! downloaded = storage.Download(PlatformContainer, standingLedgerBlob)

                    return
                        match downloaded with
                        | Error e -> Error $"the standing-subscription ledger could not be read: {e}"
                        | Ok bytes ->
                            // An array of [key, scopeId] pairs: a BCL shape, so the
                            // ledger reads without the wire converter set.
                            try
                                let entries =
                                    JsonSerializer.Deserialize<string array array>(Encoding.UTF8.GetString bytes)

                                if isNull entries || entries |> Array.exists (fun e -> isNull e || e.Length <> 2) then
                                    Error "the standing-subscription ledger is not a list of [key, scope] pairs"
                                else
                                    Ok(entries |> Array.map (fun e -> e[0], e[1]) |> Set.ofArray)
                            with ex ->
                                Error
                                    $"the standing-subscription ledger is not a ledger this version wrote: {ex.Message}"
            }

            member _.Write(seeded) = async {
                let entries =
                    seeded |> Set.toArray |> Array.map (fun (key, scopeId) -> [| key; scopeId |])

                let! result =
                    storage.Upload(
                        PlatformContainer,
                        standingLedgerBlob,
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize entries)
                    )

                return result |> Result.map ignore
            }

    /// Build the blob-backed standing-subscription ledger over the
    /// deployment's `IBlobStorage`.
    let standingLedger (storage: IBlobStorage) : IStandingSubscriptionLedger =
        BlobStandingSubscriptionLedger(storage) :> IStandingSubscriptionLedger
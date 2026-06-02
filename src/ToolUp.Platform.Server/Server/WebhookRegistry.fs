module ToolUp.Platform.WebhookRegistry

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: always `_platform`.
// Subscription blob: `webhooks/{scopeId}/subscriptions/{subscriptionId:N}.json`
// Delivery blob:     `webhooks/{scopeId}/deliveries/{subscriptionId:N}/{ts}-{deliveryId:N}.json`
//
// The scope sits in the path prefix so per-scope operations are a
// cheap `List(container, "webhooks/{scopeId}/...")` call and cross-
// scope leakage is structurally impossible — the registry never widens
// the prefix during a scoped operation. `ListAllActive` is the *only*
// path that lists `webhooks/` directly; it's server-internal (not on
// `IWebhookApi`) and only the dispatcher calls it.

let private platformContainer = "_platform"

let private subscriptionBlob (scopeId: string) (subscriptionId: Guid) =
    $"webhooks/{scopeId}/subscriptions/{subscriptionId:N}.json"

let private subscriptionsPrefix (scopeId: string) = $"webhooks/{scopeId}/subscriptions/"

let private allSubscriptionsRoot = "webhooks/"

let private deliveryBlob (scopeId: string) (subscriptionId: Guid) (delivery: WebhookDelivery) =
    let ts =
        delivery.AttemptedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ss-fffffffZ")

    $"webhooks/{scopeId}/deliveries/{subscriptionId:N}/{ts}-{delivery.DeliveryId:N}.json"

let private deliveriesPrefix (scopeId: string) (subscriptionId: Guid) =
    $"webhooks/{scopeId}/deliveries/{subscriptionId:N}/"

let private scopeDeliveriesPrefix (scopeId: string) = $"webhooks/{scopeId}/deliveries/"

let private allDeliveriesRoot = "webhooks/"

// ─── JSON ────────────────────────────────────────────────────────
//
// Subscriptions and deliveries both round-trip to the Fable admin UI,
// so use `FableJsonConverter` (DU-aware shape compatible with
// `Fable.SimpleJson` on the client). Same pattern as `BlobConfigStore`.

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<'T>(json, options))
        with _ ->
            None

// ─── Concurrency helper ──────────────────────────────────────────

/// Download many blobs in parallel, tolerating individual
/// deserialisation failures by skipping them. Mirrors
/// `PersistentEventStore.downloadAll` — a partial result is preferable
/// to a total failure for the dispatcher's hot path.
let private downloadAll<'T> (blobStorage: IBlobStorage) (blobNames: string list) : Async<'T list> = async {
    let! results =
        blobNames
        |> List.map (fun name -> async {
            let! result = blobStorage.Download(platformContainer, name)

            return
                match result with
                | Ok bytes -> Json.tryDeserialize<'T> bytes
                | Error _ -> None
        })
        |> Async.Parallel

    return results |> Array.choose id |> Array.toList
}

// ─── IWebhookRegistry — blob-backed ──────────────────────────────

/// Blob-backed `IWebhookRegistry`. One JSON blob per subscription,
/// stored in the reserved `_platform` container under the scope-
/// prefixed path. Updates are read-modify-write — no atomic
/// compare-and-swap, but subscriptions change rarely (only via admin
/// action and the dispatcher's `SetConsecutiveFailures`), so the
/// last-write-wins window is acceptable. A distributed implementation
/// adding ETag-based CAS drops in without changing the interface.
type BlobWebhookRegistry(storage: IBlobStorage) =
    let load (scopeId: string) (subscriptionId: Guid) = async {
        let! result = storage.Download(platformContainer, subscriptionBlob scopeId subscriptionId)

        match result with
        | Ok bytes -> return Json.tryDeserialize<WebhookSubscription> bytes
        | Error _ -> return None
    }

    let save (sub: WebhookSubscription) = async {
        let bytes = Json.serialize sub
        let! result = storage.Upload(platformContainer, subscriptionBlob sub.ScopeId sub.SubscriptionId, bytes)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error e
    }

    interface IWebhookRegistry with
        member _.CreateSubscription(subscription) = save subscription

        member _.ListSubscriptions(scopeId) = async {
            let! names = storage.List(platformContainer, subscriptionsPrefix scopeId)
            return! downloadAll<WebhookSubscription> storage names
        }

        member _.GetSubscription(scopeId, subscriptionId) = load scopeId subscriptionId

        member _.UpdateStatus(scopeId, subscriptionId, status) = async {
            match! load scopeId subscriptionId with
            | None -> return Error "Subscription not found."
            | Some sub ->
                let resetFailures =
                    status = WebhookStatus.Active && sub.Status = WebhookStatus.Disabled

                let updated = {
                    sub with
                        Status = status
                        ConsecutiveFailures = if resetFailures then 0 else sub.ConsecutiveFailures
                }

                return! save updated
        }

        member _.SetConsecutiveFailures(scopeId, subscriptionId, count) = async {
            match! load scopeId subscriptionId with
            | None -> return Error "Subscription not found."
            | Some sub ->
                return!
                    save {
                        sub with
                            ConsecutiveFailures = max 0 count
                    }
        }

        member _.DeleteSubscription(scopeId, subscriptionId) = async {
            let! _ = storage.Delete(platformContainer, subscriptionBlob scopeId subscriptionId)
            return Ok()
        }

        member _.ListAllActive() = async {
            // Two-stage list: enumerate the entire `webhooks/` prefix,
            // keep only paths matching `webhooks/{scopeId}/subscriptions/...`,
            // download them, filter to Active. The flat-list design of
            // `IBlobStorage` doesn't surface "directories" — we filter
            // on path shape ourselves.
            let! names = storage.List(platformContainer, allSubscriptionsRoot)

            let subscriptionNames =
                names
                |> List.filter (fun n -> n.Contains "/subscriptions/" && n.EndsWith ".json")

            let! all = downloadAll<WebhookSubscription> storage subscriptionNames
            return all |> List.filter (fun s -> s.Status = WebhookStatus.Active)
        }

let createRegistry (storage: IBlobStorage) : IWebhookRegistry =
    BlobWebhookRegistry(storage) :> IWebhookRegistry

// ─── IWebhookDeliveryLog — blob-backed ───────────────────────────

/// Blob-backed `IWebhookDeliveryLog`. One JSON blob per attempt; the
/// blob name's timestamp prefix orders rows lexicographically and lets
/// `Prune` filter on name without parsing the JSON body.
///
/// The `ListRecent` path lists every blob under the subscription's
/// prefix, then sorts and trims — fine while delivery counts are
/// modest (Phase 6d's retention is bounded by the dispatcher's
/// periodic `Prune`). A distributed implementation backing this with
/// an append-optimised log (Kafka, EventGrid) would drop in without
/// changing the interface.
type BlobWebhookDeliveryLog(storage: IBlobStorage) =
    /// Parse the timestamp from a blob name of the form
    /// `webhooks/.../deliveries/.../{ts}-{guid:N}.json`. The timestamp
    /// component is `yyyy-MM-ddTHH-mm-ss-fffffffZ` (28 chars) — written
    /// with hyphens in the time positions because some blob backends
    /// disallow `:` in names. Restore them here before parsing.
    /// Returns `None` for unparseable names so a stray file in the
    /// path doesn't crash retention.
    let parseTimestamp (name: string) : DateTime option =
        try
            let lastSlash = name.LastIndexOf '/'

            if lastSlash < 0 then
                None
            else
                let leaf = name.Substring(lastSlash + 1)
                // `{ts}-{guid:N}.json` — guid:N is 32 chars, `.json` is
                // 5 chars, separator is 1 char, so the suffix length is
                // 38. Anything shorter isn't one of our delivery blobs.
                if leaf.Length < 38 + 28 then
                    None
                else
                    let tsPart = leaf.Substring(0, leaf.Length - 38)
                    let sb = StringBuilder(tsPart)
                    sb[13] <- ':'
                    sb[16] <- ':'
                    sb[19] <- '.'

                    match
                        DateTime.TryParse(
                            sb.ToString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind
                        )
                    with
                    | true, dt -> Some(dt.ToUniversalTime())
                    | _ -> None
        with _ ->
            None

    interface IWebhookDeliveryLog with
        member _.Record(scopeId, delivery) = async {
            let bytes = Json.serialize delivery

            let! result =
                storage.Upload(platformContainer, deliveryBlob scopeId delivery.SubscriptionId delivery, bytes)

            match result with
            | Ok _ -> return Ok()
            | Error e -> return Error e
        }

        member _.ListRecent(scopeId, subscriptionId, limit) = async {
            let! names = storage.List(platformContainer, deliveriesPrefix scopeId subscriptionId)
            // Lex-desc on name == time-desc because the timestamp prefix
            // is ISO-ordered. Trim before download to keep the hot path
            // bounded.
            let trimmed = names |> List.sortByDescending id |> List.truncate (max 0 limit)

            let! rows = downloadAll<WebhookDelivery> storage trimmed
            return rows |> List.sortByDescending _.AttemptedAt
        }

        member _.Prune(scopeIdOpt, olderThan) = async {
            let prefix =
                match scopeIdOpt with
                | Some scopeId -> scopeDeliveriesPrefix scopeId
                | None -> allDeliveriesRoot

            let! names = storage.List(platformContainer, prefix)

            let toDelete =
                names
                |> List.filter (fun n -> n.Contains "/deliveries/" && n.EndsWith ".json")
                |> List.filter (fun n ->
                    match parseTimestamp n with
                    | Some ts -> ts < olderThan
                    | None -> false)

            let! _ =
                toDelete
                |> List.map (fun name -> storage.Delete(platformContainer, name))
                |> Async.Parallel

            return ()
        }

let createDeliveryLog (storage: IBlobStorage) : IWebhookDeliveryLog =
    BlobWebhookDeliveryLog(storage) :> IWebhookDeliveryLog
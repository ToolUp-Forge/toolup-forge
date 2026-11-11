// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Infra

open ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text.Json
open ToolUp.Platform.BlobStorage

// ─── The shipped budget ledgers ──────────────────────────────────────────
//
// Two implementations of `IBudgetLedger` and the small latch that keeps a
// threshold warning from becoming a per-request log line.
//
// **Two tiers of atomicity, and the difference is honest rather than
// hidden.** `Reserve` must make read → decide → reserve indivisible, or a
// concurrency ceiling does not bound a burst (see `IBudgetLedger`'s
// header). `BlobBudgetLedger` gets there through `BlobDocumentStore`'s
// guarded update, in two steps:
//
//   1. The document store's write stripes serialise writers to one row
//      **within this process**. On a single-instance deployment that is
//      complete.
//   2. When the composed blob storage also implements
//      `IConditionalBlobStorage`, the write is an ETag compare-and-swap
//      with bounded retries, which extends the guarantee **across
//      replicas**. Two instances racing on one row make one of them lose
//      the CAS and re-run the whole decision against the winner's state —
//      so the loser is re-decided, never merged.
//
// A deployment on a blob backend with no ETag support gets tier 1 only,
// and the limitation is stated rather than papered over: with N replicas a
// concurrency ceiling is enforced per replica, so the effective ceiling is
// up to N× the configured one. That is a real weakening and it is
// deliberately not resolved by taking a distributed lock — a lock on the
// admission path would put every request in the deployment behind one
// round-trip, to tighten a bound whose whole job is to be approximately
// right about a resource that is already approximately measured. The
// remedy for a deployment that needs the exact bound is a conditional blob
// backend, which every cloud one is.
//
// **`Utf8JsonWriter` / `JsonDocument` rather than reflection over the F#
// records**, exactly as `BlobIdempotencyStore` does: the round-trip is then
// independent of STJ's F#-record handling, and the on-disk shape is one an
// operator can read and edit.
//
// **What an unusable row means.** A row whose bytes do not decode is set
// aside by the document store (copied beside itself) and the period starts
// again from zero consumption — the row is a running tally, so its loss
// costs an over-admission, never a wedged scope. A row that decodes but
// belongs to another scope or period (a mis-derived path) reads as zero and
// is rewritten. A row that cannot be READ, and a write that fails for a
// reason other than contention, admit without durable accounting, logged:
// the fail-open direction in `IBudgetLedger`'s header.

/// Phase 689 — JSON codec for a stored usage row. Separated from the
/// ledger so a test can pin the wire form without a blob backend, and so a
/// future admin surface writes exactly what the ledger reads.
///
/// **The domain is in the PATH, not the row**, which is why the stored
/// shape is byte-identical to the one Phase 451 has been writing: a row
/// written by that phase's store before this seam existed reads back
/// through this codec unchanged.
[<RequireQualifiedAccess>]
module BudgetUsageJson =

    /// Bumped only on an incompatible layout change. A blob carrying an
    /// unknown version is treated as unreadable (→ zero consumption),
    /// never guessed at.
    [<Literal>]
    let SchemaVersion = 1

    /// Serialise a usage row.
    let serialise (usage: BudgetUsage) : byte[] =
        use ms = new IO.MemoryStream()

        (use writer = new Utf8JsonWriter(ms)
         writer.WriteStartObject()
         writer.WriteNumber("SchemaVersion", SchemaVersion)
         writer.WriteString("ScopeId", usage.ScopeId)
         writer.WriteString("PeriodKey", usage.PeriodKey)
         writer.WriteNumber("InFlight", usage.InFlight)
         writer.WriteNumber("Spent", usage.Spent)
         writer.WriteNumber("UpdatedAtTicks", usage.UpdatedAt.Ticks)
         writer.WriteEndObject()
         writer.Flush())

        ms.ToArray()

    /// Parse a stored row as it stands, before any key cross-check. The
    /// domain is in the path, not the row, so the result carries an empty
    /// `Domain`. `Error` for bytes that are not a row of this schema.
    let internal decodeRow (bytes: byte[]) : Result<BudgetUsage, string> =
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory<byte> bytes)
            let root = doc.RootElement
            let version = root.GetProperty("SchemaVersion").GetInt32()

            if version <> SchemaVersion then
                Error $"usage row schema version {version}, expected {SchemaVersion}"
            else
                Ok {
                    Domain = ""
                    ScopeId = root.GetProperty("ScopeId").GetString()
                    PeriodKey = root.GetProperty("PeriodKey").GetString()
                    InFlight = root.GetProperty("InFlight").GetInt32()
                    Spent = root.GetProperty("Spent").GetDecimal()
                    UpdatedAt = DateTime(root.GetProperty("UpdatedAtTicks").GetInt64(), DateTimeKind.Utc)
                }
        with ex ->
            Error ex.Message

    /// The decoded row, if it is `key`'s.
    ///
    /// The key cross-check is the second half of the GP 4 guarantee: the
    /// blob path already partitions by scope, and this makes a mis-derived
    /// path degrade to "no consumption recorded" rather than to one tenant
    /// spending another's allowance.
    let internal forKey (key: BudgetLedgerKey) (row: BudgetUsage) : BudgetUsage option =
        if row.ScopeId = key.ScopeId && row.PeriodKey = key.PeriodKey then
            Some { row with Domain = key.Domain }
        else
            None

    /// Parse a usage row, refusing one that is not for `key`.
    let deserialise (key: BudgetLedgerKey) (bytes: byte[]) : BudgetUsage option =
        match decodeRow bytes with
        | Ok row -> forKey key row
        | Error _ -> None

/// Phase 689 — the refusal a ledger produces when it cannot make a
/// decision stick, distinct from one the policy produced.
[<RequireQualifiedAccess>]
module BudgetContention =
    /// Dimension label for a refusal caused by write contention rather
    /// than by a configured ceiling. A domain that wants its refusals to
    /// read in its own vocabulary supplies its own factory to the ledger;
    /// this is the honest default for one that does not.
    [<Literal>]
    let Dimension = "contention"

    /// The default contention refusal: no quota, because none was
    /// consulted — what was hit is the row, not a ceiling.
    let denial (usage: BudgetUsage) : BudgetDenial = {
        Domain = usage.Domain
        ScopeId = usage.ScopeId
        ClassLabel = ""
        Dimension = Dimension
        Quota = 0M
        Spent = decimal usage.InFlight
        Requested = 1M
        PeriodKey = usage.PeriodKey
    }

/// Phase 689 — the blob-backed `IBudgetLedger`: one JSON row per
/// (domain, scope, period) under the reserved `_platform` container.
///
/// `blobs` is the composed `IBlobStorage`; when it also implements
/// `IConditionalBlobStorage` the reservation write becomes a cross-replica
/// ETag CAS (see the file header). `logger` is optional — omitting it is
/// silent and behaviour-preserving (GP 11). `clock` exists so period
/// boundaries are testable without waiting for midnight. `onContention`
/// lets a domain phrase the lost-CAS refusal in its own dimension
/// vocabulary.
type BlobBudgetLedger
    (
        blobs: IBlobStorage,
        ?logger: ILogger,
        ?container: string,
        ?clock: unit -> DateTime,
        ?onContention: BudgetUsage -> BudgetDenial
    ) =

    let container = defaultArg container BudgetLedgerLayout.DefaultContainer
    let now = defaultArg clock (fun () -> DateTime.UtcNow)
    let contention = defaultArg onContention BudgetContention.denial

    /// The key whose container is `container`: `_platform` by default, a
    /// deployment's own container read back to its key otherwise.
    let scope =
        ScopeKey.tryParseContainer container
        |> Option.defaultValue (ScopeKey.Claim container)

    let warn (message: string) =
        match logger with
        | Some log -> log.Warn message
        | None -> ()

    /// Bounded CAS attempts (five, with no pause between them). Small on
    /// purpose: contention on ONE key is the burst this ledger exists to
    /// bound, and a caller who loses five races in a row is, in practice, a
    /// caller whose scope is saturated — which is the answer the budget was
    /// going to give anyway. An unbounded retry here would turn a saturated
    /// scope into an unbounded write amplification against the blob backend.
    let rows =
        BlobDocumentStore<BudgetUsage>(
            blobs,
            {
                Encode = BudgetUsageJson.serialise
                Decode = BudgetUsageJson.decodeRow
            },
            (logger
             |> Option.defaultWith (fun () -> JsonConsoleLogger.JsonConsoleLogger() :> ILogger)),
            {
                MaxRetries = 4
                InitialBackoff = TimeSpan.Zero
            }
        )

    let pathOf (key: BudgetLedgerKey) =
        DocPath.ofString (BudgetLedgerLayout.usageBlob key)

    /// The live row for `key` from what the store read: zero consumption
    /// for an absent row, and for one that belongs to another scope or
    /// period (rewritten by the next write).
    let rowFor (key: BudgetLedgerKey) (stored: BudgetUsage option) : BudgetUsage =
        match stored with
        | None -> BudgetLedgerKey.emptyUsage key
        | Some row ->
            match BudgetUsageJson.forKey key row with
            | Some usage -> usage
            | None ->
                warn
                    $"BlobBudgetLedger: the usage row for domain '{key.Domain}' scope '{key.ScopeId}' period '{key.PeriodKey}' belongs to scope '{row.ScopeId}' period '{row.PeriodKey}' — treating as zero consumption and rewriting."

                BudgetLedgerKey.emptyUsage key

    /// Run `mutate` against the live row and persist the result in one
    /// guarded update, the whole decision re-run on a lost CAS. `mutate`
    /// returns `Error` to abandon the write (a refusal), `Ok` to persist.
    let mutateUsage
        (key: BudgetLedgerKey)
        (mutate: BudgetUsage -> Result<BudgetUsage, BudgetDenial>)
        : Async<Result<BudgetUsage, BudgetDenial>> =
        async {
            // The row the last decision ran over: the contention refusal
            // names it, and an infrastructure failure re-decides over it.
            let lastSeen = ref (BudgetLedgerKey.emptyUsage key)

            let! outcome =
                rows.UpdateOrRestart(
                    scope,
                    pathOf key,
                    fun stored ->
                        let current = rowFor key stored
                        lastSeen.Value <- current

                        match mutate current with
                        // Refused: nothing is written, so a denial costs one
                        // read and never a blob write.
                        | Error denial -> BlobUpdate.Keep(Error denial)
                        | Ok updated -> BlobUpdate.Write(updated, Ok updated)
                )

            match outcome with
            | Ok result -> return result
            | Error(BlobMapStoreError.Contended _) ->
                // Lost the CAS on every attempt: the row is hot. Admitting
                // here would be the one failure direction a budget may not
                // have on its *contended* path — that is precisely the burst
                // being bounded — so this refuses, and says why in the
                // denial rather than in a log line the caller never sees.
                return Error(contention lastSeen.Value)
            | Error e ->
                // Infrastructure, not contention. Do not re-decide in a loop
                // against a backend that is failing — report and let the
                // caller proceed, for the fail-open reason in the
                // `IBudgetLedger` header.
                warn
                    $"BlobBudgetLedger: domain '{key.Domain}' scope '{key.ScopeId}' period '{key.PeriodKey}': {BlobMapStoreError.describe e}. Budget accounting for this request is not durable."

                return mutate lastSeen.Value
        }

    interface IBudgetLedger with
        member _.ReadUsage(key: BudgetLedgerKey) = async {
            match! rows.Read(scope, pathOf key) with
            | Ok stored -> return rowFor key stored
            | Error e ->
                warn
                    $"BlobBudgetLedger: domain '{key.Domain}' scope '{key.ScopeId}' period '{key.PeriodKey}': {BlobMapStoreError.describe e} — reading as zero consumption."

                return BudgetLedgerKey.emptyUsage key
        }

        member _.Reserve(key, cost, decide) =
            mutateUsage key (fun current ->
                match decide current with
                | Error denial -> Error denial
                | Ok() -> Ok(BudgetUsage.reserve cost (now ()) current))

        member _.Release(key, costAdjustment) = async {
            let! _ = mutateUsage key (fun current -> Ok(BudgetUsage.settle costAdjustment (now ()) current))
            return ()
        }

/// Phase 689 — an in-process `IBudgetLedger` for tests and single-node
/// development.
///
/// Genuinely atomic (one lock over a dictionary) and genuinely
/// non-durable: everything is lost on restart, which for a concurrency
/// reservation is the *safe* loss — a restart releases every leaked slot.
type InMemoryBudgetLedger(?clock: unit -> DateTime) =
    let now = defaultArg clock (fun () -> DateTime.UtcNow)
    let rows = ConcurrentDictionary<string, BudgetUsage>()
    let gate = obj ()

    let current (key: BudgetLedgerKey) =
        match rows.TryGetValue(BudgetLedgerKey.cacheKey key) with
        | true, row -> row
        | _ -> BudgetLedgerKey.emptyUsage key

    interface IBudgetLedger with
        member _.ReadUsage(key: BudgetLedgerKey) = async { return current key }

        member _.Reserve(key, cost, decide) = async {
            return
                lock gate (fun () ->
                    let row = current key

                    match decide row with
                    | Error denial -> Error denial
                    | Ok() ->
                        let updated = BudgetUsage.reserve cost (now ()) row
                        rows[BudgetLedgerKey.cacheKey key] <- updated
                        Ok updated)
        }

        member _.Release(key, costAdjustment) = async {
            lock gate (fun () ->
                rows[BudgetLedgerKey.cacheKey key] <- BudgetUsage.settle costAdjustment (now ()) (current key))
        }

/// Phase 689 — reports a threshold crossing once per key rather than on
/// every subsequent request.
///
/// A warning is a leading indicator; one emitted on every admission after
/// the crossing is a log line an operator filters out, which is the
/// indicator not working. Keyed by the whole ledger key, so it
/// self-expires when the period rolls — the new period is a key that has
/// never been latched, and there is no eviction job to get wrong.
type BudgetWarningLatch() =
    let latched = ConcurrentDictionary<string, bool>()

    /// `true` exactly once per key — for the caller that wins the race to
    /// report the crossing.
    member _.ShouldReport(key: BudgetLedgerKey) : bool =
        latched.TryAdd(BudgetLedgerKey.cacheKey key, true)

    /// Forget every latch. For a test asserting the once-per-period
    /// property in both directions; a deployment never calls it.
    member _.Reset() : unit = latched.Clear()
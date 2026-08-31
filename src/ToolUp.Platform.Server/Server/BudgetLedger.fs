// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open ToolUp.Platform.BlobStorage

// ─── Phase 689 — the shipped budget ledgers ──────────────────────────────
//
// Two implementations of `IBudgetLedger` and the small latch that keeps a
// threshold warning from becoming a per-request log line. All three were
// Phase 451's, written against `ComputeBudgetUsage` and reachable only by
// a compute submission; the code is unchanged and the types are the seam's,
// which is the whole of what this phase did to them.
//
// **Two tiers of atomicity, and the difference is honest rather than
// hidden.** `Reserve` must make read → decide → reserve indivisible, or a
// concurrency ceiling does not bound a burst (see `IBudgetLedger`'s
// header). `BlobBudgetLedger` gets there in two steps:
//
//   1. A per-key `SemaphoreSlim` serialises writers **within this
//      process**. On a single-instance deployment that is complete, and it
//      is the same mechanism `UsageBatchFlusher` uses for its per-bucket
//      read-modify-write.
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
// independent of STJ's F#-record handling, a corrupt or foreign blob is a
// parse failure we turn into the zero row rather than an exception out of
// an admission check, and the on-disk shape is one an operator can read
// and edit.

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

    /// Parse a usage row, refusing one that is not for `key`.
    ///
    /// The key cross-check is the second half of the GP 4 guarantee: the
    /// blob path already partitions by scope, and this makes a mis-derived
    /// path degrade to "no consumption recorded" rather than to one tenant
    /// spending another's allowance.
    let deserialise (key: BudgetLedgerKey) (bytes: byte[]) : BudgetUsage option =
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory<byte> bytes)
            let root = doc.RootElement

            let matches =
                root.GetProperty("SchemaVersion").GetInt32() = SchemaVersion
                && root.GetProperty("ScopeId").GetString() = key.ScopeId
                && root.GetProperty("PeriodKey").GetString() = key.PeriodKey

            if not matches then
                None
            else
                Some {
                    Domain = key.Domain
                    ScopeId = key.ScopeId
                    PeriodKey = key.PeriodKey
                    InFlight = root.GetProperty("InFlight").GetInt32()
                    Spent = root.GetProperty("Spent").GetDecimal()
                    UpdatedAt = DateTime(root.GetProperty("UpdatedAtTicks").GetInt64(), DateTimeKind.Utc)
                }
        with _ ->
            None

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

    let conditional =
        match box blobs with
        | :? IConditionalBlobStorage as c -> Some c
        | _ -> None

    /// Bounded CAS retries. Small on purpose: contention on ONE key is the
    /// burst this ledger exists to bound, and a caller who loses five
    /// races in a row is, in practice, a caller whose scope is saturated —
    /// which is the answer the budget was going to give anyway. An
    /// unbounded retry here would turn a saturated scope into an unbounded
    /// write amplification against the blob backend.
    let maxCasAttempts = 5

    /// Per-key write gate. Bounded by the number of keys this process has
    /// touched; entries are cheap (one semaphore) and a period key stops
    /// being written to when the period rolls, so the natural bound is
    /// "active scopes", not "requests".
    let gates = ConcurrentDictionary<string, SemaphoreSlim>()

    let gateFor (key: BudgetLedgerKey) =
        gates.GetOrAdd(BudgetLedgerKey.cacheKey key, fun _ -> new SemaphoreSlim(1, 1))

    let warn (message: string) =
        match logger with
        | Some log -> log.Warn message
        | None -> ()

    /// The stored row plus its ETag, or the zero row plus `None`.
    let readUsageWithETag (key: BudgetLedgerKey) : Async<BudgetUsage * string option> = async {
        let name = BudgetLedgerLayout.usageBlob key

        let unreadable () =
            warn
                $"BlobBudgetLedger: unreadable usage row for domain '{key.Domain}' scope '{key.ScopeId}' period '{key.PeriodKey}' — treating as zero consumption and rewriting."

            BudgetLedgerKey.emptyUsage key

        match conditional with
        | Some cond ->
            match! cond.DownloadWithETag(container, name) with
            | Ok(bytes, etag) ->
                match BudgetUsageJson.deserialise key bytes with
                | Some usage -> return usage, Some etag
                // Corrupt or foreign: treat as no consumption, but keep
                // the etag so the repairing write still CASes against what
                // we read rather than blind-overwriting a row another
                // writer may have just fixed.
                | None -> return unreadable (), Some etag
            | Error _ -> return BudgetLedgerKey.emptyUsage key, None
        | None ->
            match! blobs.Download(container, name) with
            | Ok bytes ->
                match BudgetUsageJson.deserialise key bytes with
                | Some usage -> return usage, None
                | None -> return unreadable (), None
            | Error _ -> return BudgetLedgerKey.emptyUsage key, None
    }

    /// Write `usage`, CASing against `expected` when the backend supports
    /// it. `false` means the CAS was refused and the caller should re-read
    /// and re-decide.
    let writeUsage (key: BudgetLedgerKey) (expected: string option) (usage: BudgetUsage) : Async<bool> = async {
        let name = BudgetLedgerLayout.usageBlob key
        let bytes = BudgetUsageJson.serialise usage

        match conditional with
        | Some cond ->
            let condition =
                match expected with
                | Some etag -> IfMatch etag
                | None -> IfAbsent

            match! cond.UploadWithETag(container, name, bytes, condition) with
            | Ok _ -> return true
            | Error(ETagMismatch _) -> return false
            | Error(ConditionalWriteFailure message) ->
                // Infrastructure, not contention. Do not re-decide in a
                // loop against a backend that is failing — report and let
                // the caller proceed, for the fail-open reason in the
                // `IBudgetLedger` header.
                warn
                    $"BlobBudgetLedger: conditional write failed for domain '{key.Domain}' scope '{key.ScopeId}' period '{key.PeriodKey}': {message}. Budget accounting for this request is not durable."

                return true
        | None ->
            match! blobs.Upload(container, name, bytes) with
            | Ok _ -> return true
            | Error message ->
                warn
                    $"BlobBudgetLedger: usage write failed for domain '{key.Domain}' scope '{key.ScopeId}' period '{key.PeriodKey}': {message}. Budget accounting for this request is not durable."

                return true
    }

    /// Run `mutate` against the live row and persist the result, retrying
    /// the whole decision on a lost CAS. `mutate` returns `Error` to
    /// abandon the write (a refusal), `Ok` to persist.
    let mutateUsage
        (key: BudgetLedgerKey)
        (mutate: BudgetUsage -> Result<BudgetUsage, BudgetDenial>)
        : Async<Result<BudgetUsage, BudgetDenial>> =
        async {
            let gate = gateFor key
            do! gate.WaitAsync() |> Async.AwaitTask

            try
                let mutable attempt = 0
                let mutable result = Unchecked.defaultof<Result<BudgetUsage, BudgetDenial>>
                let mutable settled = false

                while not settled do
                    attempt <- attempt + 1
                    let! current, etag = readUsageWithETag key

                    match mutate current with
                    | Error denial ->
                        // Refused: nothing is written, so a denial costs
                        // one read and never a blob write.
                        result <- Error denial
                        settled <- true
                    | Ok updated ->
                        let! written = writeUsage key etag updated

                        if written then
                            result <- Ok updated
                            settled <- true
                        elif attempt >= maxCasAttempts then
                            // Lost the CAS `maxCasAttempts` times: the row
                            // is hot. Admitting here would be the one
                            // failure direction a budget may not have on
                            // its *contended* path — that is precisely the
                            // burst being bounded — so this refuses, and
                            // says why in the denial rather than in a log
                            // line the caller never sees.
                            result <- Error(contention current)
                            settled <- true

                return result
            finally
                gate.Release() |> ignore
        }

    interface IBudgetLedger with
        member _.ReadUsage(key: BudgetLedgerKey) = async {
            let! usage, _ = readUsageWithETag key
            return usage
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
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform.BlobStorage

// ─── Phase 451 — the blob-backed compute-budget store ────────────────────
//
// The default `IComputeBudgetStore`: budgets and usage rows as JSON blobs
// under the reserved `_platform` container, keyed by scope and — for usage
// — by period, so a period reset costs nothing (a period nothing has been
// charged to is a blob that does not exist, and a blob that does not exist
// reads as zero).
//
// **Two tiers of atomicity, and the difference is honest rather than
// hidden.** `Admit` must make read → decide → reserve indivisible, or the
// concurrency cap does not bound a burst (see `IComputeBudgetStore`'s
// header). This store gets there in two steps:
//
//   1. A per-`(scope, period)` `SemaphoreSlim` serialises writers **within
//      this process**. On a single-instance deployment that is complete,
//      and it is the same mechanism `UsageBatchFlusher` uses for its
//      per-bucket read-modify-write.
//   2. When the composed blob storage also implements
//      `IConditionalBlobStorage`, the write is an ETag compare-and-swap
//      with bounded retries, which extends the guarantee **across
//      replicas**. Two instances racing on one row make one of them lose
//      the CAS and re-run the whole decision against the winner's state —
//      so the loser is re-decided, never merged.
//
// A deployment on a blob backend with no ETag support gets tier 1 only,
// and the limitation is stated rather than papered over: with N replicas
// the concurrency cap is enforced per replica, so the effective ceiling is
// up to N× the configured one. That is a real weakening and it is
// deliberately not resolved by taking a distributed lock — a lock on the
// admission path would put every submission in the deployment behind one
// round-trip, to tighten a bound whose whole job is to be approximately
// right about a resource that is already approximately measured. The
// remedy for a deployment that needs the exact bound is a conditional
// blob backend, which every cloud one is.
//
// **A read failure is unrestricted, never a refusal.** `GetBudget` and the
// usage read both degrade to "no limit" on a storage error. The failure
// direction a budget may have is admitting work it should have refused;
// the direction it may NOT have is turning a transient storage blip into a
// deployment-wide refusal of every submission. A budget that fails closed
// is a budget an operator switches off after the first incident, which
// leaves them with no budget at all.
//
// **`Utf8JsonWriter` / `JsonDocument` rather than reflection over the F#
// records**, exactly as `BlobIdempotencyStore` and `MemoizedComputeDispatcher`
// do: the round-trip is then independent of STJ's F#-record and DU
// handling, a corrupt or foreign blob is a parse failure we turn into the
// zero row rather than an exception out of an admission check, and the
// on-disk shape is one an operator can read and edit.

/// Phase 451 — JSON codec for the two stored shapes. Separated from the
/// store so a test can pin the wire form without a blob backend, and so a
/// future admin surface writes exactly what the store reads.
[<RequireQualifiedAccess>]
module ComputeBudgetJson =

    /// Bumped only on an incompatible layout change. A blob carrying an
    /// unknown version is treated as unreadable (→ unrestricted / zero),
    /// never guessed at.
    [<Literal>]
    let SchemaVersion = 1

    let private writeLimits (writer: Utf8JsonWriter) (name: string) (limits: ComputeBudgetLimits) =
        writer.WriteStartObject name
        writer.WriteNumber("MaxConcurrent", limits.MaxConcurrent)

        match limits.MaxRunDuration with
        | Some d -> writer.WriteNumber("MaxRunDurationSeconds", int64 d.TotalSeconds)
        | None -> writer.WriteNull "MaxRunDurationSeconds"

        writer.WriteNumber("PeriodAllowance", limits.PeriodAllowance)
        writer.WriteEndObject()

    let private readLimits (element: JsonElement) : ComputeBudgetLimits =
        let readInt (name: string) =
            match element.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0

        let readDecimal (name: string) =
            match element.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDecimal()
            | _ -> 0M

        let duration =
            match element.TryGetProperty "MaxRunDurationSeconds" with
            | true, v when v.ValueKind = JsonValueKind.Number -> Some(TimeSpan.FromSeconds(float (v.GetInt64())))
            | _ -> None

        {
            MaxConcurrent = readInt "MaxConcurrent"
            MaxRunDuration = duration
            PeriodAllowance = readDecimal "PeriodAllowance"
        }

    /// Serialise a budget.
    let serialiseBudget (budget: ComputeBudget) : byte[] =
        use ms = new IO.MemoryStream()

        (use writer = new Utf8JsonWriter(ms)
         writer.WriteStartObject()
         writer.WriteNumber("SchemaVersion", SchemaVersion)
         writer.WriteString("Period", ComputeBudgetPeriod.label budget.Period)
         writeLimits writer "Limits" budget.Limits
         writer.WriteStartObject "ClassLimits"

         // Ordinal-sorted so the blob is byte-stable for a given budget —
         // an unstable serialisation would make every no-op write look
         // like a change to an ETag CAS and to a diffing operator.
         for cls, limits in budget.ClassLimits |> Map.toSeq |> Seq.sortBy fst do
             writeLimits writer cls limits

         writer.WriteEndObject()
         writer.WriteEndObject()
         writer.Flush())

        ms.ToArray()

    /// Parse a budget. `None` for a corrupt, foreign or
    /// unknown-version blob — never an exception.
    let deserialiseBudget (bytes: byte[]) : ComputeBudget option =
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory<byte> bytes)
            let root = doc.RootElement

            if root.GetProperty("SchemaVersion").GetInt32() <> SchemaVersion then
                None
            else
                let period =
                    match root.GetProperty("Period").GetString() with
                    | "perpetual" -> ComputeBudgetPeriod.Perpetual
                    | "daily" -> ComputeBudgetPeriod.Daily
                    | _ -> ComputeBudgetPeriod.Monthly

                let classLimits =
                    root.GetProperty "ClassLimits"
                    |> _.EnumerateObject()
                    |> Seq.map (fun p -> p.Name, readLimits p.Value)
                    |> Map.ofSeq

                Some {
                    Period = period
                    Limits = readLimits (root.GetProperty "Limits")
                    ClassLimits = classLimits
                }
        with _ ->
            None

    /// Serialise a usage row.
    let serialiseUsage (usage: ComputeBudgetUsage) : byte[] =
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

    /// Parse a usage row, refusing one that is not for `scopeId` +
    /// `periodKey`.
    ///
    /// The key cross-check is the second half of the GP 4 guarantee: the
    /// blob path already partitions by scope, and this makes a mis-derived
    /// path degrade to "no consumption recorded" rather than to one tenant
    /// spending another's allowance.
    let deserialiseUsage (scopeId: string) (periodKey: string) (bytes: byte[]) : ComputeBudgetUsage option =
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory<byte> bytes)
            let root = doc.RootElement

            let matches =
                root.GetProperty("SchemaVersion").GetInt32() = SchemaVersion
                && root.GetProperty("ScopeId").GetString() = scopeId
                && root.GetProperty("PeriodKey").GetString() = periodKey

            if not matches then
                None
            else
                Some {
                    ScopeId = scopeId
                    PeriodKey = periodKey
                    InFlight = root.GetProperty("InFlight").GetInt32()
                    Spent = root.GetProperty("Spent").GetDecimal()
                    UpdatedAt = DateTime(root.GetProperty("UpdatedAtTicks").GetInt64(), DateTimeKind.Utc)
                }
        with _ ->
            None

/// Phase 451 — the blob-backed `IComputeBudgetStore` registered when
/// `ServerConfig.ComputeBudget = EnabledComputeBudget`.
///
/// `blobs` is the composed `IBlobStorage`; when it also implements
/// `IConditionalBlobStorage` the admission write becomes a cross-replica
/// ETag CAS (see the file header). `logger` is optional — omitting it is
/// silent and behaviour-preserving (GP 11). `clock` exists so period
/// boundaries are testable without waiting for midnight.
type BlobComputeBudgetStore(blobs: IBlobStorage, ?logger: ILogger, ?container: string, ?clock: unit -> DateTime) =

    let container = defaultArg container ComputeBudgetLayout.DefaultContainer
    let now = defaultArg clock (fun () -> DateTime.UtcNow)

    let conditional =
        match box blobs with
        | :? IConditionalBlobStorage as c -> Some c
        | _ -> None

    /// Bounded CAS retries. Small on purpose: contention on ONE scope's
    /// ONE period row is the burst this store exists to bound, and a
    /// caller who loses five races in a row is, in practice, a caller
    /// whose scope is saturated — which is the answer the budget was
    /// going to give anyway. An unbounded retry here would turn a
    /// saturated scope into an unbounded write amplification against the
    /// blob backend.
    let maxCasAttempts = 5

    /// Per-`(scope, period)` write gate. Bounded by the number of
    /// scope+period pairs this process has touched; entries are cheap
    /// (one semaphore) and a period key stops being written to when the
    /// period rolls, so the natural bound is "active scopes", not "runs".
    let gates = ConcurrentDictionary<string, SemaphoreSlim>()

    let gateFor (scopeId: string) (periodKey: string) =
        gates.GetOrAdd(scopeId + "|" + periodKey, fun _ -> new SemaphoreSlim(1, 1))

    let warn (message: string) =
        match logger with
        | Some log -> log.Warn message
        | None -> ()

    /// The stored usage row plus its ETag, or the zero row plus `None`.
    let readUsageWithETag (scopeId: string) (periodKey: string) : Async<ComputeBudgetUsage * string option> = async {
        let name = ComputeBudgetLayout.usageBlob scopeId periodKey

        match conditional with
        | Some cond ->
            match! cond.DownloadWithETag(container, name) with
            | Ok(bytes, etag) ->
                match ComputeBudgetJson.deserialiseUsage scopeId periodKey bytes with
                | Some usage -> return usage, Some etag
                | None ->
                    // Corrupt or foreign: treat as no consumption, but
                    // keep the etag so the repairing write still CASes
                    // against what we read rather than blind-overwriting
                    // a row another writer may have just fixed.
                    warn
                        $"BlobComputeBudgetStore: unreadable usage row for scope '{scopeId}' period '{periodKey}' — treating as zero consumption and rewriting."

                    return ComputeBudgetUsage.empty scopeId periodKey, Some etag
            | Error _ -> return ComputeBudgetUsage.empty scopeId periodKey, None
        | None ->
            match! blobs.Download(container, name) with
            | Ok bytes ->
                match ComputeBudgetJson.deserialiseUsage scopeId periodKey bytes with
                | Some usage -> return usage, None
                | None ->
                    warn
                        $"BlobComputeBudgetStore: unreadable usage row for scope '{scopeId}' period '{periodKey}' — treating as zero consumption and rewriting."

                    return ComputeBudgetUsage.empty scopeId periodKey, None
            | Error _ -> return ComputeBudgetUsage.empty scopeId periodKey, None
    }

    /// Write `usage`, CASing against `expected` when the backend supports
    /// it. `Ok false` means the CAS was refused and the caller should
    /// re-read and re-decide.
    let writeUsage
        (scopeId: string)
        (periodKey: string)
        (expected: string option)
        (usage: ComputeBudgetUsage)
        : Async<bool> =
        async {
            let name = ComputeBudgetLayout.usageBlob scopeId periodKey
            let bytes = ComputeBudgetJson.serialiseUsage usage

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
                    // Infrastructure, not contention. Do not re-decide in
                    // a loop against a backend that is failing — report
                    // and let the caller proceed, for the fail-open
                    // reason in the file header.
                    warn
                        $"BlobComputeBudgetStore: conditional write failed for scope '{scopeId}' period '{periodKey}': {message}. Budget accounting for this submission is not durable."

                    return true
            | None ->
                match! blobs.Upload(container, name, bytes) with
                | Ok _ -> return true
                | Error message ->
                    warn
                        $"BlobComputeBudgetStore: usage write failed for scope '{scopeId}' period '{periodKey}': {message}. Budget accounting for this submission is not durable."

                    return true
        }

    /// Run `mutate` against the live row and persist the result, retrying
    /// the whole decision on a lost CAS. `mutate` returns `Error` to
    /// abandon the write (a refusal), `Ok` to persist.
    let mutateUsage
        (scopeId: string)
        (periodKey: string)
        (mutate: ComputeBudgetUsage -> Result<ComputeBudgetUsage, ComputeBudgetDenial>)
        : Async<Result<ComputeBudgetUsage, ComputeBudgetDenial>> =
        async {
            let gate = gateFor scopeId periodKey
            do! gate.WaitAsync() |> Async.AwaitTask

            try
                let mutable attempt = 0

                let mutable result =
                    Unchecked.defaultof<Result<ComputeBudgetUsage, ComputeBudgetDenial>>

                let mutable settled = false

                while not settled do
                    attempt <- attempt + 1
                    let! current, etag = readUsageWithETag scopeId periodKey

                    match mutate current with
                    | Error denial ->
                        // Refused: nothing is written, so a denial costs
                        // one read and never a blob write.
                        result <- Error denial
                        settled <- true
                    | Ok updated ->
                        let! written = writeUsage scopeId periodKey etag updated

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
                            // line the submitter never sees.
                            result <-
                                Error {
                                    ScopeId = scopeId
                                    SubmitterClass = SubmitterClass.label SubmitterClass.Human
                                    Dimension = ComputeBudgetDimension.label ComputeBudgetDimension.Concurrency
                                    Quota = 0M
                                    Spent = decimal current.InFlight
                                    Requested = 1M
                                    PeriodKey = periodKey
                                }

                            settled <- true

                return result
            finally
                gate.Release() |> ignore
        }

    interface IComputeBudgetStore with
        member _.GetBudget(scopeId: string) = async {
            match! blobs.Download(container, ComputeBudgetLayout.budgetBlob scopeId) with
            | Error _ ->
                // Absent or unreachable — unrestricted. See the file
                // header on why this direction and not the other.
                return ComputeBudget.unrestricted
            | Ok bytes ->
                match ComputeBudgetJson.deserialiseBudget bytes with
                | Some budget -> return budget
                | None ->
                    warn
                        $"BlobComputeBudgetStore: unreadable budget blob for scope '{scopeId}' — treating the scope as unrestricted. Re-save the budget to repair it."

                    return ComputeBudget.unrestricted
        }

        member _.SetBudget(scopeId: string, budget: ComputeBudget) = async {
            match!
                blobs.Upload(
                    container,
                    ComputeBudgetLayout.budgetBlob scopeId,
                    ComputeBudgetJson.serialiseBudget budget
                )
            with
            | Ok _ -> return Ok()
            | Error message -> return Error message
        }

        member _.ReadUsage(scopeId: string, periodKey: string) = async {
            let! usage, _ = readUsageWithETag scopeId periodKey
            return usage
        }

        member _.Admit(scopeId, periodKey, cost, decide) =
            mutateUsage scopeId periodKey (fun current ->
                match decide current with
                | Error denial -> Error denial
                | Ok() ->
                    Ok {
                        current with
                            InFlight = current.InFlight + 1
                            Spent = current.Spent + cost
                            UpdatedAt = now ()
                    })

        member _.Settle(scopeId, periodKey, costAdjustment) = async {
            let! _ =
                mutateUsage scopeId periodKey (fun current ->
                    Ok {
                        current with
                            // Clamped: a negative in-flight count would
                            // silently grant extra concurrency.
                            InFlight = max 0 (current.InFlight - 1)
                            // Spend is likewise floored — an adjustment
                            // larger than the period's recorded spend
                            // means the reservation was written in a
                            // period that has since rolled, and crediting
                            // the NEW period for it would manufacture
                            // allowance out of a clock boundary.
                            Spent = max 0M (current.Spent + costAdjustment)
                            UpdatedAt = now ()
                    })

            return ()
        }

/// Phase 451 — an in-process `IComputeBudgetStore` for tests and
/// single-node development.
///
/// Genuinely atomic (one lock over a dictionary) and genuinely
/// non-durable: everything is lost on restart, which for a concurrency
/// reservation is the *safe* loss — a restart releases every leaked slot.
/// Not registered by compose; a deployment gets `BlobComputeBudgetStore`.
type InMemoryComputeBudgetStore(?clock: unit -> DateTime) =
    let now = defaultArg clock (fun () -> DateTime.UtcNow)
    let budgets = ConcurrentDictionary<string, ComputeBudget>()
    let usage = ConcurrentDictionary<string, ComputeBudgetUsage>()
    let gate = obj ()

    let key (scopeId: string) (periodKey: string) = scopeId + "|" + periodKey

    interface IComputeBudgetStore with
        member _.GetBudget(scopeId: string) = async {
            match budgets.TryGetValue scopeId with
            | true, budget -> return budget
            | _ -> return ComputeBudget.unrestricted
        }

        member _.SetBudget(scopeId: string, budget: ComputeBudget) = async {
            budgets[scopeId] <- budget
            return Ok()
        }

        member _.ReadUsage(scopeId: string, periodKey: string) = async {
            match usage.TryGetValue(key scopeId periodKey) with
            | true, row -> return row
            | _ -> return ComputeBudgetUsage.empty scopeId periodKey
        }

        member _.Admit(scopeId, periodKey, cost, decide) = async {
            return
                lock gate (fun () ->
                    let k = key scopeId periodKey

                    let current =
                        match usage.TryGetValue k with
                        | true, row -> row
                        | _ -> ComputeBudgetUsage.empty scopeId periodKey

                    match decide current with
                    | Error denial -> Error denial
                    | Ok() ->
                        let updated = {
                            current with
                                InFlight = current.InFlight + 1
                                Spent = current.Spent + cost
                                UpdatedAt = now ()
                        }

                        usage[k] <- updated
                        Ok updated)
        }

        member _.Settle(scopeId, periodKey, costAdjustment) = async {
            lock gate (fun () ->
                let k = key scopeId periodKey

                let current =
                    match usage.TryGetValue k with
                    | true, row -> row
                    | _ -> ComputeBudgetUsage.empty scopeId periodKey

                usage[k] <- {
                    current with
                        InFlight = max 0 (current.InFlight - 1)
                        Spent = max 0M (current.Spent + costAdjustment)
                        UpdatedAt = now ()
                })
        }
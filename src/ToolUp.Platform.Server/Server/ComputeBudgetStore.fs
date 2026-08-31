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
    ///
    /// Phase 689: the stored shape is `BudgetUsageJson`'s, which was
    /// lifted from this function — so the bytes are the ones this store
    /// has always written, and a row written before the seam existed reads
    /// back through the shared ledger unchanged.
    let serialiseUsage (usage: ComputeBudgetUsage) : byte[] =
        BudgetUsageJson.serialise (ComputeBudgetUsage.toBudgetUsage usage)

    /// Parse a usage row, refusing one that is not for `scopeId` +
    /// `periodKey`.
    ///
    /// The key cross-check is the second half of the GP 4 guarantee: the
    /// blob path already partitions by scope, and this makes a mis-derived
    /// path degrade to "no consumption recorded" rather than to one tenant
    /// spending another's allowance.
    let deserialiseUsage (scopeId: string) (periodKey: string) (bytes: byte[]) : ComputeBudgetUsage option =
        BudgetUsageJson.deserialise (ComputeBudgetLayout.ledgerKey scopeId periodKey) bytes
        |> Option.map ComputeBudgetUsage.ofBudgetUsage

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

    let warn (message: string) =
        match logger with
        | Some log -> log.Warn message
        | None -> ()

    /// Phase 689 — the consumption half, on the shared ledger.
    ///
    /// Every property this store's header described — the per-key
    /// semaphore, the ETag CAS with bounded retries, the fail-open read,
    /// the exact blob path — is the ledger's, because the ledger is this
    /// code with the seam's types substituted for compute's. The store
    /// keeps the half that is genuinely its own: where the *ceilings*
    /// live, which is a per-domain question the seam deliberately does not
    /// answer.
    ///
    /// The contention refusal is phrased in compute's own vocabulary, so a
    /// lost-CAS refusal reads to a submitter exactly as it did before —
    /// a concurrency refusal, which is what a hot row means here.
    let ledger =
        BlobBudgetLedger(
            blobs,
            ?logger = logger,
            container = container,
            ?clock = clock,
            onContention =
                fun usage -> {
                    Domain = usage.Domain
                    ScopeId = usage.ScopeId
                    ClassLabel = SubmitterClass.label SubmitterClass.Human
                    Dimension = ComputeBudgetDimension.label ComputeBudgetDimension.Concurrency
                    Quota = 0M
                    Spent = decimal usage.InFlight
                    Requested = 1M
                    PeriodKey = usage.PeriodKey
                }
        )
        :> IBudgetLedger

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
            let! usage = ledger.ReadUsage(ComputeBudgetLayout.ledgerKey scopeId periodKey)
            return ComputeBudgetUsage.ofBudgetUsage usage
        }

        member _.Admit(scopeId, periodKey, cost, decide) = async {
            let! reserved =
                ledger.Reserve(
                    ComputeBudgetLayout.ledgerKey scopeId periodKey,
                    cost,
                    fun row ->
                        decide (ComputeBudgetUsage.ofBudgetUsage row)
                        |> Result.mapError ComputeBudgetDenial.toBudgetDenial
                )

            return
                reserved
                |> Result.map ComputeBudgetUsage.ofBudgetUsage
                |> Result.mapError ComputeBudgetDenial.ofBudgetDenial
        }

        member _.Settle(scopeId, periodKey, costAdjustment) =
            // The in-flight and spend clamps live in `BudgetUsage.settle`,
            // shared by every ledger so two of them cannot drift on what
            // settling means.
            ledger.Release(ComputeBudgetLayout.ledgerKey scopeId periodKey, costAdjustment)

/// Phase 451 — an in-process `IComputeBudgetStore` for tests and
/// single-node development.
///
/// Genuinely atomic (one lock over a dictionary) and genuinely
/// non-durable: everything is lost on restart, which for a concurrency
/// reservation is the *safe* loss — a restart releases every leaked slot.
/// Not registered by compose; a deployment gets `BlobComputeBudgetStore`.
type InMemoryComputeBudgetStore(?clock: unit -> DateTime) =
    let budgets = ConcurrentDictionary<string, ComputeBudget>()

    /// Phase 689 — the consumption half, on the shared in-memory ledger.
    let ledger = InMemoryBudgetLedger(?clock = clock) :> IBudgetLedger

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
            let! row = ledger.ReadUsage(ComputeBudgetLayout.ledgerKey scopeId periodKey)
            return ComputeBudgetUsage.ofBudgetUsage row
        }

        member _.Admit(scopeId, periodKey, cost, decide) = async {
            let! reserved =
                ledger.Reserve(
                    ComputeBudgetLayout.ledgerKey scopeId periodKey,
                    cost,
                    fun row ->
                        decide (ComputeBudgetUsage.ofBudgetUsage row)
                        |> Result.mapError ComputeBudgetDenial.toBudgetDenial
                )

            return
                reserved
                |> Result.map ComputeBudgetUsage.ofBudgetUsage
                |> Result.mapError ComputeBudgetDenial.ofBudgetDenial
        }

        member _.Settle(scopeId, periodKey, costAdjustment) =
            ledger.Release(ComputeBudgetLayout.ledgerKey scopeId periodKey, costAdjustment)
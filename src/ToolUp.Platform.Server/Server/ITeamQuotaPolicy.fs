namespace ToolUp.Platform.Usage

open System

/// Per-scope (per-team) compute quota policy. Decorators wrapping
/// `IAIProvider.SendMessage`, `IJobScheduler.TriggerOnce`, and
/// `IEmbeddingProvider.GenerateEmbedding` consult this before the
/// actual call, returning a typed `QuotaBreached` error rather than
/// throwing. Phase 9 already ships `DefaultTeamStorageQuotaBytes` +
/// `RateLimit` (request rate); this fills the compute gap.
///
/// The default impl reads the policy from the reserved `_platform.usage`
/// `ModuleConfigSchema` via `IConfigStore`. Missing config = unrestricted
/// (`Result.Ok ()`), so deployments that don't enforce quotas pay
/// nothing.
///
/// Phase 9c portability audit:
///   1. Identity by value: `scopeId: string`. ✓
///   2. Async at every boundary: both methods return `Async<_>`. ✓
///   3. Retry/backoff as data: not applicable — quota is point-in-time.
///   4. Stateless handlers: the default impl reads `IConfigStore` per
///      call; no in-memory cursor that survives restarts. ✓
///   5. No cross-shard ordering: per-scope decisions are independent. ✓
///   6. Precision lower-bound: counts and budgets are `decimal` /
///      `int`; concurrency is `int`. No timing primitives. ✓
type ITeamQuotaPolicy =
    /// Check whether dispatching one more concurrent job for `scopeId`
    /// would breach the configured `MaxConcurrentJobs`. `currentInFlight`
    /// is the live count the caller has — the decorator inspects
    /// `IJobStore` / its own counter and passes it in.
    abstract CheckJobConcurrency: scopeId: string * currentInFlight: int -> Async<Result<unit, QuotaBreached>>

    /// Check whether `requested` more units of `kind` (typically
    /// `ResourceKinds.aiTokensInput` or `ResourceKinds.aiTokensOutput`)
    /// would breach the per-day or per-month token budget.
    /// Implementation reads usage history from `IUsageLog` to compute
    /// current consumption.
    abstract CheckTokenBudget: scopeId: string * kind: string * requested: decimal -> Async<Result<unit, QuotaBreached>>

/// SDK-default no-op `ITeamQuotaPolicy`. Both checks return `Ok ()`
/// unconditionally. Registered when `ServerConfig.UsageMetering =
/// NoUsageMetering` so decorators can resolve the service
/// unconditionally — `ITeamQuotaPolicy` is never `null` in DI when the
/// SDK is composed. Mirrors the `NoOpUsageLog` and `NoOpAuditLog`
/// patterns.
type NoOpTeamQuotaPolicy() =
    interface ITeamQuotaPolicy with
        member _.CheckJobConcurrency(_scopeId, _currentInFlight) = async.Return(Ok())

        member _.CheckTokenBudget(_scopeId, _kind, _requested) = async.Return(Ok())

/// Reserved `IConfigStore` key for the per-team usage / quota schema.
/// Distinct from the general `_platform` schema — quota fields have a
/// distinct lifecycle (admin-only, billing-coupled) and crowding them
/// into the general settings schema would couple unrelated concerns.
[<RequireQualifiedAccess>]
module UsageConfigKey =
    [<Literal>]
    let value = "_platform.usage"

    /// Field key — maximum concurrent jobs per team. `int`, default 0
    /// (= unrestricted).
    [<Literal>]
    let maxConcurrentJobs = "MaxConcurrentJobs"

    /// Field key — maximum AI input tokens per UTC day. `decimal`,
    /// default 0 (= unrestricted).
    [<Literal>]
    let maxAITokensPerDay = "MaxAITokensPerDay"

    /// Field key — maximum AI input tokens per UTC month. `decimal`,
    /// default 0 (= unrestricted).
    [<Literal>]
    let maxAITokensPerMonth = "MaxAITokensPerMonth"
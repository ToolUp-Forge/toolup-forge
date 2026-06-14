module ToolUp.Platform.TeamQuotaPolicy

open System
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Usage

// ─── Phase 9d — Team quota policy default impl + decorators ──────
//
// `BlobBackedTeamQuotaPolicy` reads the per-scope quota from
// `IConfigStore` keyed by the reserved `_platform.usage` schema. The
// schema's three fields (`MaxConcurrentJobs`, `MaxAITokensPerDay`,
// `MaxAITokensPerMonth`) all default to 0 = unrestricted. Missing
// config = unrestricted (`Result.Ok ()`).
//
// The token-budget check sums the scope's history from `IUsageLog` for
// the current UTC day / month, adds the requested quantity, and
// compares against the configured ceiling. Reads bypass the channel —
// query paths read directly from rollup blobs.
//
// The decorators (`QuotaGatedAIProvider`, `QuotaGatedJobScheduler`)
// call into the policy before dispatching the wrapped operation. On
// `Ok`, the underlying call proceeds. On `Error QuotaBreached`, the
// decorator translates the breach into the wrapped interface's typed
// failure shape — never throws.

/// Simple 4-chars-per-token heuristic for pre-call budget estimation.
/// Advisory only — the post-call metered value is authoritative for
/// billing. Drift between estimate and actual is acceptable; the
/// contract is "do not block legitimate calls because the estimator
/// was off by a few tokens", not "guarantee a tight bound".
module RequestTokenEstimator =
    /// Estimate a request's input token count from its raw text
    /// content. The 4-chars-per-token ratio is industry-standard for
    /// English prose; non-English / code-heavy prompts drift higher.
    let estimateChars (totalChars: int) : decimal = decimal totalChars / 4M

    /// Estimate a request's input token count from a list of
    /// `(role, text)` pairs. The role label is included because a
    /// real tokeniser would tokenise each turn separately with
    /// boundary tokens; the estimator ignores boundaries but counts
    /// role-name characters as a small overhead.
    let estimateMessages (messages: (string * string) list) : decimal =
        messages
        |> List.sumBy (fun (role, text) -> role.Length + text.Length)
        |> estimateChars

// ─── Default blob-backed policy ──────────────────────────────────

/// Reads quota config via `IConfigStore` against the reserved
/// `_platform.usage` schema; reads usage history via `IUsageLog`.
/// Missing config = unrestricted.
type BlobBackedTeamQuotaPolicy(configStore: IConfigStore, usageLog: IUsageLog) =

    /// Look up an `int` quota value from the persisted raw config map.
    /// Missing or unparseable returns `0` (= unrestricted).
    let readInt (raw: Map<string, string>) (key: string) : int =
        match raw |> Map.tryFind key with
        | Some s ->
            match Int32.TryParse s with
            | true, v -> v
            | _ -> 0
        | None -> 0

    /// Look up a `decimal` quota value from the persisted raw config
    /// map. Missing or unparseable returns `0M` (= unrestricted).
    let readDecimal (raw: Map<string, string>) (key: string) : decimal =
        match raw |> Map.tryFind key with
        | Some s ->
            match Decimal.TryParse s with
            | true, v -> v
            | _ -> 0M
        | None -> 0M

    let readQuotaConfig (scopeId: string) = async {
        // Construct a minimal scope handle for IConfigStore — the
        // store keys on `scope.ScopeId`. Persistence is irrelevant to
        // the lookup because the blob-backed store keys directly on
        // scope id (config-store implementations that scope on
        // persistence are wrong for shared infra config).
        let scope: StorageScope = {
            ScopeId = scopeId
            Container = "_platform"
            Persist = true
        }

        let! raw = configStore.GetRaw(scope, UsageConfigKey.value)

        return {|
            MaxConcurrentJobs = readInt raw UsageConfigKey.maxConcurrentJobs
            MaxAITokensPerDay = readDecimal raw UsageConfigKey.maxAITokensPerDay
            MaxAITokensPerMonth = readDecimal raw UsageConfigKey.maxAITokensPerMonth
        |}
    }

    /// Sum quantity for a kind across the requested UTC date range.
    let sumKindInRange (scopeId: string) (kind: string) (range: DateTime * DateTime) = async {
        let! records = usageLog.Query(scopeId, Some kind, Some range)
        return records |> List.sumBy _.Quantity
    }

    interface ITeamQuotaPolicy with
        member _.CheckJobConcurrency(scopeId, currentInFlight) = async {
            let! cfg = readQuotaConfig scopeId

            if cfg.MaxConcurrentJobs <= 0 then
                return Ok()
            elif currentInFlight + 1 > cfg.MaxConcurrentJobs then
                return
                    Error {
                        Kind = "concurrency"
                        Limit = decimal cfg.MaxConcurrentJobs
                        Requested = decimal (currentInFlight + 1)
                        ScopeId = scopeId
                    }
            else
                return Ok()
        }

        member _.CheckTokenBudget(scopeId, kind, requested) = async {
            let! cfg = readQuotaConfig scopeId

            // Both budgets are independent — fail on the first breach.
            // Day budget first because it's the tighter window and
            // therefore the more interactive feedback for the user.
            let now = DateTime.UtcNow
            let dayStart = now.Date
            let dayEnd = dayStart.AddDays(1.0).AddMilliseconds(-1.0)

            let monthStart = DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            let monthEnd = monthStart.AddMonths(1).AddMilliseconds(-1.0)

            if cfg.MaxAITokensPerDay > 0M then
                let! soFarDay = sumKindInRange scopeId kind (dayStart, dayEnd)

                if soFarDay + requested > cfg.MaxAITokensPerDay then
                    return
                        Error {
                            Kind = kind
                            Limit = cfg.MaxAITokensPerDay
                            Requested = soFarDay + requested
                            ScopeId = scopeId
                        }
                elif cfg.MaxAITokensPerMonth > 0M then
                    let! soFarMonth = sumKindInRange scopeId kind (monthStart, monthEnd)

                    if soFarMonth + requested > cfg.MaxAITokensPerMonth then
                        return
                            Error {
                                Kind = kind
                                Limit = cfg.MaxAITokensPerMonth
                                Requested = soFarMonth + requested
                                ScopeId = scopeId
                            }
                    else
                        return Ok()
                else
                    return Ok()
            elif cfg.MaxAITokensPerMonth > 0M then
                let! soFarMonth = sumKindInRange scopeId kind (monthStart, monthEnd)

                if soFarMonth + requested > cfg.MaxAITokensPerMonth then
                    return
                        Error {
                            Kind = kind
                            Limit = cfg.MaxAITokensPerMonth
                            Requested = soFarMonth + requested
                            ScopeId = scopeId
                        }
                else
                    return Ok()
            else
                return Ok()
        }

// ─── Decorator: IAIProvider ──────────────────────────────────────
//
// `QuotaGatedAIProvider` consults `ITeamQuotaPolicy.CheckTokenBudget`
// before delegating to the inner provider. On breach, returns a typed
// `AIProviderError.PermanentClient` carrying the quota-breach summary
// — the agent loop classifies this as catastrophic (no retry) and
// surfaces the message to the user.
//
// **Why `IAIProvider`, not `IAIProviderFactory`:** quota check fires
// where the cost is paid (per-turn, inside the agent loop). The
// metering middleware sits at the factory layer (per-request
// resolution) where it captures the BYOK / PlatformManaged origin
// once. Different lifecycles, different layers — Plan agent
// validation point #4.

/// Wraps an `IAIProvider` with a per-call token-budget check via
/// `ITeamQuotaPolicy`. The pre-call estimate is advisory; the post-
/// call metered value (recorded by the separate metering middleware)
/// is authoritative for billing.
type QuotaGatedAIProvider(inner: IAIProvider, scopeId: string, quotaPolicy: ITeamQuotaPolicy) =

    let estimateInputTokens (messages: AIProviderMessage list) : decimal =
        let asPair (m: AIProviderMessage) = (m.Role, m.Content)

        messages |> List.map asPair |> RequestTokenEstimator.estimateMessages

    interface IAIProvider with
        member _.Capabilities = inner.Capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            let estimated = estimateInputTokens messages
            let! gate = quotaPolicy.CheckTokenBudget(scopeId, ResourceKinds.aiTokensInput, estimated)

            match gate with
            | Error breach ->
                let msg =
                    sprintf
                        "Token quota exceeded (%s): limit=%M requested=%M scope=%s"
                        breach.Kind
                        breach.Limit
                        breach.Requested
                        breach.ScopeId

                return Error(AIProviderError.PermanentClient(429, msg))
            | Ok() -> return! inner.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)
        }

        // Phase 67b — structured-output path is quota-gated identically to
        // SendMessage: pre-call advisory estimate against the same
        // aiTokensInput budget, then delegate to the inner provider.
        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) = async {
            let estimated = estimateInputTokens messages
            let! gate = quotaPolicy.CheckTokenBudget(scopeId, ResourceKinds.aiTokensInput, estimated)

            match gate with
            | Error breach ->
                let msg =
                    sprintf
                        "Token quota exceeded (%s): limit=%M requested=%M scope=%s"
                        breach.Kind
                        breach.Limit
                        breach.Requested
                        breach.ScopeId

                return Error(AIProviderError.PermanentClient(429, msg))
            | Ok() -> return! inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)
        }

// ─── Decorator: IJobScheduler ────────────────────────────────────
//
// `QuotaGatedJobScheduler` wraps `IJobScheduler.TriggerOnce` and
// consults `ITeamQuotaPolicy.CheckJobConcurrency`. Cron-triggered
// jobs and event-triggered jobs bypass the gate by design — those
// firings are operator-configured (the registration surface), not
// caller-driven. The `currentInFlight` parameter is the policy
// caller's responsibility to compute; the SDK default decorator
// passes `0` because reliably tracking in-flight without an
// `IJobStore.ListRunningRuns` query would require a Phase 9c-grade
// per-scope counter tied to job-completion events. Deployments
// needing tight gating supply their own decorator that consults
// `IJobStore` directly. The interface is the portable contract.

/// Wraps an `IJobScheduler` so that `TriggerOnce` consults the quota
/// policy before dispatching. Other methods delegate unchanged.
type QuotaGatedJobScheduler(inner: IJobScheduler, quotaPolicy: ITeamQuotaPolicy) =

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = inner.RegisterHandler(name, handler)

        member _.RegisterHandlerAsync(name, handler) =
            inner.RegisterHandlerAsync(name, handler)

        member _.Schedule(registration) = inner.Schedule(registration)

        member _.Cancel(scopeId, jobId) = inner.Cancel(scopeId, jobId)

        member _.Disable(scopeId, jobId) = inner.Disable(scopeId, jobId)

        member _.Enable(scopeId, jobId) = inner.Enable(scopeId, jobId)

        member _.Get(scopeId, jobId) = inner.Get(scopeId, jobId)

        member _.ListJobs(scopeId) = inner.ListJobs(scopeId)

        member _.GetRecentRuns(scopeId, jobId, count) =
            inner.GetRecentRuns(scopeId, jobId, count)

        member _.TriggerOnce(scopeId, jobId, byUserId) = async {
            let! gate = quotaPolicy.CheckJobConcurrency(scopeId, 0)

            match gate with
            | Error breach ->
                return
                    Error(
                        sprintf
                            "Job concurrency quota exceeded: limit=%M requested=%M scope=%s"
                            breach.Limit
                            breach.Requested
                            breach.ScopeId
                    )
            | Ok() -> return! inner.TriggerOnce(scopeId, jobId, byUserId)
        }

        member _.NotifyEventWritten(scopeId, eventType, eventId) =
            inner.NotifyEventWritten(scopeId, eventType, eventId)
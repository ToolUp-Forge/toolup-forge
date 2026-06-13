// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open System.Text.Json
open System.Text.RegularExpressions
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.Metrics
open ToolUp.Platform.Secrets

// ─── Phase 10h — in-process IOAuthTokenRefresher ──────────────────
//
// SDK-default `IOAuthTokenRefresher` registered when
// `ServerConfig.OAuthRefresher = EnabledOAuthRefresher`. Performs
// vanilla OAuth 2.0 refresh-token grants (RFC 6749 §6) against the
// upstream `TokenEndpoint`, persists rotated tokens through
// `ISecretStore`, emits the `_platform.oauth.refresh` audit family +
// `toolup.oauth.refresh.*` metrics.
//
// **Scheduling model.** Each registered descriptor schedules a
// `CronTrigger "*/5 * * * *"` job under handler
// `_platform.oauth.refresh`. The handler short-circuits when the
// cached access-token expiry is more than `ScheduleAheadOfExpiry`
// away (typical: 95+% of dispatches are short-circuit no-ops). When
// the window opens the handler calls `IOAuthTokenRefresher.RefreshNow`
// which does the actual HTTP work. The 5-minute tick is below the
// `JobPrecision.Minute` floor of the in-process scheduler (so the
// handler is guaranteed to wake at least once inside the
// `ScheduleAheadOfExpiry` window) and large enough that wasted
// no-op dispatches are cheap (one `ISecretStore.GetSecret` + a
// timestamp compare).
//
// **Interactive refresh.** Connectors observing a 401 from a cached
// access token can call `RefreshNow` directly; the substrate runs
// the HTTP refresh unconditionally regardless of scheduler-window
// state, persists the new tokens, and returns the outcome.
// `RefreshNow` is concurrency-safe — `ISecretStore.SetSecret`
// implementations are last-write-wins, so two near-simultaneous
// refreshes race for the latest token but never corrupt either key.
//
// **Statelessness across restarts.** In-memory descriptor registry
// is purely a read-through cache for admin-UI calls
// (`GetDescriptor` / `ListDescriptors`). Persistent state lives in
// `IJobScheduler`-backed JobDefinitions; the handler reads the
// descriptor from `JobContext.Payload` on every dispatch. After
// process restart `Recover` rebuilds the in-memory cache by
// listing platform-scope jobs.
//
// **Six-rule portability audit (GP 12).**
//   1. Identity by value      — descriptors + secret-store keys are
//                                strings; the registry is keyed by
//                                `(Provider, ConfigId)`.
//   2. Async at boundary      — all interface methods return
//                                `Async<_>`.
//   3. Retry as data          — `OAuthRefreshResult` discriminates;
//                                the handler reads the tag and
//                                returns the corresponding
//                                `JobResult`.
//   4. Stateless handler      — `OAuthRefreshJobHandler` resolves
//                                the descriptor from
//                                `JobContext.Payload`; no in-memory
//                                state required to dispatch.
//   5. No cross-shard ordering — refresh ordering is per-descriptor
//                                via the `(Provider, ConfigId)`
//                                `Idempotency.Key`; no cross-key
//                                ordering claimed.
//   6. Precision at lower bound — `JobPrecision.Minute` matches the
//                                in-process scheduler floor; the
//                                5-minute cron is well above it.

module InProcessOAuthTokenRefresher =

    /// Job-scheduler handler name under which the refresh job is
    /// registered. Companion code should reference this constant
    /// rather than re-typing the string.
    [<Literal>]
    let HandlerName = "_platform.oauth.refresh"

    /// Cron expression for the recurring refresh tick. 5 minutes is
    /// the granularity at which the handler wakes; it short-circuits
    /// unless the cached access-token expiry is inside
    /// `descriptor.ScheduleAheadOfExpiry`.
    [<Literal>]
    let RefreshCronExpression = "*/5 * * * *"

    /// Default retry policy for refresh jobs. 5 attempts over
    /// ~15 minutes (30s → 60s → 120s → 240s → 480s, capped at
    /// `MaxBackoff`). On exhaustion the handler dead-letters and
    /// emits `OAuthRefreshDeadLettered`.
    let DefaultRetryPolicy: JobRetryPolicy = {
        MaxAttempts = 5
        InitialBackoff = TimeSpan.FromSeconds 30.0
        MaxBackoff = TimeSpan.FromMinutes 5.0
        DeadLetterDestination = None
    }

    // ─── Metric names ────────────────────────────────────────────

    [<Literal>]
    let MetricAttemptedTotal = "toolup.oauth.refresh.attempted_total"

    [<Literal>]
    let MetricSucceededTotal = "toolup.oauth.refresh.succeeded_total"

    [<Literal>]
    let MetricFailedTotal = "toolup.oauth.refresh.failed_total"

    [<Literal>]
    let MetricLatencyMs = "toolup.oauth.refresh.latency_ms"

    // ─── Internal helpers ────────────────────────────────────────

    /// STJ options for descriptor (de)serialisation — uses
    /// `FableConverters` so the payload shape matches the SDK
    /// convention everywhere else. Options are private; the
    /// helpers below are the public surface.
    let private jsonOptions = FableConverters.create ()

    /// Serialise a descriptor to a JSON string (for
    /// `JobRegistration.Payload`).
    let serializeDescriptor (descriptor: OAuthRefreshDescriptor) : string =
        JsonSerializer.Serialize(descriptor, jsonOptions)

    /// Deserialise a descriptor from a JSON string. Returns `None`
    /// when the payload is unparseable; the job handler treats this
    /// as `JobResult.PermanentFailure` (the JobDefinition is
    /// structurally broken).
    let tryDeserializeDescriptor (payload: string) : OAuthRefreshDescriptor option =
        try
            Some(JsonSerializer.Deserialize<OAuthRefreshDescriptor>(payload, jsonOptions))
        with _ ->
            None

    /// Build the `application/x-www-form-urlencoded` body for an
    /// OAuth 2.0 refresh-token grant request (RFC 6749 §6).
    let private buildRefreshFormBody (clientId: string) (clientSecret: string) (refreshToken: string) : string =
        let encode = WebUtility.UrlEncode

        sprintf
            "grant_type=refresh_token&client_id=%s&client_secret=%s&refresh_token=%s"
            (encode clientId)
            (encode clientSecret)
            (encode refreshToken)

    /// Parse a successful 200-status JSON body from the token
    /// endpoint. Returns `(accessToken, expiresIn, optional new
    /// refreshToken)` on success; raises on unparseable JSON or
    /// missing required fields (caller maps the raise to
    /// `PermanentError`).
    let private parseRefreshSuccess (body: string) : string * int * string option =
        use doc = JsonDocument.Parse body
        let root = doc.RootElement
        let access = root.GetProperty("access_token").GetString()
        let expiresIn = root.GetProperty("expires_in").GetInt32()

        let rotated =
            match root.TryGetProperty("refresh_token") with
            | true, el when el.ValueKind = JsonValueKind.String -> Some(el.GetString())
            | _ -> None

        access, expiresIn, rotated

    /// Detect an OAuth 2.0 `invalid_grant` response (RFC 6749 §5.2),
    /// which the substrate maps to `TokenInvalidatedByProvider`.
    /// Provider variants (`invalid_token`, `unauthorized_client` on
    /// some providers) are not folded in here — providers that need
    /// that mapping override the refresher.
    let private isInvalidGrantResponse (body: string) : bool =
        try
            use doc = JsonDocument.Parse body

            match doc.RootElement.TryGetProperty("error") with
            | true, el when el.ValueKind = JsonValueKind.String -> el.GetString() = "invalid_grant"
            | _ -> false
        with _ ->
            false

    /// Phase 129/TIDY-UP — scrub an upstream token-endpoint response body
    /// before it lands in an `OAuthRefreshResult` reason / audit row. Some
    /// providers reflect request context (incl. the submitted refresh
    /// token / client_secret) in their `error_description`; redact long
    /// token-shaped runs and cap length so a secret never reaches the
    /// durable audit log. The pattern is linear (no nested quantifier — no
    /// catastrophic backtracking).
    let internal scrubUpstreamBody (body: string) : string =
        if String.IsNullOrEmpty body then
            ""
        else
            let redacted = Regex.Replace(body, @"[A-Za-z0-9_\-\.]{20,}", "<redacted>")

            if redacted.Length > 256 then
                redacted.Substring(0, 256) + "…(truncated)"
            else
                redacted

    // ─── Implementation ─────────────────────────────────────────

    /// SDK-default `IOAuthTokenRefresher`. Hand-rolled OAuth 2.0
    /// refresh against the descriptor's `TokenEndpoint`. Connectors
    /// with non-standard refresh semantics inject their own
    /// `IOAuthTokenRefresher` over DI; the SDK does not re-register
    /// its default in that case.
    ///
    /// `httpClient` is captured at construction; tests inject a
    /// fake `HttpMessageHandler` via the standard `new HttpClient(handler)`
    /// pattern. Production composition uses the singleton
    /// `HttpClient` registered by `services.AddHttpClient`.
    type Impl
        (
            jobScheduler: IJobScheduler,
            secretStore: ISecretStore,
            auditLog: IAuditLog,
            metricsSink: IMetricsSink,
            rateLimiter: IRateLimiter,
            httpClient: HttpClient,
            logger: ILogger
        ) =

        let descriptors = ConcurrentDictionary<string, OAuthRefreshDescriptor>()

        let tagsFor (descriptor: OAuthRefreshDescriptor) : Map<string, string> =
            Map.ofList [ "provider", descriptor.Provider ]

        let recordMetrics (descriptor: OAuthRefreshDescriptor) (succeeded: bool) (elapsedMs: int64) =
            let tags = tagsFor descriptor
            metricsSink.Increment(MetricAttemptedTotal, tags)

            metricsSink.Increment(
                (if succeeded then
                     MetricSucceededTotal
                 else
                     MetricFailedTotal),
                tags
            )

            metricsSink.Record(MetricLatencyMs, float elapsedMs, tags)

        let emitRefreshedAudit
            (descriptor: OAuthRefreshDescriptor)
            (newExpiry: DateTimeOffset)
            (elapsedMs: int64)
            : Async<unit> =
            auditLog.Record(
                descriptor.ScopeId,
                OAuthTokenRefreshed {
                    Provider = descriptor.Provider
                    ConfigId = descriptor.ConfigId
                    ScopeId = descriptor.ScopeId
                    NewExpiry = newExpiry.UtcDateTime
                    Attempt = 1
                    ElapsedMs = elapsedMs
                }
            )

        let emitInvalidatedAudit (descriptor: OAuthRefreshDescriptor) (reason: string) : Async<unit> =
            auditLog.Record(
                descriptor.ScopeId,
                OAuthRefreshTokenInvalidated {
                    Provider = descriptor.Provider
                    ConfigId = descriptor.ConfigId
                    ScopeId = descriptor.ScopeId
                    Reason = reason
                }
            )

        /// Run one refresh attempt against the descriptor's
        /// upstream. Side effect: on `Refreshed` persists the new
        /// access token + expiry to `ISecretStore`, rotates the
        /// refresh token when the upstream rotated it, and emits
        /// the `OAuthTokenRefreshed` audit; on
        /// `TokenInvalidatedByProvider` emits
        /// `OAuthRefreshTokenInvalidated`. Per-attempt transient
        /// failures + dead-letter records are the job handler's
        /// responsibility.
        let attemptRefresh (descriptor: OAuthRefreshDescriptor) : Async<OAuthRefreshResult> = async {
            let sw = Stopwatch.StartNew()

            let rateLimitKey: RateLimitKey = {
                ScopeId = descriptor.ScopeId
                Provider = descriptor.Provider
                SubKey = Some "refresh"
            }

            let! rateDecision = rateLimiter.Wait rateLimitKey

            match rateDecision with
            | Refused reason ->
                sw.Stop()
                recordMetrics descriptor false sw.ElapsedMilliseconds
                return TransientError(sprintf "rate-limited: %s" reason)
            | _ ->

                let! clientSecret = secretStore.GetSecret(descriptor.ScopeId, descriptor.ClientSecretKey)
                let! refreshToken = secretStore.GetSecret(descriptor.ScopeId, descriptor.RefreshTokenKey)

                match clientSecret, refreshToken with
                | None, _ ->
                    sw.Stop()
                    recordMetrics descriptor false sw.ElapsedMilliseconds
                    return PermanentError(sprintf "client secret missing at key '%s'" descriptor.ClientSecretKey)
                | _, None ->
                    sw.Stop()
                    recordMetrics descriptor false sw.ElapsedMilliseconds
                    return PermanentError(sprintf "refresh token missing at key '%s'" descriptor.RefreshTokenKey)
                | Some cs, Some rt ->
                    try
                        let body = buildRefreshFormBody descriptor.ClientId cs rt
                        use req = new HttpRequestMessage(HttpMethod.Post, descriptor.TokenEndpoint)
                        req.Content <- new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
                        let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                        let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

                        if resp.IsSuccessStatusCode then
                            try
                                let access, expiresIn, rotated = parseRefreshSuccess respBody
                                let newExpiry = DateTimeOffset.UtcNow.AddSeconds(float expiresIn)

                                let! accessWrite =
                                    secretStore.SetSecret(
                                        descriptor.ScopeId,
                                        OAuthRefreshDescriptor.accessTokenKey descriptor,
                                        access
                                    )

                                let! expiryWrite =
                                    secretStore.SetSecret(
                                        descriptor.ScopeId,
                                        OAuthRefreshDescriptor.accessExpiryKey descriptor,
                                        newExpiry.ToString("o")
                                    )

                                let! rotatedWrite =
                                    match rotated with
                                    | Some r -> secretStore.SetSecret(descriptor.ScopeId, descriptor.RefreshTokenKey, r)
                                    | None -> async { return Ok() }

                                sw.Stop()
                                let elapsedMs = sw.ElapsedMilliseconds
                                recordMetrics descriptor true elapsedMs

                                match accessWrite, expiryWrite, rotatedWrite with
                                | Ok(), Ok(), Ok() ->
                                    do! emitRefreshedAudit descriptor newExpiry elapsedMs
                                    return Refreshed newExpiry
                                | Error e, _, _
                                | _, Error e, _
                                | _, _, Error e ->
                                    // Secret-store write failure is transient — the
                                    // refresh itself succeeded but the persistence
                                    // path is broken (write-locked, network blip).
                                    // Retry will re-fetch + re-persist on the next
                                    // attempt; we deliberately do not emit
                                    // OAuthTokenRefreshed because the new token never
                                    // reached the durable store.
                                    return TransientError(sprintf "secret-store write: %s" e)
                            with ex ->
                                sw.Stop()
                                recordMetrics descriptor false sw.ElapsedMilliseconds
                                return PermanentError(sprintf "response parse: %s" ex.Message)
                        elif isInvalidGrantResponse respBody then
                            sw.Stop()
                            recordMetrics descriptor false sw.ElapsedMilliseconds
                            do! emitInvalidatedAudit descriptor "invalid_grant"
                            return TokenInvalidatedByProvider
                        elif int resp.StatusCode >= 500 then
                            sw.Stop()
                            recordMetrics descriptor false sw.ElapsedMilliseconds

                            return
                                TransientError(
                                    sprintf "upstream %d: %s" (int resp.StatusCode) (scrubUpstreamBody respBody)
                                )
                        else
                            sw.Stop()
                            recordMetrics descriptor false sw.ElapsedMilliseconds

                            return
                                PermanentError(
                                    sprintf "upstream %d: %s" (int resp.StatusCode) (scrubUpstreamBody respBody)
                                )
                    with
                    | :? HttpRequestException as ex ->
                        sw.Stop()
                        recordMetrics descriptor false sw.ElapsedMilliseconds
                        return TransientError(sprintf "network: %s" ex.Message)
                    | :? TaskCanceledException as ex ->
                        sw.Stop()
                        recordMetrics descriptor false sw.ElapsedMilliseconds
                        return TransientError(sprintf "timeout: %s" ex.Message)
                    | ex ->
                        sw.Stop()
                        recordMetrics descriptor false sw.ElapsedMilliseconds
                        return TransientError(sprintf "unexpected: %s" ex.Message)
        }

        /// Schedule (or re-schedule) the cron job for a descriptor.
        let scheduleRefreshJob (descriptor: OAuthRefreshDescriptor) : Async<unit> = async {
            let key = OAuthRefreshDescriptor.key descriptor

            let registration: JobRegistration = {
                ScopeId = "_platform"
                Handler = HandlerName
                Payload = serializeDescriptor descriptor
                Trigger = CronTrigger RefreshCronExpression
                Idempotency = Some { Key = key; TtlSeconds = 60 }
                RetryPolicy = DefaultRetryPolicy
                ShardKey = Some descriptor.Provider
                Precision = Minute
                CreatedBy = "system"
                Tags =
                    Map.ofList [
                        "_platform.oauth.refresh.provider", descriptor.Provider
                        "_platform.oauth.refresh.configId", descriptor.ConfigId
                    ]
            }

            let! result = jobScheduler.Schedule registration

            match result with
            | Ok _ -> ()
            | Error err -> logger.Warn(sprintf "[OAuthTokenRefresher] schedule failed for %s: %A" key err)
        }

        /// Cancel every scheduled refresh job for a descriptor key.
        /// Best-effort — failures are logged at `Warn`.
        let cancelRefreshJob (descriptorKey: string) : Async<unit> = async {
            try
                let! jobs = jobScheduler.ListJobs "_platform"

                let matching =
                    jobs
                    |> List.filter (fun j ->
                        j.Handler = HandlerName
                        && (j.Idempotency |> Option.exists (fun i -> i.Key = descriptorKey)))

                for job in matching do
                    do! jobScheduler.Cancel("_platform", job.JobId)
            with ex ->
                logger.Warn(sprintf "[OAuthTokenRefresher] cancel failed for %s: %s" descriptorKey ex.Message)
        }

        /// Rebuild the in-memory descriptor registry from persisted
        /// JobDefinitions. Called by compose at startup so admin-UI
        /// reads (`GetDescriptor` / `ListDescriptors`) see existing
        /// descriptors even before any connector re-registers in
        /// the new process.
        member _.Recover() : Async<unit> = async {
            try
                let! jobs = jobScheduler.ListJobs "_platform"

                let recovered =
                    jobs
                    |> List.filter (fun j -> j.Handler = HandlerName)
                    |> List.choose (fun j -> tryDeserializeDescriptor j.Payload)

                for descriptor in recovered do
                    descriptors[OAuthRefreshDescriptor.key descriptor] <- descriptor

                logger.Info(
                    sprintf "[OAuthTokenRefresher] recovered %d descriptor(s) from JobScheduler" recovered.Length
                )
            with ex ->
                logger.Warn(sprintf "[OAuthTokenRefresher] recovery failed: %s" ex.Message)
        }

        /// Expose the in-memory descriptor lookup for the job
        /// handler. The handler uses this on every dispatch to map
        /// `(provider, configId)` to a descriptor; on a cold cache
        /// miss the handler falls back to deserialising the payload
        /// from `JobContext.Payload`.
        member _.TryGetDescriptor(provider: string, configId: string) : OAuthRefreshDescriptor option =
            let key = sprintf "%s:%s" provider configId

            match descriptors.TryGetValue key with
            | true, d -> Some d
            | _ -> None

        /// Surface the underlying attempt-refresh for the job
        /// handler. Bypasses the registered-descriptor lookup —
        /// the handler resolves the descriptor itself (so a
        /// recovered descriptor on a cold registry is still
        /// dispatchable).
        member _.AttemptRefresh(descriptor: OAuthRefreshDescriptor) : Async<OAuthRefreshResult> =
            attemptRefresh descriptor

        interface IOAuthTokenRefresher with

            member this.RefreshNow(provider, configId) = async {
                let key = sprintf "%s:%s" provider configId

                match descriptors.TryGetValue key with
                | false, _ -> return PermanentError(sprintf "unknown descriptor: %s" key)
                | true, descriptor -> return! attemptRefresh descriptor
            }

            member _.RegisterDescriptor(descriptor) = async {
                let key = OAuthRefreshDescriptor.key descriptor
                descriptors[key] <- descriptor
                do! scheduleRefreshJob descriptor
            }

            member _.UnregisterDescriptor(provider, configId) = async {
                let key = sprintf "%s:%s" provider configId

                match descriptors.TryRemove key with
                | true, descriptor ->
                    do! cancelRefreshJob key

                    let! _ =
                        secretStore.DeleteSecret(descriptor.ScopeId, OAuthRefreshDescriptor.accessTokenKey descriptor)

                    let! _ =
                        secretStore.DeleteSecret(descriptor.ScopeId, OAuthRefreshDescriptor.accessExpiryKey descriptor)

                    return ()
                | _ ->
                    // Idempotent — unknown descriptor is a no-op.
                    return ()
            }

            member _.GetDescriptor(provider, configId) = async {
                let key = sprintf "%s:%s" provider configId

                return
                    match descriptors.TryGetValue key with
                    | true, d -> Some d
                    | _ -> None
            }

            member _.ListDescriptors() = async { return descriptors.Values |> Seq.toList }

    /// Factory. Compose-time wiring constructs the instance, registers
    /// it under both `IOAuthTokenRefresher` (interface) and the
    /// concrete type (so `Recover` is callable).
    let create
        (jobScheduler: IJobScheduler)
        (secretStore: ISecretStore)
        (auditLog: IAuditLog)
        (metricsSink: IMetricsSink)
        (rateLimiter: IRateLimiter)
        (httpClient: HttpClient)
        (logger: ILogger)
        : Impl =
        Impl(jobScheduler, secretStore, auditLog, metricsSink, rateLimiter, httpClient, logger)
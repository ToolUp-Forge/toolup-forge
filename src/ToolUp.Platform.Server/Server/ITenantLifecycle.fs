namespace ToolUp.Platform

// ─── ITenantLifecycle ────────────────────────────────────────────
//
// Phase 54 — tenant provisioning + offboarding hook (server-tier).
// Companions self-register a hook the same way they self-register
// `IHealthCheck` / `IConfigValidator`: `services.AddSingleton<ITenantLifecycle>(instance)`.
// `TenantLifecycleAggregator` walks the registered hooks and runs every
// one for a provision / deprovision call, in parallel with a per-hook
// timeout, aggregating into a `LifecycleSummary` with audit + per-hook
// isolation.
//
// The four first-party hooks the SDK ships wrap already-shipped
// surfaces:
//   * `EncryptionKeyLifecycle`      — `PerScopeKeyResolver.DestroyKey` (crypto-shred).
//   * `MembershipCacheLifecycle`    — evicts the team's active-team cache entries.
//   * `JobSchedulerLifecycle`       — cancels every scheduled job in the scope.
//   * `DataSubjectRequestLifecycle` — runs registered `IErasureHandler`s for the scope.
//
// Each first-party hook returns `Skipped` (not `Failed`) when its
// substrate is inactive (e.g. `EncryptionKeyLifecycle` under a
// non-`PerScopeKeyResolver` resolver), so an offboard on a minimal
// deployment runs clean rather than erroring.
//
// **Six-rule portability audit (Phase 9c, GP 12):**
//   1. Identity by value      — `scopeId` / `actorUserId` are strings;
//                               the hook's `Name` is a string. No live
//                               handles cross the boundary.
//   2. Async at every boundary — both methods return `Async<_>`.
//   3. Retry/supervision as data — the outcome is a `LifecycleHookResult`
//                                  value (`Completed` / `Skipped` /
//                                  `Failed`), never an `OnFailure`
//                                  callback. The aggregator owns retry /
//                                  timeout policy as data.
//   4. Stateless between calls — a hook reads its substrate from DI /
//                                its injected dependency on every
//                                invocation; no in-memory cumulative
//                                state between provision / deprovision
//                                calls.
//   5. No cross-shard ordering — hooks run in parallel; the aggregator
//                                promises ordering only within one
//                                scope's run (the `LifecycleSummary`
//                                lists outcomes in completion order). No
//                                cross-tenant ordering is implied.
//   6. Precision at lower bound — the aggregator's per-hook timeout is
//                                declared at the `Second` lower bound
//                                (default 30 s `OnProvisioned` /
//                                5 min `OnDeprovisioned`); a hook must
//                                not assume sub-second scheduling.

/// One subsystem's contribution to tenant provision / offboard
/// choreography. Register via `services.AddSingleton<ITenantLifecycle>(instance)`;
/// the aggregator reads `desc.ImplementationInstance` at run time (the
/// same instance-registration contract `IHealthCheck` /
/// `IConfigValidator` use).
type ITenantLifecycle =
    /// Stable hook name. Appears in the `LifecycleSummary` outcomes, the
    /// audit trail, and the admin UI. No spaces / control chars;
    /// namespace against the owning companion (`"encryption-key"`,
    /// `"membership-cache"`) so two companions can't collide.
    abstract Name: string

    /// Run this hook's provisioning work for `scopeId`, attributed to
    /// `actorUserId`. Returns the hook's terminal disposition. A hook
    /// whose substrate is inactive returns `Skipped reason` (not a
    /// failure). A hook that attempts work and fails returns
    /// `Failed error` — the aggregator records it and continues.
    abstract OnProvisioned: scopeId: string * actorUserId: string -> Async<LifecycleHookResult>

    /// Run this hook's offboarding work for `scopeId`, attributed to
    /// `actorUserId`. Same outcome contract as `OnProvisioned`. The
    /// aggregator never aborts the offboard on a `Failed` — a single
    /// misbehaving hook must not block the crypto-shred / erasure of the
    /// rest.
    abstract OnDeprovisioned: scopeId: string * actorUserId: string -> Async<LifecycleHookResult>
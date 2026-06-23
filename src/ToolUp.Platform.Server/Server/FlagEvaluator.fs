module ToolUp.Platform.FlagEvaluator

open ToolUp.Platform

/// Resolved feature-flag reader. Captures the store, the set of
/// declared flags (module + platform), and an optional logger. One
/// instance per request is not necessary — the evaluator is stateless
/// and safe to share across the process; request-scoped dependencies
/// arrive through `AccessContext` on each call.
///
/// Record of functions rather than an interface because (a) stubbing
/// in tests is trivial (construct the record literally), (b) it's
/// F#-idiomatic at this size, and (c) a future move to an interface
/// stays non-breaking — the three members become interface abstracts
/// with the same signatures.
type FlagEvaluator = {
    /// Walk `User → Team → Platform` for an explicit override. `None`
    /// when no scope has set this key — callers apply the declared
    /// default (see `IsEnabled` / `ResolveVariant`).
    TryEvaluate: string -> AccessContext -> Async<FlagValue option>

    /// Boolean-flag convenience. Walks overrides; falls back to the
    /// declared default from the registered `FeatureFlag` for this
    /// key. An unknown key (no registered declaration) returns
    /// `false` and logs a Warn — typo protection.
    IsEnabled: string -> AccessContext -> Async<bool>

    /// Variant-flag convenience. Walks overrides; falls back to the
    /// declared default. An unknown key returns `""` and logs a Warn;
    /// a known key whose declared default is `Bool` (schema drift)
    /// also returns `""` and logs.
    ResolveVariant: string -> AccessContext -> Async<string>

    /// Resolve one declared flag for the caller's context, returning a
    /// `FlagValue` coerced to match the declared shape. Routes through
    /// `IsEnabled` for `Bool` declarations and `ResolveVariant` for
    /// `Variant` declarations, so schema drift (stored shape mismatches
    /// declared shape) is corrected on the returned map. Used by
    /// `IFeatureFlagApi.GetResolvedFlags` to build the prefetch payload
    /// that the client shell consumes.
    Resolve: FeatureFlag -> AccessContext -> Async<FlagValue>
}

// ─── Scope walk ─────────────────────────────────────────────────

/// Scopes to consult for a given access context, in resolution order.
/// `Anonymous` has no User or Team — only Platform is read. Authenticated
/// modes always consult User; `Team` mode additionally consults Team
/// when a team is active. Platform is the universal fallback and is
/// always read last.
///
/// The walk stops on the first `Some` value — User beats Team beats
/// Platform. The precedence is "User flags → Team flags → Platform
/// flags → declared default".
let private scopesFor (ctx: AccessContext) : FlagScope list =
    match ctx.Subject with
    | AnonymousSession _ -> [ FlagScope.Platform ]
    | AuthenticatedUser userId -> [ FlagScope.User userId; FlagScope.Platform ]
    | TeamMember(userId, teamId) -> [ FlagScope.User userId; FlagScope.Team teamId; FlagScope.Platform ]
    | ClaimBearer _ ->
        // Claim-bearer requests do not participate in user / team flag
        // overrides — the claim itself is the authority envelope per
        // design §3.4 ("Claim bearer | Not loaded — always Map.empty |
        // Always None"). Platform-scope defaults still apply.
        [ FlagScope.Platform ]

/// First-Some walk over an async sequence of scope reads. Short-
/// circuits on the first override so missing upper layers don't pay
/// the I/O cost of scopes further down the walk.
let private firstSome (store: IFeatureFlagStore) (key: string) (scopes: FlagScope list) : Async<FlagValue option> =
    let rec loop =
        function
        | [] -> async.Return None
        | scope :: rest -> async {
            let! v = store.GetFlag(scope, key)

            match v with
            | Some _ -> return v
            | None -> return! loop rest
          }

    loop scopes

// ─── Factory ────────────────────────────────────────────────────

/// Premium check used by `createWithSources` to decide whether a
/// `PremiumOnly`-registered key may evaluate to `true`. Anonymous /
/// claim-bearer subjects fail the check without consulting the
/// claims store; authenticated subjects route through
/// `IUserClaims.GetPremiumStatus` for the active provider's truth.
let private isPremium (userClaims: IUserClaims) (ctx: AccessContext) : Async<bool> = async {
    match ctx.Subject with
    | AnonymousSession _
    | ClaimBearer _ -> return false
    | AuthenticatedUser userId
    | TeamMember(userId, _) ->
        let! status = userClaims.GetPremiumStatus userId

        match status with
        | Premium _ -> return true
        | NotPremium -> return false
}

/// First-Some walk over an external `IFlagSource` list for a declared
/// flag. Phase 239 — consulted after the store scope walk, before the
/// declared default.
let private firstSomeSource
    (flagSources: IFlagSource list)
    (flag: FeatureFlag)
    (ctx: AccessContext)
    : Async<FlagValue option> =
    let rec loop =
        function
        | [] -> async.Return None
        | (s: IFlagSource) :: rest -> async {
            let! v = s.Resolve(flag, ctx)

            match v with
            | Some _ -> return v
            | None -> return! loop rest
          }

    loop flagSources

/// Assemble an evaluator with an optional external flag-source layer
/// (Phase 239). `declared` is the union of platform-level and
/// module-declared flags the deployment knows about — the admin UI
/// renders against the same set and the evaluator uses it to look up
/// defaults and to validate that a read key was actually declared.
///
/// `flagSources` are read-only external resolvers (e.g. an OpenFeature
/// companion) consulted only when no in-process scope set the key, and
/// before the declared default. `flagSources = []` is byte-for-byte
/// equivalent to the pre-239 evaluator — `create` is exactly this with
/// no sources. `TryEvaluate` is the explicit-*override* reader and stays
/// store-only; the external layer applies in the value resolvers
/// (`IsEnabled` / `ResolveVariant` / `Resolve`).
let createWithFlagSources
    (store: IFeatureFlagStore)
    (declared: FeatureFlag seq)
    (flagSources: IFlagSource list)
    (logger: ILogger option)
    : FlagEvaluator =
    let declaredMap = declared |> Seq.map (fun f -> f.Key, f) |> Map.ofSeq

    let warn (msg: string) =
        match logger with
        | Some l -> l.Warn msg
        | None -> ()

    let tryEvaluate (key: string) (ctx: AccessContext) : Async<FlagValue option> = firstSome store key (scopesFor ctx)

    let isEnabled (key: string) (ctx: AccessContext) : Async<bool> = async {
        let! overrideValue = tryEvaluate key ctx

        match overrideValue with
        | Some(FlagValue.Bool b) -> return b
        | Some(FlagValue.Variant _) ->
            // Persisted value type doesn't match the expected shape
            // — schema drift (admin set a variant for a key the module
            // declared as bool). Warn and fall back to declared default.
            warn $"FeatureFlag '{key}': expected Bool override, found Variant — falling back to declared default"

            match Map.tryFind key declaredMap with
            | Some { DefaultValue = FlagValue.Bool b } -> return b
            | _ -> return false
        | None ->
            match Map.tryFind key declaredMap with
            | Some flag ->
                // No in-process override — consult external sources
                // (type-aware) before the declared default.
                let! fromSource = firstSomeSource flagSources flag ctx

                match fromSource, flag.DefaultValue with
                | Some(FlagValue.Bool b), _ -> return b
                | Some(FlagValue.Variant _), FlagValue.Bool dflt ->
                    warn $"FeatureFlag '{key}': source returned Variant for a Bool flag — declared default"
                    return dflt
                | _, FlagValue.Bool b -> return b
                | _, FlagValue.Variant _ ->
                    warn $"FeatureFlag '{key}': declared as Variant but read as Bool — returning false"
                    return false
            | None ->
                warn $"FeatureFlag '{key}': read but not declared by any module — returning false"
                return false
    }

    let resolveVariant (key: string) (ctx: AccessContext) : Async<string> = async {
        let! overrideValue = tryEvaluate key ctx

        match overrideValue with
        | Some(FlagValue.Variant(_, chosen)) -> return chosen
        | Some(FlagValue.Bool _) ->
            warn $"FeatureFlag '{key}': expected Variant override, found Bool — falling back to declared default"

            match Map.tryFind key declaredMap with
            | Some {
                       DefaultValue = FlagValue.Variant(_, dflt)
                   } -> return dflt
            | _ -> return ""
        | None ->
            match Map.tryFind key declaredMap with
            | Some flag ->
                let! fromSource = firstSomeSource flagSources flag ctx

                match fromSource, flag.DefaultValue with
                | Some(FlagValue.Variant(_, chosen)), _ -> return chosen
                | Some(FlagValue.Bool _), FlagValue.Variant(_, dflt) ->
                    warn $"FeatureFlag '{key}': source returned Bool for a Variant flag — declared default"
                    return dflt
                | _, FlagValue.Variant(_, dflt) -> return dflt
                | _, FlagValue.Bool _ ->
                    warn $"FeatureFlag '{key}': declared as Bool but read as Variant — returning empty string"
                    return ""
            | None ->
                warn $"FeatureFlag '{key}': read but not declared by any module — returning empty string"
                return ""
    }

    let resolve (flag: FeatureFlag) (ctx: AccessContext) : Async<FlagValue> = async {
        match flag.DefaultValue with
        | FlagValue.Bool _ ->
            let! b = isEnabled flag.Key ctx
            return FlagValue.Bool b
        | FlagValue.Variant(options, _) ->
            let! v = resolveVariant flag.Key ctx
            return FlagValue.Variant(options, v)
    }

    {
        TryEvaluate = tryEvaluate
        IsEnabled = isEnabled
        ResolveVariant = resolveVariant
        Resolve = resolve
    }

/// Assemble an evaluator with no external flag sources — the pre-239
/// behaviour, unchanged. `create store declared logger` ≡
/// `createWithFlagSources store declared [] logger`.
let create (store: IFeatureFlagStore) (declared: FeatureFlag seq) (logger: ILogger option) : FlagEvaluator =
    createWithFlagSources store declared [] logger

/// Phase 62 — evaluator with extra source-gating registered against
/// flag keys. Identical to `create` for any key not in `sources` —
/// the scope walk runs unchanged. Keys mapped to
/// `FeatureFlagSource.PremiumOnly` short-circuit to `false` when
/// `IUserClaims.GetPremiumStatus` reports `NotPremium` (or the
/// subject is anonymous / claim-bearer); premium subjects fall
/// through to the normal walk.
///
/// `createWithSources sources NoOpUserClaims` with `sources = empty`
/// produces an evaluator byte-for-byte equivalent to `create`. The
/// two factories share `declaredMap` / `firstSome` / warning
/// behaviour by construction.
let createWithSources
    (store: IFeatureFlagStore)
    (declared: FeatureFlag seq)
    (sources: FeatureFlagSourceRegistry)
    (userClaims: IUserClaims)
    (logger: ILogger option)
    : FlagEvaluator =
    let inner = create store declared logger

    let gate (key: string) (ctx: AccessContext) (fallback: Async<'T>) (denied: 'T) : Async<'T> = async {
        match sources.TryFind key with
        | None -> return! fallback
        | Some FeatureFlagSource.PremiumOnly ->
            let! premium = isPremium userClaims ctx

            if premium then return! fallback else return denied
    }

    let tryEvaluate (key: string) (ctx: AccessContext) : Async<FlagValue option> =
        gate key ctx (inner.TryEvaluate key ctx) (Some(FlagValue.Bool false))

    let isEnabled (key: string) (ctx: AccessContext) : Async<bool> =
        gate key ctx (inner.IsEnabled key ctx) false

    let resolveVariant (key: string) (ctx: AccessContext) : Async<string> =
        // Variant flags gated by `PremiumOnly` return the declared
        // default for non-premium subjects rather than `""` — the
        // floor semantics apply to the boolean axis only; variant
        // resolution still reports the declared default so callers
        // that build UI choosers don't see an empty string they
        // never declared. Premium subjects fall through to the
        // normal walk.
        async {
            match sources.TryFind key with
            | None -> return! inner.ResolveVariant key ctx
            | Some FeatureFlagSource.PremiumOnly ->
                let! premium = isPremium userClaims ctx

                if premium then
                    return! inner.ResolveVariant key ctx
                else
                    // Mirror `inner.ResolveVariant`'s declared-default
                    // path without going through the override read —
                    // the override would have been ignored anyway.
                    match declared |> Seq.tryFind (fun f -> f.Key = key) with
                    | Some {
                               DefaultValue = FlagValue.Variant(_, dflt)
                           } -> return dflt
                    | _ -> return ""
        }

    let resolve (flag: FeatureFlag) (ctx: AccessContext) : Async<FlagValue> = async {
        match flag.DefaultValue with
        | FlagValue.Bool _ ->
            let! b = isEnabled flag.Key ctx
            return FlagValue.Bool b
        | FlagValue.Variant(options, _) ->
            let! v = resolveVariant flag.Key ctx
            return FlagValue.Variant(options, v)
    }

    {
        TryEvaluate = tryEvaluate
        IsEnabled = isEnabled
        ResolveVariant = resolveVariant
        Resolve = resolve
    }
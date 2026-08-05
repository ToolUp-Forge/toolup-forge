module ToolUp.Platform.ModuleVisibilityResolver

open ToolUp.Platform

// ─── Phase 637 — the server-side scope walk ──────────────────────────
//
// `ModuleVisibility.resolve` (Core) is the pure narrowing fold; this
// module is the I/O half — which scopes to read, in which order, for a
// given caller. The split matters because the fold is the thing the
// client runs too (against the resolution the server ships it), and a
// pure function is the only shape that can live in both tiers.

/// Scopes to consult for a caller, ordered **outermost-first**:
/// `Platform`, then `Team`, then `User`.
///
/// **This is `FlagEvaluator.scopesFor` reversed, deliberately.** The
/// flag walk is innermost-first because it stops on the first hit — an
/// inner scope OVERRIDES an outer one. Visibility layers instead
/// COMPOSE, each narrowing the last, so the walk must start from the
/// broadest authority and tighten inwards. Reversing the list is the
/// entire difference; the scope SET is identical, which is what "the
/// same scope walk the feature-flag substrate uses" means and why an
/// operator can reason about both with one mental model.
///
/// Claim-bearer requests read Platform only, matching the flag walk:
/// the claim itself is the authority envelope, and a share-token holder
/// has no user or team scope to narrow by.
let scopesFor (ctx: AccessContext) : FlagScope list =
    match ctx.Subject with
    | AnonymousSession _ -> [ FlagScope.Platform ]
    | AuthenticatedUser userId -> [ FlagScope.Platform; FlagScope.User userId ]
    | TeamMember(userId, teamId) -> [ FlagScope.Platform; FlagScope.Team teamId; FlagScope.User userId ]
    | ClaimBearer _ -> [ FlagScope.Platform ]

/// Read every layer that declares a profile for this caller, in walk
/// order, and fold them into one resolution.
///
/// Unlike the flag walk there is no short-circuit: every scope is read,
/// because a layer that declares nothing contributes nothing but a layer
/// further in still narrows. That is up to three blob reads on a
/// deployment that has opted in, and zero on one that has not — the
/// caller (`computeAccessibleModules`'s handler) skips this entirely
/// when `ServerConfig.ModuleVisibility = NoModuleVisibility`.
///
/// `None` back means no layer declared a profile: the deployment is
/// unconfigured and every downstream consumer admits everything (GP 11).
let resolveFor
    (store: IModuleVisibilityStore)
    (registeredModuleIds: string list)
    (ctx: AccessContext)
    : Async<ModuleVisibilityResolution option> =
    async {
        let scopes = scopesFor ctx

        let! layers =
            scopes
            |> List.map (fun scope -> async {
                let! profile = store.GetProfile scope
                // Normalise the scope from the WALK, not from the stored
                // record: a profile whose persisted `Scope` drifted (a
                // hand-edited blob, a document copied between scopes)
                // would otherwise mis-report which layer contributed it
                // in `ContributingScopes`, which is the field an operator
                // reads to answer "where did this come from".
                return profile |> Option.map (fun p -> { p with Scope = scope })
            })
            |> Async.Sequential

        return ModuleVisibility.resolve registeredModuleIds (layers |> Array.toList |> List.choose id)
    }
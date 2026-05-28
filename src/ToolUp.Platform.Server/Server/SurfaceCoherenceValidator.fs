module ToolUp.Platform.SurfaceCoherenceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.ConfigValidation

// ─── Phase 66 Stream B.2 — Surface coherence validator ───────────────
//
// Central preflight check that the deployment's declared `Surfaces`,
// per-module / per-route `SurfaceRequirement` overrides, share-token
// store wiring, decorator chain, and auth provider all describe a
// coherent system. Failures are operator-actionable with a remediation
// suggestion baked into the message.
//
// 10 rules per design §3.8 + §3.11 — 8 base rules covering the
// `Surfaces` / `SurfaceRequirement` / `ShareTokenStore` / `Auth`
// substrate, plus 2 added by the `RevokeOnIssuerRemoved` companion
// (Stream C.5) coherence pass.
//
// Rule numbering (matches design enumeration):
//   1. `Surfaces` is empty.                                  ERROR
//   2. Duplicate `SurfaceProfile` constructors.              ERROR
//   3. Module surface-requirement unreachable.               ERROR
//   4. Route surface-requirement unreachable.                ERROR
//   5. `ClaimBearer` declared with `NoShareTokenStore` set.  WARNING
//      (See "Auto-promotion vs Rule 5" note below — the
//      auto-promotion in A.7 keeps deployments booting
//      even when the operator left the default, so this
//      validator surfaces the dependency on auto-promotion
//      as a Warning rather than refusing startup.)
//   6. `EnabledShareTokenStore` but no `ClaimBearer` surface. WARNING
//   7. Anonymous-only `Surfaces` + non-HeaderAuth provider.   WARNING
//   8. Any non-Anonymous surface + no auth provider declared. ERROR
//   9. `withShareTokenStoreDecorator` wired but no
//      `ClaimBearer` surface.                                WARNING (§3.11)
//  10. `withShareTokenStoreDecorator` wired but no `Team`
//      surface.                                              WARNING (§3.11)
//
// Auto-promotion vs Rule 5. A.7's `ComposeNotifications.shareTokenStoreInstance`
// auto-promotes `NoShareTokenStore` → `EnabledShareTokenStore` when
// `DeploymentConfig.hasClaimBearer config` is true, so a deployment
// with `ClaimBearer` in `Surfaces` boots cleanly even when the
// operator never set `ShareTokenStore = EnabledShareTokenStore`. The
// design doc enumerates Rule 5 as ERROR ("validator then refuses"),
// but emitting an error here would contradict the auto-promotion and
// regress A.7. The compromise: Rule 5 fires as a WARNING — the
// operator sees that they are relying on auto-promotion and may
// choose to be explicit, but startup is not refused. If a future
// `ExplicitlyDisabledShareTokenStore` sentinel lands, the rule can
// be upgraded to ERROR for that specific case.
//
// One `ValidationResult` per `Validate ()` call. The aggregator
// collapses Error / Warning / Ok into log lines + abort behaviour; the
// validator emits an aggregated message per severity (Error wins; if
// no Error, Warning wins; else Ok).

/// Severity-rule pair used internally by `SurfaceCoherenceValidator`.
type private RuleOutcome =
    | RuleOk
    | RuleWarning of message: string
    | RuleError of message: string

/// Project a `SurfaceProfile` to the `SubjectKind` it produces. The
/// resolution algorithm (design §3.1) maps one Surface to one Subject
/// kind 1:1; this projection is the inverse used to ask "does any
/// declared surface produce the kind this requirement accepts?".
let private producedKind =
    function
    | SurfaceProfile.Anonymous _ -> AnonymousKind
    | SurfaceProfile.AuthenticatedUser _ -> UserKind
    | SurfaceProfile.Team _ -> TeamMemberKind
    | SurfaceProfile.ClaimBearer _ -> ClaimBearerKind

/// Tag a `SurfaceProfile` with its constructor name for the
/// "duplicate constructors" rule. Carries no payload — two
/// `Team` entries with different `Switching` values are still
/// duplicates (the model admits one team surface per deployment).
let private constructorTag =
    function
    | SurfaceProfile.Anonymous _ -> "Anonymous"
    | SurfaceProfile.AuthenticatedUser _ -> "AuthenticatedUser"
    | SurfaceProfile.Team _ -> "Team"
    | SurfaceProfile.ClaimBearer _ -> "ClaimBearer"

let private requirementLabel (req: SurfaceRequirement) : string =
    let order = [ AnonymousKind; UserKind; TeamMemberKind; ClaimBearerKind ]

    let label =
        function
        | AnonymousKind -> "AnonymousKind"
        | UserKind -> "UserKind"
        | TeamMemberKind -> "TeamMemberKind"
        | ClaimBearerKind -> "ClaimBearerKind"

    order
    |> List.filter (fun k -> req.AcceptedSubjects.Contains k)
    |> List.map label
    |> String.concat "; "

let private producedKindsOf (surfaces: SurfaceProfile list) : Set<SubjectKind> =
    surfaces |> List.map producedKind |> Set.ofList

let private collectRules
    (config: ServerConfig)
    (authProvider: IAuthProvider option)
    (moduleSurfaceDefaults: (string * SurfaceRequirement) list)
    (routeSurfaceOverrides: ((string * string) * SurfaceRequirement) list)
    (shareTokenStoreDecoratorCount: int)
    : RuleOutcome list =
    [
        // Rule 1 — Surfaces is empty.
        if List.isEmpty config.Surfaces then
            yield
                RuleError
                    "ServerConfig.Surfaces is empty. Declare at least one SurfaceProfile (e.g. Surfaces = Surfaces.individual). A deployment with no surfaces has no resolvable subjects and rejects every request."

        // Rule 2 — Duplicate `SurfaceProfile` constructors.
        let duplicates =
            config.Surfaces
            |> List.map constructorTag
            |> List.groupBy id
            |> List.choose (fun (tag, occurrences) ->
                if List.length occurrences > 1 then
                    Some(tag, List.length occurrences)
                else
                    None)

        if not (List.isEmpty duplicates) then
            let labels =
                duplicates
                |> List.map (fun (tag, n) -> sprintf "%s × %d" tag n)
                |> String.concat ", "

            yield
                RuleError(
                    sprintf
                        "ServerConfig.Surfaces contains duplicate SurfaceProfile constructors (%s). Each constructor (Anonymous / AuthenticatedUser / Team / ClaimBearer) may appear at most once."
                        labels
                )

        let produced = producedKindsOf config.Surfaces

        // Rule 3 — module-level DefaultSurfaceRequirement unreachable.
        if not (List.isEmpty config.Surfaces) then
            let surfacesLabel = DeploymentConfig.surfacesLabel config

            for (routePrefix, requirement) in moduleSurfaceDefaults do
                let intersection = Set.intersect requirement.AcceptedSubjects produced

                if Set.isEmpty intersection then
                    yield
                        RuleError(
                            sprintf
                                "Module route prefix '%s' declares DefaultSurfaceRequirement accepting [%s], but ServerConfig.Surfaces (%s) produces none of those SubjectKinds. The module is unreachable. Either add the matching SurfaceProfile to Surfaces or change the module's DefaultSurfaceRequirement."
                                routePrefix
                                (requirementLabel requirement)
                                surfacesLabel
                        )

            // Rule 4 — per-route override unreachable.
            for ((method, path), requirement) in routeSurfaceOverrides do
                let intersection = Set.intersect requirement.AcceptedSubjects produced

                if Set.isEmpty intersection then
                    yield
                        RuleError(
                            sprintf
                                "Route override (%s %s) declares SurfaceRequirement accepting [%s], but ServerConfig.Surfaces (%s) produces none of those SubjectKinds. The route is unreachable. Either add the matching SurfaceProfile to Surfaces or remove the override."
                                method
                                path
                                (requirementLabel requirement)
                                surfacesLabel
                        )

        // Rule 5 — `ClaimBearer` declared with `NoShareTokenStore` set.
        // Downgraded to Warning vs the design's ERROR — see header
        // "Auto-promotion vs Rule 5" comment.
        if
            DeploymentConfig.hasClaimBearer config
            && (match config.ShareTokenStore with
                | NoShareTokenStore -> true
                | EnabledShareTokenStore -> false)
        then
            yield
                RuleWarning
                    "ServerConfig.Surfaces declares SurfaceProfile.ClaimBearer but ServerConfig.ShareTokenStore = NoShareTokenStore. Compose auto-promotes the store to EnabledShareTokenStore so the surface remains reachable, but the explicit setting is incoherent. Set ServerConfig.ShareTokenStore = EnabledShareTokenStore to remove this warning, or drop the ClaimBearer surface to disable share-token-gated access."

        // Rule 6 — `EnabledShareTokenStore` but no `ClaimBearer` surface.
        if
            (not (DeploymentConfig.hasClaimBearer config))
            && (match config.ShareTokenStore with
                | EnabledShareTokenStore -> true
                | NoShareTokenStore -> false)
        then
            yield
                RuleWarning
                    "ServerConfig.ShareTokenStore = EnabledShareTokenStore but ServerConfig.Surfaces declares no SurfaceProfile.ClaimBearer. The store is wired but no surface accepts claim-bearer subjects, so issued tokens are unreachable. Either add SurfaceProfile.claimBearer to Surfaces or set ShareTokenStore = NoShareTokenStore."

        // Rule 7 — Anonymous-only Surfaces + non-HeaderAuth provider.
        let anonymousOnly =
            not (List.isEmpty config.Surfaces)
            && not (DeploymentConfig.requiresAnyAuth config)

        let nonHeaderAuthRegistered =
            match authProvider with
            | None -> false
            | Some provider -> provider.GetType() <> typeof<HeaderAuthProvider.HeaderAuthProvider>

        if anonymousOnly && nonHeaderAuthRegistered then
            yield
                RuleWarning
                    "ServerConfig.Surfaces declares only Anonymous shape(s) but ServerApp.withAuth registered a non-HeaderAuthProvider auth provider. Anonymous surfaces do not resolve authenticated subjects, so the auth provider is unreachable at runtime. Drop the withAuth call, or add SurfaceProfile.individual / SurfaceProfile.team to Surfaces if authenticated surfaces are intended."

        // Rule 8 — Any non-Anonymous surface + no auth provider declared.
        if DeploymentConfig.requiresAnyAuth config && Option.isNone authProvider then
            yield
                RuleError(
                    sprintf
                        "ServerConfig.Surfaces = %s requires an auth provider, but ServerApp.withAuth was never called. Compose defaults to HeaderAuthProvider, which trusts the X-User-Id header without cryptographic proof — any caller can spoof any user id. Register an auth provider (e.g. OIDC) via ServerApp.withAuth before run."
                        (DeploymentConfig.surfacesLabel config)
                )

        // Rule 9 — decorator wired but no ClaimBearer surface (§3.11).
        if
            shareTokenStoreDecoratorCount > 0
            && not (DeploymentConfig.hasClaimBearer config)
        then
            yield
                RuleWarning(
                    sprintf
                        "ServerApp.withShareTokenStoreDecorator was called %d time(s) but ServerConfig.Surfaces declares no SurfaceProfile.ClaimBearer. The decorator chain wraps the share-token store, but no surface accepts claim-bearer subjects, so the decorator is structurally inert. Add SurfaceProfile.claimBearer to Surfaces, or remove the withShareTokenStoreDecorator call."
                        shareTokenStoreDecoratorCount
                )

        // Rule 10 — decorator wired but no Team surface (§3.11).
        // The `RevokeOnIssuerRemoved` companion subscribes to team
        // `MembershipChanged.Removed` notifications; without a Team
        // surface there is no team scope and the decorator never fires.
        if shareTokenStoreDecoratorCount > 0 && not (DeploymentConfig.hasTeamScope config) then
            yield
                RuleWarning(
                    sprintf
                        "ServerApp.withShareTokenStoreDecorator was called %d time(s) but ServerConfig.Surfaces declares no SurfaceProfile.Team. Decorators such as RevokeOnIssuerRemoved react to team MembershipChanged events; without a Team surface there is no team scope and the decorator is structurally inert. Add SurfaceProfile.team / SurfaceProfile.multiTeam to Surfaces, or remove the withShareTokenStoreDecorator call."
                        shareTokenStoreDecoratorCount
                )
    ]

let private severity =
    function
    | RuleOk -> 0
    | RuleWarning _ -> 1
    | RuleError _ -> 2

let private joinMessages (messages: string list) : string =
    messages |> String.concat "\n  - " |> sprintf "\n  - %s"

let private aggregate (outcomes: RuleOutcome list) : ValidationResult =
    let errors =
        outcomes
        |> List.choose (function
            | RuleError m -> Some m
            | _ -> None)

    let warnings =
        outcomes
        |> List.choose (function
            | RuleWarning m -> Some m
            | _ -> None)

    if not (List.isEmpty errors) then
        Error(sprintf "SurfaceCoherenceValidator refused startup:%s" (joinMessages errors))
    elif not (List.isEmpty warnings) then
        Warning(sprintf "SurfaceCoherenceValidator warnings:%s" (joinMessages warnings))
    else
        Ok

/// Phase 66 Stream B.2 — config validator that walks `ServerConfig.Surfaces`,
/// the module / route surface-requirement overlays, the `ShareTokenStore`
/// selection, the `ShareTokenStoreDecorators` chain, and the registered
/// auth provider, refusing startup or warning per the 10 rules in design
/// §3.8 + §3.11.
type SurfaceCoherenceValidator
    (
        config: ServerConfig,
        authProvider: IAuthProvider option,
        moduleSurfaceDefaults: (string * SurfaceRequirement) list,
        routeSurfaceOverrides: ((string * string) * SurfaceRequirement) list,
        shareTokenStoreDecoratorCount: int,
        ?timeout: TimeSpan
    ) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "surface-coherence"
        member _.Timeout = timeout

        member _.Validate() = async {
            let outcomes =
                collectRules
                    config
                    authProvider
                    moduleSurfaceDefaults
                    routeSurfaceOverrides
                    shareTokenStoreDecoratorCount

            return aggregate outcomes
        }

/// Public projection of the 10 rules' outcomes against a given
/// configuration. Used by tests + the future `/dev/inspect`
/// Validators panel so the per-rule structure is visible without
/// parsing the aggregated message back out.
let evaluate
    (config: ServerConfig)
    (authProvider: IAuthProvider option)
    (moduleSurfaceDefaults: (string * SurfaceRequirement) list)
    (routeSurfaceOverrides: ((string * string) * SurfaceRequirement) list)
    (shareTokenStoreDecoratorCount: int)
    : ValidationResult =
    collectRules config authProvider moduleSurfaceDefaults routeSurfaceOverrides shareTokenStoreDecoratorCount
    |> aggregate
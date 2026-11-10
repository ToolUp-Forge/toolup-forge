module ToolUp.Platform.Scope.StorageScopeResolver

open ToolUp.Platform

/// A caller's storage scope from the deployment's one resolver: the subject
/// `ISubjectResolver` answers, projected to a storage scope by the
/// deployment's derivation (`StorageScopeDerivation.tryFromSubject`, passed
/// in because it reads `ServerConfig`). There is no second resolver.
module SubjectScope =

    /// A resolver refusal in the scope vocabulary: an unsupported subject is
    /// unauthenticated, a removed member is not a member, a failure is a
    /// failure.
    let toScopeError (error: SubjectResolutionError) : ScopeResolutionError =
        match error with
        | SubjectResolutionError.UnsupportedSubject _ -> NotAuthenticated
        | SubjectResolutionError.NotTeamMember teamId -> NotTeamMember teamId
        | SubjectResolutionError.SubjectResolutionFailed msg -> ScopeResolutionFailed msg

    /// Resolve `request`'s subject and derive its storage scope. A subject
    /// that holds no scope of its own (a publication reader) resolves to
    /// none: `NotAuthenticated`. A signed-in user the deployment cannot place
    /// as a user is one with no active team to be placed in — the only shape
    /// left for them — so that refusal says `NoActiveTeam`.
    let resolve
        (subjects: ISubjectResolver)
        (derive: Subject -> StorageScope option)
        (request: SubjectResolutionRequest)
        : Async<Result<StorageScope, ScopeResolutionError>> =
        async {
            match! subjects.Resolve request with
            | Error(SubjectResolutionError.UnsupportedSubject UserKind) when
                request.User
                |> Option.exists (fun user -> not (Auth.AuthenticatedUser.isAnonymous user))
                ->
                return Error NoActiveTeam
            | Error error -> return Error(toScopeError error)
            | Ok subject ->
                match derive subject with
                | Some scope -> return Ok scope
                | None -> return Error NotAuthenticated
        }

/// The one place a request's storage scope becomes a
/// `ResolvedScope`. The scope-resolution middleware calls `remember` with
/// the scope it derived for the request; a fact door calls `forRequest`
/// and gets either that value or the explicit anonymous scope. Nothing
/// else can mint one: `ResolvedScope`'s representation is private to
/// `ToolUp.Platform.Core` and its constructor is internal, visible to this
/// assembly and to no consumer. So a store or gate member that takes a
/// `ResolvedScope` is, by type, asking about the scope the principal
/// resolved to — never about a string a caller chose.
///
/// This module deliberately does NOT run a resolver of its own. Resolution
/// happens once per request in the middleware; a second path here would
/// be a second resolver with its own semantics. A request the middleware
/// did not resolve a scope for (a resolver error, or a route the
/// middleware does not cover) is the anonymous scope — a shard of its own
/// that holds only what was asserted anonymously, never a widening.
module ScopeResolution =
    open Microsoft.AspNetCore.Http

    /// The `HttpContext.Items` key the middleware records the request's
    /// `ResolvedScope` under. Only a `ResolvedScope` value is honoured at
    /// this key, and only this assembly can produce one.
    [<Literal>]
    let internal ItemsKey = "ToolUp.ResolvedScope"

    /// Mint a `ResolvedScope` from the resolver's output. Internal: the
    /// middleware and the test pack are its only callers.
    let internal ofStorageScope (scope: StorageScope) : ResolvedScope = ResolvedScope.ofStorageScope scope

    /// Mint the request's `ResolvedScope` from the scope the middleware
    /// resolved and record it on the request for `forRequest` to find.
    /// Internal: called once per request by the scope-resolution
    /// middleware, and by the test pack to stand in for it.
    let internal remember (ctx: HttpContext) (scope: StorageScope) : ResolvedScope =
        let resolved = ofStorageScope scope
        ctx.Items[ItemsKey] <- box resolved
        resolved

    /// Record an ALREADY-RESOLVED scope on a context the platform builds for
    /// carried work — a background agent loop running for a job under the
    /// scope on `JobContext.Scope` (Phase 985), or a chat turn's agent loop
    /// running on a context built off the request it serves (Phase 990),
    /// which carries the request's own resolution. It mints nothing: a
    /// `ResolvedScope` can only have come from this module or from the
    /// scheduler's carrier, so every door that reads the context through
    /// `forRequest` still reads only a scope the platform resolved, and an
    /// anonymous one stays anonymous.
    let carry (ctx: HttpContext) (scope: ResolvedScope) : unit = ctx.Items[ItemsKey] <- box scope

    /// The scope the platform resolved for this request, or the explicit
    /// anonymous scope when it resolved none. This is how a fact door
    /// obtains its scope — it never reads `StorageScope` from the request
    /// items and never falls back to a user id or a literal.
    let forRequest (ctx: HttpContext) : ResolvedScope =
        match ctx.Items.TryGetValue ItemsKey with
        | true, (:? ResolvedScope as scope) -> scope
        | _ -> ResolvedScope.anonymous
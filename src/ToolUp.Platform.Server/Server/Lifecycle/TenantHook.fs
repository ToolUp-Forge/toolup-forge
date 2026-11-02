// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Compose

open ToolUp.Platform

open System

// ─── TenantHook — a tenant-lifecycle hook as a value ─────────────────
//
// Every first-party hook answered the same three questions by hand, per
// call: is my substrate composed (an `IServiceProvider` probe), what do I
// say when it is not (a `Skipped` sentence of its own), and which kind of
// scope is this (a parse of the scope id). A `TenantHook<'dep>` declares
// the first as a `Resolve` and a noun (`Requires`), the third is
// `ScopeId.parse` — one grammar, `ScopeKey`'s — and `TenantHook.asLifecycle`
// answers the second once, for every hook. The hook keeps only its body.

/// What kind of scope a lifecycle `scopeId` names, read through
/// `ScopeKey`'s container grammar.
[<RequireQualifiedAccess>]
type ScopeKind =
    /// A `team-{id}` scope.
    | Team of teamId: string
    /// A `user-{id}` scope.
    | User of userId: string
    /// Any other scope id (the platform shard, a session, a claim, or an id
    /// with no container prefix), carried as written.
    | Other of scopeId: string

[<RequireQualifiedAccess>]
module ScopeId =

    /// The kind of scope `scopeId` names. The one scope-id grammar the
    /// lifecycle hooks read, and it is `ScopeKey.tryParseContainer`'s.
    let parse (scopeId: string) : ScopeKind =
        match ScopeKey.tryParseContainer scopeId with
        | Some(ScopeKey.Team teamId) -> ScopeKind.Team teamId
        | Some(ScopeKey.User userId) -> ScopeKind.User userId
        | _ -> ScopeKind.Other scopeId

/// A tenant-lifecycle hook as a value: a name, the substrate it acts on
/// (`'dep`, resolved from the container on every call — GP 12 rule 4), and
/// what it does on each transition. `TenantHook.asLifecycle` turns it into
/// the `ITenantLifecycle` the aggregator runs; a transition the hook does
/// not act on, or a call with the substrate absent, answers `Skipped`
/// without reaching the body.
[<NoEquality; NoComparison>]
type TenantHook<'dep> = {
    /// The hook's stable name (the aggregator's ledger key).
    Name: string
    /// What the hook needs, as a noun phrase ("an ITeamStore"), named in
    /// the `Skipped` outcome when it is not composed.
    Requires: string
    /// The substrate, from the container; `None` when it is not composed.
    Resolve: IServiceProvider -> 'dep option
    /// Provisioning work, given the full provisioning context (the request
    /// is `None` on paths that carry none). `None`: provisioning has
    /// nothing to do for this hook.
    OnProvisioned: ('dep -> TenantProvisioningContext -> Async<LifecycleHookResult>) option
    /// Offboarding work over `scopeId`, by `actorUserId`. `None`:
    /// offboarding has nothing to do for this hook.
    OnDeprovisioned: ('dep -> string -> string -> Async<LifecycleHookResult>) option
    /// A mutation-free offboard preview: how many records the offboard of
    /// `scopeId` would affect, and a sentence saying what. `None`: the hook
    /// offers no preview.
    Preview: ('dep -> string -> string -> Async<int * string>) option
}

[<RequireQualifiedAccess>]
module TenantHook =

    /// The `ITenantLifecycle` (and, when the hook previews, the
    /// `ITenantLifecyclePreview`) that runs `hook` over `services`.
    let asLifecycle (hook: TenantHook<'dep>) (services: IServiceProvider) : ITenantLifecycle =
        let absent = sprintf "%s is not composed" hook.Requires

        let run (body: ('dep -> Async<LifecycleHookResult>) option) (noAction: string) = async {
            match body with
            | None -> return LifecycleHookResult.Skipped noAction
            | Some act ->
                match hook.Resolve services with
                | Some dep -> return! act dep
                | None -> return LifecycleHookResult.Skipped absent
        }

        let provision (context: TenantProvisioningContext) =
            run (hook.OnProvisioned |> Option.map (fun f dep -> f dep context)) "no provisioning action"

        let deprovision (scopeId: string) (actorUserId: string) =
            run (hook.OnDeprovisioned |> Option.map (fun f dep -> f dep scopeId actorUserId)) "no offboard action"

        let onProvisioned (scopeId, actorUserId) =
            provision {
                ScopeId = scopeId
                ActorUserId = actorUserId
                Request = None
            }

        match hook.Preview with
        | None ->
            { new ITenantLifecycle with
                member _.Name = hook.Name
                member _.OnProvisioned(scopeId, actorUserId) = onProvisioned (scopeId, actorUserId)
                member _.OnDeprovisioned(scopeId, actorUserId) = deprovision scopeId actorUserId
              interface ITenantLifecycleProvisionContext with
                  member _.OnProvisionedWith(context) = provision context
            }
        | Some preview ->
            { new ITenantLifecycle with
                member _.Name = hook.Name
                member _.OnProvisioned(scopeId, actorUserId) = onProvisioned (scopeId, actorUserId)
                member _.OnDeprovisioned(scopeId, actorUserId) = deprovision scopeId actorUserId
              interface ITenantLifecycleProvisionContext with
                  member _.OnProvisionedWith(context) = provision context
              interface ITenantLifecyclePreview with
                  member _.OnDeprovisionPreview(scopeId, actorUserId) = async {
                      match hook.Resolve services with
                      | Some dep ->
                          let! count, detail = preview dep scopeId actorUserId
                          return LifecyclePreviewItem.affecting hook.Name count detail
                      | None -> return LifecyclePreviewItem.affecting hook.Name 0 (absent + " — nothing would change")
                  }
            }
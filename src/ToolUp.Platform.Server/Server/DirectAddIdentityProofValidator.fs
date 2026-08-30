// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DirectAddIdentityProofValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 549 — a proof requirement that can actually be honoured ───
//
// `ServerConfig.DirectAddIdentityProof = RequireDirectoryProof` tells the
// direct-add membership paths (`TeamApi.AddTeamMember`,
// `TeamApi.CreateTeamWithOwner`) to resolve the supplied principal id
// against the composed `IUserDirectory` before writing a membership row.
// The mode is meaningless without that companion: there is nothing to ask.
//
// **`Error`, not `Warning`, and the grade is the decision.** Every other
// deps-shaped validator in this set warns, because the subsystem it names
// is a capability whose absence degrades a feature — an unsent email, a
// typed `SubstrateDisabled` refusal on first request. This one names a
// SECURITY control the operator deliberately turned on, and the only two
// request-time behaviours available to a proof gate with nothing to
// consult are (a) refuse every direct add, which is an outage discovered
// one failed add at a time, or (b) admit every id unverified, which is
// silently the pre-549 behaviour under a config that says otherwise. The
// handler picks (a) — it fails closed — but neither is a state a
// deployment should be allowed to boot into, and a warning scrolls past.
// GP 11 is untouched: the default `NoIdentityProof` self-gates to `Ok`,
// so no existing deployment can be refused by this check.
//
// **It probes the `IServiceCollection`, not a forge-held instance** (the
// `InviteEmailCapabilityValidator` / `DeployPlaneDepsValidator` shape):
// forge composes no directory, so its presence IS the question. The
// collection is captured by reference and read at `Validate()` time, so a
// registration appended by a later compose stage — `withUserDirectory`
// among them — is visible.

/// Refuse startup when the direct-add existence proof is required but no
/// `IUserDirectory` companion is composed to supply it. Self-gates to
/// `Ok` under the default `NoIdentityProof`.
type DirectAddIdentityProofValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    let directoryRegistered () =
        services
        |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType = typeof<IUserDirectory>)

    interface IConfigValidator with
        member _.Name = "direct-add-identity-proof"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.DirectAddIdentityProof with
            | NoIdentityProof -> return Ok
            | RequireDirectoryProof ->
                if directoryRegistered () then
                    return Ok
                else
                    return
                        Error(
                            "ServerConfig.DirectAddIdentityProof = RequireDirectoryProof requires every direct member add (TeamApi.AddTeamMember, TeamApi.CreateTeamWithOwner) to resolve the supplied principal id against an IUserDirectory before the membership row is written, but no IUserDirectory companion is composed — so there is nothing to resolve against and the handler would refuse every direct add. "
                            + "Compose an IUserDirectory companion (e.g. ToolUp.AuthProviders.EntraDirectory) via ServerApp.withUserDirectory, or register your own IUserDirectory singleton before Build; "
                            + "or set ServerConfig.DirectAddIdentityProof = NoIdentityProof (TOOLUP_REQUIRE_DIRECTORY_PROOF_FOR_DIRECT_ADD=disabled) to keep the default admin-asserted membership posture. "
                            + "Note the invite-by-email path is unaffected either way: a pending invite is consumed at sign-in, which is its own existence proof."
                        )
        }
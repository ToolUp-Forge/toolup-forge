module ToolUp.Forms.PublishableFormConfigValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Forms.FormSchema

// ─── Phase 21b — Publishable-form preflight validator ────────────────
// ─── Phase 21e — mode-aware severity ─────────────────────────────────
//
// Startup-time check that the deployment's share-link surface is
// coherent. Catches the two most common operator misconfigurations:
//
//   1. A `Publishable` form schema is registered (via
//      `FormsServerApp.withFormSchema`) but `ServerConfig.PublicBaseUrl`
//      is `None` — `IssueTokens` will fail at runtime with "PublicBaseUrl
//      not set"; surfacing the same fact at startup means operators see
//      it before respondents try to load the link.
//
//   2. `Publishable` schemas are registered but `ServerConfig.ShareTokenStore
//      = NoShareTokenStore` — `IssueTokens` will fail at runtime; same
//      reasoning, surface earlier.
//
// **Severity (Phase 21e).** Persistent-data modes (`Individual` /
// `Team` / `MultiTeam`) treat the gap as `Error` — boot fails so a
// misconfigured production deployment cannot silently ship a
// token-less public surface (no signed-token gate, no use-limit
// enforcement, no revocation). Ephemeral / demo modes
// (`Anonymous` / `AuthenticatedEphemeral`) preserve the original
// `Warning` shape so dry runs and demos still boot.
//
// The escape hatch `ServerConfig.AcceptUnsignedPublishable = true`
// (env `TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE=1`) downgrades the
// persistent-mode `Error` to `Warning` for the
// staging-shape-in-production-mode edge case — explicit operator
// opt-in, traceable in the validator output.
//
// Runtime case (operator saves a Publishable schema after startup,
// without configured URL / store) is caught at first `IssueTokens`
// call — the handler returns `FormError.StorageFailed` with the same
// remediation hint.

type PublishableFormConfigValidator
    (config: ServerConfig, registeredSchemas: Map<FormSchemaId, FormSchema>, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    let registeredPublishable =
        registeredSchemas
        |> Map.toSeq
        |> Seq.filter (fun (_, s) -> s.Visibility = Publishable)
        |> Seq.map fst
        |> List.ofSeq

    interface IConfigValidator with
        member _.Name = "forms-publishable-config"
        member _.Timeout = timeout

        member _.Validate() = async {
            let issues = ResizeArray<string>()

            if not (List.isEmpty registeredPublishable) then
                if config.ShareTokenStore = NoShareTokenStore then
                    issues.Add(
                        sprintf
                            "Publishable schemas registered (%s) but ServerConfig.ShareTokenStore = NoShareTokenStore — IssueTokens will fail at runtime."
                            (String.concat ", " registeredPublishable)
                    )

                match config.PublicBaseUrl with
                | None ->
                    issues.Add(
                        sprintf
                            "Publishable schemas registered (%s) but ServerConfig.PublicBaseUrl = None — IssueTokens will fail at runtime."
                            (String.concat ", " registeredPublishable)
                    )
                | Some url when String.IsNullOrWhiteSpace url ->
                    issues.Add "ServerConfig.PublicBaseUrl is set to whitespace; embed URLs will be malformed."
                | Some _ -> ()

            if issues.Count = 0 then
                return Ok
            else
                let combined = String.concat "; " (List.ofSeq issues)

                // Phase 21e — persistent-data deployments treat the
                // gap as `Error` so a misconfigured production
                // deployment cannot silently ship a token-less public
                // surface. `AcceptUnsignedPublishable = true` downgrades
                // to `Warning` for the staging-shape-in-production-mode
                // edge case. The predicate matches the retiring
                // `Individual` / `Team` / `MultiTeam` modes — i.e. any
                // authenticated-persistent surface — and excludes
                // `Anonymous` / `AuthenticatedEphemeral`.
                let isPersistentMode = DeploymentConfig.hasPersistentAuthenticatedStorage config

                if isPersistentMode && not config.AcceptUnsignedPublishable then
                    return
                        Error(
                            sprintf
                                "%s Set ServerConfig.AcceptUnsignedPublishable = true (or TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE=1) to override if intentional."
                                combined
                        )
                else
                    return Warning combined
        }
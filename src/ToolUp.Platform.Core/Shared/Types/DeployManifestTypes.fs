// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Deploy-manifest substrate (Phase 26) ────────────────────────────
//
// Typed schema for the deploy manifest a consumer hands to the deploy
// plane. Carries the structural shape any deploy backend needs (app
// identity + runtime image + secrets + domains + modules +
// dependencies) without binding to a specific on-disk format. TOML /
// YAML / JSON / F# DSL serialisers live in the operator's CLI
// (`diametrical#26.C` task 26.C.2), not in this substrate; the typed
// surface stays format-agnostic so consumers can drive it from
// whichever source they prefer.
//
// **Schema-versioned + forward-compatible.** Every manifest carries a
// `SchemaVersion` integer; this substrate ships `v1` and treats higher
// versions as upgradable-when-the-consumer-upgrades. Unknown extension
// fields are carried as opaque string maps (`ExtensionFields`) so a
// `v1` runtime can round-trip a `v2`-authored manifest without losing
// data — the unknown fields are preserved on disk and surfaced to
// operator tooling, but not interpreted.
//
// **No portability audit on this file.** These are pure data records
// with no methods. The six-rule portability audit applies to the four
// substrate interfaces (`IBuildOrchestrator`, `ITenantFleet`,
// `IDeployPipeline`, `IContainerScheduler`) — manifest types ride
// across the interfaces by value, which is rule 1 by construction.

/// Identity of the application being deployed. `Name` is the human
/// label (shown in admin UIs, logs, support tickets); `Slug` is the
/// machine-safe DNS-compatible identifier the deploy plane uses to
/// derive container names, domain prefixes, and audit trails;
/// `Region` is the operator-defined deployment region (free-form
/// string — the substrate does not enumerate regions, that's
/// operator policy).
type DeployManifestApp = {
    /// Human-readable application name. Free-form UTF-8.
    Name: string
    /// DNS-safe slug. Lowercase, ASCII letters / digits / hyphens
    /// only; must start with a letter; max 63 chars (DNS label
    /// limit). Validated by `ITenantFleet.IsSlugAvailable` before
    /// `ProvisionTenant`.
    Slug: string
    /// Operator-defined deployment region. The substrate stores this
    /// as a free-form string; mapping to physical placement is the
    /// operator's concern (e.g. ToolUp Cloud maps `"eu-west"` to a
    /// Fly Machines region, a self-hosted operator may treat it as
    /// a Kubernetes nodegroup label).
    Region: string
}

/// Healthcheck declaration. The deploy plane uses this to wait for a
/// container to become healthy before flipping traffic to it
/// (blue-green) and to populate `IContainerScheduler.GetContainerStatus`.
type DeployManifestHealthcheck = {
    /// HTTP path to probe (e.g. `"/health"` or `"/ready"`).
    Path: string
    /// Port to probe. When `None`, defaults to the runtime's primary
    /// port (the deploy plane's container scheduler decides what
    /// that is — typically the only exposed port).
    Port: int option
    /// Maximum wallclock the deploy plane allows for the first
    /// successful probe before declaring the container failed. The
    /// substrate does not enforce a lower bound; operator policy
    /// applies (ToolUp Cloud caps at 5 minutes per `diametrical#26.C`
    /// sub-phase 26.C.8).
    InitialDelay: TimeSpan
    /// Interval between probes once `InitialDelay` has elapsed.
    Interval: TimeSpan
}

/// Runtime selector. Tells the deploy plane which framework /
/// container image to launch. `Framework` is a free-form string the
/// operator's `IContainerScheduler` interprets (e.g. ToolUp Cloud
/// recognises `"dotnet:10"`, `"node:20"`, `"static"`); `Image` is the
/// fully-qualified container image reference when the consumer ships
/// their own image (`Some "ghcr.io/me/app:v1.2.3"`) or `None` when
/// the deploy plane builds from source.
type DeployManifestRuntime = {
    /// Framework label. Free-form; operator-defined vocabulary.
    Framework: string
    /// Optional pre-built container image reference. When `None`,
    /// `IBuildOrchestrator.EnqueueBuild` is the path that produces
    /// the image. When `Some`, the deploy plane skips the build
    /// phase and pulls directly.
    Image: string option
    /// Healthcheck the deploy plane uses to verify the container is
    /// ready to serve traffic.
    Healthcheck: DeployManifestHealthcheck
}

/// Secret reference. The substrate carries only the *intent* — a name
/// to look the secret up by, and the source the operator should pull
/// it from. The actual secret material never crosses the manifest
/// boundary (the operator's CLI resolves at deploy time via
/// `ISecretStore`).
type DeployManifestSecret = {
    /// Logical name the application reads the secret by (e.g.
    /// `"DATABASE_URL"`, `"STRIPE_SECRET_KEY"`). Becomes the
    /// environment-variable name inside the container.
    Name: string
    /// Where the operator should resolve the secret from. Free-form
    /// string; operator-defined vocabulary. ToolUp Cloud recognises
    /// patterns like `"vault://kv/prod/db-url"` or
    /// `"keyvault://my-vault/db-url"`; a self-hosted operator MAY
    /// treat it as a literal value (dev only — substrate does not
    /// enforce).
    Source: string
}

/// Custom domain declaration. The deploy plane provisions TLS and
/// routes traffic from `Hostname` to the application's container(s).
/// `Hostname` is a fully-qualified domain (no scheme, no path);
/// `TlsMode` is the operator-defined certificate provisioning mode.
type DeployManifestDomain = {
    /// Fully-qualified domain name (e.g. `"app.example.com"`). The
    /// deploy plane validates DNS resolution and certificate
    /// provisioning per the operator's policy.
    Hostname: string
    /// Free-form TLS mode label. Operator-defined vocabulary (ToolUp
    /// Cloud recognises `"acme-le"` for Let's Encrypt and `"custom"`
    /// for a bring-your-own certificate path).
    TlsMode: string
}

/// Module reference. Pointers the application's composition root
/// uses to register modules — the manifest does not declare module
/// behaviour, only which modules participate. Each entry is a
/// (package id, version) pair the consumer's `Directory.Packages.props`
/// or runtime loader resolves.
type DeployManifestModuleRef = {
    /// NuGet package id (e.g. `"ToolUp.AI"`).
    PackageId: string
    /// Pinned package version. Substrate does not interpret version
    /// ranges — the manifest carries an exact version per deploy.
    Version: string
}

/// Dependency reference. External services the application requires
/// (databases, queues, third-party APIs). The substrate does not
/// provision dependencies — the operator's CLI is responsible for
/// the side-effect — but it records them so the deploy plane can
/// gate the deploy on dependency availability + audit dependency
/// changes between deploys.
type DeployManifestDependency = {
    /// Free-form name the application uses to refer to the
    /// dependency (e.g. `"primary-postgres"`, `"event-bus"`).
    Name: string
    /// Free-form kind label. Operator-defined vocabulary (e.g.
    /// `"postgres:16"`, `"redis"`, `"kafka"`).
    Kind: string
    /// Optional connection-string template. When `Some`, the
    /// deploy plane resolves `{secret:NAME}` placeholders against
    /// `DeployManifestSecret` entries at deploy time.
    ConnectionTemplate: string option
}

/// Full deploy manifest. The typed surface every deploy plane
/// composes against. `ExtensionFields` carries unknown top-level
/// fields from a future-schema-version manifest so this runtime can
/// round-trip without dropping data — the field is opaque to the
/// substrate but visible to operator tooling.
type DeployManifest = {
    /// Schema version this manifest was authored against. The
    /// substrate accepts `DeployManifest.SchemaVersion` exactly;
    /// higher versions surface as
    /// `ManifestValidationError.UnsupportedSchemaVersion` and the
    /// caller upgrades the substrate before retrying.
    SchemaVersion: int
    /// Application identity.
    App: DeployManifestApp
    /// Runtime selector + healthcheck.
    Runtime: DeployManifestRuntime
    /// Secrets the application reads at startup. Resolved at deploy
    /// time by the operator's CLI; not transmitted across the
    /// manifest boundary.
    Secrets: DeployManifestSecret list
    /// Custom domains the deploy plane should route to this
    /// application.
    Domains: DeployManifestDomain list
    /// Modules the consumer's composition root composes.
    Modules: DeployManifestModuleRef list
    /// External dependencies (databases, queues, third-party APIs).
    Dependencies: DeployManifestDependency list
    /// Future-schema-version extension fields, preserved verbatim.
    /// Key = top-level field name; value = opaque string the
    /// serialiser captured. Empty for `v1`-authored manifests.
    ExtensionFields: Map<string, string>
}

/// Validation outcome. The substrate ships a structural validator
/// (`DeployManifest.validate`); operator-side CLIs layer additional
/// validation on top (slug-uniqueness, secret-source-exists, etc.).
type ManifestValidationError =
    /// Schema version higher than the substrate supports. The caller
    /// upgrades the substrate before retrying — downgrading the
    /// manifest is not a substrate-side option.
    | UnsupportedSchemaVersion of seen: int * supported: int
    /// `App.Slug` violates the DNS-label format (empty, too long, or
    /// contains characters outside `[a-z0-9-]`).
    | InvalidSlug of slug: string * reason: string
    /// A required field was empty when content was required (e.g.
    /// `App.Name` is `""`, `Runtime.Healthcheck.Path` is `""`).
    | MissingRequiredField of field: string
    /// `Domains` contains the same hostname twice. The deploy plane
    /// cannot route a single hostname to two destinations.
    | DuplicateDomain of hostname: string
    /// `Modules` contains the same package id twice with different
    /// versions. The runtime cannot load two versions of the same
    /// package simultaneously.
    | ConflictingModuleVersions of packageId: string * versions: string list

/// Helpers + structural validator. Operator-side CLIs add their own
/// validation pass on top (slug-uniqueness against `ITenantFleet`,
/// secret-source resolution against `ISecretStore`, etc.).
[<RequireQualifiedAccess>]
module DeployManifest =
    /// Schema version. Bumped only when a breaking change to the typed
    /// surface lands (a renamed field, a removed required field, a
    /// retyping). Additive changes that consumers can ignore (a new
    /// optional field, a new variant on an extension DU) do not bump.
    /// Substrate ships `1`; future operator-side CLIs MAY emit higher
    /// versions, in which case
    /// `ManifestValidationError.UnsupportedSchemaVersion` is the
    /// contract failure path.
    [<Literal>]
    let SchemaVersion = 1

    /// Empty manifest at the current schema version with placeholder
    /// app identity. Use as a starting point for builder-style
    /// construction in tests or interactive tools; production
    /// consumers typically deserialise from disk.
    let empty: DeployManifest = {
        SchemaVersion = SchemaVersion
        App = { Name = ""; Slug = ""; Region = "" }
        Runtime = {
            Framework = ""
            Image = None
            Healthcheck = {
                Path = "/health"
                Port = None
                InitialDelay = TimeSpan.FromSeconds 5.0
                Interval = TimeSpan.FromSeconds 10.0
            }
        }
        Secrets = []
        Domains = []
        Modules = []
        Dependencies = []
        ExtensionFields = Map.empty
    }

    /// DNS-label validity per RFC 1035 (relaxed: we allow leading
    /// digit because cloud providers commonly do, but reject
    /// uppercase to keep the slug case-insensitive on every
    /// container scheduler we've seen). Substrate-side check only;
    /// operator-side CLIs layer uniqueness on top via
    /// `ITenantFleet.IsSlugAvailable`.
    let isValidSlug (slug: string) : bool =
        if String.IsNullOrEmpty slug then
            false
        elif slug.Length > 63 then
            false
        else
            slug
            |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-')
            && not (slug.StartsWith "-")
            && not (slug.EndsWith "-")

    /// Structural validation pass. Returns every error found (does
    /// not short-circuit on the first failure) — operators get one
    /// round-trip per validate call.
    let validate (manifest: DeployManifest) : Result<unit, ManifestValidationError list> =
        let errors = ResizeArray<ManifestValidationError>()

        if manifest.SchemaVersion > SchemaVersion then
            errors.Add(UnsupportedSchemaVersion(manifest.SchemaVersion, SchemaVersion))

        if String.IsNullOrEmpty manifest.App.Name then
            errors.Add(MissingRequiredField "App.Name")

        if not (isValidSlug manifest.App.Slug) then
            errors.Add(
                InvalidSlug(
                    manifest.App.Slug,
                    "must be lowercase ASCII letters / digits / hyphens, 1-63 chars, no leading/trailing hyphen"
                )
            )

        if String.IsNullOrEmpty manifest.App.Region then
            errors.Add(MissingRequiredField "App.Region")

        if String.IsNullOrEmpty manifest.Runtime.Framework then
            errors.Add(MissingRequiredField "Runtime.Framework")

        if String.IsNullOrEmpty manifest.Runtime.Healthcheck.Path then
            errors.Add(MissingRequiredField "Runtime.Healthcheck.Path")

        let domainDupes =
            manifest.Domains
            |> List.groupBy _.Hostname
            |> List.filter (fun (_, xs) -> xs.Length > 1)
            |> List.map fst

        for h in domainDupes do
            errors.Add(DuplicateDomain h)

        let moduleConflicts =
            manifest.Modules
            |> List.groupBy _.PackageId
            |> List.map (fun (pid, xs) -> pid, xs |> List.map _.Version |> List.distinct)
            |> List.filter (fun (_, vs) -> vs.Length > 1)

        for (pid, vs) in moduleConflicts do
            errors.Add(ConflictingModuleVersions(pid, vs))

        if errors.Count = 0 then Ok() else Error(List.ofSeq errors)
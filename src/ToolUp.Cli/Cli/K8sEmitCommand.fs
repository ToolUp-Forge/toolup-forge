// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup k8s emit` — turn a deploy manifest into a Helm chart or a
/// flat Kubernetes manifest set, one Deployment per process role. The
/// build-time companion to `docker emit`: it reuses that host's image
/// spec (container port, non-root uid 10001) rather than defining a
/// second container contract, and it ships no runtime surface — an app
/// that never deploys to Kubernetes is byte-for-byte unchanged.
///
/// Pure BCL (the `toolup` CLI carries no SDK dependency — see
/// `ToolUp.Cli.Tests.fsproj`'s "the `toolup` CLI itself stays pure-BCL"
/// note). The manifest records and the validator below therefore
/// *mirror* `DeployManifest` and its `validate`, and a
/// parity test in `ToolUp.Cli.Tests` pins the two together — the same
/// anti-drift mechanism `memberships doctor` uses against
/// `MembershipDoctor`. Serialisation living here rather than in the
/// substrate is what `DeployManifestTypes.fs` asks for in its header:
/// "TOML / YAML / JSON / F# DSL serialisers live in the operator's CLI".
///
/// What the emitter does NOT consume, deliberately: `app.region` (a
/// placement label for the operator's deploy plane, not a Kubernetes
/// object), `modules` (a composition-root fact resolved at build time)
/// and `dependencies` (external services the operator provisions). They
/// are read and preserved, never silently reinterpreted as cluster
/// objects.
module ToolUp.Cli.K8sEmitCommand

open System
open System.IO
open System.Text.Json
open ToolUp.Cli.Dispatch

// ─── Deploy-manifest mirror (pinned to the DeployManifest substrate) ──

/// Identity of the application being deployed. Mirrors
/// `DeployManifestApp`.
type ManifestApp = {
    /// Human-readable application name.
    Name: string
    /// DNS-safe slug; the stem of every emitted resource name.
    Slug: string
    /// Operator-defined deployment region. Carried, not emitted.
    Region: string
}

/// Healthcheck declaration. Mirrors `DeployManifestHealthcheck`, with
/// the substrate's two `TimeSpan` fields carried as whole seconds —
/// Kubernetes probe fields are integer seconds, so the wire shape does
/// not pretend to a precision the target cannot express.
type ManifestHealthcheck = {
    /// HTTP path the liveness probe hits. Defaults to `/health`.
    Path: string
    /// Port the probes hit, and the port the container binds. `None`
    /// means the image's own default (5000).
    Port: int option
    /// Seconds before the first probe fires.
    InitialDelaySeconds: int
    /// Seconds between probes.
    IntervalSeconds: int
}

/// Runtime selector. Mirrors `DeployManifestRuntime`.
type ManifestRuntime = {
    /// Free-form framework label; operator-defined vocabulary.
    Framework: string
    /// Fully-qualified container image reference, when the consumer
    /// ships its own image. `None` means the deploy plane builds from
    /// source — in which case `--image` must supply the reference.
    Image: string option
    /// Probe declaration the emitted `livenessProbe` / `readinessProbe`
    /// are derived from.
    Healthcheck: ManifestHealthcheck
}

/// Secret reference. Mirrors `DeployManifestSecret`. Carries intent
/// only — the emitter never writes secret material.
type ManifestSecret = {
    /// Environment-variable name the container reads the secret by.
    Name: string
    /// Where the operator resolves the secret from. Emitted as a YAML
    /// comment beside the reference.
    Source: string
}

/// Custom domain declaration. Mirrors `DeployManifestDomain`.
type ManifestDomain = {
    /// Fully-qualified hostname (no scheme, no path).
    Hostname: string
    /// Free-form TLS mode label. Empty or `none` emits no TLS entry.
    TlsMode: string
}

/// Module reference. Mirrors `DeployManifestModuleRef`. Read and
/// validated (for conflicting versions), not emitted.
type ManifestModuleRef = {
    /// NuGet package id.
    PackageId: string
    /// Pinned package version.
    Version: string
}

/// Dependency reference. Mirrors `DeployManifestDependency`. Read, not
/// emitted — provisioning is the operator's act.
type ManifestDependency = {
    /// Name the application refers to the dependency by.
    Name: string
    /// Free-form kind label.
    Kind: string
    /// Optional connection-string template.
    ConnectionTemplate: string option
}

/// Full deploy manifest. Mirrors `DeployManifest`.
type Manifest = {
    /// Schema version the manifest was authored against.
    SchemaVersion: int
    /// Application identity.
    App: ManifestApp
    /// Runtime selector + healthcheck.
    Runtime: ManifestRuntime
    /// Secrets the application reads at startup.
    Secrets: ManifestSecret list
    /// Custom domains to route to this application.
    Domains: ManifestDomain list
    /// Modules the consumer's composition root composes.
    Modules: ManifestModuleRef list
    /// External dependencies.
    Dependencies: ManifestDependency list
    /// Unknown top-level fields, preserved verbatim as raw JSON so a
    /// newer-schema manifest round-trips through this reader without
    /// losing data.
    ExtensionFields: Map<string, string>
}

/// Validation outcome. Mirrors `ManifestValidationError` case for case
/// — the parity test compares this union against the substrate's over
/// the same inputs.
type ValidationError =
    /// Schema version higher than this reader supports.
    | UnsupportedSchemaVersion of seen: int * supported: int
    /// `app.slug` violates the DNS-label format.
    | InvalidSlug of slug: string * reason: string
    /// A required field was empty when content was required.
    | MissingRequiredField of field: string
    /// `domains` contains the same hostname twice.
    | DuplicateDomain of hostname: string
    /// `modules` contains one package id at two versions.
    | ConflictingModuleVersions of packageId: string * versions: string list

/// Schema version this reader supports. Mirrors
/// `DeployManifest.SchemaVersion`.
[<Literal>]
let SupportedSchemaVersion = 1

/// Container port the shipped Docker image binds
/// (`templates/platformsdk-docker/Dockerfile.template`:
/// `ASPNETCORE_URLS=http://+:5000`, `EXPOSE 5000`). Used when the
/// manifest's healthcheck declares no port.
[<Literal>]
let DefaultContainerPort = 5000

/// Readiness-probe `timeoutSeconds`. Mirrors
/// `IHealthCheck.defaultTimeout` (5s) —
/// the per-probe ceiling the SDK's aggregator enforces, so a readiness
/// probe that waits longer would be waiting on a request the server has
/// already abandoned.
[<Literal>]
let DefaultProbeTimeoutSeconds = 5

/// Liveness-probe `timeoutSeconds`: twice the readiness budget. A
/// readiness timeout de-lists a pod from its Service; a liveness
/// timeout restarts it. The more destructive verdict gets the wider
/// margin — which is also exactly the 10s/5s split technical-guide
/// chapter 14 §Kubernetes hand-wrote before this command existed.
[<Literal>]
let LivenessProbeTimeoutSeconds = 10

/// Readiness endpoint the SDK mounts for every registered probe
/// (`Compose/ConfigurePipeline.fs` — `MapHealthChecks "/ready"`). Not
/// operator-overridable: it is an SDK-mounted route, not a policy.
[<Literal>]
let ReadinessPath = "/ready"

/// DNS-label validity. Mirrors `DeployManifest.isValidSlug`.
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

/// Structural validation pass. Mirrors `DeployManifest.validate`:
/// returns every error found rather than short-circuiting, so an
/// operator gets one round-trip per call.
let validate (manifest: Manifest) : Result<unit, ValidationError list> =
    let errors = ResizeArray<ValidationError>()

    if manifest.SchemaVersion > SupportedSchemaVersion then
        errors.Add(UnsupportedSchemaVersion(manifest.SchemaVersion, SupportedSchemaVersion))

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

/// Human-readable rendering of a validation error — one line per error,
/// printed to stderr before the command refuses.
let describeError (e: ValidationError) : string =
    match e with
    | UnsupportedSchemaVersion(seen, supported) ->
        sprintf "schemaVersion %d is newer than this CLI supports (%d) — upgrade the tool" seen supported
    | InvalidSlug(slug, reason) -> sprintf "app.slug '%s' is invalid: %s" slug reason
    | MissingRequiredField field -> sprintf "required field %s is empty" field
    | DuplicateDomain hostname -> sprintf "domains contains '%s' twice" hostname
    | ConflictingModuleVersions(packageId, versions) ->
        sprintf "modules pins %s at %s" packageId (String.concat " and " versions)

// ─── Manifest reader ────────────────────────────────────────────────
//
// Permissive where the substrate validator is authoritative (a missing
// string reads as "" so `validate` reports it uniformly), strict about
// shape (a missing `app` object or a string where a number belongs is a
// reader error — the validator has no vocabulary for those).

let private knownTopLevel =
    set [
        "schemaVersion"
        "app"
        "runtime"
        "secrets"
        "domains"
        "modules"
        "dependencies"
    ]

let private prop (o: JsonElement) (name: string) : JsonElement option =
    match o.TryGetProperty name with
    | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
    | _ -> None

let private str (o: JsonElement) (name: string) : string =
    match prop o name with
    | Some v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | Some v -> failwithf "field '%s' must be a string (got %O)" name v.ValueKind
    | None -> ""

let private strOpt (o: JsonElement) (name: string) : string option =
    match prop o name with
    | Some v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | Some v -> failwithf "field '%s' must be a string (got %O)" name v.ValueKind
    | None -> None

let private intOpt (o: JsonElement) (name: string) : int option =
    match prop o name with
    | Some v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
    | Some v -> failwithf "field '%s' must be a number (got %O)" name v.ValueKind
    | None -> None

let private intOr (o: JsonElement) (name: string) (fallback: int) =
    intOpt o name |> Option.defaultValue fallback

let private objOf (o: JsonElement) (name: string) : JsonElement =
    match prop o name with
    | Some v when v.ValueKind = JsonValueKind.Object -> v
    | Some v -> failwithf "field '%s' must be an object (got %O)" name v.ValueKind
    | None -> failwithf "required field '%s' is missing" name

let private arrayOf (o: JsonElement) (name: string) : JsonElement list =
    match prop o name with
    | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
    | Some v -> failwithf "field '%s' must be an array (got %O)" name v.ValueKind
    | None -> []

/// Read a deploy manifest from its JSON wire form. The shape is
/// `DeployManifest` field-for-field in camelCase; the two `TimeSpan`
/// fields are `initialDelaySeconds` / `intervalSeconds`. Unknown
/// top-level fields are preserved verbatim in `ExtensionFields`.
let parseManifest (json: string) : Result<Manifest, string> =
    try
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            failwith "the deploy manifest must be a JSON object"

        let schemaVersion =
            match intOpt root "schemaVersion" with
            | Some v -> v
            | None -> failwith "required field 'schemaVersion' is missing"

        let app = objOf root "app"
        let runtime = objOf root "runtime"
        let healthcheck = objOf runtime "healthcheck"

        let extensions =
            root.EnumerateObject()
            |> Seq.filter (fun p -> not (knownTopLevel.Contains p.Name))
            |> Seq.map (fun p -> p.Name, p.Value.GetRawText())
            |> Map.ofSeq

        Ok {
            SchemaVersion = schemaVersion
            App = {
                Name = str app "name"
                Slug = str app "slug"
                Region = str app "region"
            }
            Runtime = {
                Framework = str runtime "framework"
                Image = strOpt runtime "image"
                Healthcheck = {
                    Path = str healthcheck "path"
                    Port = intOpt healthcheck "port"
                    InitialDelaySeconds = intOr healthcheck "initialDelaySeconds" 5
                    IntervalSeconds = intOr healthcheck "intervalSeconds" 10
                }
            }
            Secrets =
                arrayOf root "secrets"
                |> List.map (fun e -> {
                    Name = str e "name"
                    Source = str e "source"
                })
            Domains =
                arrayOf root "domains"
                |> List.map (fun e -> {
                    Hostname = str e "hostname"
                    TlsMode = str e "tlsMode"
                })
            Modules =
                arrayOf root "modules"
                |> List.map (fun e -> {
                    PackageId = str e "packageId"
                    Version = str e "version"
                })
            Dependencies =
                arrayOf root "dependencies"
                |> List.map (fun e -> {
                    Name = str e "name"
                    Kind = str e "kind"
                    ConnectionTemplate = strOpt e "connectionTemplate"
                })
            ExtensionFields = extensions
        }
    with
    | Failure message -> Error message
    | :? JsonException as ex -> Error(sprintf "invalid JSON: %s" ex.Message)
    | :? InvalidOperationException as ex -> Error(sprintf "unexpected field type: %s" ex.Message)
    | :? FormatException as ex -> Error(sprintf "unexpected field value: %s" ex.Message)

// ─── Roles ──────────────────────────────────────────────────────────

/// One process role to emit a Deployment for — a `ServerConfig`
/// `ProcessProfile` value plus the naming, replica and Service
/// decisions that follow from it.
type Role = {
    /// The `TOOLUP_PROCESS_PROFILE` value this Deployment sets.
    Profile: string
    /// `app.kubernetes.io/component` label, and the file-name suffix.
    Component: string
    /// Whether the role mounts an HTTP pipeline, and so gets a Service
    /// and can back an Ingress.
    ServesHttp: bool
    /// Replica count. Worker and dispatcher roles are pinned to 1:
    /// `IDistributedLock` (Phase 9i) is unshipped, so a second replica
    /// duplicate-fires every scheduler tick (technical-guide chapter 14).
    Replicas: int
}

/// The four `ProcessProfile` roles, keyed by their profile value. The
/// parity test pins this set against the `ProcessProfile` union so a
/// fifth case cannot land without this list noticing.
let roles = [
    {
        Profile = "AllInOne"
        Component = "app"
        ServesHttp = true
        Replicas = 2
    }
    {
        Profile = "WebOnly"
        Component = "web"
        ServesHttp = true
        Replicas = 2
    }
    {
        Profile = "WorkerOnly"
        Component = "worker"
        ServesHttp = false
        Replicas = 1
    }
    {
        Profile = "DispatcherOnly"
        Component = "dispatcher"
        ServesHttp = false
        Replicas = 1
    }
]

/// Resolve a `--profile` token (case-insensitively) to its role.
let tryRole (profile: string) : Role option =
    roles
    |> List.tryFind (fun r -> String.Equals(r.Profile, profile, StringComparison.OrdinalIgnoreCase))

/// Resource name for a role: the bare slug for `AllInOne` (which is by
/// definition the whole application, so nothing can collide with it)
/// and `<slug>-<component>` for every split role.
let resourceName (slug: string) (role: Role) =
    if role.Component = "app" then
        slug
    else
        slug + "-" + role.Component

// ─── Emission ───────────────────────────────────────────────────────

/// Output shape. `Helm` writes a chart whose image coordinates, replica
/// counts and Secret name are `values.yaml` knobs; `Flat` writes the
/// same objects with those values resolved in place.
type Format =
    /// A Helm chart: `Chart.yaml` + `values.yaml` + `templates/`.
    | Helm
    /// Plain manifests, one file per object, ready for `kubectl apply`.
    | Flat

/// One emitted file: a path relative to the output directory, and its
/// content. `plan` produces the whole set before anything is written,
/// so an emit is all-or-nothing.
type EmittedFile = {
    /// Path relative to the output directory, using `/` separators.
    RelativePath: string
    /// Full file content.
    Content: string
}

let private templateOf (name: string) = Templating.readEmbedded ("k8s/" + name)

/// DNS-safe stem for a hostname, used to name its TLS Secret:
/// `app.example.com` → `app-example-com`, `*.example.com` →
/// `wildcard-example-com`.
let hostStem (hostname: string) =
    hostname.Replace("*", "wildcard").Replace('.', '-').Replace('_', '-').Trim('-').ToLowerInvariant()

/// Split an image reference into repository and tag. A reference with
/// no tag reads as `:latest`; a registry port (`host:5000/img`) is not
/// mistaken for a tag because only a colon *after* the last slash counts.
let splitImage (image: string) =
    let lastSlash = image.LastIndexOf '/'
    let lastColon = image.LastIndexOf ':'

    if lastColon > lastSlash && lastColon >= 0 then
        image.Substring(0, lastColon), image.Substring(lastColon + 1)
    else
        image, "latest"

let private secretEnvBlock (format: Format) (slug: string) (secrets: ManifestSecret list) =
    let secretName =
        match format with
        | Helm -> "{{ .Values.secrets.name }}"
        | Flat -> slug + "-secrets"

    if List.isEmpty secrets then
        "            # no secrets declared in the deploy manifest"
    else
        secrets
        |> List.collect (fun s -> [
            sprintf "            # source: %s" s.Source
            sprintf "            - name: %s" s.Name
            "              valueFrom:"
            "                secretKeyRef:"
            sprintf "                  name: \"%s\"" secretName
            sprintf "                  key: %s" s.Name
        ])
        |> String.concat "\n"

let private tlsBlock (slug: string) (domains: ManifestDomain list) =
    let secured =
        domains
        |> List.filter (fun d ->
            not (String.IsNullOrWhiteSpace d.TlsMode)
            && not (String.Equals(d.TlsMode, "none", StringComparison.OrdinalIgnoreCase)))

    if List.isEmpty secured then
        "  # no TLS: no domain declares a tlsMode"
    else
        [
            "  tls:"
            yield!
                secured
                |> List.collect (fun d -> [
                    sprintf "    # tlsMode: %s" d.TlsMode
                    "    - hosts:"
                    sprintf "        - \"%s\"" d.Hostname
                    sprintf "      secretName: \"%s-tls-%s\"" slug (hostStem d.Hostname)
                ])
        ]
        |> String.concat "\n"

let private rulesBlock (backend: string) (port: int) (domains: ManifestDomain list) =
    domains
    |> List.collect (fun d -> [
        sprintf "    - host: \"%s\"" d.Hostname
        "      http:"
        "        paths:"
        "          - path: /"
        "            pathType: Prefix"
        "            backend:"
        "              service:"
        sprintf "                name: \"%s\"" backend
        "                port:"
        sprintf "                  number: %d" port
    ])
    |> String.concat "\n"

/// Service port every emitted Service publishes. Fixed at the HTTP
/// default so an Ingress backend reference needs no per-install lookup;
/// the container-side port is the manifest's.
[<Literal>]
let ServicePort = 80

let private renderDeployment (format: Format) (m: Manifest) (image: string) (role: Role) =
    let slug = m.App.Slug
    let port = m.Runtime.Healthcheck.Port |> Option.defaultValue DefaultContainerPort

    let imageToken =
        match format with
        | Helm -> "{{ .Values.image.repository }}:{{ .Values.image.tag }}"
        | Flat -> image

    let replicaToken =
        match format with
        | Helm -> sprintf "{{ .Values.replicas.%s }}" role.Component
        | Flat -> string role.Replicas

    templateOf "deployment.yaml.template"
    |> Templating.substitute [
        "name", resourceName slug role
        "slug", slug
        "role", role.Component
        "profile", role.Profile
        "replicas", replicaToken
        "image", imageToken
        "container-port", string port
        "probe-port", string port
        "liveness-path", m.Runtime.Healthcheck.Path
        "readiness-path", ReadinessPath
        "initial-delay", string m.Runtime.Healthcheck.InitialDelaySeconds
        "period", string m.Runtime.Healthcheck.IntervalSeconds
        "liveness-timeout", string LivenessProbeTimeoutSeconds
        "readiness-timeout", string DefaultProbeTimeoutSeconds
        "secret-env", secretEnvBlock format slug m.Secrets
    ]

let private renderService (m: Manifest) (role: Role) =
    templateOf "service.yaml.template"
    |> Templating.substitute [
        "name", resourceName m.App.Slug role
        "slug", m.App.Slug
        "role", role.Component
        "service-port", string ServicePort
    ]

let private renderIngress (m: Manifest) (role: Role) =
    templateOf "ingress.yaml.template"
    |> Templating.substitute [
        "name", m.App.Slug
        "slug", m.App.Slug
        "role", role.Component
        "tls-block", tlsBlock m.App.Slug m.Domains
        "rules-block", rulesBlock (resourceName m.App.Slug role) ServicePort m.Domains
    ]

let private renderChart (m: Manifest) (image: string) =
    let _, tag = splitImage image

    templateOf "Chart.yaml.template"
    |> Templating.substitute [
        "slug", m.App.Slug
        "app-name", m.App.Name
        "schema-version", string m.SchemaVersion
        "app-version", tag
    ]

let private renderValues (m: Manifest) (image: string) (selected: Role list) =
    let repository, tag = splitImage image

    let replicaBlock =
        selected
        |> List.map (fun r -> sprintf "  %s: %d" r.Component r.Replicas)
        |> String.concat "\n"

    templateOf "values.yaml.template"
    |> Templating.substitute [
        "image-repository", repository
        "image-tag", tag
        "secret-name", m.App.Slug + "-secrets"
        "replica-block", replicaBlock
    ]

let private renderNotes (m: Manifest) (image: string) (selected: Role list) =
    let repository, tag = splitImage image

    templateOf "NOTES.txt.template"
    |> Templating.substitute [
        "app-name", m.App.Name
        "slug", m.App.Slug
        "role-list", (selected |> List.map _.Profile |> String.concat ", ")
        "image-repository", repository
        "image-tag", tag
        "secret-name", m.App.Slug + "-secrets"
        "liveness-path", m.Runtime.Healthcheck.Path
        "readiness-path", ReadinessPath
    ]

/// Build the complete emission for a manifest, an image reference, a
/// role set and a format. Pure: reads embedded templates, touches no
/// disk. Every refusal the emitter can reach before writing is an
/// `Error` here rather than a half-written directory.
let plan (format: Format) (m: Manifest) (image: string) (selected: Role list) : Result<EmittedFile list, string> =
    let serving = selected |> List.filter _.ServesHttp

    if List.isEmpty selected then
        Error "no roles selected — pass --profile with at least one ProcessProfile value"
    elif not (List.isEmpty m.Domains) && List.isEmpty serving then
        Error
            "the manifest declares domains but no selected role mounts an HTTP pipeline — add AllInOne or WebOnly to --profile, or drop the domains"
    elif List.length serving > 1 then
        Error
            "more than one selected role mounts an HTTP pipeline — an Ingress backend would be ambiguous; emit one HTTP-serving role at a time"
    else
        let prefix =
            match format with
            | Helm -> "templates/"
            | Flat -> ""

        let file path content = {
            RelativePath = path
            Content = content
        }

        let deployments =
            selected
            |> List.map (fun r ->
                file (sprintf "%sdeployment-%s.yaml" prefix r.Component) (renderDeployment format m image r))

        let services =
            serving
            |> List.map (fun r -> file (sprintf "%sservice-%s.yaml" prefix r.Component) (renderService m r))

        let ingress =
            match serving, m.Domains with
            | [ r ], _ :: _ -> [ file (prefix + "ingress.yaml") (renderIngress m r) ]
            | _ -> []

        let chartFiles =
            match format with
            | Flat -> []
            | Helm -> [
                file "Chart.yaml" (renderChart m image)
                file "values.yaml" (renderValues m image selected)
                file ".helmignore" (templateOf "helmignore.template")
                file "templates/NOTES.txt" (renderNotes m image selected)
              ]

        Ok(chartFiles @ deployments @ services @ ingress)

// ─── Command ────────────────────────────────────────────────────────

/// Parsed `k8s emit` invocation.
type Options = {
    /// Path to the deploy manifest JSON. Required.
    ManifestPath: string option
    /// Comma-separated `ProcessProfile` values; one Deployment each.
    Profiles: string
    /// `helm` or `flat`.
    Format: string
    /// Directory the files are written into.
    OutputDir: string
    /// Container image reference, overriding the manifest's
    /// `runtime.image` (and required when the manifest carries none).
    Image: string option
    /// Overwrite existing files instead of refusing.
    Force: bool
}

let private defaults = {
    ManifestPath = None
    Profiles = "AllInOne"
    Format = "helm"
    OutputDir = "."
    Image = None
    Force = false
}

/// Parse the residual args after `k8s emit`. Pure: no IO, no value
/// validation beyond shape — `Error` carries a usage message.
let rec private parse (opts: Options) (args: string list) : Result<Options, string> =
    match args with
    | [] -> Ok opts
    | "--manifest" :: v :: rest -> parse { opts with ManifestPath = Some v } rest
    | "--profile" :: v :: rest -> parse { opts with Profiles = v } rest
    | "--format" :: v :: rest -> parse { opts with Format = v } rest
    // `--output-dir` is the spelling every sibling subcommand uses;
    // `--out` is the one this command was specified with. Both work.
    | "--out" :: v :: rest
    | "--output-dir" :: v :: rest -> parse { opts with OutputDir = v } rest
    | "--image" :: v :: rest -> parse { opts with Image = Some v } rest
    | "--force" :: rest -> parse { opts with Force = true } rest
    | ("--manifest" | "--profile" | "--format" | "--out" | "--output-dir" | "--image") :: [] ->
        Error(sprintf "missing value for %s" (List.head args))
    | unknown :: _ -> Error(sprintf "unrecognised argument: %s" unknown)

/// Resolve the `--format` token.
let tryFormat (token: string) : Format option =
    match token.ToLowerInvariant() with
    | "helm" -> Some Helm
    | "flat" -> Some Flat
    | _ -> None

/// Resolve the `--profile` token list to roles, rejecting an unknown
/// name, a repeat, and `AllInOne` paired with a split role (which is a
/// contradiction: `AllInOne` already runs every subsystem).
let resolveRoles (token: string) : Result<Role list, string> =
    let names =
        token.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> List.ofArray

    let rec resolve acc remaining =
        match remaining with
        | [] -> Ok(List.rev acc)
        | (name: string) :: rest ->
            match tryRole name with
            | None ->
                Error(
                    sprintf
                        "unknown ProcessProfile '%s' — expected one of %s"
                        name
                        (roles |> List.map _.Profile |> String.concat ", ")
                )
            | Some role when acc |> List.exists (fun (r: Role) -> r.Profile = role.Profile) ->
                Error(sprintf "--profile names %s twice" role.Profile)
            | Some role -> resolve (role :: acc) rest

    match names with
    | [] -> Error "--profile needs at least one ProcessProfile value"
    | _ ->
        match resolve [] names with
        | Error e -> Error e
        | Ok selected when
            List.length selected > 1
            && selected |> List.exists (fun r -> r.Profile = "AllInOne")
            ->
            Error "AllInOne already runs every subsystem — do not pair it with a split role"
        | Ok selected -> Ok selected

let private helpText = [
    "Usage: toolup k8s emit --manifest <deploy.json> [--profile <ProcessProfile[,...]>]"
    "                       [--format helm|flat] [--out <dir>] [--image <ref>] [--force]"
    ""
    "Emits a Helm chart (default) or a flat Kubernetes manifest set from a deploy"
    "manifest: one Deployment per process role, a Service for each HTTP-serving role,"
    "and an Ingress when the manifest declares domains. Build-time only — nothing this"
    "command emits is loaded at runtime."
    ""
    "Options:"
    "  --manifest <file>    Deploy-manifest JSON (schema v1). (required)"
    "  --profile <list>     Comma-separated ProcessProfile values — AllInOne, WebOnly,"
    "                       WorkerOnly, DispatcherOnly. One Deployment each. Default AllInOne."
    "  --format helm|flat   Helm chart, or plain manifests for `kubectl apply`. Default helm."
    "  --out <dir>          Directory to write into (alias: --output-dir). Default '.'."
    "  --image <ref>        Container image reference. Overrides the manifest's"
    "                       runtime.image, and is required when the manifest has none."
    "  --force              Overwrite existing files instead of refusing."
    ""
    "Probes follow the endpoints the SDK mounts: liveness on the manifest's"
    "runtime.healthcheck.path (default /health), readiness on /ready. Worker and"
    "dispatcher roles are pinned to one replica until IDistributedLock ships."
    ""
    "Secrets are referenced, never written: create the Secret named in the output with"
    "one key per secrets[] entry before installing."
]

let private usageError (message: string) =
    eprintfn "toolup k8s emit: %s" message
    eprintfn ""
    helpText |> List.iter (eprintfn "%s")
    ExitUsage

let private runtimeError (message: string) =
    eprintfn "toolup k8s emit: %s" message
    ExitRuntimeError

/// Refusal reason, carrying which exit code it maps to: a bad
/// invocation prints the help (`ExitUsage`), a bad *input* does not
/// (`ExitRuntimeError`) — re-reading the usage text does not help an
/// operator whose manifest fails validation.
type private Refusal =
    | Usage of string
    | Runtime of string list

/// Everything between "the args parsed" and "the files are written":
/// pure but for reading the manifest off disk, so every refusal is
/// reached before a single file is created.
let private resolveEmission (opts: Options) : Result<Format * EmittedFile list, Refusal> =
    let usage m = Error(Usage m)
    let runtime m = Error(Runtime [ m ])

    match opts.ManifestPath with
    | None -> usage "--manifest is required"
    | Some manifestPath ->
        match tryFormat opts.Format with
        | None -> usage (sprintf "--format must be 'helm' or 'flat' (got '%s')" opts.Format)
        | Some format ->
            match resolveRoles opts.Profiles with
            | Error message -> usage message
            | Ok selected ->
                if not (File.Exists manifestPath) then
                    runtime (sprintf "deploy manifest not found: %s" manifestPath)
                else
                    match parseManifest (File.ReadAllText manifestPath) with
                    | Error message -> runtime (sprintf "%s: %s" manifestPath message)
                    | Ok manifest ->
                        match validate manifest with
                        | Error errors ->
                            Error(
                                Runtime(
                                    sprintf "%s is not a valid deploy manifest:" manifestPath
                                    :: (errors |> List.map (fun e -> "  " + describeError e))
                                )
                            )
                        | Ok() ->
                            match opts.Image |> Option.orElse manifest.Runtime.Image with
                            | None ->
                                runtime
                                    "no container image: the manifest's runtime.image is null (the deploy plane would build from source) and --image was not passed"
                            | Some image ->
                                match plan format manifest image selected with
                                | Error message -> runtime message
                                | Ok files -> Ok(format, files)

let private runWith (opts: Options) : int =
    match resolveEmission opts with
    | Error(Usage message) -> usageError message
    | Error(Runtime messages) ->
        match messages with
        | head :: tail ->
            eprintfn "toolup k8s emit: %s" head
            tail |> List.iter (eprintfn "%s")
        | [] -> ()

        ExitRuntimeError
    | Ok(_, files) ->
        let targets =
            files
            |> List.map (fun f ->
                f, Path.Combine(opts.OutputDir, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)))

        let clashes =
            if opts.Force then
                []
            else
                targets |> List.filter (snd >> File.Exists)

        match clashes with
        | _ :: _ ->
            eprintfn "toolup k8s emit: refusing to overwrite existing file(s) (pass --force to overwrite):"

            for (f, _) in clashes do
                eprintfn "  %s" f.RelativePath

            ExitRuntimeError
        | [] ->
            for (f, path) in targets do
                let dir = Path.GetDirectoryName path

                if not (String.IsNullOrEmpty dir) then
                    Directory.CreateDirectory dir |> ignore

                File.WriteAllText(path, f.Content)
                printfn "wrote %s" f.RelativePath

            ExitOk

/// The registered `toolup k8s emit` command.
let command = {
    Path = [ "k8s"; "emit" ]
    Summary = "Emit a Helm chart or flat k8s manifests from a deploy manifest."
    Help = helpText
    Run =
        fun args ->
            match parse defaults args with
            | Error message -> usageError message
            | Ok opts -> runWith opts
}
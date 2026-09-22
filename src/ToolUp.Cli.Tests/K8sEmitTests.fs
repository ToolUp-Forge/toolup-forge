// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.K8sEmitTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Expecto
open FSharp.Reflection
open YamlDotNet.Serialization
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Cli
open ToolUp.Cli.Dispatch

// ─── Phase 194 — `k8s emit` ──────────────────────────────────────────
//
// Two jobs here, and they are different jobs.
//
// 1. **Structure.** The command writes YAML, so the gate parses the YAML
//    back and asserts over the document tree — kinds, one Deployment per
//    role, probe paths and timeouts, secret references, ingress hosts —
//    rather than over the text. A `--format helm` chart is a Go template
//    and not YAML until it is rendered, so the helm arm is rendered
//    first, by a deliberately tiny `{{ .Values.x }}` resolver reading the
//    chart's OWN `values.yaml`. That makes ONE assertion body cover both
//    formats and turns a values/template mismatch — a reference to a
//    value the chart never defines — into a failure rather than a silent
//    empty string at install time. A real `helm lint` runs when
//    `TOOLUP_HELM_PATH` names a binary, and is skipped otherwise: no helm
//    exists on the dev machine or in CI.
//
// 2. **Parity.** The `toolup` CLI is pure-BCL (see the fsproj note), so
//    `K8sEmitCommand` mirrors `DeployManifest` and its `validate` instead
//    of referencing them. These tests pin the mirror to the substrate —
//    the same anti-drift mechanism `memberships doctor` uses against
//    `MembershipDoctor` — over the whole error vocabulary, plus the two
//    substrate constants the emitter derives probe values from and the
//    `ProcessProfile` union the role table enumerates.

// ─── YAML reading ────────────────────────────────────────────────────

let private deserializer = DeserializerBuilder().Build()

let private parseYaml (text: string) : obj =
    deserializer.Deserialize<obj>(new StringReader(text))

let private asMap (node: obj) =
    match node with
    | :? IDictionary<obj, obj> as d -> d
    | other -> failtestf "expected a YAML mapping, got %A" other

let private items (node: obj) =
    match node with
    | :? System.Collections.IEnumerable as e when not (node :? string) -> e |> Seq.cast<obj> |> List.ofSeq
    | other -> failtestf "expected a YAML sequence, got %A" other

let private indexPattern =
    Regex(@"^(?<key>[^\[\]]+)(\[(?<index>\d+)\])?$", RegexOptions.Compiled)

/// Navigate a parsed YAML document by a dotted path, e.g.
/// `spec.template.spec.containers[0].livenessProbe.httpGet.path`.
let rec private dig (node: obj) (path: string) : obj =
    let step (current: obj) (segment: string) =
        let m = indexPattern.Match segment

        if not m.Success then
            failtestf "unreadable path segment '%s'" segment

        let map = asMap current
        let key = m.Groups["key"].Value

        let child =
            match map.TryGetValue(box key) with
            | true, v -> v
            | _ -> failtestf "no key '%s' in %A" key (map.Keys |> Seq.toList)

        if m.Groups["index"].Success then
            items child |> List.item (int m.Groups["index"].Value)
        else
            child

    path.Split '.' |> Array.fold step node

let private digStr (node: obj) (path: string) = string (dig node path)

let private tryDig (node: obj) (path: string) : obj option =
    try
        Some(dig node path)
    with _ ->
        None

// ─── The tiny Helm renderer ──────────────────────────────────────────

let private valuesReference =
    Regex(@"\{\{\s*\.Values\.(?<path>[A-Za-z0-9_.]+)\s*\}\}", RegexOptions.Compiled)

/// Resolve every `{{ .Values.a.b }}` in a chart template against the
/// chart's own `values.yaml`. A reference the values file does not
/// define fails the test — that is the whole point of rendering rather
/// than string-matching.
let private renderChartTemplate (values: obj) (text: string) =
    valuesReference.Replace(
        text,
        MatchEvaluator(fun m ->
            let path = m.Groups["path"].Value

            match tryDig values path with
            | Some v -> string v
            | None -> failtestf "chart template references .Values.%s, which values.yaml does not define" path)
    )

// ─── Fixture ─────────────────────────────────────────────────────────

let private manifestJson =
    """
{
  "schemaVersion": 1,
  "app": { "name": "Acme Portal", "slug": "acme-portal", "region": "eu-west" },
  "runtime": {
    "framework": "dotnet:10",
    "image": "ghcr.io/acme/portal:v1.4.2",
    "healthcheck": { "path": "/health", "port": 5000, "initialDelaySeconds": 30, "intervalSeconds": 30 }
  },
  "secrets": [
    { "name": "DATABASE_URL", "source": "vault://kv/prod/db-url" },
    { "name": "STRIPE_SECRET_KEY", "source": "vault://kv/prod/stripe" }
  ],
  "domains": [ { "hostname": "app.acme.example", "tlsMode": "acme-le" } ],
  "modules": [ { "packageId": "ToolUp.AI", "version": "0.23.0" } ],
  "dependencies": [ { "name": "primary-postgres", "kind": "postgres:16", "connectionTemplate": "Host=db" } ],
  "futureField": { "carried": true }
}
"""

let private tempDir () =
    let path =
        Path.Combine(Path.GetTempPath(), "toolup-k8s-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory path |> ignore
    path

/// Emit into a fresh directory and hand back (directory, exit code).
let private emitInto (json: string) (args: string list) =
    let dir = tempDir ()
    let manifestPath = Path.Combine(dir, "deploy.json")
    File.WriteAllText(manifestPath, json)
    let outDir = Path.Combine(dir, "out")

    let code =
        K8sEmitCommand.command.Run([ "--manifest"; manifestPath; "--out"; outDir ] @ args)

    outDir, code

/// Emit for one format × profile set and return every emitted `.yaml`
/// file as (relative path, parsed document) — helm templates rendered
/// against the chart's values first.
let private emitDocs (format: string) (profiles: string) =
    let outDir, code =
        emitInto manifestJson [ "--format"; format; "--profile"; profiles ]

    Expect.equal code ExitOk (sprintf "emit %s %s succeeds" format profiles)

    let relative (p: string) =
        Path.GetRelativePath(outDir, p).Replace('\\', '/')

    let all =
        Directory.GetFiles(outDir, "*", SearchOption.AllDirectories) |> List.ofArray

    let values =
        if format = "helm" then
            Some(parseYaml (File.ReadAllText(Path.Combine(outDir, "values.yaml"))))
        else
            None

    let docs =
        all
        |> List.filter (fun p -> p.EndsWith ".yaml")
        |> List.map (fun p ->
            let text = File.ReadAllText p

            let rendered =
                match values with
                | Some v when relative p <> "values.yaml" && relative p <> "Chart.yaml" -> renderChartTemplate v text
                | _ -> text

            relative p, parseYaml rendered)

    outDir, (all |> List.map relative), docs

// ─── Substrate conversion (for the parity cases) ─────────────────────

let private toSubstrate (m: K8sEmitCommand.Manifest) : DeployManifest = {
    SchemaVersion = m.SchemaVersion
    App = {
        Name = m.App.Name
        Slug = m.App.Slug
        Region = m.App.Region
    }
    Runtime = {
        Framework = m.Runtime.Framework
        Image = m.Runtime.Image
        Healthcheck = {
            Path = m.Runtime.Healthcheck.Path
            Port = m.Runtime.Healthcheck.Port
            InitialDelay = TimeSpan.FromSeconds(float m.Runtime.Healthcheck.InitialDelaySeconds)
            Interval = TimeSpan.FromSeconds(float m.Runtime.Healthcheck.IntervalSeconds)
        }
    }
    Secrets = m.Secrets |> List.map (fun s -> { Name = s.Name; Source = s.Source })
    Domains =
        m.Domains
        |> List.map (fun d -> {
            Hostname = d.Hostname
            TlsMode = d.TlsMode
        })
    Modules =
        m.Modules
        |> List.map (fun x -> {
            PackageId = x.PackageId
            Version = x.Version
        })
    Dependencies =
        m.Dependencies
        |> List.map (fun d -> {
            Name = d.Name
            Kind = d.Kind
            ConnectionTemplate = d.ConnectionTemplate
        })
    ExtensionFields = m.ExtensionFields
}

/// One comparable key per error, so the two unions can be compared
/// without either side's type leaking into the other's.
let private mirrorKey (e: K8sEmitCommand.ValidationError) =
    match e with
    | K8sEmitCommand.UnsupportedSchemaVersion(seen, supported) -> sprintf "schema(%d,%d)" seen supported
    | K8sEmitCommand.InvalidSlug(slug, _) -> sprintf "slug(%s)" slug
    | K8sEmitCommand.MissingRequiredField field -> sprintf "missing(%s)" field
    | K8sEmitCommand.DuplicateDomain hostname -> sprintf "domain(%s)" hostname
    | K8sEmitCommand.ConflictingModuleVersions(packageId, versions) ->
        sprintf "modules(%s,%s)" packageId (String.concat "|" versions)

let private substrateKey (e: ManifestValidationError) =
    match e with
    | ManifestValidationError.UnsupportedSchemaVersion(seen, supported) -> sprintf "schema(%d,%d)" seen supported
    | ManifestValidationError.InvalidSlug(slug, _) -> sprintf "slug(%s)" slug
    | ManifestValidationError.MissingRequiredField field -> sprintf "missing(%s)" field
    | ManifestValidationError.DuplicateDomain hostname -> sprintf "domain(%s)" hostname
    | ManifestValidationError.ConflictingModuleVersions(packageId, versions) ->
        sprintf "modules(%s,%s)" packageId (String.concat "|" versions)

let private keysOf (result: Result<unit, 'e list>) (key: 'e -> string) =
    match result with
    | Ok() -> []
    | Error errors -> errors |> List.map key |> List.sort

/// A manifest read from `manifestJson` with one field bent, so each
/// parity case exercises exactly one arm of the validator.
let private bend (edit: K8sEmitCommand.Manifest -> K8sEmitCommand.Manifest) =
    match K8sEmitCommand.parseManifest manifestJson with
    | Ok m -> edit m
    | Error e -> failtestf "the fixture manifest must parse: %s" e

// ─── Structural expectations ─────────────────────────────────────────

let private expectedRoles (profiles: string) =
    profiles.Split ','
    |> Array.map (fun p -> K8sEmitCommand.tryRole(p.Trim()).Value)
    |> List.ofArray

let private assertStructure (format: string) (profiles: string) =
    let _, files, docs = emitDocs format profiles
    let selected = expectedRoles profiles
    let prefix = if format = "helm" then "templates/" else ""

    let doc name =
        docs |> List.find (fun (p, _) -> p = name) |> snd

    // One Deployment per role, and no more.
    let deployments =
        docs |> List.filter (fun (_, d) -> tryDig d "kind" = Some(box "Deployment"))

    Expect.equal
        (List.length deployments)
        (List.length selected)
        (sprintf "%s/%s: one Deployment per selected role" format profiles)

    for role in selected do
        let name = sprintf "%sdeployment-%s.yaml" prefix role.Component
        Expect.contains files name (sprintf "%s emitted" name)
        let d = doc name
        Expect.equal (digStr d "apiVersion") "apps/v1" "Deployment apiVersion"
        Expect.equal (digStr d "kind") "Deployment" "kind"
        Expect.equal (digStr d "metadata.name") (K8sEmitCommand.resourceName "acme-portal" role) "resource name"
        Expect.equal (digStr d "spec.replicas") (string role.Replicas) "replica count survives the values round-trip"

        Expect.equal
            (digStr d "spec.template.spec.containers[0].env[0].value")
            role.Profile
            "TOOLUP_PROCESS_PROFILE names this role"

        // Probes: paths against the endpoints the SDK mounts, timeouts
        // derived from IHealthCheck.defaultTimeout, delay/period from the
        // manifest's healthcheck.
        let container = dig d "spec.template.spec.containers[0]"
        Expect.equal (digStr container "livenessProbe.httpGet.path") "/health" "liveness path"
        Expect.equal (digStr container "readinessProbe.httpGet.path") "/ready" "readiness path"
        Expect.equal (digStr container "livenessProbe.httpGet.port") "5000" "liveness port"

        Expect.equal
            (digStr container "livenessProbe.timeoutSeconds")
            (string K8sEmitCommand.LivenessProbeTimeoutSeconds)
            "liveness timeout"

        Expect.equal
            (digStr container "readinessProbe.timeoutSeconds")
            (string K8sEmitCommand.DefaultProbeTimeoutSeconds)
            "readiness timeout"

        Expect.equal (digStr container "readinessProbe.initialDelaySeconds") "30" "initial delay from the manifest"
        Expect.equal (digStr container "readinessProbe.periodSeconds") "30" "period from the manifest"

        // Both secrets reach the container as secretKeyRef entries — never
        // as literal values.
        let env = items (dig container "env") |> List.map (fun e -> digStr e "name")
        Expect.contains env "DATABASE_URL" "first secret is an env entry"
        Expect.contains env "STRIPE_SECRET_KEY" "second secret is an env entry"

        let secretRef =
            dig container "env[2].valueFrom.secretKeyRef"
            |> fun r -> digStr r "name", digStr r "key"

        Expect.equal secretRef ("acme-portal-secrets", "DATABASE_URL") "secretKeyRef names the Secret and the key"

        Expect.isNone
            (tryDig container "env[2].value")
            "a secret is referenced, never inlined — no literal value beside the ref"

    // A Service for every HTTP-serving role, and for no other.
    for role in selected do
        let name = sprintf "%sservice-%s.yaml" prefix role.Component

        if role.ServesHttp then
            Expect.contains files name (sprintf "%s emitted for an HTTP-serving role" name)
            let s = doc name
            Expect.equal (digStr s "kind") "Service" "kind"
            Expect.equal (digStr s "spec.ports[0].targetPort") "http" "Service targets the named container port"
        else
            Expect.isFalse (List.contains name files) (sprintf "%s NOT emitted for a non-HTTP role" name)

    // Ingress: hosts and TLS secret names come from the manifest's domains.
    let ingressName = prefix + "ingress.yaml"
    Expect.contains files ingressName "ingress emitted for a manifest that declares domains"
    let ing = doc ingressName
    Expect.equal (digStr ing "kind") "Ingress" "kind"
    Expect.equal (digStr ing "spec.rules[0].host") "app.acme.example" "ingress host"
    Expect.equal (digStr ing "spec.tls[0].hosts[0]") "app.acme.example" "TLS host"
    Expect.equal (digStr ing "spec.tls[0].secretName") "acme-portal-tls-app-acme-example" "TLS secret name"

    let backend = digStr ing "spec.rules[0].http.paths[0].backend.service.name"
    let servingRole = selected |> List.find _.ServesHttp
    Expect.equal backend (K8sEmitCommand.resourceName "acme-portal" servingRole) "ingress backs the HTTP-serving role"

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "K8sEmit" [
        testList "manifest reader" [
            test "reads the whole v1 shape" {
                match K8sEmitCommand.parseManifest manifestJson with
                | Error e -> failtestf "expected Ok, got %s" e
                | Ok m ->
                    Expect.equal m.SchemaVersion 1 "schemaVersion"
                    Expect.equal m.App.Slug "acme-portal" "slug"
                    Expect.equal m.Runtime.Image (Some "ghcr.io/acme/portal:v1.4.2") "image"
                    Expect.equal m.Runtime.Healthcheck.Port (Some 5000) "probe port"
                    Expect.equal m.Runtime.Healthcheck.IntervalSeconds 30 "interval"
                    Expect.equal (List.length m.Secrets) 2 "two secrets"
                    Expect.equal (List.length m.Domains) 1 "one domain"
                    Expect.equal m.Dependencies.Head.ConnectionTemplate (Some "Host=db") "optional field read"
            }

            test "an unknown top-level field is preserved verbatim" {
                match K8sEmitCommand.parseManifest manifestJson with
                | Error e -> failtestf "expected Ok, got %s" e
                | Ok m ->
                    Expect.isTrue (m.ExtensionFields.ContainsKey "futureField") "carried into ExtensionFields"
                    Expect.stringContains m.ExtensionFields["futureField"] "carried" "verbatim, not reinterpreted"
            }

            test "a missing schemaVersion is a reader error, not a silent v1" {
                let json = manifestJson.Replace("\"schemaVersion\": 1,", "")
                Expect.isError (K8sEmitCommand.parseManifest json) "no schemaVersion → Error"
            }

            test "a wrong-typed field names the field" {
                let json = manifestJson.Replace("\"slug\": \"acme-portal\"", "\"slug\": 7")

                match K8sEmitCommand.parseManifest json with
                | Ok _ -> failtest "expected Error"
                | Error e -> Expect.stringContains e "slug" "the message names the offending field"
            }

            test "malformed JSON is an error, not an exception" {
                Expect.isError (K8sEmitCommand.parseManifest "{ nope") "broken JSON → Error"
            }
        ]

        testList "parity with the DeployManifest substrate" [
            test "the schema version matches" {
                Expect.equal
                    K8sEmitCommand.SupportedSchemaVersion
                    DeployManifest.SchemaVersion
                    "the CLI mirror must support exactly the substrate's schema version"
            }

            test "the readiness probe timeout is IHealthCheck.defaultTimeout" {
                Expect.equal
                    (float K8sEmitCommand.DefaultProbeTimeoutSeconds)
                    IHealthCheck.defaultTimeout.TotalSeconds
                    "the emitted readiness timeout is the substrate's per-probe ceiling, not a second opinion"
            }

            test "the role table enumerates the ProcessProfile union exactly" {
                let unionCases =
                    FSharpType.GetUnionCases typeof<ProcessProfile>
                    |> Array.map _.Name
                    |> Array.sort
                    |> List.ofArray

                let tableCases = K8sEmitCommand.roles |> List.map _.Profile |> List.sort

                Expect.equal
                    tableCases
                    unionCases
                    "a fifth ProcessProfile case must not land without a role row — the emitter would silently skip it"
            }

            test "isValidSlug agrees with the substrate on a spread of slugs" {
                for slug in
                    [
                        "acme-portal"
                        "a"
                        ""
                        "-lead"
                        "trail-"
                        "UPPER"
                        "has_underscore"
                        "9digits"
                        String.replicate 64 "x"
                    ] do
                    Expect.equal
                        (K8sEmitCommand.isValidSlug slug)
                        (DeployManifest.isValidSlug slug)
                        (sprintf "slug '%s'" slug)
            }

            test "validate agrees with the substrate, error for error" {
                let cases = [
                    "valid", bend id
                    "bad slug",
                    bend (fun m -> {
                        m with
                            App = { m.App with Slug = "Acme_Portal" }
                    })
                    "missing name",
                    bend (fun m -> {
                        m with
                            App = { m.App with Name = "" }
                    })
                    "missing region",
                    bend (fun m -> {
                        m with
                            App = { m.App with Region = "" }
                    })
                    "missing framework",
                    bend (fun m -> {
                        m with
                            Runtime = { m.Runtime with Framework = "" }
                    })
                    "missing probe path",
                    bend (fun m -> {
                        m with
                            Runtime = {
                                m.Runtime with
                                    Healthcheck = { m.Runtime.Healthcheck with Path = "" }
                            }
                    })
                    "future schema", bend (fun m -> { m with SchemaVersion = 99 })
                    "duplicate domain",
                    bend (fun m -> {
                        m with
                            Domains = m.Domains @ m.Domains
                    })
                    "conflicting modules",
                    bend (fun m -> {
                        m with
                            Modules =
                                m.Modules
                                @ [
                                    {
                                        PackageId = "ToolUp.AI"
                                        Version = "0.24.0"
                                    }
                                ]
                    })
                    "everything at once",
                    bend (fun m -> {
                        m with
                            SchemaVersion = 99
                            App = {
                                Name = ""
                                Slug = "NOPE"
                                Region = ""
                            }
                            Domains = m.Domains @ m.Domains
                    })
                ]

                for (label, mirror) in cases do
                    let mine = keysOf (K8sEmitCommand.validate mirror) mirrorKey
                    let theirs = keysOf (DeployManifest.validate (toSubstrate mirror)) substrateKey

                    Expect.equal
                        mine
                        theirs
                        (sprintf "'%s': the mirror and the substrate must report the same errors" label)
            }
        ]

        testList "option handling" [
            test "an unknown profile is rejected and names the alternatives" {
                match K8sEmitCommand.resolveRoles "Sidecar" with
                | Ok _ -> failtest "expected Error"
                | Error e -> Expect.stringContains e "WorkerOnly" "the message lists the valid values"
            }

            test "a repeated profile is rejected" {
                Expect.isError
                    (K8sEmitCommand.resolveRoles "WebOnly,WebOnly")
                    "a repeat would collide on resource names"
            }

            test "AllInOne cannot be paired with a split role" {
                Expect.isError
                    (K8sEmitCommand.resolveRoles "AllInOne,WorkerOnly")
                    "AllInOne already runs every subsystem"
            }

            test "profiles are case-insensitive and trimmed" {
                match K8sEmitCommand.resolveRoles " webonly , WORKERONLY " with
                | Error e -> failtestf "expected Ok, got %s" e
                | Ok roles -> Expect.equal (roles |> List.map _.Profile) [ "WebOnly"; "WorkerOnly" ] "resolved"
            }

            test "the format token is resolved, and nothing else is" {
                Expect.equal (K8sEmitCommand.tryFormat "HELM") (Some K8sEmitCommand.Helm) "helm"
                Expect.equal (K8sEmitCommand.tryFormat "flat") (Some K8sEmitCommand.Flat) "flat"
                Expect.isNone (K8sEmitCommand.tryFormat "kustomize") "an unsupported format is not guessed at"
            }

            test "an image reference splits on the tag, not on a registry port" {
                Expect.equal (K8sEmitCommand.splitImage "ghcr.io/acme/portal:v1") ("ghcr.io/acme/portal", "v1") "tagged"
                Expect.equal (K8sEmitCommand.splitImage "acme/portal") ("acme/portal", "latest") "untagged"

                Expect.equal
                    (K8sEmitCommand.splitImage "registry.local:5000/acme/portal")
                    ("registry.local:5000/acme/portal", "latest")
                    "a registry port is not a tag"
            }

            test "a TLS secret stem is DNS-safe" {
                Expect.equal (K8sEmitCommand.hostStem "app.acme.example") "app-acme-example" "dots become hyphens"
                Expect.equal (K8sEmitCommand.hostStem "*.acme.example") "wildcard-acme-example" "a wildcard is named"
            }

            test "missing --manifest is a usage error" {
                Expect.equal (K8sEmitCommand.command.Run []) ExitUsage "no manifest → 2"
            }

            test "a manifest that is not on disk is a runtime error, not a usage error" {
                Expect.equal
                    (K8sEmitCommand.command.Run [ "--manifest"; Path.Combine(tempDir (), "absent.json") ])
                    ExitRuntimeError
                    "re-printing the usage text would not help here"
            }

            test "an invalid manifest is refused before anything is written" {
                let json =
                    manifestJson.Replace("\"slug\": \"acme-portal\"", "\"slug\": \"Acme Portal\"")

                let outDir, code = emitInto json []
                Expect.equal code ExitRuntimeError "validation failure → 1"
                Expect.isFalse (Directory.Exists outDir) "no output directory is created by a refused emit"
            }

            test "a manifest with no image and no --image is refused" {
                let json = manifestJson.Replace("\"image\": \"ghcr.io/acme/portal:v1.4.2\",", "")
                let _, code = emitInto json []
                Expect.equal code ExitRuntimeError "no image → 1"
            }

            test "--image supplies the reference a build-from-source manifest lacks" {
                let json = manifestJson.Replace("\"image\": \"ghcr.io/acme/portal:v1.4.2\",", "")

                let outDir, code =
                    emitInto json [ "--format"; "flat"; "--image"; "registry.local/acme:9" ]

                Expect.equal code ExitOk "with --image it emits"

                Expect.stringContains
                    (File.ReadAllText(Path.Combine(outDir, "deployment-app.yaml")))
                    "registry.local/acme:9"
                    "the override reaches the container spec"
            }

            test "domains with no HTTP-serving role are refused rather than dropped" {
                let _, code = emitInto manifestJson [ "--profile"; "WorkerOnly" ]
                Expect.equal code ExitRuntimeError "an unroutable domain set is a refusal, not silence"
            }

            test "emit refuses to overwrite without --force, then obeys --force" {
                let dir = tempDir ()
                let manifestPath = Path.Combine(dir, "deploy.json")
                File.WriteAllText(manifestPath, manifestJson)
                let outDir = Path.Combine(dir, "out")
                let args = [ "--manifest"; manifestPath; "--out"; outDir; "--format"; "flat" ]
                Expect.equal (K8sEmitCommand.command.Run args) ExitOk "first emit"
                Expect.equal (K8sEmitCommand.command.Run args) ExitRuntimeError "clash without --force → 1"
                Expect.equal (K8sEmitCommand.command.Run(args @ [ "--force" ])) ExitOk "force → 0"
            }
        ]

        testList "emitted structure" [
            for profiles in [ "AllInOne"; "WebOnly"; "WebOnly,WorkerOnly"; "WebOnly,DispatcherOnly" ] do
                for format in [ "helm"; "flat" ] do
                    test (sprintf "%s / %s" format profiles) { assertStructure format profiles }

            test "the helm chart carries Chart.yaml, values.yaml, .helmignore and NOTES.txt" {
                let _, files, _ = emitDocs "helm" "WebOnly,WorkerOnly"

                for expected in [ "Chart.yaml"; "values.yaml"; ".helmignore"; "templates/NOTES.txt" ] do
                    Expect.contains files expected (sprintf "%s emitted" expected)
            }

            test "Chart.yaml names the app and its image tag" {
                let outDir, _, _ = emitDocs "helm" "AllInOne"
                let chart = parseYaml (File.ReadAllText(Path.Combine(outDir, "Chart.yaml")))
                Expect.equal (digStr chart "apiVersion") "v2" "Helm v2 chart"
                Expect.equal (digStr chart "name") "acme-portal" "chart name is the slug"
                Expect.equal (digStr chart "appVersion") "v1.4.2" "appVersion is the image tag"
            }

            test "values.yaml carries exactly the knobs the templates reference" {
                let outDir, _, _ = emitDocs "helm" "WebOnly,WorkerOnly"
                let values = parseYaml (File.ReadAllText(Path.Combine(outDir, "values.yaml")))
                Expect.equal (digStr values "image.repository") "ghcr.io/acme/portal" "repository"
                Expect.equal (digStr values "image.tag") "v1.4.2" "tag"
                Expect.equal (digStr values "secrets.name") "acme-portal-secrets" "secret name"
                Expect.equal (digStr values "replicas.web") "2" "web replicas"
                Expect.equal (digStr values "replicas.worker") "1" "worker replicas pinned to one"
            }

            test "the flat format resolves every value in place — no template syntax survives" {
                let outDir, files, _ = emitDocs "flat" "WebOnly,WorkerOnly"

                for f in files do
                    let text =
                        File.ReadAllText(Path.Combine(outDir, f.Replace('/', Path.DirectorySeparatorChar)))

                    Expect.isFalse (text.Contains "{{") (sprintf "%s carries no unresolved token" f)

                Expect.isFalse (List.contains "Chart.yaml" files) "a flat emit writes no chart metadata"
            }

            test "no emitted file contains secret material" {
                let outDir, files, _ = emitDocs "flat" "AllInOne"

                for f in files do
                    let text =
                        File.ReadAllText(Path.Combine(outDir, f.Replace('/', Path.DirectorySeparatorChar)))

                    Expect.isFalse (text.Contains "\"kind\": \"Secret\"") "no Secret object"
                    Expect.isFalse (text.Contains "kind: Secret") "the emitter never writes a Secret"
            }

            test "a manifest with no domains emits no Ingress" {
                let json =
                    manifestJson.Replace(
                        "\"domains\": [ { \"hostname\": \"app.acme.example\", \"tlsMode\": \"acme-le\" } ],",
                        ""
                    )

                let outDir, code = emitInto json [ "--format"; "flat" ]
                Expect.equal code ExitOk "emits"
                Expect.isFalse (File.Exists(Path.Combine(outDir, "ingress.yaml"))) "no domains, no Ingress"
            }

            test "a domain with no TLS mode emits no tls block" {
                let json = manifestJson.Replace("\"tlsMode\": \"acme-le\"", "\"tlsMode\": \"\"")
                let outDir, code = emitInto json [ "--format"; "flat" ]
                Expect.equal code ExitOk "emits"
                let ing = parseYaml (File.ReadAllText(Path.Combine(outDir, "ingress.yaml")))
                Expect.isNone (tryDig ing "spec.tls") "no tlsMode means no TLS entry, not an empty one"
                Expect.equal (digStr ing "spec.rules[0].host") "app.acme.example" "the rule is still emitted"
            }
        ]

        // Env-gated: no helm binary exists on the dev machine or in CI, so
        // this arm reports Pending unless TOOLUP_HELM_PATH names one. When
        // it does, the real linter is the judge of the chart — the
        // structural cases above are a standing approximation of it.
        test "helm lint accepts the emitted chart (TOOLUP_HELM_PATH)" {
            match Environment.GetEnvironmentVariable "TOOLUP_HELM_PATH" with
            | null
            | "" -> skiptest "TOOLUP_HELM_PATH is unset — no helm binary to lint with"
            | helm ->
                let outDir, code = emitInto manifestJson [ "--format"; "helm" ]
                Expect.equal code ExitOk "emit succeeds"

                let psi =
                    ProcessStartInfo(helm, RedirectStandardOutput = true, RedirectStandardError = true)

                psi.ArgumentList.Add "lint"
                psi.ArgumentList.Add outDir
                use proc = Process.Start psi
                let stdout = proc.StandardOutput.ReadToEnd()
                let stderr = proc.StandardError.ReadToEnd()
                proc.WaitForExit()
                Expect.equal proc.ExitCode 0 (sprintf "helm lint failed:\n%s\n%s" stdout stderr)
        }
    ]
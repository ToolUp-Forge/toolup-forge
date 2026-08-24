module ToolUp.Platform.Tests.InProcess.ConfigResolverTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigKeys
open ToolUp.Platform.ConfigResolver

// ─── Phase 696 — the deployment configuration manifest ────────────────
//
// The layer below the environment: one JSON file whose keys are the
// canonical env-var names, bound through the config-key registry, hashed
// over its raw bytes.
//
// What is asserted here, in the order the phase asks for it:
//
//   * precedence — env beats manifest beats default, per key;
//   * discovery — TOOLUP_CONFIG_FILE wins, else the probe, else nothing,
//     and a *named* file that is absent refuses rather than degrading
//     quietly to the probe;
//   * both refusals — an unknown key and a secret key, each naming what
//     to do instead, with no acceptance hatch on the secret arm;
//   * hash stability — the same bytes hash the same, and a byte that
//     changes changes the hash (no canonicalisation, so formatting counts);
//   * provenance labels — every effective value knows which layer it came
//     from, and `--diff` keeps only the ones some layer supplied;
//   * the GP 11 gate — with nothing installed, resolution is the env read
//     it always was, key for key.
//
// Every case restores process state on the way out: the resolver installs
// into a process-wide seam (as the environment itself is process-wide),
// so a leaked manifest would contaminate every sibling case in the pack.

/// Run `body` with one env var temporarily set, restoring the prior value.
let private withEnv (name: string) (value: string option) (body: unit -> unit) =
    let prior = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

/// Run `body` with `manifest` installed, then return the process to the
/// no-manifest state.
let private withManifest (values: (string * string) list) (body: unit -> unit) =
    try
        ConfigResolution.install {
            Path = "test://manifest"
            Hash = "0000000000000000000000000000000000000000000000000000000000000000"
            Values = Map.ofList values
            PendingKeys = []
        }

        body ()
    finally
        ConfigResolution.clear ()

let private bytes (s: string) = Encoding.UTF8.GetBytes s

let private parseOk (source: string) =
    match parseBytes "test://manifest" (bytes source) with
    | Ok load -> load
    | Error e -> failtestf "expected the manifest to parse, but it was refused: %s" e

let private parseRefusal (source: string) =
    match parseBytes "test://manifest" (bytes source) with
    | Error e -> e
    | Ok _ -> failtest "expected the manifest to be refused, but it parsed"

/// A temporary directory that behaves as a deployment's content root.
let private withTempRoot (body: string -> unit) =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-cfg-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        body dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let tests =
    testList "ConfigResolver" [

        // ─── A — binder + precedence ──────────────────────────────────

        testCase "a manifest binds registered keys, rendering scalars as the reader would have seen them"
        <| fun _ ->
            let load =
                parseOk
                    """
                    {
                      "$schema": "./toolup.config.schema.json",
                      "TOOLUP_MODULE": "reports",
                      "TOOLUP_REPLICA_COUNT": 3,
                      "TOOLUP_REQUIRE_HTTPS": true,
                      "TOOLUP_TRUST_FORWARDED_HEADERS": false
                    }
                    """

            Expect.equal (Map.tryFind Names.moduleFilter load.Snapshot.Values) (Some "reports") "string binds verbatim"

            Expect.equal
                (Map.tryFind Names.replicaCount load.Snapshot.Values)
                (Some "3")
                "a JSON number renders as the string an env var would have carried"

            Expect.equal (Map.tryFind Names.requireHttps load.Snapshot.Values) (Some "true") "true renders as \"true\""

            Expect.equal
                (Map.tryFind Names.trustForwardedHeaders load.Snapshot.Values)
                (Some "false")
                "false renders as \"false\""

            Expect.isFalse
                (Map.containsKey "$schema" load.Snapshot.Values)
                "$schema is tolerated for editor validation but never bound"

            Expect.isEmpty load.Warnings "every key here is bindable, so nothing is pending"

        testCase "precedence: env beats manifest, manifest beats default"
        <| fun _ ->
            withManifest [ Names.moduleFilter, "manifest-value"; Names.logLevel, "Debug" ] (fun () ->
                withEnv Names.moduleFilter (Some "env-value") (fun () ->
                    withEnv Names.logLevel None (fun () ->
                        Expect.equal
                            (ConfigResolution.tryResolve Names.moduleFilter)
                            (Some("env-value", ConfigResolution.EnvConfigSource))
                            "an env var overrides the declared manifest value — the file is the reviewed base, env is the per-instance lane"

                        Expect.equal
                            (ConfigResolution.tryResolve Names.logLevel)
                            (Some("Debug", ConfigResolution.ManifestConfigSource))
                            "with no env var, the manifest supplies the value"

                        Expect.equal
                            (ConfigResolution.tryResolve Names.oidcIssuer)
                            None
                            "a key no layer sets resolves to nothing, leaving the reader's default")))

        testCase "an empty manifest value reads as unset, exactly as an empty env var does"
        <| fun _ ->
            withManifest [ Names.oidcIssuer, "" ] (fun () ->
                withEnv Names.oidcIssuer None (fun () ->
                    Expect.equal
                        (ConfigResolution.tryValue Names.oidcIssuer)
                        None
                        "the two lanes must agree on emptiness, or a key's meaning would depend on which one set it"))

        testCase "ServerConfig.fromEnv reads the manifest — the Phase 71.A cluster is migrated"
        <| fun _ ->
            let logger =
                ToolUp.Platform.ConsoleLogger.ConsoleLogger(LogLevel.Error, Set.empty) :> ILogger

            withEnv Names.replicaCount None (fun () ->
                withEnv Names.requireHttps None (fun () ->
                    let baseline = ServerConfig.fromEnv logger ServerConfigOverrides.empty

                    Expect.equal baseline.ReplicaCount 1 "no manifest, no env — the declared default stands"
                    Expect.isFalse baseline.RequireHttps "no manifest, no env — the declared default stands"

                    withManifest [ Names.replicaCount, "4"; Names.requireHttps, "true" ] (fun () ->
                        let bound = ServerConfig.fromEnv logger ServerConfigOverrides.empty

                        Expect.equal bound.ReplicaCount 4 "the manifest value reaches the config record"
                        Expect.isTrue bound.RequireHttps "the manifest value reaches the config record")))

        // ─── B — refusals ─────────────────────────────────────────────

        testCase "an unknown key refuses startup and names it"
        <| fun _ ->
            let message = parseRefusal """{ "TOOLUP_AUTH_MOD": "oidc" }"""

            Expect.stringContains message "TOOLUP_AUTH_MOD" "the refusal names the offending key"

            Expect.stringContains
                message
                "not a recognised config key"
                "the refusal says what is wrong, not merely that something is"

        testCase "every unknown key is named, so one edit fixes the file"
        <| fun _ ->
            let message =
                parseRefusal """{ "TOOLUP_NOT_A_KEY": "x", "TOOLUP_ALSO_NOT_A_KEY": "y" }"""

            Expect.stringContains message "TOOLUP_NOT_A_KEY" "first offender named"

            Expect.stringContains
                message
                "TOOLUP_ALSO_NOT_A_KEY"
                "second offender named too — refusing one at a time would cost one boot per typo"

        testCase "a secret key refuses and names the env var to set instead — no acceptance hatch"
        <| fun _ ->
            let secretKey = all |> List.find _.IsSecret |> _.EnvVar
            let message = parseRefusal (sprintf """{ "%s": "hunter2" }""" secretKey)

            Expect.stringContains message secretKey "the refusal names the secret key"

            Expect.stringContains
                message
                "environment variable instead"
                "the refusal names the lane to use instead of merely refusing"

            Expect.isFalse
                (message.Contains "TOOLUP_ACCEPT")
                "there is deliberately no acceptance hatch — one secret in the file destroys its shareability claim"

        testCase "TOOLUP_CONFIG_FILE cannot be set from inside the manifest"
        <| fun _ ->
            let message = parseRefusal """{ "TOOLUP_CONFIG_FILE": "./other.json" }"""

            Expect.stringContains message Names.configFile "the refusal names the key"

            Expect.stringContains
                message
                "own location"
                "a manifest naming its own location has already been read by the time the line is seen"

        testCase "the manifest is strict JSON — comments and trailing commas are refused"
        <| fun _ ->
            let commented =
                parseRefusal
                    """
                    {
                      // why this hatch is open
                      "TOOLUP_AUTH_MODE": "oidc"
                    }
                    """

            Expect.stringContains commented "not valid JSON" "a comment is a parse refusal, not a tolerated extra"

            let trailing = parseRefusal """{ "TOOLUP_AUTH_MODE": "oidc", }"""
            Expect.stringContains trailing "not valid JSON" "a trailing comma is a parse refusal"

        testCase "non-scalar and null values are refused"
        <| fun _ ->
            let arrayValued = parseRefusal """{ "TOOLUP_AUTH_MODE": ["oidc"] }"""
            Expect.stringContains arrayValued "must be a string, number or boolean" "an array is not a config value"

            let nullValued = parseRefusal """{ "TOOLUP_AUTH_MODE": null }"""

            Expect.stringContains
                nullValued
                "remove the line"
                "null is refused with the fix named — absent and null must not be indistinguishable in a diff"

        testCase "a manifest that is not an object is refused"
        <| fun _ ->
            let message = parseRefusal """[ "TOOLUP_AUTH_MODE" ]"""
            Expect.stringContains message "must contain a JSON object" "the shape is named"

        // ─── C — hash ─────────────────────────────────────────────────

        testCase "the hash is SHA-256 over the raw bytes, with no canonicalisation"
        <| fun _ ->
            // Known vector, computed independently of this code path: the
            // SHA-256 of the two bytes `{}`.
            Expect.equal
                (hashBytes (bytes "{}"))
                "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"
                "hashBytes agrees with the standard SHA-256 of the same bytes"

            let a = parseOk """{ "TOOLUP_AUTH_MODE": "oidc" }"""
            let b = parseOk """{ "TOOLUP_AUTH_MODE": "oidc" }"""

            Expect.equal a.Snapshot.Hash b.Snapshot.Hash "identical bytes hash identically"

            let reformatted = parseOk """{"TOOLUP_AUTH_MODE":"oidc"}"""

            Expect.notEqual
                a.Snapshot.Hash
                reformatted.Snapshot.Hash
                "semantically identical but differently formatted bytes hash differently — the attested artefact is the file AS DEPLOYED"

        testCase "the boot line names the file and the hash"
        <| fun _ ->
            withManifest [ Names.authMode, "oidc" ] (fun () ->
                match bootLine () with
                | None -> failtest "a loaded manifest must produce a boot line"
                | Some line ->
                    Expect.stringContains line "test://manifest" "the boot line names the file"
                    Expect.stringContains line "sha256:" "the boot line carries the hash")

            Expect.isNone (bootLine ()) "no manifest, no new boot-log line — an existing deployment's log is unchanged"

        // ─── D — bindability ──────────────────────────────────────────

        testCase "a registered but not-yet-bindable key is accepted with a warning naming it"
        <| fun _ ->
            let pendingKey =
                all
                |> List.filter (fun d -> not d.IsSecret && d.EnvVar <> Names.configFile)
                |> List.map _.EnvVar
                |> List.find (isManifestBindable >> not)

            let load = parseOk (sprintf """{ "%s": "x" }""" pendingKey)

            Expect.equal load.Snapshot.PendingKeys [ pendingKey ] "the key is recorded as pending"

            Expect.isNonEmpty
                load.Warnings
                "a declared-but-unread key warns — silently ignoring it is worse than no manifest"

            Expect.stringContains (List.head load.Warnings) pendingKey "the warning names the key"

        testCase "every declared-bindable key carries a descriptor and is not a secret"
        <| fun _ ->
            let registered = all |> List.map _.EnvVar |> Set.ofList
            let secrets = all |> List.filter _.IsSecret |> List.map _.EnvVar |> Set.ofList

            Expect.isEmpty
                (Set.difference manifestBindable registered |> Set.toList)
                "a bindable key with no descriptor could never be validated at the registry boundary"

            Expect.isEmpty
                (Set.intersect manifestBindable secrets |> Set.toList)
                "a secret can never be manifest-bindable — the loader refuses it outright"

        // ─── E — provenance + --diff ──────────────────────────────────

        testCase "--print-config labels each value with the layer it came from"
        <| fun _ ->
            withManifest [ Names.replicaCount, "7" ] (fun () ->
                withEnv Names.logLevel (Some "Debug") (fun () ->
                    withEnv Names.replicaCount None (fun () ->
                        let dump = StartupModes.renderConfigReport false all

                        Expect.stringContains dump "TOOLUP_LOG_LEVEL = Debug  [env]" "an env value is labelled env"

                        Expect.stringContains
                            dump
                            "TOOLUP_REPLICA_COUNT = 7  [manifest]"
                            "a manifest value is labelled manifest"

                        Expect.stringContains
                            dump
                            "Manifest sha256:"
                            "the report states the hash of the declared intent")))

        testCase "--print-config --diff keeps only the values some layer supplied"
        <| fun _ ->
            withManifest [ Names.replicaCount, "7" ] (fun () ->
                withEnv Names.replicaCount None (fun () ->
                    let diff = StartupModes.renderConfigReport true all

                    Expect.stringContains diff "TOOLUP_REPLICA_COUNT" "a manifest-set key is a deviation and is shown"

                    Expect.isFalse
                        (diff.Contains Names.acceptLocalFallback)
                        "a key nothing set is on its default and is dropped from the diff view"))

        testCase "--diff is detected as a modifier, and does not disturb mode detection"
        <| fun _ ->
            Expect.isTrue (StartupModes.diffRequested [ "app"; "--print-config"; "--diff" ]) "flag detected"
            Expect.isTrue (StartupModes.diffRequested [ "app"; "--DIFF" ]) "case-insensitive"
            Expect.isFalse (StartupModes.diffRequested [ "app"; "--print-config" ]) "absent when not passed"

            Expect.equal
                (StartupModes.detect [ "app"; "--print-config"; "--diff" ])
                StartupModes.PrintConfig
                "the modifier leaves the mode alone"

        testCase "a set secret stays redacted whichever layer would have supplied it"
        <| fun _ ->
            let secretKey = all |> List.find _.IsSecret |> _.EnvVar

            withEnv secretKey (Some "super-secret-token-value") (fun () ->
                let dump = StartupModes.renderConfigReport false all

                Expect.isFalse (dump.Contains "super-secret-token-value") "the value must never appear"
                Expect.stringContains dump "<redacted>" "it is shown as redacted")

        // ─── discovery ────────────────────────────────────────────────

        testCase "discovery: TOOLUP_CONFIG_FILE wins over the probed default"
        <| fun _ ->
            withTempRoot (fun root ->
                let probed = Path.Combine(root, DefaultManifestFileName)
                let named = Path.Combine(root, "explicit.json")
                File.WriteAllText(probed, "{}")
                File.WriteAllText(named, "{}")

                withEnv Names.configFile (Some named) (fun () ->
                    match discover root with
                    | Ok(Some path) ->
                        Expect.equal
                            (Path.GetFileName path)
                            "explicit.json"
                            "the explicitly named file wins over the probe"
                    | other -> failtestf "expected the named file, got %A" other))

        testCase "discovery: an explicitly named file that is absent refuses"
        <| fun _ ->
            withTempRoot (fun root ->
                withEnv Names.configFile (Some(Path.Combine(root, "nope.json"))) (fun () ->
                    match discover root with
                    | Error message ->
                        Expect.stringContains message Names.configFile "the refusal names the variable"

                        Expect.stringContains
                            message
                            "no file exists"
                            "silently falling back to the probe would be exactly the declared-but-not-applied failure this layer exists to prevent"
                    | other -> failtestf "expected a refusal, got %A" other))

        testCase "discovery: the probe finds ./toolup.config.json, and its absence is a no-op"
        <| fun _ ->
            withTempRoot (fun root ->
                withEnv Names.configFile None (fun () ->
                    Expect.equal (discover root) (Ok None) "no file, nothing loaded — resolution is unchanged (GP 11)"

                    File.WriteAllText(Path.Combine(root, DefaultManifestFileName), "{}")

                    match discover root with
                    | Ok(Some path) ->
                        Expect.equal
                            (Path.GetFileName path)
                            DefaultManifestFileName
                            "the probe finds the default name"
                    | other -> failtestf "expected the probed file, got %A" other))

        testCase "load reads, hashes and binds a manifest from disk"
        <| fun _ ->
            withTempRoot (fun root ->
                let path = Path.Combine(root, DefaultManifestFileName)
                let source = """{ "TOOLUP_REPLICA_COUNT": 2 }"""
                File.WriteAllText(path, source)

                withEnv Names.configFile None (fun () ->
                    match load root with
                    | Ok(Some loaded) ->
                        Expect.equal
                            (Map.tryFind Names.replicaCount loaded.Snapshot.Values)
                            (Some "2")
                            "the value binds"

                        Expect.equal
                            loaded.Snapshot.Hash
                            (hashBytes (File.ReadAllBytes path))
                            "the recorded hash is over the bytes on disk"
                    | other -> failtestf "expected a loaded manifest, got %A" other))

        // ─── F — the GP 11 gate ───────────────────────────────────────

        testCase "with no manifest, resolution is the environment read it always was"
        <| fun _ ->
            Expect.isFalse
                (ConfigResolution.isInstalled ())
                "no case may leak an installed manifest into the rest of the pack"

            // Quantified over the whole registry rather than a sample: the
            // claim is that NOTHING changed for a deployment that has not
            // written the file, and a spot-check could not support it.
            for d in all do
                let direct =
                    match Environment.GetEnvironmentVariable d.EnvVar with
                    | null
                    | "" -> None
                    | v -> Some v

                Expect.equal
                    (ConfigResolution.tryValue d.EnvVar)
                    direct
                    (sprintf "%s resolves exactly as a direct env read with no manifest installed" d.EnvVar)

                Expect.equal
                    (ConfigResolution.sourceOf d.EnvVar)
                    (match direct with
                     | Some _ -> ConfigResolution.EnvConfigSource
                     | None -> ConfigResolution.DefaultConfigSource)
                    (sprintf "%s reports the only two layers that can be in play" d.EnvVar)

        testCase "source labels are the stable report vocabulary"
        <| fun _ ->
            Expect.equal (ConfigResolution.ConfigSource.label ConfigResolution.LiteralConfigSource) "literal" "literal"
            Expect.equal (ConfigResolution.ConfigSource.label ConfigResolution.EnvConfigSource) "env" "env"

            Expect.equal
                (ConfigResolution.ConfigSource.label ConfigResolution.ManifestConfigSource)
                "manifest"
                "manifest"

            Expect.equal
                (ConfigResolution.ConfigSource.label ConfigResolution.OverrideConfigSource)
                "override"
                "override"

            Expect.equal (ConfigResolution.ConfigSource.label ConfigResolution.DefaultConfigSource) "default" "default"
    ]
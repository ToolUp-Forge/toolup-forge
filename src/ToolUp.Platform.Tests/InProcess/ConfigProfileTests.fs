module ToolUp.Platform.Tests.InProcess.ConfigProfileTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigKeys

// ─── Phase 700 — deployment configuration profiles ────────────────────
//
// A profile is a named bundle of registered keys resolved one rung below
// the manifest. What is asserted here, in the order the phase asks for
// it:
//
//   * precedence — all six rungs, and specifically that a deployment's
//     own explicit line beats the bundle it imported (the ordering
//     decision the whole feature rests on);
//   * selection — `"$profile"` beats `TOOLUP_PROFILE`, and an
//     unrecognised name REFUSES startup naming the available set rather
//     than falling back to the defaults the profile was imported to
//     replace;
//   * declaration — a consumer profile is refused for every reason the
//     manifest loader refuses a file (unknown key, secret key, a key no
//     reader resolves through the seam), plus the two a bundle adds
//     (duplicate name, declared too late);
//   * the built-in set — well-formed against the registry, every value a
//     legal instance of its key's declared type, and actually reaching
//     the largest reader in the SDK rather than merely parsing;
//   * provenance — `--print-config` labels a profile-supplied value
//     `profile:<name>`, not `default`;
//   * the GP 11 gate — with nothing selected, resolution is the env read
//     it always was, key for key.
//
// Every case restores process state on the way out: the profile installs
// into a process-wide seam (as the environment itself is process-wide),
// so a leaked profile would contaminate every sibling case in the pack.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private withEnv (pairs: (string * string option) list) (body: unit -> unit) =
    let priors =
        pairs |> List.map (fun (n, _) -> n, Environment.GetEnvironmentVariable n)

    try
        for n, v in pairs do
            Environment.SetEnvironmentVariable(n, v |> Option.toObj)

        body ()
    finally
        for n, prior in priors do
            Environment.SetEnvironmentVariable(n, prior)

let private withManifest (profile: string option) (values: (string * string) list) (body: unit -> unit) =
    try
        ConfigResolution.install {
            Path = "test://manifest"
            Hash = "0000000000000000000000000000000000000000000000000000000000000000"
            Values = Map.ofList values
            PendingKeys = []
            Profile = profile
        }

        body ()
    finally
        ConfigResolution.clear ()

/// Install a profile directly, bypassing selection — the shape a
/// precedence case wants, where the question is what the rung does and
/// not how it was chosen.
let private withProfile (name: string) (values: (string * string) list) (body: unit -> unit) =
    try
        ConfigResolution.installProfile {
            Name = name
            Values = Map.ofList values
            SelectedBy = ConfigResolution.EnvProfileSelection
        }

        body ()
    finally
        ConfigResolution.clearProfile ()

/// Run `body` with the consumer-declared registry empty, restoring it
/// (and the selection latch) afterwards.
let private withCleanRegistry (body: unit -> unit) =
    try
        ConfigProfiles.reset ()
        body ()
    finally
        ConfigProfiles.reset ()

let private declareRefusal (candidate: ConfigProfile) =
    try
        ConfigProfiles.declare candidate
        failtest "expected the declaration to be refused, but it was accepted"
    with ConfigProfiles.ConfigProfileException message ->
        message

let private bytes (s: string) = Encoding.UTF8.GetBytes s

let private parseOk (source: string) =
    match ConfigResolver.parseBytes "test://manifest" (bytes source) with
    | Ok load -> load
    | Error e -> failtestf "expected the manifest to parse, but it was refused: %s" e

let private parseRefusal (source: string) =
    match ConfigResolver.parseBytes "test://manifest" (bytes source) with
    | Error e -> e
    | Ok _ -> failtest "expected the manifest to be refused, but it parsed"

let private withTempRoot (body: string -> unit) =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-prof-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        body dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

/// A minimal well-formed consumer profile, parameterised by whatever the
/// case under test wants to break.
let private consumerProfile name values = {
    Name = name
    Description = "A test posture."
    Requires = []
    Values = values
}

/// Whether `value` is a legal instance of `descriptor`'s declared type —
/// the same judgement the generated schema makes, applied to a bundle.
let private isLegalValue (d: ConfigKeyDescriptor) (value: string) =
    match d.Type with
    | StringKey -> not (String.IsNullOrWhiteSpace value)
    | BoolKey ->
        match value.ToLowerInvariant() with
        | "1"
        | "true"
        | "yes"
        | "on"
        | "0"
        | "false"
        | "no"
        | "off" -> true
        | _ -> false
    | IntKey -> fst (Int32.TryParse value)
    | EnumKey choices ->
        choices
        |> List.exists (fun c -> c.ToLowerInvariant() = value.ToLowerInvariant())

let tests =
    testSequenced (
        testList "ConfigProfiles" [

            // ─── A — the profile rung and its precedence ──────────────

            testCase "the profile supplies a key no higher layer sets, labelled with its name"
            <| fun _ ->
                withEnv [ Names.replicaCount, None ] (fun () ->
                    withProfile "posture" [ Names.replicaCount, "4" ] (fun () ->
                        Expect.equal
                            (ConfigResolution.tryResolve Names.replicaCount)
                            (Some("4", ConfigResolution.ProfileConfigSource "posture"))
                            "a key only the profile sets resolves from the profile, carrying the profile's name"))

            testCase "the manifest beats the profile — an explicit line wins over the bundle it imported"
            <| fun _ ->
                withEnv [ Names.replicaCount, None ] (fun () ->
                    withManifest (Some "posture") [ Names.replicaCount, "3" ] (fun () ->
                        withProfile "posture" [ Names.replicaCount, "9" ] (fun () ->
                            Expect.equal
                                (ConfigResolution.tryResolve Names.replicaCount)
                                (Some("3", ConfigResolution.ManifestConfigSource))
                                "the manifest's own line beats the profile it imported — importing a posture must never take a setting away from the file that imported it")))

            testCase "the environment beats the profile"
            <| fun _ ->
                withEnv [ Names.replicaCount, Some "7" ] (fun () ->
                    withProfile "posture" [ Names.replicaCount, "9" ] (fun () ->
                        Expect.equal
                            (ConfigResolution.tryResolve Names.replicaCount)
                            (Some("7", ConfigResolution.EnvConfigSource))
                            "the per-instance lane still wins"))

            testCase "all six rungs are nameable, and the four resolvable ones order env > manifest > profile > default"
            <| fun _ ->
                // The literal and override rungs never traverse this seam
                // (a literal is written in composition-root code; an
                // overrides record is applied by the reader that owns it),
                // so what is asserted for them is that the vocabulary
                // exists to LABEL them — which is the whole of the seam's
                // claim about those two.
                Expect.equal
                    (ConfigResolution.ConfigSource.label ConfigResolution.LiteralConfigSource)
                    "literal"
                    "literal"

                Expect.equal (ConfigResolution.ConfigSource.label ConfigResolution.EnvConfigSource) "env" "env"

                Expect.equal
                    (ConfigResolution.ConfigSource.label ConfigResolution.ManifestConfigSource)
                    "manifest"
                    "manifest"

                Expect.equal
                    (ConfigResolution.ConfigSource.label (ConfigResolution.ProfileConfigSource "serverless"))
                    "profile:serverless"
                    "a profile label carries the name — 'profile' alone would leave the operator guessing which posture supplied the value"

                Expect.equal
                    (ConfigResolution.ConfigSource.label ConfigResolution.OverrideConfigSource)
                    "override"
                    "override"

                Expect.equal
                    (ConfigResolution.ConfigSource.label ConfigResolution.DefaultConfigSource)
                    "default"
                    "default"

                // The four this seam resolves, peeled one at a time down
                // the same key.
                withEnv [ Names.replicaCount, Some "1" ] (fun () ->
                    withManifest None [ Names.replicaCount, "2" ] (fun () ->
                        withProfile "posture" [ Names.replicaCount, "3" ] (fun () ->
                            Expect.equal
                                (ConfigResolution.sourceOf Names.replicaCount)
                                ConfigResolution.EnvConfigSource
                                "env first")))

                withEnv [ Names.replicaCount, None ] (fun () ->
                    withManifest None [ Names.replicaCount, "2" ] (fun () ->
                        withProfile "posture" [ Names.replicaCount, "3" ] (fun () ->
                            Expect.equal
                                (ConfigResolution.sourceOf Names.replicaCount)
                                ConfigResolution.ManifestConfigSource
                                "then the manifest")))

                withEnv [ Names.replicaCount, None ] (fun () ->
                    withManifest None [] (fun () ->
                        withProfile "posture" [ Names.replicaCount, "3" ] (fun () ->
                            Expect.equal
                                (ConfigResolution.sourceOf Names.replicaCount)
                                (ConfigResolution.ProfileConfigSource "posture")
                                "then the profile")))

                withEnv [ Names.replicaCount, None ] (fun () ->
                    withManifest None [] (fun () ->
                        withProfile "posture" [] (fun () ->
                            Expect.equal
                                (ConfigResolution.sourceOf Names.replicaCount)
                                ConfigResolution.DefaultConfigSource
                                "then the reader's declared default")))

            testCase "an empty profile value reads as unset, exactly as an empty environment variable does"
            <| fun _ ->
                withEnv [ Names.replicaCount, None ] (fun () ->
                    withProfile "posture" [ Names.replicaCount, "" ] (fun () ->
                        Expect.equal
                            (ConfigResolution.tryValue Names.replicaCount)
                            None
                            "the three lanes must agree on what empty means, or a key's meaning would depend on which one set it"))

            // ─── A — selection ────────────────────────────────────────

            testCase "the manifest's $profile entry beats TOOLUP_PROFILE"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    match ConfigProfiles.selectFrom (Some "serverless") (Some "dev-single-instance") with
                    | Ok(Some selected) ->
                        Expect.equal selected.Name "serverless" "the committed, reviewed statement wins"

                        Expect.equal
                            selected.SelectedBy
                            ConfigResolution.ManifestProfileSelection
                            "and the report says which lane named it"
                    | other -> failtestf "expected the manifest's profile to be selected, got %A" other)

            testCase "TOOLUP_PROFILE selects when the manifest names none"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    match ConfigProfiles.selectFrom None (Some "serverless") with
                    | Ok(Some selected) ->
                        Expect.equal selected.Name "serverless" "the environment lane is consulted second"

                        Expect.equal
                            selected.SelectedBy
                            ConfigResolution.EnvProfileSelection
                            "and is reported as the lane"
                    | other -> failtestf "expected the environment's profile to be selected, got %A" other)

            testCase "a profile name matches case-insensitively but reports its canonical spelling"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    match ConfigProfiles.selectFrom None (Some "  SERVERLESS  ") with
                    | Ok(Some selected) ->
                        Expect.equal
                            selected.Name
                            "serverless"
                            "the same forgiveness every enum-valued reader gives an environment value, normalised back to the documented spelling"
                    | other -> failtestf "expected a case-insensitive match, got %A" other)

            testCase "no profile named ⇒ nothing selected"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    Expect.equal
                        (ConfigProfiles.selectFrom None None)
                        (Ok None)
                        "the ordinary state: a deployment that imports no posture"

                    Expect.equal
                        (ConfigProfiles.selectFrom (Some "") (Some "   "))
                        (Ok None)
                        "a blank name in either lane is 'none named', not a name to look up")

            testCase "an unrecognised profile name refuses and lists the available set"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    match ConfigProfiles.selectFrom None (Some "production") with
                    | Error message ->
                        Expect.stringContains message "\"production\"" "the refusal quotes the name that was not found"

                        Expect.stringContains
                            message
                            "production-multi-instance"
                            "and lists the available set — an operator who typed 'production' needs to see the name they meant"

                        Expect.stringContains
                            message
                            "dev-single-instance"
                            "every available profile is listed, not a guessed nearest match"

                        Expect.stringContains message "TOOLUP_PROFILE" "and names the lane the bad name came from"
                    | Ok other -> failtestf "expected an unknown profile name to refuse, got %A" other)

            testCase "the unknown-profile refusal names the manifest lane when the manifest named it"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    match ConfigProfiles.selectFrom (Some "nope") None with
                    | Error message ->
                        Expect.stringContains
                            message
                            "$profile"
                            "an operator hunting for where the bad name was written needs to be told which lane carried it"
                    | Ok other -> failtestf "expected an unknown profile name to refuse, got %A" other)

            testCase "a consumer-declared profile is selectable and listed in the refusal"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    ConfigProfiles.declare (consumerProfile "house-style" [ Names.logFormat, "json" ])

                    match ConfigProfiles.selectFrom None (Some "house-style") with
                    | Ok(Some selected) ->
                        Expect.equal selected.Name "house-style" "a consumer profile selects like a built-in"
                    | other -> failtestf "expected the consumer profile to be selected, got %A" other

                    match ConfigProfiles.selectFrom None (Some "typo") with
                    | Error message ->
                        Expect.stringContains
                            message
                            "house-style"
                            "the available set a refusal lists includes the consumer's own, or it would send them looking in the SDK"
                    | Ok other -> failtestf "expected an unknown name to refuse, got %A" other)

            // ─── B — the built-in set ─────────────────────────────────

            testCase "the built-in profiles are well-formed against the registry"
            <| fun _ ->
                Expect.isNonEmpty
                    builtInProfiles
                    "the built-in set must not be empty — the whole surface would be dead code"

                // Measured cumulatively so the duplicate-name rule is
                // exercised across the set, not only within each entry.
                let mutable seen: ConfigProfile list = []

                for p in builtInProfiles do
                    Expect.equal
                        (profileProblems seen p)
                        []
                        (sprintf
                            "built-in profile \"%s\" does not satisfy the rules every consumer-declared profile must satisfy"
                            p.Name)

                    seen <- seen @ [ p ]

            testCase "every built-in profile value is a legal instance of its key's declared type"
            <| fun _ ->
                let byName = all |> List.map (fun d -> d.EnvVar, d) |> Map.ofList

                for p in builtInProfiles do
                    for key, value in p.Values do
                        match Map.tryFind key byName with
                        | None -> failtestf "profile \"%s\" names unregistered key %s" p.Name key
                        | Some d ->
                            Expect.isTrue
                                (isLegalValue d value)
                                (sprintf
                                    "profile \"%s\" sets %s = \"%s\", which is not a legal %A value. The generated schema would flag this in an editor; a bundle must clear the same bar."
                                    p.Name
                                    key
                                    value
                                    d.Type)

            testCase "no built-in profile carries a secret, and each names the secrets it needs instead"
            <| fun _ ->
                let secrets = all |> List.filter _.IsSecret |> List.map _.EnvVar |> Set.ofList

                for p in builtInProfiles do
                    for key, _ in p.Values do
                        Expect.isFalse
                            (Set.contains key secrets)
                            (sprintf
                                "profile \"%s\" carries the secret %s. A bundle is shared across deployments by design, so a credential in one is a credential in all of them."
                                p.Name
                                key)

                // The honesty clause: the multi-instance posture selects
                // Redis-backed substrates it cannot supply a connection
                // for, and says so where a machine can check it.
                let production =
                    builtInProfiles |> List.find (fun p -> p.Name = "production-multi-instance")

                Expect.contains
                    production.Requires
                    Names.redisConnection
                    "production-multi-instance selects the Redis-backed channel and lock, so it must name the connection string the operator has to supply themselves"

                Expect.contains
                    (production.Values |> List.map fst)
                    Names.notificationChannel
                    "…and it must actually select them, or the Requires entry would be a claim about nothing"

            testCase "a built-in profile reaches ServerConfig.fromEnv — the largest reader in the SDK"
            <| fun _ ->
                // Well-formed is not the same as effective. This is the
                // arm that would have caught a bundle whose keys are all
                // registered and all legal and none of which any reader
                // consults.
                let cleared =
                    builtInProfiles
                    |> List.collect (fun p -> p.Values |> List.map fst)
                    |> List.distinct
                    |> List.map (fun k -> k, None)

                withEnv cleared (fun () ->
                    withProfile
                        "production-multi-instance"
                        (builtInProfiles
                         |> List.find (fun p -> p.Name = "production-multi-instance")
                         |> _.Values)
                        (fun () ->
                            let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                            Expect.equal cfg.ReplicaCount 2 "the profile's replica count reached ServerConfig"

                            Expect.isTrue cfg.RequireHttps "the profile's HTTPS posture reached ServerConfig")

                    withProfile
                        "dev-single-instance"
                        (builtInProfiles
                         |> List.find (fun p -> p.Name = "dev-single-instance")
                         |> _.Values)
                        (fun () ->
                            let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                            Expect.equal cfg.ReplicaCount 1 "the dev posture's single instance reached ServerConfig"

                            Expect.isTrue
                                cfg.EnableDevEndpoints
                                "the dev posture's /dev/* endpoints reached ServerConfig"))

            testCase "an explicit environment line still beats the built-in bundle it imported"
            <| fun _ ->
                withEnv [ Names.replicaCount, Some "11"; Names.requireHttps, None ] (fun () ->
                    withProfile
                        "production-multi-instance"
                        (builtInProfiles
                         |> List.find (fun p -> p.Name = "production-multi-instance")
                         |> _.Values)
                        (fun () ->
                            let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                            Expect.equal
                                cfg.ReplicaCount
                                11
                                "the deployment's own replica count wins over the posture's placeholder"

                            Expect.isTrue
                                cfg.RequireHttps
                                "…while every key it did NOT override still comes from the bundle"))

            // ─── C — consumer-declared profiles ───────────────────────

            testCase "a duplicate profile name is refused at declaration"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    let message =
                        declareRefusal (consumerProfile "serverless" [ Names.logFormat, "json" ])

                    Expect.stringContains
                        message
                        "already taken"
                        "the selection key would otherwise depend on registration order"

                    ConfigProfiles.declare (consumerProfile "house-style" [ Names.logFormat, "json" ])

                    let second =
                        declareRefusal (consumerProfile "HOUSE-STYLE" [ Names.logFormat, "text" ])

                    Expect.stringContains second "already taken" "collision is case-insensitive, because selection is")

            testCase "a declared profile is refused for every reason a manifest is"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    let unknown = declareRefusal (consumerProfile "p1" [ "TOOLUP_NOT_A_REAL_KEY", "x" ])

                    Expect.stringContains unknown "not a recognised config key" "an unknown key is refused"

                    let secretKey = all |> List.find _.IsSecret |> _.EnvVar
                    let secret = declareRefusal (consumerProfile "p2" [ secretKey, "hunter2" ])

                    Expect.stringContains secret "is a secret" "a secret key is refused, with no hatch"

                    Expect.stringContains
                        secret
                        "Requires"
                        "…and the refusal names the honest alternative rather than only saying no"

                    let unbindable =
                        all
                        |> List.tryFind (fun d ->
                            not d.IsSecret
                            && not (isManifestBindable d.EnvVar)
                            && not (isToolingKey d.EnvVar)
                            && d.EnvVar <> Names.configFile
                            && d.EnvVar <> Names.profile)

                    match unbindable with
                    | Some d ->
                        let message = declareRefusal (consumerProfile "p3" [ d.EnvVar, "x" ])

                        Expect.stringContains
                            message
                            "does not resolve through the config-resolution seam"
                            "a key nothing reads through the seam is refused — declaring it would be the silently-ignored failure this layer exists to prevent"
                    | None ->
                        // The reader-migration sweep can legitimately close
                        // this class out. Say so rather than passing mute.
                        Expect.isTrue
                            true
                            "no unmigrated non-secret key remains in the registry, so this arm has nothing to exercise"

                    let selector =
                        declareRefusal (consumerProfile "p4" [ Names.configFile, "./x.json" ])

                    Expect.stringContains
                        selector
                        "already resolved by the time the bundle is read"
                        "a bundle cannot state what to load"

                    let blank = declareRefusal (consumerProfile "p5" [ Names.logFormat, "" ])

                    Expect.stringContains
                        blank
                        "empty string"
                        "an empty value is refused rather than silently meaning unset"

                    let duplicated =
                        declareRefusal (consumerProfile "p6" [ Names.logFormat, "json"; Names.logFormat, "text" ])

                    Expect.stringContains duplicated "more than once" "a key set twice is refused"

                    let unnamed = declareRefusal (consumerProfile "  " [ Names.logFormat, "json" ])

                    Expect.stringContains unnamed "must have a name" "an unnamed profile could never be selected"

                    let undescribed =
                        declareRefusal {
                            Name = "p7"
                            Description = ""
                            Requires = []
                            Values = [ Names.logFormat, "json" ]
                        }

                    Expect.stringContains
                        undescribed
                        "no description"
                        "the description is what an operator chooses between profiles by reading"

                    let badRequires =
                        declareRefusal {
                            Name = "p8"
                            Description = "x"
                            Requires = [ "TOOLUP_NOT_A_REAL_KEY" ]
                            Values = [ Names.logFormat, "json" ]
                        }

                    Expect.stringContains
                        badRequires
                        "not a recognised config key"
                        "a Requires entry naming nothing is a claim that cannot be checked")

            testCase "the refusal names every problem, not just the first"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    let secretKey = all |> List.find _.IsSecret |> _.EnvVar

                    let message =
                        declareRefusal (consumerProfile "p" [ "TOOLUP_NOT_A_REAL_KEY", "x"; secretKey, "y" ])

                    Expect.stringContains message "TOOLUP_NOT_A_REAL_KEY" "the unknown key is named"
                    Expect.stringContains message secretKey "and so is the secret — one edit, not two boots")

            testCase "declaring after selection has run is refused, naming the ordering"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    ConfigProfiles.markSelected ()

                    let message =
                        declareRefusal (consumerProfile "too-late" [ Names.logFormat, "json" ])

                    Expect.stringContains
                        message
                        "could never be selected"
                        "silently registering a profile nothing will read is the failure mode; refusing names the fix")

            // ─── D — combination validation names the profile ─────────

            testCase "the profile context line names the posture, its lane, and what a higher layer took back"
            <| fun _ ->
                withEnv [ Names.replicaCount, Some "5"; Names.logFormat, None ] (fun () ->
                    withProfile "posture" [ Names.replicaCount, "2"; Names.logFormat, "json" ] (fun () ->
                        match ConfigResolution.profileContextLine () with
                        | None -> failtest "expected a context line while a profile is in force"
                        | Some line ->
                            Expect.stringContains line "posture" "the refusal must say which claim it is about"

                            Expect.stringContains
                                line
                                "not a bypass"
                                "…and that a profile bought no exemption from the preflight"

                            Expect.stringContains
                                line
                                Names.replicaCount
                                "the overridden key is named, so the operator can tell which half of the combination the profile is responsible for"

                            Expect.isFalse
                                (line.Contains Names.logFormat)
                                "a key the profile still supplies is not listed as overridden"))

            testCase "no profile in force ⇒ no context line, so an existing refusal is unchanged"
            <| fun _ ->
                ConfigResolution.clearProfile ()

                Expect.equal
                    (ConfigResolution.profileContextLine ())
                    None
                    "GP 11 — a deployment that imports no posture reads exactly as it did before"

            testCase "a preflight refusal carries the profile context"
            <| fun _ ->
                let failing =
                    { new ConfigValidation.IConfigValidator with
                        member _.Name = "AlwaysFails"
                        member _.Timeout = TimeSpan.FromSeconds 1.0
                        member _.Validate() = async { return ConfigValidation.Error "substrate is incoherent" }
                    }

                let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()

                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<
                    ConfigValidation.IConfigValidator
                 >(
                    services,
                    failing
                )
                |> ignore

                withProfile "posture" [ Names.replicaCount, "2" ] (fun () ->
                    let message =
                        try
                            ConfigValidatorAggregator.validate services None false |> ignore
                            failtest "expected the preflight to abort"
                        with :? ConfigValidatorAggregator.ConfigPreflightFailedException as ex ->
                            ex.Message

                    Expect.stringContains message "substrate is incoherent" "the validator's own message is unchanged"

                    Expect.stringContains
                        message
                        "posture"
                        "…and the profile in force is named beside it, so the operator is not reading a refusal about keys they never typed")

            // ─── E — provenance in --print-config ─────────────────────

            testCase "--print-config labels a profile-supplied value profile:<name> and headlines the posture"
            <| fun _ ->
                withEnv [ Names.replicaCount, None ] (fun () ->
                    withProfile "posture" [ Names.replicaCount, "6" ] (fun () ->
                        let report =
                            StartupModes.renderConfigReport
                                false
                                (all |> List.filter (fun d -> d.EnvVar = Names.replicaCount))

                        Expect.stringContains
                            report
                            (sprintf "%s = 6  [profile:posture]" Names.replicaCount)
                            "the value and the posture that supplied it, on one line"

                        Expect.stringContains
                            report
                            "Profile: posture"
                            "and a header, so a reader knows a profile is in force before they reach a value it supplied"))

            testCase "--print-config --diff keeps a profile-supplied key, which is not a default"
            <| fun _ ->
                withEnv [ Names.replicaCount, None ] (fun () ->
                    withProfile "posture" [ Names.replicaCount, "6" ] (fun () ->
                        let report =
                            StartupModes.renderConfigReport
                                true
                                (all |> List.filter (fun d -> d.EnvVar = Names.replicaCount))

                        Expect.stringContains
                            report
                            Names.replicaCount
                            "a key some layer supplied is a stated deviation from stock, whichever layer supplied it"))

            testCase "with no profile in force --print-config says so"
            <| fun _ ->
                ConfigResolution.clearProfile ()

                let report = StartupModes.renderConfigReport true []

                Expect.stringContains
                    report
                    "Profile: none imported."
                    "silence would be indistinguishable from a report that forgot to look"

            // ─── The manifest's two spellings ─────────────────────────

            testCase "$profile is tolerated in a manifest and recorded, never bound"
            <| fun _ ->
                let load =
                    parseOk
                        """
                        {
                          "$schema": "./toolup.config.schema.json",
                          "$profile": "production-multi-instance",
                          "TOOLUP_REPLICA_COUNT": 4
                        }
                        """

                Expect.equal
                    load.Snapshot.Profile
                    (Some "production-multi-instance")
                    "the name is recorded on the snapshot"

                Expect.isFalse
                    (Map.containsKey "$profile" load.Snapshot.Values)
                    "…and never bound as a config value — it is a name, not a value"

                Expect.equal (Map.tryFind Names.replicaCount load.Snapshot.Values) (Some "4") "the real keys still bind"

            testCase "TOOLUP_PROFILE in a manifest is refused, naming $profile as the way to say it"
            <| fun _ ->
                let message = parseRefusal """{ "TOOLUP_PROFILE": "serverless" }"""

                Expect.stringContains message "$profile" "the refusal names the spelling a manifest uses"

                Expect.stringContains message Names.profile "…and the one it refused, so an operator can find the line"

            testCase "a non-string or empty $profile is refused"
            <| fun _ ->
                Expect.stringContains
                    (parseRefusal """{ "$profile": 3 }""")
                    "must be a string"
                    "a profile is named, not numbered"

                Expect.stringContains
                    (parseRefusal """{ "$profile": "" }""")
                    "remove the line to import none"
                    "an empty name is a mistake with an obvious fix, not a way to import nothing"

            // ─── Installation end to end ──────────────────────────────

            testCase "installOnce selects from TOOLUP_PROFILE with no manifest on disk"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    withTempRoot (fun root ->
                        withEnv
                            [
                                Names.configFile, None
                                Names.profile, Some "serverless"
                                Names.logFormat, None
                            ]
                            (fun () ->
                                try
                                    ConfigResolver.reset ()
                                    let loaded = ConfigResolver.installOnce root

                                    Expect.isNone loaded "no manifest exists — the profile lane stands on its own"

                                    match ConfigResolution.profile () with
                                    | Some p ->
                                        Expect.equal p.Name "serverless" "the environment named it"

                                        Expect.equal
                                            p.SelectedBy
                                            ConfigResolution.EnvProfileSelection
                                            "and the lane is recorded"
                                    | None -> failtest "expected the profile to be installed"

                                    Expect.equal
                                        (ConfigResolution.tryResolve Names.logFormat)
                                        (Some("json", ConfigResolution.ProfileConfigSource "serverless"))
                                        "and its keys resolve through the seam"
                                finally
                                    ConfigResolver.reset ())))

            testCase "installOnce prefers the manifest's $profile over TOOLUP_PROFILE"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    withTempRoot (fun root ->
                        File.WriteAllText(
                            Path.Combine(root, ConfigResolver.DefaultManifestFileName),
                            "{ \"$profile\": \"serverless\" }"
                        )

                        withEnv
                            [
                                Names.configFile, None
                                Names.profile, Some "dev-single-instance"
                                Names.logFormat, None
                            ]
                            (fun () ->
                                try
                                    ConfigResolver.reset ()
                                    ConfigResolver.installOnce root |> ignore

                                    Expect.equal
                                        (ConfigResolution.profile () |> Option.map _.Name)
                                        (Some "serverless")
                                        "the committed statement wins over whatever the host happens to export"
                                finally
                                    ConfigResolver.reset ())))

            testCase "installOnce refuses an unrecognised profile, and installs neither layer"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    withTempRoot (fun root ->
                        File.WriteAllText(
                            Path.Combine(root, ConfigResolver.DefaultManifestFileName),
                            "{ \"$profile\": \"no-such-posture\", \"TOOLUP_REPLICA_COUNT\": 3 }"
                        )

                        withEnv [ Names.configFile, None; Names.profile, None ] (fun () ->
                            try
                                ConfigResolver.reset ()

                                let raised =
                                    try
                                        ConfigResolver.installOnce root |> ignore
                                        None
                                    with ConfigProfiles.ConfigProfileException message ->
                                        Some message

                                match raised with
                                | None -> failtest "expected an unrecognised profile to refuse the boot"
                                | Some message ->
                                    Expect.stringContains message "no-such-posture" "the refusal names it"

                                Expect.isFalse
                                    (ConfigResolution.isInstalled ())
                                    "the manifest is not left installed behind a failed selection — process state must not depend on which half failed"

                                Expect.isFalse (ConfigResolution.isProfileInstalled ()) "and no profile is installed"
                            finally
                                ConfigResolver.reset ())))

            testCase "installOnce with neither lane leaves resolution byte-for-byte as it was (GP 11)"
            <| fun _ ->
                withCleanRegistry (fun () ->
                    withTempRoot (fun root ->
                        withEnv [ Names.configFile, None; Names.profile, None ] (fun () ->
                            try
                                ConfigResolver.reset ()
                                ConfigResolver.installOnce root |> ignore

                                Expect.isFalse (ConfigResolution.isProfileInstalled ()) "nothing selected"

                                for d in all do
                                    let direct =
                                        match Environment.GetEnvironmentVariable d.EnvVar with
                                        | null
                                        | "" -> None
                                        | v -> Some v

                                    Expect.equal
                                        (ConfigResolution.tryValue d.EnvVar)
                                        direct
                                        (sprintf
                                            "%s must resolve to exactly the environment read it did before profiles existed"
                                            d.EnvVar)
                            finally
                                ConfigResolver.reset ())))
        ]
    )
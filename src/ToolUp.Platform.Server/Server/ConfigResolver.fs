// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Discovery, strict parse, refusals and hashing for the deployment
/// configuration manifest — the loader behind the `ConfigResolution` seam.
module ToolUp.Platform.ConfigResolver

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.ConfigKeys

// ─── Phase 696 — the deployment configuration manifest loader ─────────
//
// One file per deployment, sitting one rung below the environment:
//
//     {
//       "$schema": "./toolup.config.schema.json",
//       "TOOLUP_AUTH_MODE": "oidc",
//       "TOOLUP_REPLICA_COUNT": 3,
//       "TOOLUP_REQUIRE_HTTPS": true
//     }
//
// Keys are the canonical environment-variable names, flat — the config-key
// registry IS the schema, with no mapping layer to drift and no second
// naming scheme to learn. Grep finds a key in the file, the environment,
// the source and the reference doc by the same string.
//
// Four properties this loader is responsible for:
//
//   * **Discovery.** `TOOLUP_CONFIG_FILE` wins; else `./toolup.config.json`
//     is probed at the content root; else nothing is loaded and resolution
//     is byte-for-byte what it was before this file existed (GP 11).
//   * **Refusals.** An unknown key refuses startup naming it (everything
//     in a hand-written file is intentional, so a typo there is
//     unambiguous — unlike the environment, which carries platform noise).
//     A secret key refuses naming the environment variable to set instead,
//     with no acceptance hatch: the manifest's whole value is that it is
//     shareable, committable and hashable, and one secret in it destroys
//     all three.
//   * **The hash.** SHA-256 over the RAW FILE BYTES, no canonicalisation.
//     The artefact a report attests to is the file as deployed; a
//     canonicaliser is a normalisation layer whose bugs would become
//     attestation bugs, and "this exact byte sequence" is the honest claim.
//   * **Honest partial coverage.** A registered key whose reader has not
//     migrated to the resolution seam yet is accepted and WARNED about,
//     naming the migration it waits on — because a manifest key that is
//     silently ignored is worse than no manifest at all.
//
// Strict JSON, parsed with `System.Text.Json`: no comments, no trailing
// commas, no YAML, no new dependency. The cost is that the file cannot
// carry an operator's "why is this hatch open" note; that want has a
// design answer waiting and is deliberately not pre-empted here.

/// The file name probed at the content root when `TOOLUP_CONFIG_FILE` is
/// not set.
[<Literal>]
let DefaultManifestFileName = "toolup.config.json"

/// The one non-registry key a manifest may carry: editors use it to find
/// the schema that validates the file as it is typed. Skipped by the
/// binder rather than bound.
///
/// Aliased to the registry-side literal rather than restating it: the
/// generated schema (`ConfigSchema.render`) declares the same property,
/// and a loader that tolerated one spelling while the schema published
/// another would refuse the very file it told the operator to write.
[<Literal>]
let SchemaKey = ConfigKeys.ManifestSchemaProperty

/// Phase 700 — the second tolerated non-registry key: the name of the
/// configuration profile this manifest imports. Recorded on the snapshot
/// rather than bound, because a name is not a value: resolving it
/// against the available profiles is a separate act with its own
/// refusal. Aliased to the registry-side literal for the same reason
/// `SchemaKey` is.
[<Literal>]
let ProfileKey = ConfigKeys.ManifestProfileProperty

/// A manifest that failed to load. The message is the operator-facing
/// refusal and names every problem found, not just the first — a file
/// with three typos should take one edit to fix, not three boots.
exception ConfigManifestException of message: string

/// The outcome of a successful load: the snapshot to install, plus any
/// non-fatal findings to surface once a logger exists.
type ManifestLoad = {
    Snapshot: ConfigResolution.ManifestSnapshot
    Warnings: string list
}

/// Lowercase hex SHA-256 over the raw bytes. No canonicalisation (D7).
let hashBytes (bytes: byte[]) : string =
    use sha = SHA256.Create()

    sha.ComputeHash bytes
    |> Array.map (fun b -> b.ToString "x2")
    |> String.concat ""

let private registeredKeys = all |> List.map _.EnvVar |> Set.ofList

let private secretKeys =
    all |> List.filter _.IsSecret |> List.map _.EnvVar |> Set.ofList

/// Render one JSON scalar as the string an existing reader would have
/// seen in the environment, so every parser downstream behaves
/// identically whichever lane supplied the value. Non-scalars are a
/// refusal: the registry's value types are all scalar, so an array or
/// object is a mistake rather than a shape we have not implemented.
let private scalarValue (key: string) (value: JsonElement) : Result<string, string> =
    match value.ValueKind with
    | JsonValueKind.String -> Ok(value.GetString())
    | JsonValueKind.True -> Ok "true"
    | JsonValueKind.False -> Ok "false"
    | JsonValueKind.Number -> Ok(value.GetRawText())
    | JsonValueKind.Null ->
        Error(
            sprintf
                "%s is null. A manifest states a value; to leave the key unset, remove the line (null is not the same as absent, and accepting it would make the two indistinguishable in a diff)."
                key
        )
    | other ->
        Error(
            sprintf
                "%s is a %s. Manifest values must be a string, number or boolean — every config key resolves to a scalar."
                key
                (string other)
        )

/// Parse manifest bytes into a snapshot. Pure over its inputs (the path
/// is carried for messages only), so the refusal and warning behaviour is
/// testable without touching the filesystem.
///
/// `Error` carries the full operator-facing refusal message; `Ok` carries
/// the snapshot plus the non-fatal warnings.
let parseBytes (path: string) (bytes: byte[]) : Result<ManifestLoad, string> =
    let hash = hashBytes bytes

    let parsed =
        try
            Ok(JsonDocument.Parse(ReadOnlyMemory bytes))
        with :? JsonException as ex ->
            Error(
                sprintf
                    "%s is not valid JSON: %s. The manifest is strict JSON — no comments and no trailing commas."
                    path
                    ex.Message
            )

    match parsed with
    | Error e -> Error e
    | Ok doc ->
        use doc = doc

        if doc.RootElement.ValueKind <> JsonValueKind.Object then
            Error(
                sprintf
                    "%s must contain a JSON object mapping config keys to values; found %s."
                    path
                    (string doc.RootElement.ValueKind)
            )
        else
            let mutable values = Map.empty
            let mutable refusals = []
            let mutable pending = []
            let mutable profile = None

            for prop in doc.RootElement.EnumerateObject() do
                let key = prop.Name

                if key = SchemaKey then
                    // Tolerated so an editor can validate the file while it
                    // is typed. Never bound.
                    ()
                elif key = ProfileKey then
                    // Phase 700 — recorded, not bound. A non-string here is
                    // refused with the same message shape a config value
                    // gets, so the file's two halves read alike.
                    match prop.Value.ValueKind with
                    | JsonValueKind.String ->
                        match prop.Value.GetString() with
                        | null
                        | "" ->
                            refusals <-
                                refusals
                                @ [
                                    sprintf
                                        "%s is empty. Name a configuration profile, or remove the line to import none."
                                        key
                                ]
                        | name -> profile <- Some name
                    | other ->
                        refusals <-
                            refusals
                            @ [
                                sprintf
                                    "%s is a %s. It names a configuration profile, so its value must be a string."
                                    key
                                    (string other)
                            ]
                elif key = Names.profile then
                    refusals <-
                        refusals
                        @ [
                            sprintf
                                "%s cannot be set in a manifest — a manifest names the profile it imports with the \"%s\" entry instead, and admitting both spellings would be two ways to say one thing. Use \"%s\": \"<profile-name>\", or set %s in the environment."
                                key
                                ProfileKey
                                ProfileKey
                                key
                        ]
                elif key = Names.configFile then
                    refusals <-
                        refusals
                        @ [
                            sprintf
                                "%s cannot be set in a manifest — it names the manifest's own location, which has already been resolved by the time this file is read. Set it in the environment."
                                key
                        ]
                elif Set.contains key secretKeys then
                    refusals <-
                        refusals
                        @ [
                            sprintf
                                "%s is a secret and must not appear in a manifest — the file is meant to be shareable, committable and hashable. Set the %s environment variable instead."
                                key
                                key
                        ]
                elif not (Set.contains key registeredKeys) then
                    refusals <-
                        refusals
                        @ [
                            sprintf
                                "%s is not a recognised config key. Every manifest key must be a documented TOOLUP_* variable — see docs/reference/config-reference.md."
                                key
                        ]
                else
                    match scalarValue key prop.Value with
                    | Error e -> refusals <- refusals @ [ e ]
                    | Ok v ->
                        values <- Map.add key v values

                        if not (isManifestBindable key) then
                            pending <- pending @ [ key ]

            if not refusals.IsEmpty then
                Error(
                    sprintf
                        "The deployment configuration manifest at %s was refused:%s%s"
                        path
                        Environment.NewLine
                        (refusals |> List.map (sprintf "  - %s") |> String.concat Environment.NewLine)
                )
            else
                let warnings =
                    match pending with
                    | [] -> []
                    | keys -> [
                        sprintf
                            "The manifest at %s sets %d key(s) whose reader has not migrated to the config-resolution seam yet, so the declared value will NOT take effect: %s. Set the environment variable instead until the reader migrates."
                            path
                            keys.Length
                            (String.concat ", " keys)
                      ]

                Ok {
                    Snapshot = {
                        Path = path
                        Hash = hash
                        Values = values
                        PendingKeys = pending
                        Profile = profile
                    }
                    Warnings = warnings
                }

/// Where the manifest would be read from, if anywhere. `Error` is the
/// one discovery refusal: an explicitly named file that does not exist.
/// Silently ignoring it would be exactly the "declared but not applied"
/// failure this whole layer exists to make impossible.
let discover (contentRoot: string) : Result<string option, string> =
    match Environment.GetEnvironmentVariable Names.configFile with
    | null
    | "" ->
        let probed = Path.Combine(contentRoot, DefaultManifestFileName)

        if File.Exists probed then
            Ok(Some(Path.GetFullPath probed))
        else
            Ok None
    | named ->
        let resolved =
            if Path.IsPathRooted named then
                named
            else
                Path.Combine(contentRoot, named)

        if File.Exists resolved then
            Ok(Some(Path.GetFullPath resolved))
        else
            Error(
                sprintf
                    "%s=%s but no file exists at %s. Unset the variable to fall back to probing ./%s, or point it at the manifest."
                    Names.configFile
                    named
                    (Path.GetFullPath resolved)
                    DefaultManifestFileName
            )

/// Discover, read, hash and parse the manifest under `contentRoot`.
/// `Ok None` is the ordinary state: no manifest, nothing installed,
/// resolution unchanged.
let load (contentRoot: string) : Result<ManifestLoad option, string> =
    match discover contentRoot with
    | Error e -> Error e
    | Ok None -> Ok None
    | Ok(Some path) ->
        let bytes =
            try
                Ok(File.ReadAllBytes path)
            with ex ->
                Error(sprintf "The deployment configuration manifest at %s could not be read: %s" path ex.Message)

        match bytes with
        | Error e -> Error e
        | Ok raw -> parseBytes path raw |> Result.map Some

// Whether this process has already run discovery. Distinct from
// `ConfigResolution.isInstalled ()`, which is false both before the first
// attempt AND after an attempt that found no file — and the difference
// matters: "nobody looked yet" is a wiring defect worth warning about,
// while "looked and found nothing" is the ordinary state.
let mutable private attempted = false

/// True once discovery has run in this process, whatever it found.
let hasLoaded () : bool = attempted

/// Reset the loader for tests. Pairs with `ConfigResolution.clear ()`
/// and `ConfigProfiles.reset ()` — a suite exercising discovery must be
/// able to return the process to its pre-boot state, and a leaked
/// consumer profile contaminates a sibling case exactly as a leaked
/// manifest does.
let reset () : unit =
    attempted <- false
    ConfigResolution.clear ()
    ConfigProfiles.reset ()

/// The bootstrap read of the profile name from the environment.
///
/// Direct, like `TOOLUP_CONFIG_FILE` above it and for the identical
/// reason: this is the variable that names WHAT TO LOAD, so it cannot be
/// resolved through the thing it selects. Reading it through the seam
/// would let a profile name itself, and the loader refuses the key
/// inside a manifest precisely so the two lanes stay distinguishable.
let private envProfileName () : string option =
    match Environment.GetEnvironmentVariable Names.profile with
    | null
    | "" -> None
    | v -> Some v

/// Discover and install the manifest, then select and install the
/// configuration profile, once per process. Raises
/// `ConfigManifestException` on any manifest refusal — an unknown key, a
/// secret key, a malformed file, or a named file that is absent — and
/// `ConfigProfiles.ConfigProfileException` on an unrecognised profile
/// name, because a deployment whose declared intent cannot be honoured
/// must not boot pretending it was.
///
/// Returns the load result so the caller can surface the hash and the
/// warnings through whatever logger it has; a repeat call returns the
/// already-installed snapshot with no warnings (they were reported the
/// first time) and re-reads nothing.
///
/// Called from `ConsoleLogger.fromEnv` — the first SDK entry point every
/// composition root reaches, and necessarily so: the log level is itself
/// a manifest-bindable key, so the manifest has to be in place before the
/// logger is built. A composition root that constructs its own logger
/// calls this directly, before `ServerConfig.fromEnv`.
let installOnce (contentRoot: string) : ManifestLoad option =
    if attempted then
        ConfigResolution.snapshot ()
        |> Option.map (fun s -> { Snapshot = s; Warnings = [] })
    else
        attempted <- true

        match load contentRoot with
        | Error message -> raise (ConfigManifestException message)
        | Ok loaded ->
            // Phase 700 — the profile rung. Selected AFTER the manifest,
            // because the manifest's `"$profile"` entry is the winning
            // lane, and installed BELOW it, so every line the manifest
            // states still beats the bundle it imported.
            //
            // Both installs happen only once both acts have succeeded: a
            // refusal aborts the boot either way, but leaving a manifest
            // installed behind a failed selection would make the process
            // state depend on which half failed, which a test suite (and
            // a `--validate-config` run) can observe.
            let selected =
                ConfigProfiles.selectFrom (loaded |> Option.bind _.Snapshot.Profile) (envProfileName ())

            ConfigProfiles.markSelected ()

            match selected with
            | Error message -> raise (ConfigProfiles.ConfigProfileException message)
            | Ok profile ->
                loaded |> Option.iter (fun l -> ConfigResolution.install l.Snapshot)
                profile |> Option.iter ConfigResolution.installProfile
                loaded

/// `installOnce` against the process's current directory — the content
/// root for every shipped host shape.
let installFromCurrentDirectory () : ManifestLoad option =
    installOnce (Directory.GetCurrentDirectory())

/// The one-line boot-log statement of declared intent: which file, and
/// the hash of the exact bytes that were read. `None` when no manifest is
/// loaded, so a deployment that has not written one logs nothing new.
let bootLine () : string option =
    ConfigResolution.snapshot ()
    |> Option.map (fun s ->
        sprintf
            "Deployment configuration manifest: %s (sha256:%s, %d key(s) bound)."
            s.Path
            s.Hash
            (Map.count s.Values))
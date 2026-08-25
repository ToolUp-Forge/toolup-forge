// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The per-key configuration resolution seam: one place that answers
/// "what is this key's effective value, and where did it come from?".
module ToolUp.Platform.ConfigResolution

// ─── Phase 696 — the declared layer beneath the environment ──────────
//
// A deployment is 30-50 flat environment strings with no reviewable
// artefact: no diff between staging and production, no single hashable
// statement of intent, and a typo boots a different subsystem without a
// murmur. The deployment configuration manifest adds a *declared* layer
// one rung BELOW the environment — one JSON file whose keys are the
// canonical env-var names, bound through the central config-key registry.
//
// This module is the resolution seam that layer arrives through. It is
// deliberately tiny and dependency-free (no file IO — see below), so it
// can sit in Core beside the registry and be consulted by
// `ServerConfig.fromEnv`, which is the largest single reader in the SDK.
//
// Precedence (the documented chain, one rung longer than before):
//
//     consumer literal  >  env var  >  manifest  >  override record  >  default
//
// Only the middle two are visible here: a consumer literal never
// traverses a reader at all, and the override record is applied by the
// reader that owns it. `ConfigSource` names all five so a provenance
// report can label any of them.
//
// **Absent manifest ⇒ byte-for-byte prior behaviour (GP 11).** With
// nothing installed, `tryResolve` is exactly the old private `envVar`
// helper: read the variable, treat null/empty as unset. Nothing changes
// for an existing deployment until it writes the file.
//
// **Why the loader is not here.** Discovering and parsing the file is
// file IO plus hashing, and Core ships its source in the nupkg for Fable
// consumers — `System.IO` cannot appear in it. The loader therefore lives
// in `ToolUp.Platform.Server` (`ConfigResolver`) and installs its result
// through this seam.

// ─── Phase 700 — the profile rung, one below the manifest ────────────
//
// A profile is a NAMED BUNDLE of registered keys, resolved one rung
// below the manifest:
//
//     literal > env > manifest > PROFILE > override > default
//
// It sits below the manifest so a deployment's explicit line always
// beats the bundle it imported — importing a posture must never take a
// setting away from the file that imported it. That single ordering
// decision is what makes a profile safe to adopt: the worst a wrong
// profile can do is supply a key the deployment had not thought about,
// and `--print-config` names the profile beside every such key.
//
// **A profile is a CLAIM, not a bypass.** The values it supplies reach
// every reader through this same seam, so the preflight validates the
// resolved combination exactly as if each key had been typed by hand.
// A profile whose bundle is incoherent is refused at boot like any
// other incoherent deployment — with the profile named, so the operator
// knows which claim the refusal is about.
//
// The type lives here rather than beside the built-in bundles for the
// same reason `ManifestSnapshot` does: this module is the seam every
// reader consults, and it must not depend on the registry.

/// Where a resolved configuration value came from. The full precedence
/// chain, so a provenance report can label any rung — not only the
/// three this module resolves itself.
type ConfigSource =
    /// A value written directly in consumer code (a `{ ServerConfig.defaults
    /// with ... }` literal). Never traverses a reader, so it always wins.
    | LiteralConfigSource
    /// An environment variable — the per-instance / secret override lane.
    | EnvConfigSource
    /// The deployment configuration manifest — the declared, reviewable base.
    | ManifestConfigSource
    /// A named configuration profile, carrying the name it was selected
    /// under. The name rides the case because a provenance report saying
    /// only "profile" would leave the operator to guess which posture
    /// supplied the value — the one question the rung exists to answer.
    | ProfileConfigSource of profile: string
    /// A curated overrides record supplied by the composition root.
    | OverrideConfigSource
    /// No layer supplied a value; the descriptor's declared default stands.
    | DefaultConfigSource

[<RequireQualifiedAccess>]
module ConfigSource =

    /// The stable lowercase label used in `--print-config`, the generated
    /// reference, and any conformance projection. Stable wire vocabulary —
    /// changing one of these strings is a breaking change to a report a
    /// deployment may be diffing against.
    let label (source: ConfigSource) : string =
        match source with
        | LiteralConfigSource -> "literal"
        | EnvConfigSource -> "env"
        | ManifestConfigSource -> "manifest"
        | ProfileConfigSource name -> "profile:" + name
        | OverrideConfigSource -> "override"
        | DefaultConfigSource -> "default"

/// A loaded deployment configuration manifest, as installed by the
/// server-side loader. Immutable (GP 5) — a reload installs a new value
/// rather than mutating this one.
type ManifestSnapshot = {
    /// The absolute path the manifest was read from, for the boot log and
    /// `--print-config` header.
    Path: string
    /// Lowercase hex SHA-256 over the **raw file bytes**. No
    /// canonicalisation: the attested artefact is the file as deployed,
    /// and a normaliser's bugs would become attestation bugs.
    Hash: string
    /// The bound key/value pairs, keyed by canonical env-var name. Values
    /// are the manifest's scalars rendered as strings, so every existing
    /// reader parses them exactly as it parses an environment value.
    Values: Map<string, string>
    /// Keys that are registered and were accepted, but whose reader has
    /// not yet migrated to this seam — so the manifest states them and
    /// nothing consults them. Surfaced as a startup warning; the whole
    /// point of declaring bindability is that this case is visible rather
    /// than latent.
    PendingKeys: string list
    /// Phase 700 — the name in the manifest's `"$profile"` entry, if it
    /// carried one. A name, not a resolved bundle: the manifest states
    /// which posture it imports, and resolving that name against the
    /// available profiles is a separate act that can fail with its own
    /// refusal.
    Profile: string option
}

[<RequireQualifiedAccess>]
module ManifestSnapshot =

    /// The value an absent manifest contributes: nothing at all.
    let none: ManifestSnapshot option = None

/// How a profile came to be in force. Two lanes, in precedence order,
/// and the distinction is operator-facing: a refusal or a boot line that
/// says only "profile X" leaves them hunting for where X was named.
type ProfileSelection =
    /// The manifest's `"$profile"` entry — the reviewable, committed
    /// statement, and the one that wins.
    | ManifestProfileSelection
    /// The `TOOLUP_PROFILE` environment variable — the per-instance lane,
    /// used when the manifest names no profile (or there is no manifest).
    | EnvProfileSelection

[<RequireQualifiedAccess>]
module ProfileSelection =

    /// How to describe the selection lane in an operator-facing line.
    let describe (selection: ProfileSelection) : string =
        match selection with
        | ManifestProfileSelection -> "the manifest's \"$profile\" entry"
        | EnvProfileSelection -> "TOOLUP_PROFILE"

/// A resolved configuration profile, as installed by the server-side
/// selector. Immutable (GP 5), like the manifest snapshot beside it.
type ProfileSnapshot = {
    /// The profile's canonical name, as the available set spells it —
    /// not necessarily as the operator typed it, since selection matches
    /// case-insensitively the way every enum-valued reader does.
    Name: string
    /// The bundle's key/value pairs, keyed by canonical env-var name.
    /// Rendered as strings for the same reason the manifest's are: every
    /// reader must parse a profile value exactly as it parses an
    /// environment one.
    Values: Map<string, string>
    /// Which lane named this profile.
    SelectedBy: ProfileSelection
}

[<RequireQualifiedAccess>]
module ProfileSnapshot =

    /// The value an unselected profile contributes: nothing at all.
    let none: ProfileSnapshot option = None

// The installed manifest is process-wide mutable state, which the SDK
// otherwise avoids. The justification is that it mirrors the thing it
// layers under: the process environment is itself one ambient, mutable,
// process-wide table, and every reader already consults it that way. The
// alternative — threading a resolver value through `ServerConfig.fromEnv`
// and its ~40 private parsers — would widen a public signature (breaking
// every composition root) to model something that is a boot-time
// singleton in fact. Written once during startup, read-only thereafter;
// `clear` exists for tests, which must be able to return the process to
// the no-manifest state.
let mutable private installedManifest: ManifestSnapshot option = None

/// True once a manifest has been installed in this process.
let isInstalled () : bool = installedManifest.IsSome

/// The installed manifest, if any. `None` is the ordinary state — a
/// deployment that has not written the file.
let snapshot () : ManifestSnapshot option = installedManifest

/// Install a loaded manifest as the layer beneath the environment.
/// Called by the server-side loader during startup, before any reader
/// resolves a key.
let install (manifest: ManifestSnapshot) : unit = installedManifest <- Some manifest

// The selected profile, held the same way and for the same reason as the
// manifest above it. Written once during startup, read-only thereafter.
let mutable private installedProfile: ProfileSnapshot option = None

/// True once a profile has been selected and installed in this process.
let isProfileInstalled () : bool = installedProfile.IsSome

/// The profile in force, if any. `None` is the ordinary state — no
/// `"$profile"` entry and no `TOOLUP_PROFILE`.
let profile () : ProfileSnapshot option = installedProfile

/// Install a resolved profile as the layer beneath the manifest. Called
/// by the server-side selector during startup, before any reader
/// resolves a key.
let installProfile (selected: ProfileSnapshot) : unit = installedProfile <- Some selected

/// Return the process to the no-profile state, leaving any installed
/// manifest alone. Test seam.
let clearProfile () : unit = installedProfile <- None

/// Return the process to the pre-boot state: no manifest and no profile.
/// Test seam — a suite that installs either must be able to restore
/// byte-for-byte prior behaviour for every case that follows it, and a
/// leaked profile contaminates a sibling case exactly as a leaked
/// manifest does.
let clear () : unit =
    installedManifest <- None
    installedProfile <- None

/// Read the raw environment variable, treating null / empty as unset —
/// the long-standing convention every `*FromEnv` reader already applies.
///
/// Fable cannot compile `System.Environment`, and a Fable client has no
/// environment to read, so under Fable this arm resolves to "unset" and
/// the whole seam degrades to the manifest layer alone (which a client
/// never installs either). `FABLE_COMPILER` is the one compile-time gate
/// permitted in packed source — Fable defines it itself.
let private readEnv (name: string) : string option =
#if FABLE_COMPILER
    ignore name
    None
#else
    match System.Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v
#endif

/// The manifest's value for `name`, if the manifest supplies one. An
/// empty string is treated as unset, exactly as an empty environment
/// variable is — the two lanes must agree or a key's meaning would depend
/// on which one set it.
let tryManifestValue (name: string) : string option =
    match installedManifest with
    | None -> None
    | Some m ->
        match Map.tryFind name m.Values with
        | Some ""
        | None -> None
        | Some v -> Some v

/// The selected profile's value for `name`, if the bundle supplies one.
/// Empty is unset, exactly as in the two lanes above it.
let tryProfileValue (name: string) : string option =
    match installedProfile with
    | None -> None
    | Some p ->
        match Map.tryFind name p.Values with
        | Some ""
        | None -> None
        | Some v -> Some v

/// Resolve one key through the seam: environment, then manifest, then
/// the selected profile. The returned source is the layer that supplied
/// the value; `None` means no layer did and the reader's own default
/// stands.
///
/// The profile sits BELOW the manifest, so a deployment's explicit line
/// always beats the bundle it imported.
///
/// With neither a manifest nor a profile installed this is precisely the
/// old env-only read, so an existing deployment resolves byte-for-byte
/// as before (GP 11).
let tryResolve (name: string) : (string * ConfigSource) option =
    match readEnv name with
    | Some v -> Some(v, EnvConfigSource)
    | None ->
        match tryManifestValue name with
        | Some v -> Some(v, ManifestConfigSource)
        | None ->
            match tryProfileValue name with
            | Some v -> Some(v, ProfileConfigSource installedProfile.Value.Name)
            | None -> None

/// The effective value for `name`, discarding provenance. The drop-in
/// shape for a reader that previously called `Environment.GetEnvironment
/// Variable` and folded null/empty to `None`.
let tryValue (name: string) : string option = tryResolve name |> Option.map fst

/// Which layer `name`'s effective value came from. Reports
/// `DefaultConfigSource` when neither the environment nor the manifest
/// supplied one — the two rungs this seam cannot observe (a consumer
/// literal, an overrides record) are applied above it by the reader that
/// owns them, so a report wanting those must say so itself.
let sourceOf (name: string) : ConfigSource =
    match tryResolve name with
    | Some(_, source) -> source
    | None -> DefaultConfigSource

/// The keys a profile supplies that a higher rung has taken back — the
/// deployment's own explicit lines, which always beat the bundle. Sorted,
/// so the same combination always reads the same way.
///
/// Not a warning anywhere: overriding an imported posture is the ordinary
/// reason to import one. It is reported because an operator reading a
/// refusal needs to know which half of the combination the profile is
/// actually responsible for.
let profileShadowedKeys () : string list =
    match installedProfile with
    | None -> []
    | Some p ->
        p.Values
        |> Map.toList
        |> List.filter (fun (key, _) -> sourceOf key <> ProfileConfigSource p.Name)
        |> List.map fst
        |> List.sort

/// The one-paragraph statement of the profile in force, for a preflight
/// refusal or a validation summary to append. `None` when no profile is
/// selected, so a deployment that imports no posture reads exactly as it
/// did before (GP 11).
///
/// This is Phase 700's D-clause made concrete. A profile is a *claimed
/// posture*, and the preflight is what checks the claim — so a refusal
/// evaluated against a combination a profile contributed to must say so,
/// or the operator reads a message about keys they never typed and
/// concludes the refusal is about something else. It deliberately does
/// NOT claim which key caused which validator to fail: the validators
/// report on composed substrate, not on keys, and inventing an
/// attribution would be worse than naming the context honestly.
let profileContextLine () : string option =
    match installedProfile with
    | None -> None
    | Some p ->
        let shadowed = profileShadowedKeys ()

        let shadowedNote =
            match shadowed with
            | [] -> ""
            | keys ->
                sprintf
                    ", of which %d %s overridden by a higher layer (%s)"
                    keys.Length
                    (if keys.Length = 1 then "is" else "are")
                    (String.concat ", " keys)

        Some(
            sprintf
                "Configuration profile in force: %s (selected by %s). It supplies %d key(s) one rung below the manifest%s. A profile is a claimed posture, not a bypass — the combination reported above was validated exactly as if every key had been set by hand. Run --print-config to see each key's effective value and the layer it came from."
                p.Name
                (ProfileSelection.describe p.SelectedBy)
                (Map.count p.Values)
                shadowedNote
        )
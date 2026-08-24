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

/// Where a resolved configuration value came from. The full precedence
/// chain, so a provenance report can label any rung — not only the two
/// this module resolves itself.
type ConfigSource =
    /// A value written directly in consumer code (a `{ ServerConfig.defaults
    /// with ... }` literal). Never traverses a reader, so it always wins.
    | LiteralConfigSource
    /// An environment variable — the per-instance / secret override lane.
    | EnvConfigSource
    /// The deployment configuration manifest — the declared, reviewable base.
    | ManifestConfigSource
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
}

[<RequireQualifiedAccess>]
module ManifestSnapshot =

    /// The value an absent manifest contributes: nothing at all.
    let none: ManifestSnapshot option = None

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

/// Return the process to the no-manifest state. Test seam — a suite that
/// installs a manifest must be able to restore byte-for-byte prior
/// behaviour for every case that follows it.
let clear () : unit = installedManifest <- None

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

/// Resolve one key through the seam: environment first, then manifest.
/// The returned source is the layer that supplied the value; `None` means
/// no layer did and the reader's own default stands.
///
/// With no manifest installed this is precisely the old env-only read, so
/// an existing deployment resolves byte-for-byte as before (GP 11).
let tryResolve (name: string) : (string * ConfigSource) option =
    match readEnv name with
    | Some v -> Some(v, EnvConfigSource)
    | None ->
        match tryManifestValue name with
        | Some v -> Some(v, ManifestConfigSource)
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
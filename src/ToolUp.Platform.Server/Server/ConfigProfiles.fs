// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The configuration-profile registry and selector: which named bundles
/// exist, and which one (if any) this deployment imported.
module ToolUp.Platform.ConfigProfiles

open ToolUp.Platform
open ToolUp.Platform.ConfigKeys

// ─── Phase 700 — profiles: selection and consumer declaration ─────────
//
// The bundles themselves are registry data (`ConfigKeys.builtInProfiles`)
// and the rung they resolve at is the seam's (`ConfigResolution`). What
// is left, and lives here, is the pair of acts that connect them:
//
//   * **Declaration.** A consumer registers its own profiles before the
//     SDK reads a key. Additive surface beside `ServerConfig`, never a
//     widened constructor — a new field on that record retypes it and
//     breaks every composition root that constructs one.
//   * **Selection.** Exactly one profile is in force, chosen by the
//     manifest's `"$profile"` entry if it carries one, else by
//     `TOOLUP_PROFILE`. An unrecognised name REFUSES startup and lists
//     what is available, because the alternative — booting on the
//     defaults the profile was imported to replace — is the "declared
//     but not applied" failure this whole layer exists to make
//     impossible, and it would present as a production deployment
//     quietly running a development posture.
//
// **Why declaration is a process-wide registration rather than a
// `ServerConfig` field.** The profile has to be in force before the
// first reader resolves a key, and the first reader is the logger:
// `TOOLUP_LOG_LEVEL` is bindable, so a profile installed after the
// logger is built could not configure the logger it configures. That is
// the same ordering `ConfigResolver.installOnce` already answers, and a
// value threaded through `ServerConfig.fromEnv` arrives strictly later.
// So this mirrors the seam it feeds: ambient, written once at startup,
// read-only thereafter.
//
// **Ordering, stated because it is easy to get wrong.** A consumer's
// declarations must land BEFORE `ConsoleLogger.fromEnv ()` (or a direct
// `ConfigResolver.installFromCurrentDirectory ()`), i.e. at the top of
// `main`. Declaring afterwards is not silently ignored: `declare`
// refuses once selection has run, naming the ordering, rather than
// registering a profile that can never be selected.

/// Raised when a profile cannot be declared or selected. The message is
/// the operator-facing refusal and names every problem found, not just
/// the first.
exception ConfigProfileException of message: string

// Consumer-declared profiles, in declaration order. Ambient for the
// reason given in the header: the profile must be in force before the
// logger exists, which is before any value a composition root could
// thread through has been constructed.
let mutable private declaredProfiles: ConfigProfile list = []

// Whether selection has already run in this process. Declaring after
// that point cannot affect anything, so it is refused rather than
// accepted into a registry nobody will read again.
let mutable private selectionRan = false

/// The profiles a consumer has declared in this process, in declaration
/// order.
let declared () : ConfigProfile list = declaredProfiles

/// Every profile that can be selected: the built-in set first, then the
/// consumer's own. Order is stable and authored, so a refusal lists them
/// the same way twice running.
let available () : ConfigProfile list = builtInProfiles @ declaredProfiles

/// Return the registry to its pre-declaration state. Test seam — a suite
/// that declares a profile must be able to restore the built-in-only set
/// for every case that follows it.
let reset () : unit =
    declaredProfiles <- []
    selectionRan <- false

/// Register a consumer-defined profile. Call at the top of `main`,
/// before `ConsoleLogger.fromEnv ()` builds the logger and the manifest
/// is discovered.
///
/// Raises `ConfigProfileException` on any problem — a duplicate name
/// (the selection key would then depend on registration order), a key
/// that is unregistered, secret, or whose reader does not resolve
/// through the seam, or a declaration arriving after selection has
/// already run. Every rule is one the manifest loader already applies to
/// a file; a bundle is the same declarative act one rung lower, and a
/// rule that held for the file but not for the bundle would be a way
/// around it.
let declare (candidate: ConfigProfile) : unit =
    if selectionRan then
        raise (
            ConfigProfileException(
                sprintf
                    "Configuration profile \"%s\" was declared after profile selection had already run, so it could never be selected. Declare profiles at the top of main, before ConsoleLogger.fromEnv () (or ConfigResolver.installFromCurrentDirectory ())."
                    candidate.Name
            )
        )

    match profileProblems (available ()) candidate with
    | [] -> declaredProfiles <- declaredProfiles @ [ candidate ]
    | problems ->
        raise (
            ConfigProfileException(
                sprintf
                    "The configuration profile declaration was refused:%s%s"
                    System.Environment.NewLine
                    (problems
                     |> List.map (sprintf "  - %s")
                     |> String.concat System.Environment.NewLine)
            )
        )

/// Register several profiles in order. Equivalent to calling `declare`
/// on each, so the first problem found still refuses — a partial
/// registration is not left behind for the ones already accepted, which
/// is fine because the refusal aborts startup.
let declareAll (candidates: ConfigProfile list) : unit = candidates |> List.iter declare

/// The profile named `name`, matched case-insensitively — the same
/// forgiveness every enum-valued reader gives an environment value.
let tryFind (name: string) : ConfigProfile option =
    let lowered = name.Trim().ToLowerInvariant()

    available () |> List.tryFind (fun p -> p.Name.ToLowerInvariant() = lowered)

/// The refusal for a name no profile answers to. Lists the available set
/// rather than only saying the name is wrong: an operator who typed
/// `production` needs to see `production-multi-instance`, and the set is
/// small enough that printing all of it is better than guessing at a
/// nearest match.
let private unknownProfileRefusal (selection: ConfigResolution.ProfileSelection) (name: string) : string =
    let names = available () |> List.map (fun p -> "\"" + p.Name + "\"")

    sprintf
        "Configuration profile \"%s\" (named by %s) is not a recognised profile. Available: %s. A profile a deployment names but the process cannot resolve would leave it running the very defaults the profile was imported to replace, so this refuses rather than falls back. Declare a consumer profile with ConfigProfiles.declare at the top of main, before the logger is built."
        name
        (ConfigResolution.ProfileSelection.describe selection)
        (String.concat ", " names)

/// Resolve the profile in force from the two candidate names, without
/// touching process state. Pure over its inputs, so the precedence and
/// the refusal are testable without an environment or a file.
///
/// `manifestProfile` is the manifest's `"$profile"` entry and wins:
/// it is the reviewed, committed statement, while the environment lane
/// is per-instance. (Note this is the one place the manifest sits ABOVE
/// the environment, and deliberately: the two are not resolving a config
/// value here, they are answering "which bundle did this deployment
/// import", and the committed file is the better authority on that than
/// whatever a host happens to have exported.)
///
/// `Ok None` is the ordinary state: neither lane named a profile,
/// nothing is installed, and resolution is byte-for-byte what it was
/// (GP 11).
let selectFrom
    (manifestProfile: string option)
    (envProfile: string option)
    : Result<ConfigResolution.ProfileSnapshot option, string> =
    let named =
        match manifestProfile with
        | Some name when not (System.String.IsNullOrWhiteSpace name) ->
            Some(name, ConfigResolution.ManifestProfileSelection)
        | _ ->
            match envProfile with
            | Some name when not (System.String.IsNullOrWhiteSpace name) ->
                Some(name, ConfigResolution.EnvProfileSelection)
            | _ -> None

    match named with
    | None -> Ok None
    | Some(name, selection) ->
        match tryFind name with
        | None -> Error(unknownProfileRefusal selection name)
        | Some p ->
            Ok(
                Some {
                    Name = p.Name
                    Values = Map.ofList p.Values
                    SelectedBy = selection
                }
            )

/// Mark selection as having run, so a later `declare` refuses rather
/// than registering a profile nothing will read. Called by the loader
/// once, whatever the outcome of selection.
let markSelected () : unit = selectionRan <- true

/// The one-line boot-log statement of the imported posture: which
/// profile, which lane named it, and how many keys it contributed.
/// `None` when no profile is in force, so a deployment that imports none
/// logs nothing new.
let bootLine () : string option =
    ConfigResolution.profile ()
    |> Option.map (fun p ->
        let shadowed = ConfigResolution.profileShadowedKeys ()

        sprintf
            "Configuration profile: %s (selected by %s, %d key(s) supplied below the manifest, %d overridden above it)."
            p.Name
            (ConfigResolution.ProfileSelection.describe p.SelectedBy)
            (Map.count p.Values)
            shadowed.Length)
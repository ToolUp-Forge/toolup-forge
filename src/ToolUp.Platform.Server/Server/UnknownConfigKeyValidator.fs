// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Preflight guard over the *names* of the `TOOLUP_*` environment
/// variables a deployment has set.
module ToolUp.Platform.UnknownConfigKeyValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigKeys
open ToolUp.Platform.ConfigValidation

// ─── Phase 695 — the name-level config guard ─────────────────────────
//
// A set `TOOLUP_*` variable whose name is in no registry entry is read by
// nothing and reported by nothing: `TOOLUP_AUTH_MOD=oidc` boots the dev
// header auth provider without a murmur, and an operator can believe a
// deployment is in an authenticated posture it is not in. The value-level
// checks cannot see this — they validate the values of keys that ARE
// known, so a key nobody knows about is outside their quantifier
// altogether.
//
// This validator closes the name level: enumerate the set `TOOLUP_*`
// variables, subtract everything the SDK can account for, and report the
// remainder with a nearest-registered-key suggestion.
//
// **Warning by default, refusal only on request.** The environment is not
// a curated artefact — it carries orchestrator injections, CI leftovers
// and, most awkwardly, the open-ended `TOOLUP_{SCOPE}_{KEY}` names the
// environment-backed secret store reads, which cannot be enumerated by
// construction. A hard refusal would therefore false-positive on
// legitimate deployments, so the default states the finding and lets the
// operator judge it; a deployment that has curated its environment and
// wants the guard to bite sets the strict-mode key. The asymmetry with the
// manifest loader — which refuses an unknown key outright — is deliberate:
// everything in a hand-written file is intentional, so a typo there is
// unambiguous.
//
// **Three exclusions, and why each is a class rather than a name.**
//   * **Registered keys** — the registry is the definition of "known".
//   * **The two declared prefixes** (`TOOLUP_COMPONENT__`,
//     `TOOLUP_EXTERNAL_COMPUTE_HTTP_`) — each is registered under its
//     prefix and its suffix is supplied at runtime, so the full names are
//     unknowable here. A stray component override is not silently accepted
//     either: `ComponentConfigResolver.overrideValidator` owns that check
//     and can do it properly, because it knows which components composed.
//   * **Tooling keys** — the build, test and analyzer names, excluded so a
//     developer box that has run a build does not warn on its own
//     leftovers.
//
// GP 11: a deployment with no unrecognised variables returns `Ok` and
// nothing changes.

/// Enumerate the names of the environment variables that are set. Taken
/// as a parameter rather than read directly so the guard is pure over an
/// injected view of the environment — deterministic, and testable without
/// mutating process-global state.
type EnvNameEnumerator = unit -> string list

/// The `TOOLUP_*` prefix every config key the SDK reads carries.
[<Literal>]
let KeyPrefix = "TOOLUP_"

/// The prefixes registered as prefixes rather than as variables in their
/// own right. A variable starting with one of these is a well-formed
/// member of that family, whatever its suffix.
let declaredPrefixes = [ Names.componentConfigPrefix; Names.externalComputeHttpPrefix ]

let private registeredKeys = all |> List.map _.EnvVar |> Set.ofList

/// Production enumerator over the process environment.
///
/// A variable set to the empty string is treated as unset, matching the
/// convention every reader in the SDK already applies — so blanking a
/// variable to disable it never produces a finding about the name.
let environmentNameEnumerator: EnvNameEnumerator =
    fun () ->
        Environment.GetEnvironmentVariables()
        |> Seq.cast<Collections.DictionaryEntry>
        |> Seq.choose (fun entry ->
            match entry.Key, entry.Value with
            | (:? string as k), (:? string as v) when not (String.IsNullOrEmpty v) -> Some k
            | _ -> None)
        |> List.ofSeq

/// Levenshtein edit distance, iterative two-row form. Used only to pick a
/// suggestion, so it is intentionally the plain algorithm over the raw
/// characters — no transposition case, no case folding (a case-only
/// difference is reported as its own, more precise finding below).
let editDistance (a: string) (b: string) : int =
    if String.IsNullOrEmpty a then
        (if isNull b then 0 else b.Length)
    elif String.IsNullOrEmpty b then
        a.Length
    else
        let mutable previous = Array.init (b.Length + 1) id
        let mutable current = Array.zeroCreate<int> (b.Length + 1)

        for i in 1 .. a.Length do
            current[0] <- i

            for j in 1 .. b.Length do
                let substitution = previous[j - 1] + (if a[i - 1] = b[j - 1] then 0 else 1)
                current[j] <- min (min (current[j - 1] + 1) (previous[j] + 1)) substitution

            let swap = previous
            previous <- current
            current <- swap

        previous[b.Length]

/// How far a registered key may sit from an unrecognised name and still be
/// offered as the likely intent. Scaled to the name's length so a long
/// name is not matched to something merely long, and capped so no
/// suggestion is ever a guess dressed as an answer.
let private suggestionThreshold (name: string) = min 4 (max 2 (name.Length / 4))

/// What the operator most likely meant by `unknown`, if anything.
///
/// A registered key differing only in case is reported as exactly that
/// rather than as a near miss: environment variable names are
/// case-sensitive on Linux, so `toolup_auth_mode` there is genuinely read
/// by nothing, and "did you mean" would understate a name that is already
/// correct apart from its case.
let suggestionFor (unknown: string) : string option =
    let caseOnly =
        registeredKeys
        |> Set.toList
        |> List.tryFind (fun k -> String.Equals(k, unknown, StringComparison.OrdinalIgnoreCase))

    match caseOnly with
    | Some k -> Some(sprintf "%s differs only in case — environment variable names are case-sensitive on Linux" k)
    | None ->
        let nearest =
            registeredKeys
            |> Set.toList
            |> List.map (fun k -> k, editDistance unknown k)
            |> List.sortBy (fun (k, d) -> d, k)
            |> List.tryHead

        match nearest with
        | Some(k, d) when d <= suggestionThreshold unknown -> Some(sprintf "did you mean %s?" k)
        | _ -> None

/// The set `TOOLUP_*` names that no registry entry, declared prefix or
/// tooling classification accounts for. Sorted and de-duplicated so the
/// message is stable across runs and platforms.
let unrecognisedNames (names: string list) : string list =
    // The prefix is matched case-INSENSITIVELY and membership Ordinal, and
    // the asymmetry is the point. A lowercased `toolup_auth_mode` is read
    // by nothing on Linux, where variable names are case-sensitive — so it
    // is exactly the silent failure this guard exists for, and a
    // case-sensitive prefix filter would drop it before the check ran.
    // Membership stays exact for the same reason: folding case there would
    // accept a name no reader can find.
    names
    |> List.filter (fun n -> n.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
    |> List.filter (fun n -> not (Set.contains n registeredKeys))
    |> List.filter (fun n -> not (isToolingKey n))
    |> List.filter (fun n ->
        declaredPrefixes
        |> List.forall (fun p -> not (n.StartsWith(p, StringComparison.Ordinal))))
    |> List.distinct
    |> List.sort

/// One unrecognised name with its suggestion, rendered for the message.
let private describeName (name: string) =
    match suggestionFor name with
    | Some hint -> sprintf "%s (%s)" name hint
    | None -> name

/// Whether strict mode is on. Read through the config-resolution seam
/// rather than the environment directly, so a deployment can declare the
/// escalation in its configuration manifest alongside everything else it
/// declares — the key is registered as manifest-bindable for exactly that
/// reason. Only the canonical truthy spellings enable, matching every
/// other boolean gate in the SDK.
let strictModeEnabled () : bool =
    match ConfigResolution.tryValue Names.strictConfig with
    | None -> false
    | Some v ->
        match v.Trim().ToLowerInvariant() with
        | "1"
        | "true"
        | "yes"
        | "on" -> true
        | _ -> false

/// The guard's verdict over an injected environment view. Pure, so the
/// whole of its behaviour is testable without touching the process
/// environment or the filesystem.
let evaluate (strict: bool) (names: string list) : ValidationResult =
    match unrecognisedNames names with
    | [] -> Ok
    | unknown ->
        let listing = unknown |> List.map describeName |> String.concat ", "

        if strict then
            Error(
                sprintf
                    "%d environment variable(s) with the %s prefix are set but name no config key the SDK reads, so nothing consults their values: %s. %s is set, so this refuses the boot rather than warning. Correct the name, unset the variable, or unset %s to downgrade this to a warning; the recognised names are in docs/reference/config-reference.md."
                    unknown.Length
                    KeyPrefix
                    listing
                    Names.strictConfig
                    Names.strictConfig
            )
        else
            Warning(
                sprintf
                    "%d environment variable(s) with the %s prefix are set but name no config key the SDK reads, so nothing consults their values: %s. Correct the name or unset the variable; the recognised names are in docs/reference/config-reference.md. This is a warning rather than a refusal because the environment is not a curated artefact — scope-keyed secrets read through the environment-backed secret store use an open-ended %s{SCOPE}_{KEY} shape that cannot be enumerated, and will be reported here. Set %s=1 once the environment is curated to make this refuse the boot."
                    unknown.Length
                    KeyPrefix
                    listing
                    KeyPrefix
                    Names.strictConfig
            )

/// Config validator that reports set `TOOLUP_*` environment variables
/// whose names are in no registry entry — values a deployment believes it
/// has supplied and that nothing reads. Warning by default; a startup
/// refusal when strict mode is on.
type UnknownConfigKeyValidator(enumEnvNames: EnvNameEnumerator) =
    /// Stable registration name (the `IConfigValidator` identity key).
    static member val Name = "unknown-config-key" with get

    interface IConfigValidator with
        member _.Name = UnknownConfigKeyValidator.Name
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return evaluate (strictModeEnabled ()) (enumEnvNames ()) }

/// The guard over the real process environment — the shape `compose`
/// registers.
let validator () : IConfigValidator =
    UnknownConfigKeyValidator(environmentNameEnumerator) :> IConfigValidator
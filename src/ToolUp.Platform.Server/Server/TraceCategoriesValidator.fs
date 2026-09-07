// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Preflight guard over the *values* inside `TOOLUP_TRACE_CATEGORIES`.
module ToolUp.Platform.TraceCategoriesValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigKeys
open ToolUp.Platform.ConfigValidation

// ─── Phase 9m.C (Gap 11) — the value-level trace-category guard ───────
//
// `TOOLUP_TRACE_CATEGORIES=webhoks,jobs` is accepted by everything. The
// name is registered, so Phase 695's unknown-config-key guard is silent
// by construction — it quantifies over key *names*, and this key's name
// is correct. The value is a whitelist matched by exact string
// membership, so `webhoks` simply matches no emission site: the logger
// emits nothing under it and reports nothing about it, and the operator
// who set it reads the silence as "the subsystem is quiet" rather than
// "my filter is misspelt". That is the whole failure mode, and it is
// invisible at every layer that could otherwise have caught it.
//
// The guard compares the configured values against the categories
// composed emission sites declared through `Logger.registerCategory`
// (see the registry's own note in `Server/Logger.fs`) and warns on the
// remainder, naming both the unmatched values and the canonical list.
//
// **Warning, never a refusal, and never gated on a knob.** A misspelt
// trace category costs an operator a diagnostic session; it does not
// make a deployment unsafe or incorrect. Escalating it under a strict
// mode — the shape Phase 695 uses — would be borrowing that guard's
// argument without its premise: 695 escalates because a mistyped *key*
// can leave a deployment in a security posture it did not choose, and
// nothing about a trace filter can.
//
// **An unregistered emission site is not a finding** (GP 13). Two arms
// return `Ok` before any comparison happens: an empty configured set
// (nothing was asked for) and an empty registry (nothing declared what
// it emits, so there is no canonical list to measure against and a
// finding would be about the SDK's own adoption rather than about this
// deployment). Registration is opt-in and additive: a package that
// declares nothing keeps a deployment's prior behaviour byte-for-byte.

/// A registered category whose only difference from `configured` is
/// case. Reported as exactly that rather than as an unmatched value,
/// because the whitelist is compared with ordinal `Set.Contains` at
/// emission time — so `AI.Agent` really does emit nothing even though
/// it names a real category, and "unmatched" would understate a value
/// that is otherwise correct.
let private caseOnlyMatch (registered: string list) (configured: string) =
    registered
    |> List.tryFind (fun r -> String.Equals(r, configured, StringComparison.OrdinalIgnoreCase))

/// One unmatched value with its case hint, rendered for the message.
let private describeValue (registered: string list) (value: string) =
    match caseOnlyMatch registered value with
    | Some r -> sprintf "%s (matches %s apart from case — the whitelist is compared case-sensitively)" value r
    | None -> value

/// The guard's verdict over an injected view of both sides. Pure, so the
/// whole of its behaviour is testable without composing a server or
/// touching the process-global registry.
///
/// `registered` is the declared canonical set; `configured` is what the
/// deployment put in `TOOLUP_TRACE_CATEGORIES` (already parsed into a
/// set by `ConsoleLogger.envSettings` and mirrored onto
/// `ServerConfig.TraceCategories`).
let evaluate (registered: string list) (configured: Set<string>) : ValidationResult =
    if Set.isEmpty configured then
        // Nothing was asked for — the default posture, no Trace output.
        Ok
    elif List.isEmpty registered then
        // Nothing declared what it emits. See the GP 13 note above:
        // without a canonical list there is nothing to be wrong about.
        Ok
    else
        let known = Set.ofList registered

        let unmatched =
            configured |> Set.filter (fun c -> not (Set.contains c known)) |> Set.toList

        match unmatched with
        | [] -> Ok
        | _ ->
            let listing = unmatched |> List.map (describeValue registered) |> String.concat ", "
            let canonical = registered |> String.concat ", "

            Warning(
                sprintf
                    "%s names %d value(s) that no composed emission site emits under, so they will trace nothing and their silence will read as an idle subsystem: %s. The categories composed emission sites declared are: %s. Correct the value or drop it; the same set is on the /dev/inspect \"Trace categories\" panel with a currently-enabled marker. This is a warning rather than a refusal because a misspelt trace filter costs a diagnostic session, not a safe deployment."
                    Names.traceCategories
                    unmatched.Length
                    listing
                    canonical
            )

/// Config validator reporting `TOOLUP_TRACE_CATEGORIES` values that no
/// composed emission site declared. Warning-only; never aborts a boot.
///
/// The registry is read through a thunk rather than captured, because
/// registration and validator construction both happen during compose
/// and their relative order is not something a validator should have to
/// depend on: `Validate()` runs at the end of compose, by which point
/// every composed package has registered.
type TraceCategoriesValidator(configured: Set<string>, registered: unit -> string list) =
    /// Stable registration name (the `IConfigValidator` identity key).
    static member val Name = "trace-categories" with get

    interface IConfigValidator with
        member _.Name = TraceCategoriesValidator.Name
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return evaluate (registered ()) configured }

/// `IDevDiagnosticsContributor` surfacing the composed category registry
/// on `/dev/inspect` under the panel name `"Trace categories"`.
///
/// The panel answers the question the preflight warning can only answer
/// once, at boot: *what can I actually turn on here?* Each declared
/// category carries an `Enabled` marker showing whether this
/// deployment's `TOOLUP_TRACE_CATEGORIES` currently selects it, and the
/// configured-but-unmatched values are listed separately so an operator
/// who missed the startup warning still sees the typo.
type TraceCategoriesContributor(configured: Set<string>, registered: unit -> string list) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let registered = registered ()
            let known = Set.ofList registered

            let payload: obj =
                box {|
                    Categories =
                        registered
                        |> List.map (fun c -> {|
                            Category = c
                            Enabled = Set.contains c configured
                        |})
                    Unmatched = configured |> Set.filter (fun c -> not (Set.contains c known)) |> Set.toList
                    Summary = {|
                        RegisteredCount = registered.Length
                        EnabledCount = configured |> Set.filter (fun c -> Set.contains c known) |> Set.count
                        ConfiguredCount = Set.count configured
                    |}
                |}

            return ("Trace categories", payload)
        }

/// The guard over this deployment's configured whitelist and the live
/// registry — the shape `compose` registers.
let validator (config: ServerConfig) : IConfigValidator =
    TraceCategoriesValidator(config.TraceCategories, Logger.registeredCategories) :> IConfigValidator

/// The `/dev/inspect` panel over the same two inputs — the shape
/// `compose` registers when dev endpoints are enabled.
let contributor (config: ServerConfig) : IDevDiagnosticsContributor =
    TraceCategoriesContributor(config.TraceCategories, Logger.registeredCategories) :> IDevDiagnosticsContributor
namespace ToolUp.Platform

open System
open System.Collections.Concurrent

/// Phase 6h follow-up — Workstream B. Server-side helpers for callers
/// that want to emit at a specific level without caring whether the
/// underlying logger implements the optional capabilities. The helpers
/// do the cap-check + no-op on each call so call sites stay terse.
///
/// Lives in `Server/` rather than `Shared/` because Fable can't reliably
/// type-test F# interfaces — the only consumer of this helper is
/// server-side F# anyway.
module Logger =
    /// Emit a category-gated trace line if the logger supports it.
    /// No-op on plain `ILogger` instances. Cheap when uninterested
    /// (one type test + one branch).
    let trace (logger: ILogger) (category: string) (message: string) =
        match logger with
        | :? ITraceLogger as t -> t.Trace(category, message)
        | _ -> ()

    // ─── Phase 9m.C — the trace-category registry ─────────────────────
    //
    // `TOOLUP_TRACE_CATEGORIES` is a whitelist of free-text category
    // names matched by exact string membership (see `ConsoleLogger`), so
    // a misspelt value is not a configuration error anywhere: it is a
    // category that matches no emission site, silently emits nothing,
    // and leaves the operator concluding the subsystem is quiet rather
    // than that the filter is wrong. Phase 695's unknown-config-key
    // guard cannot see it — that guard quantifies over key *names*, and
    // `TOOLUP_TRACE_CATEGORIES` is a perfectly well-known name.
    //
    // Closing it needs one fact nothing in the process held: which
    // category names the composed emission sites actually emit under.
    // This registry is that fact. A site declares its category once at
    // startup (`Logger.registerCategory "ai.agent"`, in the owning
    // package's compose path) and `TraceCategoriesValidator` reports any
    // configured value the composed set does not account for.
    //
    // **Registration is declarative, not observed** (GP 13). It would be
    // cheaper to have `trace` add its own category on the way past, and
    // it would also be useless: the preflight runs at the end of compose,
    // before any trace line is emitted, so an observed registry is empty
    // exactly when the validator reads it. Declaring at compose is what
    // makes the fact available at the moment it is needed.
    //
    // **Absence is never a finding.** An emission site that registers
    // nothing does not turn the validator red — the validator treats an
    // empty registry as "nothing to compare against" and returns `Ok`.
    // That asymmetry is deliberate: registration is opt-in and
    // downstream sites the SDK never sees may emit under names it cannot
    // enumerate, so a strict reading would report every deployment that
    // has not adopted this. The registry earns its warnings only where a
    // package has said what it emits.

    /// The category names composed emission sites have declared.
    /// `ConcurrentDictionary` rather than a locked `HashSet` because
    /// registration is unordered and races benignly — two packages
    /// registering during compose must not have to coordinate — and the
    /// read side (`registeredCategories`) is called once at preflight
    /// and once per `/dev/inspect` render, never on a request path.
    ///
    /// Ordinal comparison, matching `ConsoleLogger`'s own
    /// `Set<string>.Contains` gate exactly: the whitelist is
    /// case-sensitive at emission time, so the registry must be
    /// case-sensitive here or the validator would pass a value the
    /// logger will go on to reject.
    let private categories = ConcurrentDictionary<string, bool>(StringComparer.Ordinal)

    /// Declare that some composed emission site emits `Trace` lines under
    /// `category`. Idempotent, thread-safe, and safe to call from any
    /// compose path in any order.
    ///
    /// A null / whitespace category is ignored rather than rejected: the
    /// registry exists to make a preflight message more precise, and a
    /// malformed declaration is not worth failing a boot over.
    let registerCategory (category: string) : unit =
        if not (String.IsNullOrWhiteSpace category) then
            categories[category.Trim()] <- true

    /// The declared categories, ordinal-sorted so preflight messages and
    /// the `/dev/inspect` panel are stable across runs and platforms.
    let registeredCategories () : string list =
        categories.Keys |> Seq.sort |> List.ofSeq

    /// Empty the registry. For test isolation and for a host that
    /// composes more than once in a process — never part of a normal
    /// boot, which registers into a fresh process.
    let clearRegisteredCategories () : unit = categories.Clear()
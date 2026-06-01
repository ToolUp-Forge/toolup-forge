namespace ToolUp.Remoting.Server

open System
open System.Reflection
open System.Threading.Tasks

// =============================================================================
// Phase 69k — source-generator-driven dispatcher (substrate + manual PoC)
// =============================================================================
//
// This file ships the RUNTIME-side substrate of Phase 69k:
//   * `IGeneratedDispatchTable<'impl>` — the contract a source-generator
//     emits one implementation of per API record type.
//   * `GeneratedDispatchRegistry` — the runtime registry where consumers
//     (or the generator's emitted `[<ModuleInitializer>]`) register the
//     emitted tables; the Giraffe adapter consults this registry to
//     short-circuit reflection when a generated table is present.
//   * `[<DispatcherTarget>]` marker attribute the source-generator would
//     scan for when emitting tables.
//
// The actual F# / Roslyn source-generator project is a follow-up; this v0
// SHIPS THE RUNTIME so consumers (and the generator) have a stable target,
// and ships a HAND-WRITTEN reference impl (see harness `JobReportDispatchTable`)
// to prove the runtime composes correctly.
//
// Performance promise: when a generated table is registered, the dispatcher
// skips reflection-driven method lookup and arg deserialisation at startup
// (the table emits direct compile-time-typed dispatch code). v0 reuses the
// existing proxy for the actual invocation; a later phase swaps in the
// generator's emitted invocation thunks for the per-method hot path.

/// Phase 69k — marker attribute on an API record type. The source-
/// generator (when it ships) scans for this attribute and emits an
/// `IGeneratedDispatchTable<'TImpl>` implementation per attributed record.
/// Without the attribute, no table is emitted and the runtime falls back
/// to reflection.
[<AttributeUsage(AttributeTargets.Interface ||| AttributeTargets.Class ||| AttributeTargets.Struct)>]
type DispatcherTargetAttribute() =
    inherit Attribute()

/// Phase 69k — the contract a source-generated table satisfies.
/// `ApiType` is the API record type the table dispatches for;
/// `RouteHandlers` returns an entry per method on that record, each
/// pre-bound to the typed handler invocation.
///
/// v0 shape is intentionally minimal — the generator's job is to emit
/// the `RouteHandlers` map without reflection, so startup cost drops
/// to the cost of evaluating a static initializer. The handler function
/// itself can still use the per-method shape recognised by Phase 69d /
/// 69e / 69f / 69g / 69h / 69j (the generator is wire-compatible by
/// construction — it produces the same JSON the reflection path produces).
/// Phase 69k — `'TContext` is the per-adapter request context type
/// (`HttpContext` for Giraffe / AspNetCore, `HttpContext` for Suave's
/// own type, etc.). Keeping it generic preserves the substrate's
/// HTTP-agnostic shape and lets adapters compose without dragging
/// ASP.NET Core into `ToolUp.Remoting.Server`.
type IGeneratedDispatchTable<'TContext, 'TImpl> =
    abstract ApiType : Type
    abstract RouteHandlers :
        unit -> (string * ('TContext -> 'TImpl -> Task)) list

/// Phase 69k — process-wide registry of generated dispatch tables.
/// Consumers (or the generator's emitted `[<ModuleInitializer>]`) call
/// `Register` once per emitted table; the Giraffe adapter calls
/// `TryGet<'TImpl>()` at `buildHttpHandler` time to decide whether the
/// generated path is available.
///
/// Thread-safe; registrations are typically once-per-process at startup.
/// Re-registration replaces the existing entry (idempotent for hot-reload
/// scenarios).
module GeneratedDispatchRegistry =

    let private tables =
        System.Collections.Concurrent.ConcurrentDictionary<Type, obj>()

    /// Register a generated dispatch table for `'TImpl`. The runtime
    /// looks up by `typeof<'TImpl>` so the generator emits one
    /// registration call per API record type.
    /// Register a generated dispatch table for `'TImpl`. The runtime
    /// stores the boxed table; callers casting via `tryGet<'TContext, 'TImpl>`
    /// recover the typed form.
    let register<'TImpl> (table: obj) : unit =
        tables[typeof<'TImpl>] <- table

    /// Try to resolve the generated dispatch table for `'TImpl`. Returns
    /// the boxed table — callers cast to their adapter's context type.
    /// `None` means no table registered (fall back to reflection).
    let tryGet<'TImpl> () : obj option =
        match tables.TryGetValue(typeof<'TImpl>) with
        | true, table -> Some table
        | false, _ -> None

    /// True if a generated table is registered for `'TImpl`. The Giraffe
    /// adapter uses this at `buildHttpHandler` to log / branch.
    let isRegistered<'TImpl> () : bool =
        tables.ContainsKey(typeof<'TImpl>)

    /// Test-only: clear all registrations. Production code never calls
    /// this — registrations are once-per-process at startup.
    let internal clearForTests () = tables.Clear()

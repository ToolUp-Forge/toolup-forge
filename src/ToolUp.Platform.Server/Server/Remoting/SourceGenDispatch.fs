namespace ToolUp.Remoting.Server

open System
open System.Reflection
open System.Text.Json
open System.Threading.Tasks

// =============================================================================
// Phase 69k — source-generator-driven dispatcher (substrate + manual PoC)
// =============================================================================
//
// This file ships the RUNTIME-side substrate of Phase 69k:
//   * `IGeneratedDispatchTable<'impl>` — the 69k v0 contract. NO adapter
//     composes it, and none will: see "Phase 906" below.
//   * `GeneratedDispatchRegistry` — the runtime registry where a generator's
//     emitted `register ()` places its table.
//   * `[<DispatcherTarget>]` marker attribute the source-generator would
//     scan for when emitting tables.
//   * (Phase 906) `GeneratedArguments`, `GeneratedMethod<'impl>`,
//     `GeneratedInvocationTable<'impl>` and the `GeneratedInvocation`
//     builders — the shape the generator's dispatch emission now targets.
//
// ─── Phase 906 — the generated invocation runs INSIDE the pre-flight chain ─
//
// Phase 804 measured a cold-start gain from dispatching without the
// reflective proxy build; Phase 856.A found it unrealisable as then
// specified, for two reasons, both of which this phase answers:
//
//   1. Nothing produced a table. The generator's dispatch emission
//      (`ToolUp.Remoting.Generator`, `Dispatch.fs`) now emits, per API
//      record, an `invocations: GeneratedInvocationTable<'impl>` and a
//      `register ()` that places it here.
//
//   2. A route handler that writes its own response would run OUTSIDE the
//      adapter's pre-flight chain (auth, rate limit, validation,
//      idempotency, audit, telemetry) — an authorisation bypass. So the
//      emitted invocation is NOT a route handler. It is one method's
//      endpoint INSIDE the proxy: the proxy (`Proxy.fs`) consults this
//      registry when it is built and, for every method the table covers,
//      composes the generated call where it would otherwise have built a
//      reflective endpoint. The adapter's chain is keyed off the proxy's
//      `InvocationResult` exactly as before, so every stage runs around a
//      generated method as it runs around a reflective one — the invocation
//      is the chain's innermost stage, never a parallel route.
//
// What the generated code owns is deliberately small: the typed call of
// the handler and the static type of its result. Everything else — the
// verb check, reading the body, the argument-array parse, each argument's
// decode (the proxy's own record-scoped seam, Phase 839), the arity
// refusal, the result's serialise and every `InvocationResult` case — is
// the proxy's own code, shared with the reflective endpoint through
// `GeneratedArguments`, so the two routes cannot drift. A method the table
// does not cover (a `Task`-returning or streaming method, or one added to
// the record after generation) is served by the reflective proxy, built
// lazily on its first request.

/// Phase 69k — marker attribute on an API record type. The source-
/// generator (when it ships) scans for this attribute and emits an
/// `IGeneratedDispatchTable<'TImpl>` implementation per attributed record.
/// Without the attribute, no table is emitted and the runtime falls back
/// to reflection.
[<AttributeUsage(AttributeTargets.Interface
                 ||| AttributeTargets.Class
                 ||| AttributeTargets.Struct)>]
type DispatcherTargetAttribute() =
    inherit Attribute()

/// Phase 69k — the v0 contract a source-generated table was to satisfy.
///
/// Phase 906 — no adapter composes this shape and none will: a
/// `'TContext -> 'TImpl -> Task` route handler writes its own response and
/// so would run outside the pre-flight chain. A generated table is a
/// `GeneratedInvocationTable<'impl>` instead, which the proxy composes
/// inside the chain. This type is retained only because removing a public
/// type is a breaking change.
///
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
    abstract ApiType: Type
    abstract RouteHandlers: unit -> (string * ('TContext -> 'TImpl -> Task)) list

/// Phase 69k — process-wide registry of generated dispatch tables.
/// A generated module's `register ()` (Phase 906) calls `register` once
/// per emitted table; the remoting proxy calls `tryGet<'TImpl>()` once,
/// when it is built, and composes a `GeneratedInvocationTable<'TImpl>` it
/// finds there (any other registered shape is ignored — reflection).
/// Register BEFORE the remoting handler is built: the proxy reads the
/// registry at build time, never per request.
///
/// Thread-safe; registrations are typically once-per-process at startup.
/// Re-registration replaces the existing entry (idempotent for hot-reload
/// scenarios).
module GeneratedDispatchRegistry =

    let private tables = System.Collections.Concurrent.ConcurrentDictionary<Type, obj>()

    /// Register a generated dispatch table for `'TImpl`. The runtime
    /// looks up by `typeof<'TImpl>` so the generator emits one
    /// registration call per API record type.
    /// Register a generated dispatch table for `'TImpl`. The runtime
    /// stores the boxed table; callers casting via `tryGet<'TContext, 'TImpl>`
    /// recover the typed form.
    let register<'TImpl> (table: obj) : unit = tables[typeof<'TImpl>] <- table

    /// Try to resolve the generated dispatch table for `'TImpl`. Returns
    /// the boxed table — callers cast to their adapter's context type.
    /// `None` means no table registered (fall back to reflection).
    let tryGet<'TImpl> () : obj option =
        match tables.TryGetValue(typeof<'TImpl>) with
        | true, table -> Some table
        | false, _ -> None

    /// True if a generated table is registered for `'TImpl`.
    let isRegistered<'TImpl> () : bool = tables.ContainsKey(typeof<'TImpl>)

    /// Test-only: clear all registrations. Production code never calls
    /// this — registrations are once-per-process at startup.
    let internal clearForTests () = tables.Clear()

/// Phase 906 — the arguments of ONE generated call, walked by the remoting
/// proxy's own code.
///
/// Generated code receives one of these per request and does two things
/// with it: takes each argument, in declaration order, with `Next<'T>()`,
/// and hands the handler's `Async<'T>` to `Complete`. Both are the proxy's
/// own steps, shared with the reflective endpoint: `Next` is its argument
/// step (the record-scoped decode, the first argument the validation stage
/// already decoded, a `unit` argument, a multipart binary section, the
/// arity refusal) and `Complete` is its completion (the refusal of a
/// surplus argument, the handler awaited, the result serialised as the
/// options say, the `InvocationResult`). A refusal raised by either is the
/// proxy's to report, exactly as on the reflective path.
///
/// Only the proxy creates one; the constructor is internal.
[<AbstractClass>]
type GeneratedArguments internal () =
    /// The next argument, decoded exactly as the reflective endpoint decodes
    /// it. `'T` is the method's parameter type at this position.
    abstract Next<'T> : unit -> 'T

    /// Complete the call: refuse a surplus argument, await the handler,
    /// write its result to the response as the proxy does.
    abstract Complete<'T> : call: Async<'T> -> Task<InvocationResult>

/// Phase 906 — one method of a generated invocation table: its name, its
/// flattened field type (the reflective proxy's `TypeInfo.flattenFuncTypes`
/// of the record field, emitted rather than computed so building the table
/// reflects over nothing) and the typed call.
[<Sealed>]
type GeneratedMethod<'impl>
    internal
    (
        name: string,
        flattenedTypes: Type[],
        decodeFirst: (string -> JsonSerializerBackend -> JsonElement -> Result<obj, DecodeError>) option,
        call: GeneratedArguments -> 'impl -> Task<InvocationResult>
    ) =
    /// The record field's name — the route's method segment.
    member _.Name = name

    /// The field's type flattened through its curried chain: every domain,
    /// then the `Async<_>` result.
    member _.FlattenedTypes = flattenedTypes

    /// The first argument's decode for the validation stage's early parse
    /// (Phase 856.B), when the method has a JSON first argument.
    member internal _.DecodeFirst = decodeFirst

    /// The typed call of the handler.
    member internal _.Call = call

/// Phase 906 — a generated API record's invocations, one per method it
/// covers. Registered through `GeneratedInvocation.register`; the remoting
/// proxy composes it when it is built.
[<Sealed>]
type GeneratedInvocationTable<'impl> internal (methods: GeneratedMethod<'impl> list) =
    /// Every covered method, in the record's declaration order.
    member _.Methods = methods

/// Phase 906 — the builders the generator's dispatch emission calls. Not
/// meant to be written by hand: a table is emitted from the record it
/// dispatches for, so its method names and types cannot drift from it
/// without failing to compile.
[<RequireQualifiedAccess>]
module GeneratedInvocation =

    let private isNoJsonFirst (flattenedTypes: Type[]) =
        flattenedTypes.Length < 2 || flattenedTypes[0] = typeof<unit>

    /// A method with no JSON first argument: an `Async<_>` value, or a
    /// method whose first parameter is `unit`.
    let forMethod<'impl>
        (name: string)
        (flattenedTypes: Type[])
        (call: GeneratedArguments -> 'impl -> Task<InvocationResult>)
        : GeneratedMethod<'impl> =
        if not (isNoJsonFirst flattenedTypes) then
            invalidArg
                (nameof flattenedTypes)
                (sprintf
                    "%s.%s takes a JSON first argument (%s); a generated table declares it with forMethodWithFirst"
                    typeof<'impl>.Name
                    name
                    flattenedTypes[0].Name)

        GeneratedMethod<'impl>(name, flattenedTypes, None, call)

    /// A method whose first parameter is `'first`, decoded early for the
    /// validation stage through the same record-scoped seam the argument
    /// walk uses (`FableConverters.tryDeserialiseFor`).
    let forMethodWithFirst<'impl, 'first>
        (name: string)
        (flattenedTypes: Type[])
        (call: GeneratedArguments -> 'impl -> Task<InvocationResult>)
        : GeneratedMethod<'impl> =
        if isNoJsonFirst flattenedTypes || flattenedTypes[0] <> typeof<'first> then
            invalidArg
                (nameof flattenedTypes)
                (sprintf
                    "%s.%s: the declared first argument %s is not the flattened field type's first domain"
                    typeof<'impl>.Name
                    name
                    typeof<'first>.Name)

        let decodeFirst (recordName: string) (backend: JsonSerializerBackend) (element: JsonElement) =
            match backend with
            | SystemTextJson options ->
                ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseFor<'first>
                    (Some recordName)
                    element
                    options
                |> Result.map box

        GeneratedMethod<'impl>(name, flattenedTypes, Some decodeFirst, call)

    /// The table of a record's generated invocations.
    let table<'impl> (methods: GeneratedMethod<'impl> list) : GeneratedInvocationTable<'impl> =
        GeneratedInvocationTable<'impl>(methods)

    /// Register a table so the remoting proxy for `'impl` composes it. Call
    /// once, before the remoting handler is built.
    let register<'impl> (table: GeneratedInvocationTable<'impl>) : unit =
        GeneratedDispatchRegistry.register<'impl> (box table)
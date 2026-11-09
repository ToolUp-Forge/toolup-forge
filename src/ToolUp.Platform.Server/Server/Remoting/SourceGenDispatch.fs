namespace ToolUp.Remoting.Server

open System
open System.Reflection
open System.Text.Json
open System.Threading.Tasks

// =============================================================================
// Generated invocations
// =============================================================================
//
// The runtime half of the source generator's dispatch emission: the
// registry a generated module's `register ()` writes its table to, and the
// shape the table takes (`GeneratedArguments`, `GeneratedMethod<'impl>`,
// `GeneratedInvocationTable<'impl>`, the `GeneratedInvocation` builders).
//
// A generated invocation is NOT a route handler: a handler that wrote its own
// response would run outside the request pipeline's stages — an
// authorisation bypass. It is one method's endpoint INSIDE the proxy: the
// proxy (`Proxy.fs`) consults this registry when it is built and, for every
// method the table covers, composes the generated call where it would
// otherwise have built a reflective endpoint. The pipeline's `invoke` stage
// calls the proxy whichever endpoint it composed, so every stage runs around
// a generated method exactly as around a reflective one.
//
// What the generated code owns is deliberately small: the typed call of the
// handler and the static type of its result. Everything else — the verb
// check, reading the body, the argument-array parse, each argument's decode
// (the proxy's own record-scoped seam), the arity refusal, the result's
// serialise and every refusal the call can end in — is the proxy's own code,
// shared with the reflective endpoint through `GeneratedArguments`, so the
// two routes cannot drift. A method the table does not cover (a
// `Task`-returning or streaming method, or one added to the record after
// generation) is served by the reflective proxy, built lazily on its first
// request.

/// Marker attribute on an API record type. The generator is driven by its
/// build items, not by this attribute, and emits a
/// `GeneratedInvocationTable<'impl>`; without a registered table the runtime
/// uses reflection.
[<AttributeUsage(AttributeTargets.Interface
                 ||| AttributeTargets.Class
                 ||| AttributeTargets.Struct)>]
type DispatcherTargetAttribute() =
    inherit Attribute()

/// Process-wide registry of generated dispatch tables.
/// A generated module's `register ()` calls `register` once
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

    /// Register a generated dispatch table for `'TImpl`, boxed and keyed by
    /// `typeof<'TImpl>`: the generator emits one registration per API record
    /// type, and `tryGet<'TImpl>` recovers it.
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

/// The arguments of ONE generated call, walked by the remoting
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
/// options say). A refusal raised by either is the
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
    abstract Complete<'T> : call: Async<'T> -> Task

/// One method of a generated invocation table: its name, its
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
        call: GeneratedArguments -> 'impl -> Task
    ) =
    /// The record field's name — the route's method segment.
    member _.Name = name

    /// The field's type flattened through its curried chain: every domain,
    /// then the `Async<_>` result.
    member _.FlattenedTypes = flattenedTypes

    /// The first argument's decode for the validation stage's early parse,
    /// when the method has a JSON first argument.
    member internal _.DecodeFirst = decodeFirst

    /// The typed call of the handler.
    member internal _.Call = call

/// A generated API record's invocations, one per method it
/// covers. Registered through `GeneratedInvocation.register`; the remoting
/// proxy composes it when it is built.
[<Sealed>]
type GeneratedInvocationTable<'impl> internal (contract: Lazy<ApiContract>, methods: GeneratedMethod<'impl> list) =
    /// Every covered method, in the record's declaration order.
    member _.Methods = methods

    /// The record's contract: the value the generator emitted beside the
    /// table, or — for a table emitted before the contract was — the one
    /// `Contract.read` yields, read once, when first asked.
    member _.Contract: ApiContract = contract.Value

/// The builders the generator's dispatch emission calls. Not
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
        (call: GeneratedArguments -> 'impl -> Task)
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
        (call: GeneratedArguments -> 'impl -> Task)
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

    /// The table of a record's generated invocations, beside the contract the
    /// generator emitted for it: a registered table reads nothing off the
    /// record at boot.
    let tableWith<'impl>
        (contract: ApiContract)
        (methods: GeneratedMethod<'impl> list)
        : GeneratedInvocationTable<'impl> =
        GeneratedInvocationTable<'impl>(Lazy<ApiContract>.CreateFromValue contract, methods)

    /// The table of a record's generated invocations, as a module generated
    /// before the contract was emitted declares it: its contract is read off
    /// the record when first asked. Rerun the generator's `dispatch` command
    /// to emit the contract as data.
    let table<'impl> (methods: GeneratedMethod<'impl> list) : GeneratedInvocationTable<'impl> =
        GeneratedInvocationTable<'impl>(lazy (Contract.read typeof<'impl>), methods)

    /// Register a table so the remoting proxy for `'impl` composes it. Call
    /// once, before the remoting handler is built.
    let register<'impl> (table: GeneratedInvocationTable<'impl>) : unit =
        GeneratedDispatchRegistry.register<'impl> (box table)
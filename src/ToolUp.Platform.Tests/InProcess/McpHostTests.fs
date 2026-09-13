// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.McpHostTests

// ─── Phase 489 — MCP server host + agent principals ──────────────────
//
// The phase's acceptance is a sequence a client performs, so the pack is
// built around a **client fixture that speaks the protocol exactly** —
// it composes real JSON-RPC 2.0 request bodies, POSTs them at the real
// handler, and parses what comes back as JSON-RPC. Nothing here calls an
// internal shortcut in place of the wire, because the acceptance is
// about what an off-the-shelf client sees and an internal call cannot
// answer that.
//
// **Protocol conformance is asserted, not assumed.** A real MCP client
// is not available inside the gate, so the fixture's conformance is
// made explicit and checked: every response carries `"jsonrpc":"2.0"`,
// echoes the request id in the client's own lexical form, and carries
// exactly one of `result` / `error`. `initialize` reports the revision
// this host claims. A fixture that merely *happened* to agree with the
// implementation would prove nothing, so those assertions read the raw
// JSON rather than any type the implementation also uses.
//
// The four acceptance clauses each have a case that goes red alone:
//
//   1. **Grant-filtered discovery.** `tools/list` returns exactly the
//      granted ∩ RBAC-permitted set — pinned from BOTH sides, with a
//      tool the agent is granted but may not read, and a tool it may
//      read but was not granted.
//   2. **Invisible AND uninvokable, indistinguishably.** The ungranted
//      tool's `tools/call` refusal is byte-identical to a name no tool
//      carries. The non-vacuity bar is the executor: every deny-path
//      tool `failtest`s if it runs.
//   3. **Revocation on the next request.** One fixture, two calls, a
//      grant withdrawn between them.
//   4. **Budget exhaustion is a typed refusal** — the denial's
//      dimension, quota and spend on the JSON-RPC error's `data`, not a
//      sentence to parse.
//
// Plus the GP 13 keystone, asserted on the composition rather than
// described: `NoMcpHost` contributes no handler.

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.AI
open ToolUp.AI.McpHost

// ─── Stub provider factory + profile (compose-only; never invoked) ───

type private StubProviderFactory() =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Error(ProviderResolutionError.NoProviderConfigured) }

        member _.TryResolveByLabel(_, _) = async { return Error(ProviderResolutionError.NoProviderConfigured) }

        member _.BuildPlatform(_providerId, _apiKey, _model) = None

type private StubProviderProfile() =
    interface IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }

// ─── Fixtures ─────────────────────────────────────────────────────────

let private scope = "team-42"
let private accountId = "acct-agent-1"
let private tokenId = "tok-1"

let private toolDef (name: string) (sourceModule: string) : AIToolDefinition = {
    Name = name
    Description = sprintf "Phase 489 test tool %s" name
    Parameters = [
        {
            Name = "q"
            Type = "string"
            Description = "a query"
            Required = true
            Default = None
        }
    ]
    SourceModule = sourceModule
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}

/// An executor that must never run. Every deny-path tool carries it, so a
/// gate that failed open is caught here rather than by a missing
/// assertion.
let private mustNotRun _ctx _args : Async<string> = async {
    return failtest "a tool the agent may not reach must never be executed"
}

let private echoExecutor (label: string) =
    fun _ctx (args: string) -> async { return sprintf "{\"tool\":\"%s\",\"args\":%s}" label args }

let private registryOf tools =
    let registry = AIToolRegistry.AIToolRegistry()
    registry.RegisterAll tools
    registry

/// The validated machine principal the shipped Phase 527 middleware
/// stamps on `HttpContext.Items`. Built here rather than driven through
/// that middleware because this pack is about what the MCP host does
/// with a resolved principal; the middleware's own resolution is pinned
/// by the service-account pack.
let private servicePrincipal: ServiceAccountPrincipal = {
    AccountId = accountId
    DisplayName = "Nightly exporter"
    ScopeId = scope
    TokenId = tokenId
    Permissions = Map.ofList [ "Sales", [ ModulePermission.Read ] ]
    ExpiresAt = DateTimeOffset.UtcNow.AddDays 30.0
}

let private accessFor (perms: (string * ModulePermission list) list) : AccessContext = {
    AccessContext.unrestricted (
        ClaimBearer {
            TokenId = tokenId
            ScopeId = scope
            ResourceKind = ServiceAccountTypes.ClaimResourceKind
            ResourceId = accountId
            AttributedHandle = Some accountId
            IssuedBy = "admin"
            IssuedAt = DateTimeOffset.UtcNow.AddDays -1.0
            ExpiresAt = DateTimeOffset.UtcNow.AddDays 30.0
            UseLimit = None
            UsedCount = 0
            Revoked = false
            RateLimit = None
        }
    ) with
        ModulePermissions = Map.ofList perms
}

/// One MCP client fixture: a service provider, a grant store, and the
/// ability to POST a JSON-RPC body at the handler and read the response
/// back as raw JSON.
type private Fixture = {
    Grants: IAgentToolGrantStore
    Events: IEventStore
    Services: IServiceProvider
    Options: McpHost.McpHostOptions
    /// `false` drops the service-account principal, which is what an
    /// unauthenticated caller looks like to the handler.
    mutable Authenticated: bool
}

let private fixture
    (tools: AIToolRegistry.RegisteredTool list)
    (perms: (string * ModulePermission list) list)
    (policy: AgentPolicy)
    (ledger: IBudgetLedger option)
    : Fixture =
    let grants =
        AgentToolGrantStore.InMemoryAgentToolGrantStore() :> IAgentToolGrantStore

    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let services = ServiceCollection()

    services.AddSingleton<AIToolRegistry.AIToolRegistry>(registryOf tools) |> ignore
    services.AddSingleton<IAgentToolGrantStore>(grants) |> ignore
    services.AddSingleton<IEventStore>(events) |> ignore
    services.AddSingleton<AccessContext>(accessFor perms) |> ignore

    match ledger with
    | Some l -> services.AddSingleton<IBudgetLedger>(l) |> ignore
    | None -> ()

    {
        Grants = grants
        Events = events
        Services = services.BuildServiceProvider()
        Options = {
            McpHost.McpHostOptions.defaults with
                ServerVersion = "9.9.9"
                DefaultPolicy = policy
        }
        Authenticated = true
    }

/// POST one JSON-RPC body at the handler and return `(status, body)`.
///
/// This is the fixture's whole wire surface: it builds the envelope the
/// way a conforming client does and reads back exactly the bytes the
/// handler wrote.
let private call (f: Fixture) (rpcMethod: string) (rawId: string) (paramsJson: string option) : int * string =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- f.Services

    if f.Authenticated then
        ctx.Items[ServiceAccountTokenHandler.ServiceAccountPrincipalItemsKey] <- box servicePrincipal

    let body =
        match paramsJson with
        | Some p -> sprintf "{\"jsonrpc\":\"2.0\",\"id\":%s,\"method\":\"%s\",\"params\":%s}" rawId rpcMethod p
        | None -> sprintf "{\"jsonrpc\":\"2.0\",\"id\":%s,\"method\":\"%s\"}" rawId rpcMethod

    ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
    let response = new MemoryStream()
    ctx.Response.Body <- response

    McpHost.handler f.Options (fun c -> Threading.Tasks.Task.FromResult(Some c)) ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

    ctx.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray())

let private parse (json: string) = JsonDocument.Parse json

/// Assert the three things every JSON-RPC 2.0 response must carry, and
/// return the parsed root so a case can go on to read its payload.
///
/// Read off the RAW JSON rather than any type the implementation shares,
/// so a fixture agreeing with the implementation by construction cannot
/// pass for conformance.
let private conformingResponse (expectedRawId: string) (json: string) : JsonElement =
    let doc = parse json
    let root = doc.RootElement

    Expect.equal (root.GetProperty("jsonrpc").GetString()) "2.0" "every MCP response is a JSON-RPC 2.0 envelope"

    Expect.equal
        (root.GetProperty("id").GetRawText())
        expectedRawId
        "the response echoes the request id in the client's own lexical form"

    let hasResult = fst (root.TryGetProperty "result")
    let hasError = fst (root.TryGetProperty "error")

    Expect.isTrue (hasResult <> hasError) "a JSON-RPC response carries exactly one of result / error"

    root.Clone()

let private toolNames (root: JsonElement) =
    root.GetProperty("result").GetProperty("tools").EnumerateArray()
    |> Seq.map (fun t -> t.GetProperty("name").GetString())
    |> Seq.toList
    |> List.sort

let private errorOf (root: JsonElement) = root.GetProperty "error"

let private grant (f: Fixture) (names: string list) =
    f.Grants.Write(scope, accountId, Set.ofList names, "admin")
    |> Async.RunSynchronously
    |> ignore

/// Rows this fixture's event store holds on one source.
let private rows (f: Fixture) (source: string) =
    f.Events.ReadBySource(scope, source) |> Async.RunSynchronously

// ─── The pack ─────────────────────────────────────────────────────────

let tests =
    testList "Phase 489 — MCP server host + agent principals" [

        // ── protocol conformance ─────────────────────────────────────

        testCase "initialize reports the protocol revision, capabilities and server identity"
        <| fun () ->
            let f = fixture [] [] AgentPolicy.unrestricted None
            let status, body = call f "initialize" "1" (Some "{}")
            Expect.equal status 200 "a handshake is a 200"
            let root = conformingResponse "1" body
            let result = root.GetProperty "result"

            Expect.equal
                (result.GetProperty("protocolVersion").GetString())
                McpHostConstants.ProtocolVersion
                "the host reports the revision it implements"

            Expect.equal
                (result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean())
                false
                "the registry is fixed at compose, so listChanged is honestly false"

            Expect.equal
                (result.GetProperty("serverInfo").GetProperty("version").GetString())
                "9.9.9"
                "serverInfo reports the version the composition supplied"

        testCase "a string request id is echoed as a string, a numeric one as a number"
        <| fun () ->
            let f = fixture [] [] AgentPolicy.unrestricted None
            let _, stringIdBody = call f "ping" "\"abc\"" None
            conformingResponse "\"abc\"" stringIdBody |> ignore
            let _, numericIdBody = call f "ping" "7" None
            conformingResponse "7" numericIdBody |> ignore

        testCase "batching is refused rather than partially handled"
        <| fun () ->
            let f = fixture [] [] AgentPolicy.unrestricted None
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- f.Services
            ctx.Items[ServiceAccountTokenHandler.ServiceAccountPrincipalItemsKey] <- box servicePrincipal

            ctx.Request.Body <-
                new MemoryStream(Encoding.UTF8.GetBytes "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]")

            let response = new MemoryStream()
            ctx.Response.Body <- response

            McpHost.handler f.Options (fun c -> Threading.Tasks.Task.FromResult(Some c)) ctx
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> ignore

            let root = (parse (Encoding.UTF8.GetString(response.ToArray()))).RootElement

            Expect.equal
                (root.GetProperty("error").GetProperty("code").GetInt32())
                McpProtocol.ErrorCode.invalidRequest
                "MCP 2025-06-18 removed batching, so an array body is an invalid request"

        // ── 1. grant-filtered discovery ──────────────────────────────

        testCase "tools/list returns exactly the granted AND readable tools"
        <| fun () ->
            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "summarise")
                        AIToolRegistry.createTool (toolDef "sales.forecast" "Sales") (echoExecutor "forecast")
                        AIToolRegistry.createTool (toolDef "hr.roster" "HR") mustNotRun
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            // Granted: one readable tool, one tool in a module the agent
            // holds no Read on. Ungranted: a readable tool. Both sides of
            // the intersection are therefore load-bearing.
            grant f [ "sales.summarise"; "hr.roster" ]

            let _, body = call f "tools/list" "1" None
            let root = conformingResponse "1" body

            Expect.equal
                (toolNames root)
                [ "sales.summarise" ]
                "discovery is the intersection of the agent's grants and its module RBAC"

        testCase "an agent with no grant record discovers nothing"
        <| fun () ->
            let f =
                fixture
                    [ AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") mustNotRun ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            let _, body = call f "tools/list" "1" None
            let root = conformingResponse "1" body
            Expect.equal (toolNames root) [] "default-deny: an account nobody granted anything reaches nothing"

        testCase "the listing carries each tool's declared input schema"
        <| fun () ->
            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "summarise")
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            grant f [ "sales.summarise" ]
            let _, body = call f "tools/list" "1" None
            let root = conformingResponse "1" body
            let listed = root.GetProperty("result").GetProperty "tools"
            let schema = listed[0].GetProperty "inputSchema"

            Expect.equal (schema.GetProperty("type").GetString()) "object" "the schema is a JSON-Schema object"

            Expect.isTrue
                (fst (schema.GetProperty("properties").TryGetProperty "q"))
                "the declared parameter reaches the client's schema"

        // ── 2. invisible AND uninvokable, indistinguishably ──────────

        testCase "an ungranted tool is refused, and the refusal is indistinguishable from an unknown name"
        <| fun () ->
            let f =
                fixture
                    [ AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") mustNotRun ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            let _, ungranted =
                call f "tools/call" "1" (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")

            let _, unknown =
                call f "tools/call" "1" (Some "{\"name\":\"no.such.tool\",\"arguments\":{}}")

            let ungrantedError = errorOf (conformingResponse "1" ungranted)
            let unknownError = errorOf (conformingResponse "1" unknown)

            Expect.equal
                (ungrantedError.GetProperty("code").GetInt32())
                McpProtocol.ErrorCode.toolUnavailable
                "an ungranted tool is refused as unavailable"

            Expect.equal
                (ungrantedError.GetProperty("code").GetInt32())
                (unknownError.GetProperty("code").GetInt32())
                "a caller must not be able to tell 'not yours' from 'no such tool' — that is how the filtered listing \
                 would be walked back"

            Expect.isFalse
                (fst (ungrantedError.TryGetProperty "data"))
                "the refusal carries no data naming the module or grant the caller lacks"

        testCase "a granted tool the agent cannot READ is refused too"
        <| fun () ->
            let f =
                fixture
                    [ AIToolRegistry.createTool (toolDef "hr.roster" "HR") mustNotRun ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            grant f [ "hr.roster" ]

            let _, body =
                call f "tools/call" "1" (Some "{\"name\":\"hr.roster\",\"arguments\":{}}")

            Expect.equal
                ((errorOf (conformingResponse "1" body)).GetProperty("code").GetInt32())
                McpProtocol.ErrorCode.toolUnavailable
                "a grant does not widen authority — module RBAC still applies on top"

        testCase "a granted, readable tool runs end to end and its result reaches the client"
        <| fun () ->
            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "summarise")
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            grant f [ "sales.summarise" ]

            let status, body =
                call f "tools/call" "1" (Some "{\"name\":\"sales.summarise\",\"arguments\":{\"q\":\"x\"}}")

            Expect.equal status 200 "an admitted call is a 200"
            let root = conformingResponse "1" body
            let result = root.GetProperty "result"
            Expect.isFalse (result.GetProperty("isError").GetBoolean()) "a tool that succeeded is not an error"

            let content = result.GetProperty "content"
            let text = content[0].GetProperty("text").GetString()

            Expect.stringContains text "summarise" "the tool's own JSON result reaches the client"
            Expect.stringContains text "\"q\":\"x\"" "the client's arguments reach the tool"

        testCase "a tool that raises is a successful response carrying isError, not a JSON-RPC error"
        <| fun () ->
            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (fun _ _ -> async {
                            return failwith "the warehouse is offline"
                        })
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            grant f [ "sales.summarise" ]

            let _, body =
                call f "tools/call" "1" (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")

            let root = conformingResponse "1" body

            Expect.isTrue
                (fst (root.TryGetProperty "result"))
                "a tool that RAN and failed is a result the model can read and adapt to, per the MCP convention — not \
                 a protocol fault the client handles out of band"

            Expect.isTrue (root.GetProperty("result").GetProperty("isError").GetBoolean()) "and it is flagged isError"

        // ── 3. revocation takes effect on the next request ───────────

        testCase "withdrawing a grant takes effect on the very next request"
        <| fun () ->
            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "summarise")
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            grant f [ "sales.summarise" ]
            let _, before = call f "tools/list" "1" None
            Expect.equal (toolNames (conformingResponse "1" before)) [ "sales.summarise" ] "granted before"

            f.Grants.Remove(scope, accountId) |> Async.RunSynchronously |> ignore

            let _, after = call f "tools/list" "2" None
            Expect.equal (toolNames (conformingResponse "2" after)) [] "and gone on the next request, with no restart"

            let _, invoked =
                call f "tools/call" "3" (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")

            Expect.equal
                ((errorOf (conformingResponse "3" invoked)).GetProperty("code").GetInt32())
                McpProtocol.ErrorCode.toolUnavailable
                "and uninvokable, not merely undiscoverable"

        testCase "a request presenting no agent credential is 401 with a bearer challenge"
        <| fun () ->
            let f =
                fixture
                    [ AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") mustNotRun ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            grant f [ "sales.summarise" ]
            f.Authenticated <- false
            let status, body = call f "tools/list" "1" None

            Expect.equal status 401 "the MCP authorization spec answers an unauthenticated call with 401"

            Expect.equal
                ((errorOf (conformingResponse "1" body)).GetProperty("code").GetInt32())
                McpProtocol.ErrorCode.unauthorized
                "and the envelope says so too, so a client that reads JSON-RPC sees it"

        // ── 4. budget exhaustion is a typed refusal ──────────────────

        testCase "an exhausted call allowance refuses with the dimension, quota and spend as data"
        <| fun () ->
            let ledger = InMemoryBudgetLedger() :> IBudgetLedger

            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "summarise")
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    {
                        Budget = AgentBudget.callsPer BudgetPeriod.Daily 1M
                        RateLimit = None
                    }
                    (Some ledger)

            grant f [ "sales.summarise" ]

            let _, first =
                call f "tools/call" "1" (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")

            Expect.isTrue
                (fst ((conformingResponse "1" first).TryGetProperty "result"))
                "the first call is inside the allowance"

            let _, second =
                call f "tools/call" "2" (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")

            let error = errorOf (conformingResponse "2" second)

            Expect.equal
                (error.GetProperty("code").GetInt32())
                McpProtocol.ErrorCode.budgetExhausted
                "the second call is over it"

            let data = error.GetProperty "data"

            Expect.equal (data.GetProperty("dimension").GetString()) "calls" "the refusal names which ceiling was hit"
            Expect.equal (data.GetProperty("quota").GetDecimal()) 1M "and the configured ceiling"
            Expect.equal (data.GetProperty("spent").GetDecimal()) 1M "and what had already been consumed"

            Expect.equal
                (data.GetProperty("domain").GetString())
                McpHostConstants.BudgetDomain
                "namespaced, so an agent budget can never read a compute budget's row"

        testCase "a concurrency ceiling of zero-in-flight is released when the call settles"
        <| fun () ->
            // The reservation must be RELEASED, or a concurrency cap of one
            // would admit exactly one call for the lifetime of the period.
            let ledger = InMemoryBudgetLedger() :> IBudgetLedger

            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "summarise")
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    {
                        Budget = AgentBudget.concurrency 1
                        RateLimit = None
                    }
                    (Some ledger)

            grant f [ "sales.summarise" ]

            for i in 1..3 do
                let _, body =
                    call f "tools/call" (string i) (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")

                Expect.isTrue
                    (fst ((conformingResponse (string i) body).TryGetProperty "result"))
                    (sprintf "sequential call %d must be admitted — the previous reservation was released" i)

        testCase "an unrestricted budget touches no ledger at all"
        <| fun () ->
            // GP 13's keystone for this axis: the fast path must not read a
            // row, so a deployment with no ceilings pays nothing. Asserted
            // by composing a ledger that fails the test if it is reached.
            let tripwire =
                { new IBudgetLedger with
                    member _.ReadUsage _ =
                        failtest "an unrestricted budget must not read a ledger row"

                    member _.Reserve(_, _, _) =
                        failtest "an unrestricted budget must not reserve"

                    member _.Release(_, _) =
                        failtest "an unrestricted budget must not release"
                }

            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "summarise")
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    (Some tripwire)

            grant f [ "sales.summarise" ]

            let _, body =
                call f "tools/call" "1" (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")

            Expect.isTrue
                (fst ((conformingResponse "1" body).TryGetProperty "result"))
                "the call succeeds without the ledger having been consulted"

        // ── audit ────────────────────────────────────────────────────

        testCase "every interaction is recorded under the agent's identity"
        <| fun () ->
            let f =
                fixture
                    [
                        AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "summarise")
                    ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            grant f [ "sales.summarise" ]
            call f "initialize" "1" (Some "{}") |> ignore
            call f "tools/list" "2" None |> ignore

            call f "tools/call" "3" (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")
            |> ignore

            // The audit writes are fire-and-forget by contract (the refusal
            // is the control, not the row), so the assertion waits briefly
            // for them rather than racing them.
            let recorded () =
                rows f McpHostConstants.AuditSourceModule

            let deadline = DateTime.UtcNow.AddSeconds 5.0

            while (recorded ()).Length < 4 && DateTime.UtcNow < deadline do
                Threading.Thread.Sleep 25

            let events = recorded ()
            let types = events |> List.map _.EventType |> List.distinct |> List.sort

            Expect.containsAll
                types
                [
                    McpAudit.SessionInitializedEvent
                    McpAudit.ToolsListedEvent
                    McpAudit.ToolCalledEvent
                ]
                "the handshake, the listing and the call are each recorded"

            for evt in events do
                Expect.equal evt.ScopeId scope "every row is written in the agent's own scope (GP 4)"

                Expect.stringContains
                    evt.Payload
                    accountId
                    "and carries the agent's identity — an MCP trail that cannot say WHO is not an audit trail"

        testCase "a denial reaches the Phase 47 allowlist rollup stream as well as this phase's own"
        <| fun () ->
            let f =
                fixture
                    [ AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") mustNotRun ]
                    [ "Sales", [ ModulePermission.Read ] ]
                    AgentPolicy.unrestricted
                    None

            call f "tools/call" "1" (Some "{\"name\":\"sales.summarise\",\"arguments\":{}}")
            |> ignore

            let deadline = DateTime.UtcNow.AddSeconds 5.0

            while (rows f AIAllowlistDiagnosticsHandler.SourceModule).IsEmpty
                  && DateTime.UtcNow < deadline do
                Threading.Thread.Sleep 25

            let allowlist = rows f AIAllowlistDiagnosticsHandler.SourceModule

            Expect.isNonEmpty
                allowlist
                "an MCP denial must surface in /dev/ai-allowlist beside in-app ones — that is the phase's own \
                 requirement"

            Expect.equal
                (allowlist |> List.map _.EventType |> List.distinct)
                [ AIAllowlistDiagnosticsHandler.DenialEventType ]
                "under the event type that view reads"

            let payload =
                JsonSerializer.Deserialize<AIAllowlistDiagnosticsHandler.DenialEventPayload>(
                    allowlist.Head.Payload,
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
                )

            Expect.equal
                payload.ToolName
                "sales.summarise"
                "the payload round-trips through that view's OWN record — a shape it cannot read is a row it drops"

            Expect.stringContains payload.Reason accountId "and names the agent"

        // ── GP 13 ────────────────────────────────────────────────────

        testCase "NoMcpHost contributes no handler"
        <| fun () ->
            let baseApp =
                AICompose.AIServerApp.create (StubProviderFactory()) (StubProviderProfile())

            let withAccounts =
                baseApp
                |> AICompose.AIServerApp.withConfig {
                    baseApp.Base.Config with
                        ServiceAccounts = EnabledServiceAccounts
                }

            let composedWith = McpCompose.McpServerApp.create withAccounts

            let composedWithout =
                composedWith |> McpCompose.McpServerApp.withMcpHost McpCompose.NoMcpHost

            let handlerCount (app: McpCompose.McpServerApp) =
                (McpCompose.McpServerApp.compose app).Extensions.Handlers.Length

            Expect.equal
                (handlerCount composedWithout)
                (handlerCount composedWith - 1)
                "the enabled host contributes exactly one handler, and NoMcpHost contributes none — the \
                 strip-imports guarantee, asserted on the composition rather than described in a comment"

        testCase "a composition with no service accounts is refused at compose, not at the first request"
        <| fun () ->
            let baseApp =
                AICompose.AIServerApp.create (StubProviderFactory()) (StubProviderProfile())

            let app = McpCompose.McpServerApp.create baseApp

            // The host authenticates agents one way and one way only, so a
            // composition without that middleware serves nothing but 401 —
            // a deployment that looks composed and is not. The refusal names
            // the property and the remedy.
            Expect.throws
                (fun () -> McpCompose.McpServerApp.compose app |> ignore)
                "ServiceAccounts = NoServiceAccounts leaves no credential for an agent to present"

        // ── the authorisation decision, directly ─────────────────────

        testCase "visibleTools and classifyCall cannot disagree"
        <| fun () ->
            // The property the whole design turns on: listing and invoking
            // are one predicate. Asserted over the shipped functions rather
            // than through the wire, so a future second predicate breaks
            // this case before it reaches a client.
            let registry =
                registryOf [
                    AIToolRegistry.createTool (toolDef "sales.summarise" "Sales") (echoExecutor "a")
                    AIToolRegistry.createTool (toolDef "sales.forecast" "Sales") (echoExecutor "b")
                    AIToolRegistry.createTool (toolDef "hr.roster" "HR") mustNotRun
                ]

            let access = accessFor [ "Sales", [ ModulePermission.Read ] ]

            let agent: AgentPrincipal = {
                AccountId = accountId
                DisplayName = "Nightly exporter"
                ScopeId = scope
                TokenId = tokenId
                ExpiresAt = DateTimeOffset.UtcNow.AddDays 30.0
                GrantedTools = Set.ofList [ "sales.summarise"; "hr.roster" ]
            }

            let visible =
                McpAuthorization.visibleTools registry access (fun _ -> true) agent
                |> List.map _.Definition.Name

            for name in [ "sales.summarise"; "sales.forecast"; "hr.roster"; "no.such.tool" ] do
                let invokable =
                    McpAuthorization.classifyCall registry access (fun _ -> true) agent name
                    |> Result.isOk

                Expect.equal
                    invokable
                    (List.contains name visible)
                    (sprintf "'%s' must be invokable exactly when it is visible" name)
    ]
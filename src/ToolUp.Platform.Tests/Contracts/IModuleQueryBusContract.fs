module ToolUp.Platform.Tests.Contracts.IModuleQueryBusContract

open Expecto
open ToolUp.Platform

// ─── IModuleQueryBus contract pack ────────────────────────────────
//
// Parametrised tests for any `IModuleQueryBus` implementation.
//
// Factory shape: `(string * ModuleQueryHandler) list -> IModuleQueryBus`.
// Tests pre-register the (moduleName, handler) pairs they need
// before constructing the bus, so the contract pack covers
// implementations that build a registry once at construction (the
// shipped `InMemoryModuleQueryBus`) and a future distributed
// implementation that registers handlers via cluster discovery.
//
// Coverage targets the interface contract — the three-valued return
// shape, permission enforcement, exception wrapping, and the
// stateless-handler contract (Phase 9c Rule 4). Cross-shard ordering
// (Rule 5) is documented as "no ordering promise"; the contract pack
// does not assert ordering.

let tests (name: string) (factory: (string * ModuleQueryHandler) list -> IModuleQueryBus) =

    // Plain handler that echoes the request payload back as the
    // response. Lets tests assert the round-trip without depending
    // on real module shapes.
    let echoHandler = {
        QueryKey = "echo"
        Handle = fun ctx -> async { return ctx.Request.Payload }
    }

    let throwingHandler = {
        QueryKey = "boom"
        Handle = fun _ -> async { return failwith "intentional failure" }
    }

    let request targetModule queryKey payload : ModuleQueryRequest = {
        TargetModule = targetModule
        QueryKey = queryKey
        Payload = payload
    }

    let unrestrictedAccess userId =
        AccessContext.unrestricted (Subject.AnonymousSession userId)

    let restrictedAccess userId moduleName perms = {
        AccessContext.unrestricted (Subject.AnonymousSession userId) with
            ModulePermissions = Map.ofList [ moduleName, perms ]
    }

    testList $"{name} — IModuleQueryBus contract" [

        testCaseAsync "Ask for unknown module returns None (graceful degradation)"
        <| async {
            let bus = factory []
            let ctx = unrestrictedAccess "alice"

            match! bus.Ask(ctx, request "Ghost" "echo" "x") with
            | None -> ()
            | other -> failtestf "Expected None for unknown module, got %A" other
        }

        testCaseAsync "Ask for known module + handler returns Ok response"
        <| async {
            let bus = factory [ "Sales", echoHandler ]
            let ctx = unrestrictedAccess "alice"

            match! bus.Ask(ctx, request "Sales" "echo" "hello") with
            | Some(Ok resp) -> Expect.equal resp.Payload "hello" "echo round-trips the payload"
            | other -> failtestf "Expected Some (Ok _), got %A" other
        }

        testCaseAsync "Ask for known module without matching QueryKey returns NoHandler"
        <| async {
            let bus = factory [ "Sales", echoHandler ]
            let ctx = unrestrictedAccess "alice"

            match! bus.Ask(ctx, request "Sales" "missing-query" "x") with
            | Some(Error(NoHandler(m, q))) ->
                Expect.equal m "Sales" "names the module"
                Expect.equal q "missing-query" "names the missing query key"
            | other -> failtestf "Expected NoHandler, got %A" other
        }

        testCaseAsync "Permission denial returns Some(Error(PermissionDenied _))"
        <| async {
            let bus = factory [ "Sales", echoHandler ]
            // Caller has only Read on a *different* module — not on Sales.
            let ctx = restrictedAccess "bob" "OtherModule" [ ModulePermission.Read ]

            match! bus.Ask(ctx, request "Sales" "echo" "x") with
            | Some(Error(PermissionDenied m)) -> Expect.equal m "Sales" "names the denied module"
            | other -> failtestf "Expected PermissionDenied, got %A" other
        }

        testCaseAsync "Empty permissions map = unrestricted (opt-in RBAC)"
        <| async {
            let bus = factory [ "Sales", echoHandler ]
            let ctx = unrestrictedAccess "alice" // ModulePermissions = Map.empty

            match! bus.Ask(ctx, request "Sales" "echo" "y") with
            | Some(Ok resp) -> Expect.equal resp.Payload "y" "empty perms → unrestricted access"
            | other -> failtestf "Expected Some (Ok _), got %A" other
        }

        testCaseAsync "Handler exceptions are wrapped — never propagate to caller"
        <| async {
            let bus = factory [ "Sales", throwingHandler ]
            let ctx = unrestrictedAccess "alice"

            match! bus.Ask(ctx, request "Sales" "boom" "x") with
            | Some(Error(HandlerFailed msg)) ->
                Expect.stringContains msg "intentional failure" "echoes the inner exception message"
            | other -> failtestf "Expected HandlerFailed, got %A" other
        }

        testCaseAsync "Handler receives the caller's AccessContext (Rule 4 stateless contract)"
        <| async {
            // Capture the AccessContext that arrives at the handler so
            // we can assert it round-trips. Handlers must read every
            // piece of state from `ModuleQueryContext` per Phase 9c
            // Rule 4 — this test asserts the bus passes context faithfully.
            let mutable observed: AccessContext option = None

            let captureHandler = {
                QueryKey = "capture"
                Handle =
                    fun ctx -> async {
                        observed <- Some ctx.AccessContext
                        return "ok"
                    }
            }

            let bus = factory [ "Sales", captureHandler ]
            let ctx = unrestrictedAccess "carol"

            let! _ = bus.Ask(ctx, request "Sales" "capture" "")

            match observed with
            | Some o -> Expect.equal o.UserId "carol" "AccessContext.UserId arrives intact"
            | None -> failtest "handler never observed an AccessContext"
        }

        testCaseAsync "Multiple handlers per module dispatch by QueryKey"
        <| async {
            let h1 = {
                QueryKey = "k1"
                Handle = fun _ -> async { return "from-k1" }
            }

            let h2 = {
                QueryKey = "k2"
                Handle = fun _ -> async { return "from-k2" }
            }

            let bus = factory [ "Sales", h1; "Sales", h2 ]
            let ctx = unrestrictedAccess "alice"

            match! bus.Ask(ctx, request "Sales" "k1" "") with
            | Some(Ok r) -> Expect.equal r.Payload "from-k1" "k1 dispatched correctly"
            | other -> failtestf "k1: expected Ok, got %A" other

            match! bus.Ask(ctx, request "Sales" "k2" "") with
            | Some(Ok r) -> Expect.equal r.Payload "from-k2" "k2 dispatched correctly"
            | other -> failtestf "k2: expected Ok, got %A" other
        }
    ]
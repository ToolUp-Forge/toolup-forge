module HelloWorld.Module.Server

open HelloWorld.Module.SharedTypes

// ─── Pure routine ────────────────────────────────────────────────
//
// The module's domain logic. Pure functions; no HttpContext, no DI,
// no ToolUp.Remoting. The composition root in the consuming server
// project wraps this routine in an HTTP API factory — see the sibling
// HelloWorld.Server project for the canonical wiring pattern.

let echoRoutine (request: EchoRequest) : EchoResponse = {
    Echoed = sprintf "Echo: %s" request.Text
}
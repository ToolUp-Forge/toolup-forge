module Template.Server

open Template.SharedTypes

// ─── Pure routine ────────────────────────────────────────────────
//
// The module's domain logic. Pure functions; no HttpContext, no DI,
// no Fable.Remoting. The composition root in the consuming server
// project wraps this routine in an HTTP API factory (with permission
// guards, scope-aware file access, etc.) — see `ToolUpApp-Server/Server.fs`
// for the canonical pattern.

let echoRoutine (request: EchoRequest) : EchoResponse = {
    Echoed = sprintf "Echo: %s" request.Text
}
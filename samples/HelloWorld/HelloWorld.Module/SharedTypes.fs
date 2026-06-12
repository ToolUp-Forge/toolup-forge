module HelloWorld.Module.SharedTypes

open ToolUp.Platform // forge-native auth attributes

// ─── Cross-cut module types ─────────────────────────────────────
//
// Types declared here are visible to BOTH the server (compiled into
// the consuming server project) AND the client (compiled into Fable
// JS via the .Client.props injection). Use this file for the API
// contract and any DTOs that cross the wire.

/// Echo request — the client sends a string, the server echoes it back.
type EchoRequest = { Text: string }

/// Echo response — what the server returns.
type EchoResponse = { Echoed: string }

/// ToolUp.Remoting API surface for this module. The client constructs
/// a typed proxy via `Api.makeProxy<HelloWorldApi>`; the server
/// implements the record in `Server.fs`. The convention is one record
/// per module with `unit -> Async<...>` or `<Request> -> Async<...>`
/// methods.
type HelloWorldApi = {
    /// Demo-scoped echo — the sample's composition root wires an
    /// anonymous-friendly resolver (`requestStartedAtResolver`
    /// returns `IsAnonymous () = true`), so anonymous callers are
    /// admitted by design.
    [<AllowAnonymous>]
    Echo: EchoRequest -> Async<EchoResponse>
}
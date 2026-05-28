module Template.SharedTypes

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

/// Fable.Remoting API surface for this module. The client constructs
/// a typed proxy via `Api.makeProxy<TemplateApi>`; the server
/// implements the record in `Server.fs`. The convention is one record
/// per module with `unit -> Async<...>` or `<Request> -> Async<...>`
/// methods.
type TemplateApi = {
    Echo: EchoRequest -> Async<EchoResponse>
}
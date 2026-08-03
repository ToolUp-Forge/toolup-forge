// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Layer 3 — wire format ───────────────────────────────────────────
//
// JSON-RPC 2.0 over HTTP/2 is the peer wire format. It is deliberately
// NOT the in-tree ToolUp.Remoting Giraffe transport: the wire format is
// a public contract committed to peers, so coupling it to the SDK's
// internal Remoting protocol would let an SDK upgrade silently break a
// peer. An open, documented wire format also keeps non-F# peer SDKs
// viable later (Phase 18e).
//
// F# DU / Option / record bodies are (de)serialised with the universal
// `FableConverters` set — the same converter set the rest of the SDK
// uses for SSE / non-Remoting JSON — so the payloads round-trip the F#
// type system without bespoke converters.

/// The structured payload carried inside a JSON-RPC request's `params`:
/// the propagated call context plus the method's positional arguments
/// (already serialised to a JSON array string by the client proxy).
type PeerWirePayload = {
    /// Identity / versioning / cascade context for this call.
    Context: PeerCallContext
    /// The method's positional arguments, serialised as a JSON array.
    Arguments: string
}

/// JSON-RPC 2.0 request envelope. `JsonRpc` is always `"2.0"`. `Method`
/// is the contract method name; the contract id is carried in the route
/// (`/peer/v1/{contractId}`), not the method string.
type JsonRpcRequest = {
    JsonRpc: string
    Method: string
    /// Serialised `PeerWirePayload`.
    Params: string
    /// Request id — derived from the call's `RootRequestId` so the wire
    /// id and the audit id line up.
    Id: string
}

/// JSON-RPC 2.0 error object.
type JsonRpcErrorBody = {
    Code: int
    Message: string
    /// Serialised `PeerError`, when the failure maps to one. `None` for
    /// protocol-level errors with no structured peer error.
    Data: string option
}

/// JSON-RPC 2.0 response envelope. Exactly one of `Result` / `Error` is
/// populated. `Result`, when present, is the serialised method result.
type JsonRpcResponse = {
    JsonRpc: string
    Result: string option
    Error: JsonRpcErrorBody option
    Id: string
}

/// Wire-format constants, serialisation helpers, and the `PeerError` ↔
/// JSON-RPC error-code mapping.
module JsonRpc =

    /// The only JSON-RPC version the substrate speaks.
    let version = "2.0"

    // Standard JSON-RPC 2.0 reserved codes.
    let parseError = -32700
    let invalidRequest = -32600
    let methodNotFound = -32601
    let invalidParams = -32602
    let internalError = -32603

    // Implementation-defined server codes (-32000 .. -32099) for the
    // peer-substrate-specific failure modes.
    let unauthorized = -32000
    let contractNotFound = -32001
    let versionMismatch = -32002
    let loopDetected = -32003
    let hopLimitExceeded = -32004
    let handlerError = -32005
    let transportError = -32006

    /// Phase 315 — the inbound body exceeded the receiver's ceiling. A
    /// server code rather than a reuse of `invalidRequest`: the request
    /// was never parsed, so nothing is known about its validity, and a
    /// caller retrying with a smaller payload needs to be able to tell
    /// "too big" from "malformed". The HTTP status is 413 alongside it.
    let requestTooLarge = -32007

    /// Phase 311 — the receiver's composed clean-room gate withheld the
    /// answer. A distinct code rather than a reuse of `handlerError`:
    /// the handler did not fail, and a caller (or an operator dashboard)
    /// has to be able to tell "your counterpart's privacy floor refused
    /// this answer" from "your counterpart's code threw" — the two have
    /// different remedies and only one of them is the caller's problem.
    /// The HTTP status stays 200 alongside it, like every other
    /// structured dispatch outcome.
    let cleanRoomWithheld = -32008

    /// The universal F# converter set, constructed once. Mirrors the
    /// SDK convention for SSE / non-Remoting JSON (`CLAUDE.md`).
    let options: JsonSerializerOptions = FableConverters.create ()

    /// Serialise any F# value to JSON using the universal converter set.
    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    /// Deserialise JSON to an F# value using the universal converter set.
    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options)

    /// Map a structured `PeerError` to its JSON-RPC error code.
    let errorCode (err: PeerError) : int =
        match err with
        | PeerUnauthorized _ -> unauthorized
        | PeerContractNotFound _ -> contractNotFound
        | PeerMethodNotFound _ -> methodNotFound
        | PeerVersionMismatch _ -> versionMismatch
        | PeerLoopDetected _ -> loopDetected
        | PeerHopLimitExceeded -> hopLimitExceeded
        | PeerTransport _ -> transportError
        | PeerHandler _ -> handlerError
        | PeerDeserialization _ -> parseError
        | PeerRequestTooLarge _ -> requestTooLarge
        | PeerCleanRoomWithheld _ -> cleanRoomWithheld

    /// One-line human-readable message for a `PeerError` (the JSON-RPC
    /// `message` field). The structured error rides in `Data`.
    let errorMessage (err: PeerError) : string =
        match err with
        | PeerUnauthorized reason -> $"Peer unauthorized: {reason}"
        | PeerContractNotFound contractId -> $"Contract not found: {contractId}"
        | PeerMethodNotFound methodName -> $"Method not found: {methodName}"
        | PeerVersionMismatch(requested, _) -> $"Contract version mismatch: v{requested.Major}.{requested.Minor}"
        | PeerLoopDetected route ->
            let path = String.concat " -> " route
            $"Peer loop detected: {path}"
        | PeerHopLimitExceeded -> "Peer hop limit exceeded"
        | PeerTransport message -> $"Peer transport error: {message}"
        | PeerHandler message -> $"Peer handler error: {message}"
        | PeerDeserialization message -> $"Peer (de)serialization error: {message}"
        | PeerRequestTooLarge limitBytes ->
            $"Peer request too large: the receiver accepts at most {limitBytes} bytes of request body"
        // Deliberately says only THAT the gate withheld, never why — the
        // quantitative reason is a counting oracle over the protected
        // cohort (see `PeerCleanRoomWithheld`).
        | PeerCleanRoomWithheld templateId ->
            $"Peer clean-room gate '{templateId}' withheld this answer: it did not clear the receiver's privacy floor"

    /// The `PeerError` DU case name, with no payload detail — the safe
    /// outcome label for audit (`PeerCallCompletedPayload.Outcome`) and
    /// metrics, where the message text could leak handler internals.
    let errorCaseName (err: PeerError) : string =
        match err with
        | PeerUnauthorized _ -> "PeerUnauthorized"
        | PeerContractNotFound _ -> "PeerContractNotFound"
        | PeerMethodNotFound _ -> "PeerMethodNotFound"
        | PeerVersionMismatch _ -> "PeerVersionMismatch"
        | PeerLoopDetected _ -> "PeerLoopDetected"
        | PeerHopLimitExceeded -> "PeerHopLimitExceeded"
        | PeerTransport _ -> "PeerTransport"
        | PeerHandler _ -> "PeerHandler"
        | PeerDeserialization _ -> "PeerDeserialization"
        | PeerRequestTooLarge _ -> "PeerRequestTooLarge"
        | PeerCleanRoomWithheld _ -> "PeerCleanRoomWithheld"

    /// Build a JSON-RPC success response carrying a serialised result.
    let success (id: string) (result: 'T) : JsonRpcResponse = {
        JsonRpc = version
        Result = Some(serialize result)
        Error = None
        Id = id
    }

    /// Build a JSON-RPC error response from a structured `PeerError`.
    let failure (id: string) (err: PeerError) : JsonRpcResponse = {
        JsonRpc = version
        Result = None
        Error =
            Some {
                Code = errorCode err
                Message = errorMessage err
                Data = Some(serialize err)
            }
        Id = id
    }

    /// Build a JSON-RPC request envelope.
    let request (id: string) (method: string) (payload: PeerWirePayload) : JsonRpcRequest = {
        JsonRpc = version
        Method = method
        Params = serialize payload
        Id = id
    }
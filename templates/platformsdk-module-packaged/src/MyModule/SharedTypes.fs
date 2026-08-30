module MODULE_NAMESPACE_ROOT.SharedTypes

open ToolUp.Platform

// ─── Cross-tier types ────────────────────────────────────────────
//
// Everything declared here is visible to BOTH tiers: it is compiled
// into the packaged assembly for the server, and packed as source
// under `fable/` for the consumer's Fable build. Keep the API
// contract and its DTOs here; keep server-only types in Server.fs.

/// The module's id token. ONE literal, referenced by both
/// registrations — `ServerModule.create` server-side and
/// `ClientModule.withId` client-side.
///
/// The two registrations travel through composition roots that never
/// see each other, and `ServerModule.Name` is an ID (the RBAC
/// permission key, the `ServerConfig.ModuleNames` entry, the client's
/// `Model.ModuleStates` key) rather than a display name. Sharing one
/// literal is how the id-parity law is satisfied by construction
/// instead of by vigilance.
[<Literal>]
let ModuleId = "MODULE_ID_TOKEN"

/// The wire `TypeName` this module's processed data carries. Same
/// argument as `ModuleId`: the server registers it on its `DataType`
/// and the client gates on it, so it lives in one place.
[<Literal>]
let DataTypeId = "MODULE_DATA_TYPE_ID"

/// Echo request — the client sends a string, the server echoes it.
type EchoRequest = { Text: string }

/// Echo response — what the server returns.
type EchoResponse = { Echoed: string }

/// The module's remoting surface. The client builds a typed proxy from
/// this record; the consumer's composition root implements it by
/// wrapping the pure routines in Server.fs. One record per module,
/// methods shaped `'Request -> Async<'Response>`.
type ModuleApi = {
    /// Anonymous-friendly by declaration. Drop the attribute (and the
    /// `open ToolUp.Platform` above) for a module whose calls must be
    /// authenticated.
    [<AllowAnonymous>]
    Echo: EchoRequest -> Async<EchoResponse>
}
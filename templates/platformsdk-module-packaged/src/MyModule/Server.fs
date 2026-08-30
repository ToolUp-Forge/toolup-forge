module MODULE_NAMESPACE_ROOT.Server

open System
open ToolUp.Platform
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Server
open MODULE_NAMESPACE_ROOT.SharedTypes

// ─── Server tier ─────────────────────────────────────────────────
//
// SERVER-ONLY by declaration: this file is listed in the packaged
// layout contract's `ServerOnlyFiles` (see Build.fs) and is NOT packed
// under `fable/`. The shadow-project conformance check fails the build
// if it ever leaks into the Fable-compiled set — which is the point,
// because a server-only API reaching Fable breaks the CONSUMER's build,
// not this repo's.

/// The module's domain logic. Pure functions — no HttpContext, no DI,
/// no remoting. The consumer's composition root wraps these in the API
/// factory that implements `ModuleApi`.
let echoRoutine (request: EchoRequest) : EchoResponse = { Echoed = $"Echo: {request.Text}" }

// ─── Data type registration ──────────────────────────────────────

/// The module's one data type. `Detect` decides whether an uploaded
/// file is ours; `Process` turns it into a tagged JSON payload plus the
/// summary entry the file manager renders.
///
/// This starter accepts nothing and processes to an empty payload —
/// replace both with real parsing. What matters structurally is that
/// `Info.Id`, `Id` and the emitted `ProcessedData.TypeName` are the SAME
/// token: that id IS the wire `TypeName`, and the conformance pack's
/// uniqueness law is stated over it.
let dataType: DataType = {
    Info = {
        Id = DataTypeId
        DisplayName = "MODULE_DISPLAY_NAME dataset"
        Schema = None
    }
    Id = DataTypeId
    SchemaVersion = DataTypes.initialSchemaVersion
    Migrations = []
    Detect = fun _ -> async { return false }
    Process =
        fun (fileName, _) -> async {
            return
                {
                    TypeName = DataTypeId
                    Payload = "{}"
                },
                {
                    FileName = fileName
                    DataType = DataTypeId
                    ProcessedAt = DateTime.UtcNow
                    Info = None
                    Error = None
                }
        }
}

// ─── Module registration (server half) ───────────────────────────

/// The server-tier registration this package exports. A consumer's
/// composition root appends it to its module list; nothing about the
/// module's identity or data types is restated there.
///
/// `ServerModule.create` takes the ID token, not a display name — see
/// the `ModuleId` literal in SharedTypes.fs for why the two tiers share
/// one constant.
let serverModule: ServerModule =
    ServerModule.create ModuleId |> ServerModule.withDataTypes [ dataType ]
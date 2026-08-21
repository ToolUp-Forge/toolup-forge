// Ambient context for `src/ToolUp.Platform/technical-guide/03-authentication-secrets-and-encryption.md`.
//
// The page's composition-root locals. `config` is the deployment's
// `ServerConfig`, threaded through every `ServerApp` pipeline the page
// shows; `inner` is the raw writable secret store the key-rotation
// section talks about by name (the un-wrapped `FileSecretStore`, not the
// `EncryptedSecretStore` decorator over it). A block that declares
// either name for itself shadows this one.

[<AutoOpen>]
module PageAmbient =

    let config: ServerConfig = failwith "ambient"

    let inner: FileSecretStore.FileSecretStore = failwith "ambient"
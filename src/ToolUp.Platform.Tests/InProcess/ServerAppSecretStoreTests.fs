module ToolUp.Platform.Tests.InProcess.ServerAppSecretStoreTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Azure
open Azure.Security.KeyVault.Secrets
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets
open ToolUp.Platform.ResilientSecretStore
open ToolUp.Platform.TransientFault

// ─── Phase 1001 — an app supplies ServerApp its secret store ─────────
//
// Before this phase `compose` built a raw `FileSecretStore` unconditionally
// and handed it to every preflight validator and internal store; an app that
// selected a vault could only register it in DI, where the platform never
// looked. So a vault-selecting deployment booted only by acknowledging
// plaintext secrets, and its internal stores wrote `secrets*.json` while the
// app believed it ran on the vault.
//
// The cases below pin the seam end to end:
//
//   * a vault-selecting app (Azure Key Vault over a substituted
//     `SecretClient`, no Azure credentials) composes ONE store: the DI
//     `ISecretStore`, the three secret-store preflight checks and the
//     platform's own secret writes all reach the vault — with NO plaintext
//     acknowledgement, NO master key and NO `TOOLUP_SECRET_STORE` spelling;
//   * through the resilience wrapper, the vault still reads EncryptsAtRest
//     and a FileSecretStore still reads plaintext;
//   * a FileSecretStore app (nothing supplied) is unchanged: refused exactly
//     as before with nothing set, passing exactly as before on a master key
//     or a `TOOLUP_SECRET_STORE` spelling.

// ─── Env helpers ─────────────────────────────────────────────────────

let private withEnv (name: string) (value: string option) (body: unit -> 'a) : 'a =
    let saved = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, saved)

/// The ambient secret-store environment, cleared, so a case states every
/// variable it depends on. Every variable the three validators read.
let private withCleanSecretEnv (body: unit -> 'a) : 'a =
    withEnv ConfigKeys.Names.secretStore None (fun () ->
        withEnv ConfigKeys.Names.acceptPlaintextSecrets None (fun () ->
            withEnv "TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE" None (fun () ->
                withEnv "TOOLUP_SECRETS_MASTER_KEY" None body)))

/// Run `body` with the process cwd AND `TOOLUP_SECRETS_PATH` unset, inside
/// a fresh empty directory, so any `secrets*.json` a FileSecretStore wrote
/// would land where the case can see it.
let private inEmptyCwd (body: string -> 'a) : 'a =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-1001-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let saved = Directory.GetCurrentDirectory()

    try
        Directory.SetCurrentDirectory dir

        withEnv ConfigKeys.Names.secretsPath None (fun () -> body dir)
    finally
        Directory.SetCurrentDirectory saved

        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private secretsFilesUnder (dir: string) =
    Directory.GetFiles(dir, "secrets*.json", SearchOption.AllDirectories)
    |> List.ofArray

// ─── A Key Vault with no Azure behind it ─────────────────────────────

/// `SecretClient` exposes a protected parameterless constructor and
/// virtual members so it can be substituted; this one keeps the vault in
/// a dictionary keyed by Key Vault secret name.
type private FakeVault() =
    let secrets = ConcurrentDictionary<string, string>()

    let notFound () =
        Task.FromException<Response<KeyVaultSecret>>(RequestFailedException(404, "SecretNotFound"))

    let reads = ConcurrentQueue<string>()

    let get (name: string) =
        reads.Enqueue name

        match secrets.TryGetValue name with
        | true, v -> Task.FromResult(Response.FromValue(KeyVaultSecret(name, v), null))
        | _ -> notFound ()

    member _.Secrets = secrets
    member _.Reads = List.ofSeq reads

    member this.Client: SecretClient =
        { new SecretClient() with
            override _.GetSecretAsync(name: string, _version: string, _ct: CancellationToken) = get name

            override _.GetSecretAsync
                (name: string, _version: string, _contentType: Nullable<SecretContentType>, _ct: CancellationToken)
                =
                get name

            override _.SetSecretAsync(name: string, value: string, _ct: CancellationToken) =
                secrets[name] <- value
                Task.FromResult(Response.FromValue(KeyVaultSecret(name, value), null))
        }

let private vaultStore (vault: FakeVault) : ISecretStore =
    ToolUp.Secrets.AzureKeyVault.createWithClient vault.Client

// ─── Config + validators ─────────────────────────────────────────────

/// An authenticated deployment with the connector-OAuth substrate on, so
/// all three secret-store checks are registered and in force.
let private authConfig () = {
    ServerConfig.defaults with
        Surfaces = Surfaces.individual
        DataIngestion = EnabledDataIngestion
        AcceptPlaintextSecretsWhenAuthRequired = false
}

let private threeChecks = [
    "secret-store-at-rest-posture"
    "encrypted-secret-store-mode"
    "oauth-secret-encryption-mode"
]

let private validatorsOver (config: ServerConfig) (store: ISecretStore) : IConfigValidator list = [
    SecretStoreAtRestPostureValidator.SecretStoreAtRestPostureValidator(config, store)
    EncryptedSecretStoreModeValidator.EncryptedSecretStoreModeValidator(config, store)
    OAuthSecretEncryptionModeValidator.OAuthSecretEncryptionModeValidator(config, store)
]

let private run (v: IConfigValidator) =
    v.Name, v.Validate() |> Async.RunSynchronously

let private expectAllOk (results: (string * ValidationResult) list) =
    for name, result in results do
        match result with
        | Ok -> ()
        | other -> failtestf "%s: expected Ok with no acknowledgement, got %A" name other

let private resultOf (name: string) (results: (string * ValidationResult) list) =
    results |> List.find (fun (n, _) -> n = name) |> snd

let private isError =
    function
    | Error _ -> true
    | _ -> false

let private policy: TransientFaultPolicy = TransientFaultPolicy.identity

// ─── The composed app ────────────────────────────────────────────────

/// Records every line the composition logs, so a case can read the real
/// preflight's verdict on each validator (`[preflight] <name>: Ok …`).
type private CapturingLogger() =
    let lines = ConcurrentQueue<string>()
    member _.Lines = List.ofSeq lines

    interface ILogger with
        member _.Debug m = lines.Enqueue m
        member _.Info m = lines.Enqueue m
        member _.Warn m = lines.Enqueue m
        member _.Error(m, _) = lines.Enqueue m

type private Composed = {
    /// Every line the composition logged, the preflight's included.
    Log: string list
    /// The service collection as compose left it at the companion hook —
    /// the platform's own registrations, the DI `ISecretStore` among them.
    Services: IServiceCollection
}

/// Compose a minimal authenticated app exactly as `ServerApp.run` does,
/// with `supplied` as the app's secret store, and let compose run its real
/// preflight. Nothing binds a port: this minimal app is refused by
/// validators this phase does not touch (header auth, SSE auth, …), so
/// compose stops at the preflight — the point at which every validator has
/// run against the composed secret store.
let private composeWith (supplied: ISecretStore option) (resilience: ResilienceMode) : Composed =
    let logger = CapturingLogger()
    let mutable captured: IServiceCollection option = None

    let extensions = {
        ComposeExtensions.empty with
            ServiceConfig =
                Some(fun services ->
                    captured <- Some services
                    services)
    }

    try
        ToolUp.Platform.Server.compose
            []
            []
            (authConfig ())
            None
            extensions
            (Some(logger :> ILogger))
            (Some(ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage() :> BlobStorage.IBlobStorage))
            None
            []
            []
            []
            []
            []
            None
            []
            None
            []
            []
            None
            []
            None
            []
            []
            None
            None
            []
            []
            []
            []
            []
            NoResilience
            resilience
            supplied
        |> ignore
    with :? ConfigValidatorAggregator.ConfigPreflightFailedException ->
        ()

    {
        Log = logger.Lines
        Services =
            captured
            |> Option.defaultWith (fun () -> failtest "compose never reached the companion hook")
    }

/// The real preflight's verdict on `name`: `Ok`, `Warning` or `Error`.
let private preflightVerdict (composed: Composed) (name: string) : string =
    let prefix = sprintf "[preflight] %s: " name

    match composed.Log |> List.filter (fun l -> l.StartsWith prefix) with
    | [ line ] -> line.Substring(prefix.Length).Split(' ').[0]
    | [] -> failtestf "the composed preflight never ran %s" name
    | many -> failtestf "the composed preflight ran %s %d times" name many.Length

let private diSecretStore (composed: Composed) : obj =
    match
        composed.Services
        |> Seq.filter (fun d -> d.ServiceType = typeof<ISecretStore>)
        |> List.ofSeq
    with
    | [ d ] -> d.ImplementationInstance
    | ds -> failtestf "expected exactly one ISecretStore registration, found %d" ds.Length

[<Tests>]
let tests =
    // Sequenced: the cases mutate process-global env vars and the cwd.
    testSequenced
    <| testList "Phase 1001 — ServerApp takes its secret store from the app" [

        // ── The ServerApp surface ──────────────────────────────────────

        test "ServerApp defaults to no supplied store; withSecretStore sets it, last call wins" {
            let app = ServerApp.empty
            Expect.isNone app.SecretStore "the default supplies nothing (FileSecretStore is composed)"

            let a = EnvironmentSecretStore.EnvironmentSecretStore() :> ISecretStore
            let b = FakeVault() |> vaultStore

            let configured = app |> ServerApp.withSecretStore a |> ServerApp.withSecretStore b

            Expect.isTrue (obj.ReferenceEquals(configured.SecretStore.Value, b)) "the last supplied store wins"
        }

        // ── composeSecretStore: one store, resilience applied ─────────

        test "nothing supplied → a raw FileSecretStore, exactly as before" {
            let store = ToolUp.Platform.Server.composeSecretStore None NoResilience
            Expect.equal (store.GetType()) typeof<FileSecretStore.FileSecretStore> "the unchanged default"
        }

        test "a supplied store with no resilience is composed as-is (the same instance)" {
            let vault = FakeVault() |> vaultStore
            let store = ToolUp.Platform.Server.composeSecretStore (Some vault) NoResilience
            Expect.isTrue (obj.ReferenceEquals(store, vault)) "no wrapper, no copy"
        }

        test "through the resilience wrapper a vault reads EncryptsAtRest and a FileSecretStore reads plaintext" {
            withCleanSecretEnv (fun () ->
                let wrappedVault =
                    ToolUp.Platform.Server.composeSecretStore
                        (Some(FakeVault() |> vaultStore))
                        (WithResiliencePolicy policy)

                let wrappedFile =
                    ToolUp.Platform.Server.composeSecretStore None (WithResiliencePolicy policy)

                Expect.isTrue (wrappedVault :? ResilientSecretStore) "the vault is wrapped"
                Expect.isTrue (wrappedFile :? ResilientSecretStore) "the default is wrapped"

                match SecretStoreAtRestPostureValidator.resolveAtRestPosture wrappedVault with
                | EncryptsAtRest reason -> Expect.stringContains reason "Azure Key Vault" "the vault's own declaration"
                | other -> failtestf "wrapped vault: expected EncryptsAtRest, got %A" other

                match SecretStoreAtRestPostureValidator.resolveAtRestPosture wrappedFile with
                | PlaintextAtRest reason ->
                    Expect.stringContains reason "FileSecretStore" "the file store's own declaration"
                | other -> failtestf "wrapped FileSecretStore: expected PlaintextAtRest, got %A" other

                Expect.isTrue (SecretStore.declaresEncryptionAtRest wrappedVault) "declared, through the wrapper"
                Expect.isFalse (SecretStore.declaresEncryptionAtRest wrappedFile) "plaintext, through the wrapper")
        }

        // ── The Key Vault test seam ───────────────────────────────────

        test "AzureKeyVaultSecretStore over a supplied client declares EncryptsAtRest and round-trips" {
            let vault = FakeVault()
            let store = vaultStore vault

            match SecretStoreAtRestPostureValidator.resolveAtRestPosture store with
            | EncryptsAtRest _ -> ()
            | other -> failtestf "expected EncryptsAtRest, got %A" other

            let set = store.SetSecret("_platform", "Api_Key", "v1") |> Async.RunSynchronously
            Expect.equal set (Result.Ok()) "the write reaches the client"
            Expect.equal (vault.Secrets["toolup--platform-api-key"]) "v1" "under the vault's sanitised name"

            Expect.equal
                (store.GetSecret("_platform", "Api_Key") |> Async.RunSynchronously)
                (Some "v1")
                "and reads back"

            Expect.isNone (store.GetSecret("_platform", "absent") |> Async.RunSynchronously) "a 404 is None"
        }

        // ── The vault-selecting app ───────────────────────────────────

        for label, resilience in [ "bare", NoResilience; "resilience-wrapped", WithResiliencePolicy policy ] do
            test $"a vault-selecting app ({label}) passes all three secret-store checks with no acknowledgement" {
                withCleanSecretEnv (fun () ->
                    let store =
                        ToolUp.Platform.Server.composeSecretStore (Some(FakeVault() |> vaultStore)) resilience

                    validatorsOver (authConfig ()) store |> List.map run |> expectAllOk)
            }

        test
            "a composed vault-selecting app: ONE store reaches DI, the three checks and the platform's own reads; no secrets*.json" {
            withCleanSecretEnv (fun () ->
                inEmptyCwd (fun dir ->
                    let vault = FakeVault()
                    let supplied = vaultStore vault
                    let composed = composeWith (Some supplied) NoResilience

                    Expect.isTrue
                        (obj.ReferenceEquals(diSecretStore composed, supplied))
                        "the DI-registered ISecretStore is the supplied vault"

                    for name in threeChecks do
                        Expect.equal (preflightVerdict composed name) "Ok" (sprintf "%s passes on the vault" name)

                    // The platform's own secret traffic reaches the vault: the
                    // `secret-store` reachability probe reads through the one
                    // composed store, and its canary read lands in the vault.
                    Expect.equal (preflightVerdict composed "secret-store") "Ok" "the reachability probe ran"

                    Expect.isTrue
                        (vault.Reads |> List.exists (fun n -> n.Contains "preflight-canary"))
                        (sprintf "the probe's read reached the vault (reads: %A)" vault.Reads)

                    // And a platform write through the composed store is held
                    // by the vault, never by a secrets file.
                    let diStore = diSecretStore composed :?> ISecretStore

                    Expect.equal
                        (diStore.SetSecret("_platform", "webhook-signing", "s3cr3t")
                         |> Async.RunSynchronously)
                        (Result.Ok())
                        "a platform write succeeds"

                    Expect.isTrue
                        (vault.Secrets.ContainsKey "toolup--platform-webhook-signing")
                        "and is held by the vault"

                    Expect.isEmpty (secretsFilesUnder dir) "no secrets*.json was written"))
        }

        test "a composed resilience-wrapped vault app passes the three checks; DI holds the wrapper over the vault" {
            withCleanSecretEnv (fun () ->
                inEmptyCwd (fun dir ->
                    let vault = FakeVault()
                    let composed = composeWith (Some(vaultStore vault)) (WithResiliencePolicy policy)

                    Expect.isTrue
                        (diSecretStore composed :? ResilientSecretStore)
                        "the supplied store is wrapped, not replaced"

                    for name in threeChecks do
                        Expect.equal
                            (preflightVerdict composed name)
                            "Ok"
                            (sprintf "%s passes through the wrapper" name)

                    Expect.isNonEmpty vault.Reads "the platform's reads reach the vault through the wrapper"
                    Expect.isEmpty (secretsFilesUnder dir) "no secrets*.json was written"))
        }

        // ── A FileSecretStore app is unchanged ────────────────────────

        test "nothing supplied, nothing set → refused exactly as before (all three checks)" {
            withCleanSecretEnv (fun () ->
                let results =
                    validatorsOver (authConfig ()) (ToolUp.Platform.Server.composeSecretStore None NoResilience)
                    |> List.map run

                for name in threeChecks do
                    Expect.isTrue (isError (resultOf name results)) (sprintf "%s refuses the default" name))
        }

        test "nothing supplied, composed: the three checks refuse exactly as before; DI holds a FileSecretStore" {
            withCleanSecretEnv (fun () ->
                inEmptyCwd (fun _ ->
                    let composed = composeWith None NoResilience

                    Expect.equal
                        ((diSecretStore composed).GetType())
                        typeof<FileSecretStore.FileSecretStore>
                        "the default is unchanged"

                    for name in threeChecks do
                        Expect.equal (preflightVerdict composed name) "Error" (sprintf "%s refuses the default" name)))
        }

        test "nothing supplied + a master key → encrypted-secret-store-mode passes exactly as before" {
            withCleanSecretEnv (fun () ->
                let key = Convert.ToBase64String(Array.zeroCreate<byte> 32)

                withEnv "TOOLUP_SECRETS_MASTER_KEY" (Some key) (fun () ->
                    let results =
                        validatorsOver (authConfig ()) (ToolUp.Platform.Server.composeSecretStore None NoResilience)
                        |> List.map run

                    Expect.equal (resultOf "encrypted-secret-store-mode" results) Ok "the key satisfies the gate"
                    // The raw FileSecretStore still writes plaintext, so the
                    // two store-reading checks still refuse — as before.
                    Expect.isTrue (isError (resultOf "secret-store-at-rest-posture" results)) "posture: as before"
                    Expect.isTrue (isError (resultOf "oauth-secret-encryption-mode" results)) "oauth: as before"))
        }

        test "nothing supplied + TOOLUP_SECRET_STORE=azure-key-vault → the env carve-outs pass exactly as before" {
            withCleanSecretEnv (fun () ->
                withEnv ConfigKeys.Names.secretStore (Some "azure-key-vault") (fun () ->
                    let results =
                        validatorsOver (authConfig ()) (ToolUp.Platform.Server.composeSecretStore None NoResilience)
                        |> List.map run

                    Expect.equal (resultOf "encrypted-secret-store-mode" results) Ok "the spelling carve-out"
                    Expect.equal (resultOf "oauth-secret-encryption-mode" results) Ok "the spelling carve-out"
                    // The declared-plaintext FileSecretStore wins the posture
                    // ladder over the spelling — the false green stays refused.
                    Expect.isTrue
                        (isError (resultOf "secret-store-at-rest-posture" results))
                        "posture reads the composed store, as before"))
        }

        test "a supplied plaintext store is not lifted by the seam (the declaration decides)" {
            withCleanSecretEnv (fun () ->
                let supplied =
                    ToolUp.Platform.Server.composeSecretStore
                        (Some(FileSecretStore.FileSecretStore(baseDir = Path.GetTempPath()) :> ISecretStore))
                        (WithResiliencePolicy policy)

                let results = validatorsOver (authConfig ()) supplied |> List.map run

                for name in threeChecks do
                    Expect.isTrue (isError (resultOf name results)) (sprintf "%s refuses a plaintext store" name))
        }
    ]
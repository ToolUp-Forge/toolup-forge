// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Support.TestFakes

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose

// ─── Fakes for the always-on unit tests ───────────────────────────
//
// Everything in this file is in-process. The connectors' PURE
// surfaces (config parsing, catalogue SQL, connection-string
// composition, type classification) are tested against these; the
// vendor round-trips are the business of the env-gated
// `RemoteDataSourceContract` arms.

/// Counts `GetSecret` calls per scope+key over any `ISecretStore`, so
/// the credential-thunk behaviour (per-call re-read, rotation without
/// reconstruction) is observable. Storage is the inner store's — seed a
/// `TestSecretStore` and wrap it; this type only adds the count.
type ReadCountingSecretStore(inner: ISecretStore) =

    let reads = ConcurrentDictionary<string * string, int>()

    /// How many times `GetSecret` has been called for this scope+key.
    /// The rotation contract is "read on every call", so the count is
    /// the observable that proves it.
    member _.ReadCount(scopeId: string, key: string) =
        match reads.TryGetValue((scopeId, key)) with
        | true, count -> count
        | false, _ -> 0

    interface ISecretStore with
        member _.GetSecret(scopeId, key) =
            reads.AddOrUpdate((scopeId, key), 1, (fun _ current -> current + 1)) |> ignore
            inner.GetSecret(scopeId, key)

        member _.SetSecret(scopeId, key, value) = inner.SetSecret(scopeId, key, value)
        member _.DeleteSecret(scopeId, key) = inner.DeleteSecret(scopeId, key)
        member _.ListKeys(scopeId) = inner.ListKeys(scopeId)

/// Build a `DataSourceConfig` with the given kind and connection
/// scope; everything else takes an inert default.
let config (sourceId: string) (kind: string) (scope: (string * string) list) : DataSourceConfig = {
    Id = sourceId
    Name = $"Test source %s{sourceId}"
    Kind = kind
    ConnectionScope = Map.ofList scope
    CredentialKey = $"%s{kind.ToLowerInvariant()}-credential"
    Tables = None
    Tags = Map.empty
}

/// Build a `DataSourceCallContext` around `config`.
let context (scopeId: string) (credential: string option) (config: DataSourceConfig) : DataSourceCallContext = {
    ScopeId = scopeId
    Config = config
    Credential = credential
}

/// Read an env var as an option — `None` for unset or blank.
let env (name: string) : string option =
    match Environment.GetEnvironmentVariable name with
    | null -> None
    | value when String.IsNullOrWhiteSpace value -> None
    | value -> Some value

/// Read an env var, or a fallback when it is unset. Used for the
/// optional knobs of an env-gated arm (a schema name, a region)
/// where a sensible default keeps the operator's setup to two
/// variables instead of six.
let envOr (name: string) (fallback: string) : string =
    env name |> Option.defaultValue fallback

/// Phase 834 — the format a connector declares EXPLICITLY. Fails when the
/// connector does not implement `IDeclaresPayloadFormat` at all, because
/// `PayloadFormat.declaredBy` would answer `Csv` for it by default and a
/// "declares Csv" assertion over the default would pass vacuously.
let declaredFormat (source: IDataSource) : PayloadFormat =
    match box source with
    | :? IDeclaresPayloadFormat as declared -> declared.PayloadFormat
    | _ -> failwith $"connector '{source.Kind}' does not implement IDeclaresPayloadFormat"
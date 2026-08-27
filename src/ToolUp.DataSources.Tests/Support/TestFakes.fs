// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Support.TestFakes

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Fakes for the always-on unit tests ───────────────────────────
//
// Everything in this file is in-process. The connectors' PURE
// surfaces (config parsing, catalogue SQL, connection-string
// composition, type classification) are tested against these; the
// vendor round-trips are the business of the env-gated
// `RemoteDataSourceContract` arms.

/// An `ISecretStore` backed by a dictionary, so the credential-thunk
/// behaviour (per-call re-read, rotation without reconstruction) is
/// testable without a cloud secret manager.
type InMemorySecretStore() =

    let secrets = ConcurrentDictionary<string * string, string>()
    let reads = ConcurrentDictionary<string * string, int>()

    /// Store (or replace) a secret.
    member _.Put(scopeId: string, key: string, value: string) = secrets[(scopeId, key)] <- value

    /// Forget a secret.
    member _.Forget(scopeId: string, key: string) =
        secrets.TryRemove((scopeId, key)) |> ignore

    /// How many times `GetSecret` has been called for this scope+key.
    /// The rotation contract is "read on every call", so the count is
    /// the observable that proves it.
    member _.ReadCount(scopeId: string, key: string) =
        match reads.TryGetValue((scopeId, key)) with
        | true, count -> count
        | false, _ -> 0

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            reads.AddOrUpdate((scopeId, key), 1, (fun _ current -> current + 1)) |> ignore

            match secrets.TryGetValue((scopeId, key)) with
            | true, value -> return Some value
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            secrets[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            secrets.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                secrets.Keys
                |> Seq.filter (fun (scope, _) -> scope = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

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
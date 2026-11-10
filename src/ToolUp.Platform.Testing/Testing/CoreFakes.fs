// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

open System.Collections.Concurrent
open ToolUp.Platform.Secrets

// ─── The fakes that need nothing but Core ─────────────────────────────
//
// A test pack that may not reference this package (a part pack of the
// Platform family, or a companion's pack, which references its family and
// substrate only) compiles this file by source link, so it reads the same
// double a consumer's tests read. Keep it Core-only: a dependency on a
// server part here breaks every pack that links it.

/// In-memory `ISecretStore`. Per-(scope,key) string entries in a
/// concurrent dictionary; writes always succeed, deletes are
/// idempotent.
type TestSecretStore() =
    let secrets = ConcurrentDictionary<string * string, string>()

    /// Pre-seed a secret without going through `SetSecret`. Useful for
    /// arrange-act-assert flows that don't want to assert on the write
    /// path.
    member _.Seed(scopeId: string, key: string, value: string) = secrets[(scopeId, key)] <- value

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match secrets.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
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
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }
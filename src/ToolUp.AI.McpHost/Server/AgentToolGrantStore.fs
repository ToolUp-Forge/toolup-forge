// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.McpHost.AgentToolGrantStore

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 489 — the two shipped `IAgentToolGrantStore` implementations ──
//
// `BlobAgentToolGrantStore` is the production one: one JSON record per
// account under `_platform/mcp-agent-grants/{scopeId}/{accountId}.json`,
// mirroring the `_platform/service-accounts/` layout the credentials
// themselves use so an operator sees one shape across the subsystem. It
// is distributed-ready — every read hits storage, nothing is cached
// between calls, and the scope sits in the path prefix so cross-scope
// enumeration is structurally impossible rather than filtered for (GP 4,
// GP 12 rule 4).
//
// `InMemoryAgentToolGrantStore` is for tests and single-node
// development, and says so. It is genuinely non-durable.
//
// **On the wire shape of the grant set.** `Set<string>` round-trips
// through the SDK's universal `FableConverters` set. A record persisted
// before a field existed deserialises that field as `null` on the STJ
// path (CLAUDE.md, "Additive fields on persisted records"), and a null
// F# `Set` raises on every operation, so the read path coerces. That is
// belt-and-braces today — the record has shipped with one shape — and it
// is exactly the guard whose absence turns the first added field into a
// production NRE.

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let tryDeserialize<'T> (bytes: byte[]) : Result<'T, string> =
        try
            let json = Encoding.UTF8.GetString bytes
            Ok(JsonSerializer.Deserialize<'T>(json, options))
        with ex ->
            Error ex.Message

/// Coerce a record read back from storage into a usable value: a `null`
/// grant set (a record written before the field existed, or by a
/// non-SDK writer) becomes the empty set rather than an NRE at the first
/// `Set.contains`. `isNull (box …)` is the Fable-safe null probe the SDK
/// uses everywhere.
let private coerce (scopeId: string) (accountId: string) (record: AgentToolGrants) : AgentToolGrants =
    let tools =
        if isNull (box record.GrantedTools) then
            Set.empty
        else
            record.GrantedTools

    {
        record with
            // The path is authoritative for identity: a record whose body
            // disagrees with where it was found is repaired towards the
            // path, never towards the body, because the path is what the
            // caller's own scope resolution produced.
            AccountId = accountId
            ScopeId = scopeId
            GrantedTools = tools
    }

/// Phase 489 — the blob-backed grant store. Distributed-ready: stateless
/// between calls, scope-partitioned by path.
type BlobAgentToolGrantStore(blobs: IBlobStorage, ?clock: unit -> DateTimeOffset) =
    let now = defaultArg clock (fun () -> DateTimeOffset.UtcNow)
    let container = McpHostConstants.GrantContainer

    interface IAgentToolGrantStore with
        member _.Read(scopeId: string, accountId: string) = async {
            let blobName = AgentGrantLayout.grantBlob scopeId accountId

            match! blobs.Download(container, blobName) with
            | Error _ ->
                // An absent record IS the empty grant set — see the seam's
                // header. `IBlobStorage.Download` does not distinguish
                // "missing" from "unavailable" in its error channel, so
                // `Exists` decides which of the two this is rather than
                // guessing from an error string.
                let! exists = blobs.Exists(container, blobName)

                if exists then
                    return
                        Error(AgentGrantError.StorageFailed $"grant record for account '{accountId}' could not be read")
                else
                    return Ok(AgentToolGrants.empty scopeId accountId)
            | Ok bytes ->
                match Json.tryDeserialize<AgentToolGrants> bytes with
                | Error detail -> return Error(AgentGrantError.Corrupt detail)
                | Ok record -> return Ok(coerce scopeId accountId record)
        }

        member _.Write(scopeId: string, accountId: string, grantedTools: Set<string>, updatedBy: string) = async {
            let record = {
                AccountId = accountId
                ScopeId = scopeId
                GrantedTools =
                    (if isNull (box grantedTools) then
                         Set.empty
                     else
                         grantedTools)
                UpdatedBy = updatedBy
                UpdatedAt = now ()
            }

            let blobName = AgentGrantLayout.grantBlob scopeId accountId

            match! blobs.Upload(container, blobName, Json.serialize record) with
            | Error detail -> return Error(AgentGrantError.StorageFailed detail)
            | Ok _ -> return Ok record
        }

        member this.List(scopeId: string) = async {
            let! names = blobs.List(container, AgentGrantLayout.scopePrefix scopeId)
            let store = this :> IAgentToolGrantStore

            let accountIds =
                names
                |> List.choose (fun name ->
                    let leaf = name.Substring(name.LastIndexOf '/' + 1)

                    if leaf.EndsWith(".json", StringComparison.Ordinal) then
                        Some(leaf.Substring(0, leaf.Length - 5))
                    else
                        None)

            let! records = accountIds |> List.map (fun id -> store.Read(scopeId, id)) |> Async.Sequential

            // A record that cannot be read is omitted from the listing
            // rather than failing it: an admin screen that shows nothing
            // because one row is corrupt is worse than one that shows the
            // rest. The failing read is still a refusal on the path that
            // matters — authorisation — because that path calls `Read`.
            return records |> Array.toList |> List.choose Result.toOption
        }

        member _.Remove(scopeId: string, accountId: string) = async {
            let blobName = AgentGrantLayout.grantBlob scopeId accountId
            let! exists = blobs.Exists(container, blobName)

            if not exists then
                return Ok()
            else
                match! blobs.Delete(container, blobName) with
                | Error detail -> return Error(AgentGrantError.StorageFailed detail)
                | Ok() -> return Ok()
        }

/// Phase 489 — an in-process grant store for tests and single-node
/// development. **Dev-only**: everything is lost on restart, and two
/// replicas do not see each other's writes.
type InMemoryAgentToolGrantStore(?clock: unit -> DateTimeOffset) =
    let now = defaultArg clock (fun () -> DateTimeOffset.UtcNow)
    let rows = ConcurrentDictionary<string * string, AgentToolGrants>()

    interface IAgentToolGrantStore with
        member _.Read(scopeId: string, accountId: string) = async {
            match rows.TryGetValue((scopeId, accountId)) with
            | true, record -> return Ok record
            | _ -> return Ok(AgentToolGrants.empty scopeId accountId)
        }

        member _.Write(scopeId: string, accountId: string, grantedTools: Set<string>, updatedBy: string) = async {
            let record = {
                AccountId = accountId
                ScopeId = scopeId
                GrantedTools =
                    (if isNull (box grantedTools) then
                         Set.empty
                     else
                         grantedTools)
                UpdatedBy = updatedBy
                UpdatedAt = now ()
            }

            rows[(scopeId, accountId)] <- record
            return Ok record
        }

        member _.List(scopeId: string) = async {
            return
                rows
                |> Seq.filter (fun kv -> fst kv.Key = scopeId)
                |> Seq.map _.Value
                |> Seq.toList
        }

        member _.Remove(scopeId: string, accountId: string) = async {
            rows.TryRemove((scopeId, accountId)) |> ignore
            return Ok()
        }
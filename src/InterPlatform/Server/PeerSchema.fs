// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open Microsoft.FSharp.Reflection
open PeerReflection

// ─── Phase 18e (Stage 1) — language-neutral contract schema ──────────
//
// The non-F# peer SDKs (TypeScript / Python) are generated from a stable,
// language-neutral *schema* of a contract — its method names, argument and
// return types, lifetime (immediate vs long-running), and the referenced
// record shapes. This is the single source of truth the generators
// consume; the wire format is the open JSON-RPC 2.0 envelope the
// foundation already commits to, which is exactly why a non-F# peer is
// viable without re-implementing the F# transport.
//
// `PeerSchema.fromContract<'TApi>` reflects over the same record-of-
// functions contract type the host (`JsonRpcPeerHost.contract`) and proxy
// reflect over, so the schema can never drift from the contract. CLR types
// map into a small, closed type vocabulary; records are flattened into a
// `Records` table (referenced by name) so a generator can emit one
// interface / dataclass per record. The schema is serialised with the
// universal `FableConverters` set, so a golden-file snapshot pins it.

/// A language-neutral type reference — the closed vocabulary the non-F#
/// generators map into their own type systems. Anything outside the
/// vocabulary degrades to `Unknown` carrying the CLR name (so a generator
/// can emit `unknown` / `Any` rather than fail).
type PeerTypeRef =
    | PrimString
    | PrimInt
    | PrimFloat
    | PrimBool
    | PrimUnit
    | PrimGuid
    | PrimDateTimeOffset
    | OptionOf of PeerTypeRef
    | ListOf of PeerTypeRef
    /// A named record (object); its fields are described in `PeerContractSchema.Records`.
    | RecordRef of name: string
    /// A named discriminated union (tagged union). Not structurally
    /// expanded in schema v1 — a generator emits a permissive type.
    | UnionRef of name: string
    | Unknown of clrName: string

/// Whether a method resolves inside the request (`Immediate`) or through
/// the job substrate (`LongRunning` — the wire returns a job id the caller
/// polls). The non-F# client generates a poll loop for the latter.
type PeerMethodLifetime =
    | ImmediateMethod
    | LongRunningMethod

/// One contract method: its name, positional argument types, the inner
/// result type (with `Async` / `PeerJobHandle` stripped), and its lifetime.
type PeerMethodSchema = {
    Name: string
    Arguments: PeerTypeRef list
    Returns: PeerTypeRef
    Lifetime: PeerMethodLifetime
}

/// A referenced record type, flattened into the schema's `Records` table.
type PeerRecordSchema = {
    Name: string
    Fields: (string * PeerTypeRef) list
}

/// The complete language-neutral schema of a peer contract. `SchemaVersion`
/// is the schema-format version (bumped if the descriptor vocabulary
/// changes), distinct from the contract's own wire `ContractVersion`.
type PeerContractSchema = {
    SchemaVersion: int
    ContractId: string
    Methods: PeerMethodSchema list
    Records: PeerRecordSchema list
}

/// Reflects a record-of-functions contract type into a `PeerContractSchema`.
module PeerSchema =

    /// Current schema-format version.
    [<Literal>]
    let formatVersion = 1

    let private optionDef = typedefof<_ option>
    let private listDef = typedefof<_ list>

    /// Map a CLR type into the neutral vocabulary, accumulating any
    /// referenced record shapes into `records` (keyed by name to dedupe +
    /// break recursion). Mutation is local to the reflection walk
    /// (justified exception to GP 5 — building an immutable result).
    let rec private typeRef
        (records: System.Collections.Generic.Dictionary<string, PeerRecordSchema>)
        (t: Type)
        : PeerTypeRef =
        if t = typeof<string> then
            PrimString
        elif t = typeof<int> || t = typeof<int64> then
            PrimInt
        elif t = typeof<float> || t = typeof<float32> || t = typeof<decimal> then
            PrimFloat
        elif t = typeof<bool> then
            PrimBool
        elif t = typeof<unit> then
            PrimUnit
        elif t = typeof<Guid> then
            PrimGuid
        elif t = typeof<DateTimeOffset> || t = typeof<DateTime> then
            PrimDateTimeOffset
        elif t.IsArray then
            ListOf(typeRef records (t.GetElementType()))
        elif t.IsGenericType && t.GetGenericTypeDefinition() = optionDef then
            OptionOf(typeRef records (t.GetGenericArguments().[0]))
        elif t.IsGenericType && t.GetGenericTypeDefinition() = listDef then
            ListOf(typeRef records (t.GetGenericArguments().[0]))
        elif FSharpType.IsRecord t then
            // Reserve the name first so a self-referential record does not
            // recurse forever, then fill its fields.
            if not (records.ContainsKey t.Name) then
                records[t.Name] <- { Name = t.Name; Fields = [] }

                let fields =
                    FSharpType.GetRecordFields t
                    |> Array.map (fun f -> f.Name, typeRef records f.PropertyType)
                    |> Array.toList

                records[t.Name] <- { Name = t.Name; Fields = fields }

            RecordRef t.Name
        elif FSharpType.IsUnion t then
            UnionRef t.Name
        else
            Unknown t.FullName

    /// Strip the `PeerJobHandle<'T>` wrapper from a long-running method's
    /// inner result, returning the unwrapped type + the lifetime.
    let private resolveReturn (records) (retType: Type) : PeerTypeRef * PeerMethodLifetime =
        if
            retType.IsGenericType
            && retType.GetGenericTypeDefinition() = typedefof<PeerJobHandle<_>>
        then
            typeRef records (retType.GetGenericArguments().[0]), LongRunningMethod
        else
            typeRef records retType, ImmediateMethod

    /// Build the schema for `'TApi` hosted under `contractId`.
    let fromContract<'TApi> (contractId: string) : PeerContractSchema =
        let apiType = typeof<'TApi>

        if not (FSharpType.IsRecord apiType) then
            failwithf "PeerSchema.fromContract requires a record contract type; %s is not a record" apiType.Name

        let records = System.Collections.Generic.Dictionary<string, PeerRecordSchema>()

        let methods =
            FSharpType.GetRecordFields apiType
            |> Array.map (fun field ->
                let argTypes, retType = functionSignature field.PropertyType
                let returns, lifetime = resolveReturn records retType

                {
                    Name = field.Name
                    Arguments = argTypes |> List.map (typeRef records)
                    Returns = returns
                    Lifetime = lifetime
                })
            |> Array.toList

        {
            SchemaVersion = formatVersion
            ContractId = contractId
            Methods = methods
            Records = records.Values |> List.ofSeq |> List.sortBy _.Name
        }

    /// Serialise a schema to the canonical JSON document the generators
    /// read (the universal `FableConverters` set — golden-file pinnable).
    let toJson (schema: PeerContractSchema) : string = JsonRpc.serialize schema
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Fable.Core

// ─── Typed principal identity: a team id is not a user id ─────────────
//
// A user id and a team id are both strings, and every seam that takes
// both (`AddMember(teamId, userId)`, `SetActiveTeam(userId, teamId)`)
// used to accept them in either order. `UserId` and `TeamId` make the
// order a compile-time fact: passing one where the other is expected is a
// type error at every seam that takes them.
//
// Each wrapper is a single-case union in `ComponentId`'s shape, with one
// difference that keeps the wire and the blob names byte-identical:
//
//   * Under Fable it is `[<Erase>]`: the runtime value IS the wrapped
//     string — no allocation, and Fable's reflection reports the field as
//     `string`, so a browser codec reads and writes the bare string.
//   * On .NET the same attribute is what the System.Text.Json converter
//     set keys on: an erased union is written as its single field and read
//     back from it, so a persisted record's identity field keeps its bytes.
//   * `ToString` returns the wrapped string on .NET (Fable forbids an
//     override on an erased type, and needs none: the value is already the
//     string), so an interpolated blob name renders exactly as before.
//
// The smart constructors refuse an empty or padded id — the rule
// `ScopeKey.tryParseContainer` applies to an id read back from a
// container. The union case itself stays public, as `ComponentId`'s does:
// it is the trusted constructor for an id that was already validated (one
// read from a store, a token's claims or a route the platform minted).
//
// Fable-safe: BCL string operations only.

/// The well-formedness rule every typed identity applies.
[<RequireQualifiedAccess>]
module Identity =

    /// An id is well formed when it is non-null, non-empty and carries no
    /// leading or trailing whitespace.
    let isWellFormed (id: string) : bool =
        not (isNull id) && id.Length > 0 && id.Trim().Length = id.Length

    /// The refusal a smart constructor returns for a malformed id.
    let internal refusal (kind: string) (id: string) : string =
        if isNull id then
            sprintf "a %s cannot be null" kind
        elif id.Length = 0 then
            sprintf "a %s cannot be empty" kind
        else
            sprintf "a %s cannot carry leading or trailing whitespace: '%s'" kind id

/// A user's identity — the authenticated principal's stable id (the
/// `sub` / `oid` claim the auth provider resolved). Encodes as the bare
/// string it wraps on both hosts.
[<Erase>]
type UserId =
    | UserId of string

    /// The wrapped id string.
    member this.Value =
        let (UserId v) = this
        v

#if !FABLE_COMPILER
    override this.ToString() = this.Value
#endif

/// A team's identity. Encodes as the bare string it wraps on both hosts.
[<Erase>]
type TeamId =
    | TeamId of string

    /// The wrapped id string.
    member this.Value =
        let (TeamId v) = this
        v

#if !FABLE_COMPILER
    override this.ToString() = this.Value
#endif

/// Construction and projection for `UserId`.
[<RequireQualifiedAccess>]
module UserId =

    /// The wrapped id string.
    let value (UserId v) : string = v

    /// A `UserId` from an untrusted string: refuses a null, empty or
    /// padded id with a message naming the fault.
    let tryCreate (raw: string) : Result<UserId, string> =
        if Identity.isWellFormed raw then
            Ok(UserId raw)
        else
            Error(Identity.refusal "user id" raw)

    /// `tryCreate` as an option.
    let tryParse (raw: string) : UserId option =
        if Identity.isWellFormed raw then Some(UserId raw) else None

/// Construction and projection for `TeamId`.
[<RequireQualifiedAccess>]
module TeamId =

    /// The wrapped id string.
    let value (TeamId v) : string = v

    /// A `TeamId` from an untrusted string: refuses a null, empty or
    /// padded id with a message naming the fault.
    let tryCreate (raw: string) : Result<TeamId, string> =
        if Identity.isWellFormed raw then
            Ok(TeamId raw)
        else
            Error(Identity.refusal "team id" raw)

    /// `tryCreate` as an option.
    let tryParse (raw: string) : TeamId option =
        if Identity.isWellFormed raw then Some(TeamId raw) else None
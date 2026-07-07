// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.LdapDirectory

open System
open System.Text

// ─── Directory seam ──────────────────────────────────────────────────
//
// A deliberately narrow abstraction over the directory operations the
// provider / health-check / validator need — search, and a
// verify-password bind. It exists so the provider's logic (credential
// extraction, filter construction, group→role mapping, caching) is
// exercised in-process against an in-memory fake directory with **no
// live LDAP server**, and so the one place that touches
// `System.DirectoryServices.Protocols` (the real factory in
// `LdapConnection.fs`) is a thin adapter reviewed like a wire contract.
//
// Six-rule portability posture (GP 12): identity-by-value (plain
// records / strings, never a live `LdapConnection` on the surface),
// async at every boundary, no retry semantics leaked (the caller
// decides), stateless between calls (each `OpenServiceBound` yields a
// fresh short-lived connection).

/// LDAP search scope.
type LdapScope =
    | BaseObject
    | OneLevel
    | Subtree

/// A search request under the currently service-bound connection.
type LdapSearch = {
    BaseDn: string
    /// A fully-formed, already-escaped RFC-4515 filter.
    Filter: string
    Scope: LdapScope
    /// Attribute names to return. Empty ⇒ all attributes.
    Attributes: string list
    /// Cap on returned entries (`0` ⇒ server default).
    SizeLimit: int
}

/// One directory entry. `Attributes` are multi-valued by nature in
/// LDAP, so every attribute maps to a *list* of string values (empty
/// list ⇒ attribute absent).
type LdapEntry = {
    Dn: string
    Attributes: Map<string, string list>
}

module LdapEntry =
    /// First value of a (possibly multi-valued, possibly absent)
    /// attribute, case-insensitive on the attribute name.
    let firstValue (attr: string) (entry: LdapEntry) : string option =
        entry.Attributes
        |> Map.tryPick (fun k v ->
            if String.Equals(k, attr, StringComparison.OrdinalIgnoreCase) then
                match v with
                | value :: _ -> Some value
                | [] -> None
            else
                None)

    /// All values of a (case-insensitive) attribute; `[]` when absent.
    let values (attr: string) (entry: LdapEntry) : string list =
        entry.Attributes
        |> Map.tryPick (fun k v ->
            if String.Equals(k, attr, StringComparison.OrdinalIgnoreCase) then
                Some v
            else
                None)
        |> Option.defaultValue []

/// A live, service-bound connection. Disposed by the caller after the
/// searches for one authentication attempt complete.
type ILdapConnection =
    inherit IDisposable
    /// Run a search under the service-bound identity.
    abstract Search: search: LdapSearch -> Async<Result<LdapEntry list, string>>

/// Opens directory connections. The real implementation wraps
/// `System.DirectoryServices.Protocols`; tests provide an in-memory
/// fake. A *factory* rather than a single connection because verifying a
/// user's password requires a *separate* bind as that user's DN — a
/// connection's bound identity cannot be swapped mid-flight without
/// re-binding, and mixing the service search and the user-password bind
/// on one connection risks the search running under the wrong identity.
type ILdapConnectionFactory =
    /// Open a connection bound as the service account (or anonymous when
    /// no service bind DN is configured), for the user search.
    abstract OpenServiceBound: unit -> Async<Result<ILdapConnection, string>>

    /// Attempt a bind as `dn` with `password`. `Ok true` ⇒ the
    /// credentials are valid; `Ok false` ⇒ invalid credentials (the
    /// directory rejected the bind, LDAP result 49); `Error msg` ⇒ a
    /// transport / TLS fault (fails closed — never admits on error).
    abstract VerifyCredentials: dn: string * password: string -> Async<Result<bool, string>>

// ─── RFC 4515 filter-value escaping ──────────────────────────────────
//
// The presented username flows into the search filter. Without
// escaping, a value like `*)(uid=admin)` is an LDAP-injection vector —
// it rewrites the filter. Escape the metacharacters per RFC 4515 §3
// (`* ( ) \` plus the NUL byte) so the value is matched literally.
// Space is a legal filter-value character and is left as-is.

/// Escape a value for safe interpolation into an LDAP search filter.
let escapeFilterValue (value: string) : string =
    let sb = StringBuilder(value.Length + 8)

    for ch in value do
        match ch with
        | '*' -> sb.Append "\\2a" |> ignore
        | '(' -> sb.Append "\\28" |> ignore
        | ')' -> sb.Append "\\29" |> ignore
        | '\\' -> sb.Append "\\5c" |> ignore
        | '\u0000' -> sb.Append "\\00" |> ignore
        | c -> sb.Append c |> ignore

    sb.ToString()

/// The AD in-chain matching-rule OID — matches an attribute against a
/// value transitively through nested group membership. Used to build
/// the nested-group filter `(member:1.2.840.113556.1.4.1941:=<userDn>)`.
[<Literal>]
let InChainMatchingRuleOid = "1.2.840.113556.1.4.1941"
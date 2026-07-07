// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.LdapGroupMapper

open System
open System.Text.Json

// ─── LDAP group → ToolUp role mapping ────────────────────────────────
//
// The directory answers "which groups is this user in"; the deployment
// decides "which ToolUp roles does each group grant". That policy lives
// in a JSON document conventionally stored under
// `_platform/auth/ldap.json`. This module owns its schema, its parser,
// and the pure role-resolution + nested-group-closure logic — all with
// no directory dependency, so the mapping policy is fully unit-tested.
//
// Example `ldap.json`:
//   {
//     "matchByCommonName": true,
//     "defaultRoles": ["member"],
//     "mappings": [
//       { "group": "ToolUp-Admins",   "roles": ["admin", "member"] },
//       { "group": "ToolUp-Analysts", "roles": ["analyst"] }
//     ]
//   }

/// One group→roles rule.
type GroupRoleMapping = {
    /// The group to match. Interpreted as a common-name (CN) when
    /// `MatchByCommonName` is set, else as a full DN. Matched
    /// case-insensitively.
    Group: string
    /// ToolUp roles granted to a member of `Group`.
    Roles: string list
}

/// The whole mapping policy.
type GroupRoleMap = {
    Mappings: GroupRoleMapping list
    /// Roles granted to every authenticated user regardless of group
    /// (e.g. a baseline `member`). Empty by default.
    DefaultRoles: string list
    /// Match `Group` values against a group's CN (friendlier, the
    /// default) rather than its full DN (exact).
    MatchByCommonName: bool
}

module GroupRoleMap =
    /// The empty policy — no mappings, no default roles, CN matching.
    /// A user in no mapped group resolves to no roles (the SDK's own
    /// team/permission model then governs authorisation).
    let empty = {
        Mappings = []
        DefaultRoles = []
        MatchByCommonName = true
    }

    // ─── DN / CN helpers ─────────────────────────────────────────────

    /// Extract the left-most RDN value from a DN. `CN=Admins,OU=Groups,
    /// DC=x` → `Admins`. Returns the input unchanged when it carries no
    /// `attr=value` head (already a bare CN). Tolerates a leading
    /// `cn=` / `CN=` / any attribute type.
    let commonNameOf (dn: string) : string =
        if String.IsNullOrWhiteSpace dn then
            ""
        else
            let head = dn.Split(',').[0].Trim()

            match head.IndexOf '=' with
            | i when i >= 0 -> head.Substring(i + 1).Trim()
            | _ -> head

    // ─── Nested-group transitive closure ─────────────────────────────
    //
    // On a directory without the AD in-chain matching rule, nested
    // membership is resolved client-side: starting from the user's
    // direct groups, walk each group's own parent groups until the set
    // stops growing. `parentsOf` returns the immediate parent-group DNs
    // of a group DN (e.g. from a `memberOf` read on the group entry).
    // Cycle-safe: a group already seen is never re-expanded.

    /// Transitive closure of group membership. `directGroups` are the
    /// user's immediate groups; `parentsOf` yields a group's immediate
    /// parents. Returns the full membership set (direct + all
    /// ancestors), de-duplicated case-insensitively, order-stable on
    /// first appearance.
    let expandNested (parentsOf: string -> string list) (directGroups: string list) : string list =
        let seen =
            System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

        let ordered = ResizeArray<string>()
        let queue = System.Collections.Generic.Queue<string>()

        for g in directGroups do
            if not (String.IsNullOrWhiteSpace g) && seen.Add g then
                ordered.Add g
                queue.Enqueue g

        while queue.Count > 0 do
            let g = queue.Dequeue()

            for parent in parentsOf g do
                if not (String.IsNullOrWhiteSpace parent) && seen.Add parent then
                    ordered.Add parent
                    queue.Enqueue parent

        List.ofSeq ordered

    // ─── Role resolution ─────────────────────────────────────────────

    /// Resolve the ToolUp roles for a user given the full set of group
    /// identifiers they belong to (already nested-expanded upstream).
    /// De-duplicated, order-stable (default roles first, then mapping
    /// order). Matching honours `MatchByCommonName`.
    let resolveRoles (map: GroupRoleMap) (userGroups: string list) : string list =
        let normalisedUserGroups =
            userGroups
            |> List.map (fun g -> if map.MatchByCommonName then commonNameOf g else g)

        let matches (ruleGroup: string) : bool =
            normalisedUserGroups
            |> List.exists (fun ug -> String.Equals(ug, ruleGroup, StringComparison.OrdinalIgnoreCase))

        let mapped =
            map.Mappings
            |> List.filter (fun rule -> matches rule.Group)
            |> List.collect _.Roles

        let ordered = ResizeArray<string>()

        let seen =
            System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for role in List.append map.DefaultRoles mapped do
            if not (String.IsNullOrWhiteSpace role) && seen.Add role then
                ordered.Add role

        List.ofSeq ordered

    // ─── JSON parsing ────────────────────────────────────────────────

    /// Parse an `ldap.json` mapping document. Lenient on shape (missing
    /// `mappings` / `defaultRoles` ⇒ empty; missing `matchByCommonName`
    /// ⇒ `true`) but strict on validity — malformed JSON returns
    /// `Error`, never a silently-empty map that would drop every role.
    let parse (json: string) : Result<GroupRoleMap, string> =
        if String.IsNullOrWhiteSpace json then
            Ok empty
        else
            try
                use doc = JsonDocument.Parse json
                let root = doc.RootElement

                let readStringList (el: JsonElement) : string list =
                    if el.ValueKind = JsonValueKind.Array then
                        [
                            for item in el.EnumerateArray() do
                                if item.ValueKind = JsonValueKind.String then
                                    let s = item.GetString()

                                    if not (String.IsNullOrWhiteSpace s) then
                                        yield s.Trim()
                        ]
                    else
                        []

                let defaultRoles =
                    match root.TryGetProperty "defaultRoles" with
                    | true, el -> readStringList el
                    | _ -> []

                let matchByCommonName =
                    match root.TryGetProperty "matchByCommonName" with
                    | true, el when el.ValueKind = JsonValueKind.False -> false
                    | _ -> true

                let mappings =
                    match root.TryGetProperty "mappings" with
                    | true, el when el.ValueKind = JsonValueKind.Array -> [
                        for m in el.EnumerateArray() do
                            match m.TryGetProperty "group" with
                            | true, g when g.ValueKind = JsonValueKind.String ->
                                let group = g.GetString()

                                if not (String.IsNullOrWhiteSpace group) then
                                    let roles =
                                        match m.TryGetProperty "roles" with
                                        | true, r -> readStringList r
                                        | _ -> []

                                    yield { Group = group.Trim(); Roles = roles }
                            | _ -> ()
                      ]
                    | _ -> []

                Ok {
                    Mappings = mappings
                    DefaultRoles = defaultRoles
                    MatchByCommonName = matchByCommonName
                }
            with ex ->
                Error(sprintf "ldap.json is not valid JSON: %s" ex.Message)
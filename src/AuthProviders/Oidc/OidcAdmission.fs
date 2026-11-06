// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.OidcAdmission

open System
open ToolUp.Platform
open ToolUp.Platform.Config

// ─── OIDC admission layering ────────────────────────────
//
// The environment-driven provider reads the claim names and the API-role grants
// itself. It reads no setting for who is admitted: the tenants, the member rule,
// the group aliases and the publication readers are the application's to declare.
// `layered` reads them from the settings the application names and lays them onto
// the configuration the provider receives.

/// The names of the settings an application's admission is read from. A list
/// setting is comma-separated.
type AdmissionSettings = {
    /// The setting listing the tenants admitted.
    AllowedTenants: string
    /// The setting listing the roles that make a principal an app user: the
    /// member rule.
    RequiredRoles: string
    /// The setting listing `groupObjectId=roleName` aliases.
    GroupAliases: string
    /// Each publication-reader role, and the setting listing the directory groups
    /// that hold it. Such a setting must be set.
    PublicationReaders: (string * string) list
    /// Settings the application's publications need, whatever their values, such
    /// as a publishing scope. Each must be set: unset reads as a publication
    /// nobody may read, which is closed and also silent.
    RequiredSettings: string list
}

module AdmissionSettings =
    /// No settings named: `layered` leaves the configuration as it is.
    let none: AdmissionSettings = {
        AllowedTenants = ""
        RequiredRoles = ""
        GroupAliases = ""
        PublicationReaders = []
        RequiredSettings = []
    }

let private listFrom (setting: string -> string option) (name: string) : string list =
    match (if String.IsNullOrEmpty name then None else setting name) with
    | None -> []
    | Some raw when String.IsNullOrWhiteSpace raw -> []
    | Some raw ->
        let entries = raw.Split ',' |> Array.map _.Trim() |> List.ofArray

        if entries |> List.exists String.IsNullOrEmpty then
            invalidOp (sprintf "%s has an empty entry; name each value once, comma-separated" name)

        entries

/// `layered` over `setting`, a setting's name to its value. Raises
/// `InvalidOperationException` when:
///
/// - a setting names a publication reader's groups or is required, and is unset;
/// - an alias is not `groupObjectId=roleName`;
/// - a directory group is aliased to more than one role, which would make what a
///   group grants depend on the order the aliases were read in;
/// - there are publication readers and no member rule (rule AZ-11:
///   `ClaimMapping.validatePublicationReaders`), for then every principal the
///   issuer admits is an app user and no reader can be confined.
///
/// A value the settings leave empty keeps what the configuration already holds, and
/// with nothing set the configuration comes back as it went in.
let layeredFrom (setting: string -> string option) (settings: AdmissionSettings) (config: AuthConfig) : AuthConfig =
    let isSet name =
        setting name |> Option.exists (String.IsNullOrWhiteSpace >> not)

    for name in settings.RequiredSettings do
        if not (isSet name) then
            invalidOp (sprintf "%s is not set; the application's publications need it" name)

    let readerGroups =
        settings.PublicationReaders
        |> List.collect (fun (role, name) ->
            match listFrom setting name with
            | [] -> invalidOp (sprintf "%s is not set; it names the groups holding the reader role '%s'" name role)
            | groups -> groups |> List.map (fun group -> group, role))

    let aliases =
        (listFrom setting settings.GroupAliases
         |> List.map (fun pair ->
             match pair.Split '=' |> Array.map _.Trim() with
             | [| group; role |] when group <> "" && role <> "" -> group, role
             | _ -> invalidOp (sprintf "%s entry '%s' is not groupObjectId=roleName" settings.GroupAliases pair)))
        @ readerGroups

    let allowedTenants = listFrom setting settings.AllowedTenants
    let requiredRoles = listFrom setting settings.RequiredRoles
    let readers = settings.PublicationReaders |> List.map fst

    if
        aliases.IsEmpty
        && allowedTenants.IsEmpty
        && requiredRoles.IsEmpty
        && readers.IsEmpty
    then
        config
    else
        let mapping = config.ClaimMapping |> Option.defaultValue ClaimMapping.none

        let allAliases = (Map.toList mapping.GroupAliases) @ aliases

        for group, roles in allAliases |> List.groupBy fst do
            if (roles |> List.map snd |> List.distinct |> List.length) > 1 then
                invalidOp (sprintf "directory group '%s' is aliased to more than one role" group)

        let keepUnlessSet (held: 'a list) (declared: 'a list) =
            if declared.IsEmpty then held else declared

        let layered = {
            mapping with
                GroupAliases = Map.ofList allAliases
                AllowedTenants = keepUnlessSet mapping.AllowedTenants allowedTenants
                RequiredRoles = keepUnlessSet mapping.RequiredRoles requiredRoles
                PublicationReaders = keepUnlessSet mapping.PublicationReaders readers
        }

        match ClaimMapping.validatePublicationReaders layered with
        | Ok() -> ()
        | Error reason -> invalidOp ("OIDC admission: " + reason)

        {
            config with
                ClaimMapping = Some layered
        }

/// Read admission from the named settings, through the platform's resolution seam,
/// and lay it onto `config`'s claim mapping. Pass it as the provider's configuration
/// step: `AuthProvider.fromEnv logger (fun l config -> OidcAuthProvider.fromConfig l
/// (OidcAdmission.layered settings config))`. Refusals: `layeredFrom`.
let layered (settings: AdmissionSettings) (config: AuthConfig) : AuthConfig =
    layeredFrom ConfigResolution.tryValue settings config
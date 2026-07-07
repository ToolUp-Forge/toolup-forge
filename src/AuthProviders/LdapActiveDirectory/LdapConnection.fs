// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.LdapConnection

open System
open System.Net
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open System.DirectoryServices.Protocols
open ToolUp.AuthProviders.LdapConfig
open ToolUp.AuthProviders.LdapDirectory

// ─── Real directory adapter (System.DirectoryServices.Protocols) ─────
//
// The ONLY file in the companion that touches
// `System.DirectoryServices.Protocols` — the seam
// (`ILdapConnectionFactory` / `ILdapConnection`) keeps every other file
// (provider, health check, validator, group mapper) free of it and
// unit-testable against an in-memory fake (GP 1 — the vendor / native
// dependency never leaks past this adapter). S.DS.P P/Invokes into the
// OS LDAP client (`wldap32` on Windows, OpenLDAP `libldap` on Linux /
// macOS), so this is a native-dependency companion in the sense of the
// authoring guide: the interop surface is narrow and localised here.
//
// The S.DS.P API is synchronous; each blocking call is offloaded to the
// thread pool via `Task.Run` so the async boundary the seam promises is
// honoured and the caller's scheduler thread is never blocked.

// ─── Certificate validation ──────────────────────────────────────────

let private verifyServerCertificate (validation: LdapCertificateValidation) (certificate: X509Certificate) : bool =
    match validation with
    | AllowUntrusted -> true
    | Strict pinnedThumbprint ->
        use cert2 = new X509Certificate2(certificate)

        match pinnedThumbprint with
        | Some pinned ->
            // Exact pin — ignore chain trust, compare the presented
            // cert's thumbprint to the operator-pinned value. Tolerate
            // the colon-separated display form and surrounding space.
            let normalised = pinned.Replace(":", "").Replace(" ", "").Trim()
            String.Equals(cert2.Thumbprint, normalised, StringComparison.OrdinalIgnoreCase)
        | None ->
            // Default chain validation against the system trust store.
            use chain = new X509Chain()
            chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
            chain.Build cert2

// ─── SearchResultEntry → LdapEntry ───────────────────────────────────

/// Attributes whose raw octet-string value is not printable text and
/// must be rendered to a canonical string form.
let private renderAttribute (name: string) (attr: DirectoryAttribute) : string list =
    let isGuid = String.Equals(name, "objectGUID", StringComparison.OrdinalIgnoreCase)
    let isSid = String.Equals(name, "objectSid", StringComparison.OrdinalIgnoreCase)

    if isGuid || isSid then
        [
            for raw in attr.GetValues(typeof<byte[]>) do
                match raw with
                | :? (byte[]) as bytes when bytes.Length > 0 ->
                    if isGuid && bytes.Length = 16 then
                        yield (Guid bytes).ToString()
                    elif isSid then
                        yield (System.Security.Principal.SecurityIdentifier(bytes, 0)).ToString()
                    else
                        yield Convert.ToHexString bytes
                | _ -> ()
        ]
    else
        [
            for raw in attr.GetValues(typeof<string>) do
                match raw with
                | :? string as s -> yield s
                | _ -> ()
        ]

let private toLdapEntry (entry: SearchResultEntry) : LdapEntry =
    let attributes =
        [
            for name in Seq.cast<string> entry.Attributes.AttributeNames do
                let attr = entry.Attributes.[name]
                name, renderAttribute name attr
        ]
        |> Map.ofList

    {
        Dn = entry.DistinguishedName
        Attributes = attributes
    }

let private toSearchScope =
    function
    | BaseObject -> SearchScope.Base
    | OneLevel -> SearchScope.OneLevel
    | Subtree -> SearchScope.Subtree

// ─── Connection construction ─────────────────────────────────────────

let private configureConnection (config: LdapConfig) : LdapConnection =
    let identifier = LdapDirectoryIdentifier(config.Host, config.Port)
    let connection = new LdapConnection(identifier)
    connection.SessionOptions.ProtocolVersion <- 3
    connection.Timeout <- TimeSpan.FromSeconds(float config.TimeoutSeconds)
    // Simple (Basic) bind — the credential is a DN + password. LDAPS /
    // StartTLS protects it on the wire; a Plaintext binding is only
    // reachable behind the explicit opt-in enforced at provider
    // construction.
    connection.AuthType <- AuthType.Basic

    match config.ChannelBinding with
    | Ldaps ->
        connection.SessionOptions.SecureSocketLayer <- true

        connection.SessionOptions.VerifyServerCertificate <-
            VerifyServerCertificateCallback(fun _ cert -> verifyServerCertificate config.CertificateValidation cert)
    | StartTls ->
        connection.SessionOptions.VerifyServerCertificate <-
            VerifyServerCertificateCallback(fun _ cert -> verifyServerCertificate config.CertificateValidation cert)

        connection.SessionOptions.StartTransportLayerSecurity(DirectoryControlCollection())
    | Plaintext -> ()

    connection

/// One service-bound connection wrapping an S.DS.P `LdapConnection`.
type private RealConnection(config: LdapConfig, connection: LdapConnection) =
    interface ILdapConnection with
        member _.Search(search: LdapSearch) = async {
            try
                let! result =
                    Task.Run(fun () ->
                        let request =
                            SearchRequest(
                                search.BaseDn,
                                search.Filter,
                                toSearchScope search.Scope,
                                (search.Attributes |> List.toArray)
                            )

                        if search.SizeLimit > 0 then
                            request.SizeLimit <- search.SizeLimit

                        let response = connection.SendRequest request :?> SearchResponse

                        [ for e in response.Entries -> toLdapEntry e ])
                    |> Async.AwaitTask

                return Ok result
            with
            | :? LdapException as ex -> return Error(sprintf "LDAP search failed: %s" ex.Message)
            | ex -> return Error(sprintf "LDAP search failed: %s" ex.Message)
        }

    interface IDisposable with
        member _.Dispose() = connection.Dispose()

/// The production `ILdapConnectionFactory` over a config + a
/// service-account bind-password *resolver*. The resolver is called
/// afresh on every service bind (it reads `ISecretStore`), so a rotated
/// bind password flows through without a recompose — the same
/// read-on-every-use posture the audit-sink companions use for their
/// tokens.
type RealLdapConnectionFactory(config: LdapConfig, resolvePassword: unit -> Async<string option>) =
    interface ILdapConnectionFactory with
        member _.OpenServiceBound() = async {
            let! servicePassword =
                if String.IsNullOrWhiteSpace config.ServiceBindDn then
                    async { return None }
                else
                    resolvePassword ()

            try
                let! connection =
                    Task.Run(fun () ->
                        let connection = configureConnection config

                        // Anonymous search bind when no service DN is
                        // configured; otherwise bind as the service
                        // account. An empty service password with a
                        // non-empty DN is refused — an unauthenticated
                        // bind (DN + empty password) succeeds
                        // anonymously on many servers and would run the
                        // search unprivileged.
                        if String.IsNullOrWhiteSpace config.ServiceBindDn then
                            connection.Bind()
                        else
                            let password = servicePassword |> Option.defaultValue ""

                            if password = "" then
                                connection.Dispose()

                                failwith
                                    "LDAP service bind DN is configured but no bind password was resolved from the secret store"

                            connection.Bind(NetworkCredential(config.ServiceBindDn, password))

                        connection)
                    |> Async.AwaitTask

                return Ok(new RealConnection(config, connection) :> ILdapConnection)
            with
            | :? LdapException as ex -> return Error(sprintf "LDAP service bind failed: %s" ex.Message)
            | ex -> return Error(sprintf "LDAP service bind failed: %s" ex.Message)
        }

        member _.VerifyCredentials(dn: string, password: string) = async {
            // A fresh, short-lived connection bound as the user — the
            // authoritative proof the password is correct. Never
            // reuse the service connection: its bound identity would
            // have to be swapped, and a failed re-bind can leave it
            // in an ambiguous state.
            try
                let! outcome =
                    Task.Run(fun () ->
                        use connection = configureConnection config

                        try
                            connection.Bind(NetworkCredential(dn, password))
                            Ok true
                        with :? LdapException as ex ->
                            // 49 = invalidCredentials — a definitive
                            // "wrong password", not a transport fault.
                            if ex.ErrorCode = 49 then Ok false else Error ex.Message)
                    |> Async.AwaitTask

                return
                    match outcome with
                    | Ok verified -> Ok verified
                    | Error msg -> Error(sprintf "LDAP user bind failed: %s" msg)
            with ex ->
                return Error(sprintf "LDAP user bind failed: %s" ex.Message)
        }

/// Build the production factory for a config + a bind-password resolver
/// (called on every service bind so a rotated password flows through).
let create (config: LdapConfig) (resolvePassword: unit -> Async<string option>) : ILdapConnectionFactory =
    RealLdapConnectionFactory(config, resolvePassword) :> ILdapConnectionFactory
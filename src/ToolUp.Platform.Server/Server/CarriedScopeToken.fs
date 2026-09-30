// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.DataProtection.KeyManagement
open Microsoft.AspNetCore.DataProtection.Repositories
open Microsoft.Extensions.DependencyInjection

// ─── Phase 935 — a platform-owned scope carrier ──────────────────────
//
// A `ResolvedScope` is a process value (Phase 797): its representation is
// private and its mints are internal to the platform's server tier. Two
// paths need a resolved scope to outlive the request that resolved it — a
// job run later, possibly in another process, and a consent recorded now
// and acted on after a restart. Both need to WRITE the scope down as a
// string that any store can hold, and turn it back into the same scope
// later. This module is the one way to do that.
//
// **The token.** `ScopeCarrier.Issue` takes a `ResolvedScope` — never a
// string — and a purpose, and seals the resolver's whole `StorageScope`
// (scope id, container, persistence) together with that purpose under the
// deployment's DataProtection key ring. `Redeem` unseals it, checks the
// purpose it was issued for against the purpose it is presented for, and
// re-mints through the internal `ResolvedScope.ofCarried`. A token it did
// not issue, a token altered by one byte and a token presented for other
// work are each a typed refusal, and a caller that wants a scope either way
// takes the anonymous scope — never a guess, never a widening.
//
// **The key material.** The deployment's DataProtection key ring over
// `IBlobStorage` (`BlobXmlRepository`, Phase 9j) — the ring the CSRF token
// already relies on. It survives a restart and is shared by every replica,
// which are exactly the two properties a carried scope needs. The carrier
// opens its own key manager over that same repository rather than the
// request pipeline's instance, because the job scheduler is built before
// the service provider exists; two managers over one ring are what two
// replicas already are. The carrier's protector purpose isolates its
// tokens from every other payload the ring seals.
//
// **Threat model.**
//
//   * FORGING a token requires the platform's key material: the key ring's
//     XML in the platform-reserved container of the blob store. A party
//     holding that can mint any token and is inside the trust boundary by
//     definition; it can equally read and write every scope's data.
//   * An IN-PROCESS CALLER holding the DI container can already do
//     everything a token would let it do. It can resolve the stores and
//     call their string members, and nothing here pretends otherwise. What
//     it cannot do without the container is construct a carrier over key
//     material of its own choosing: the constructors that take key material
//     are internal, and the one public constructor (`ephemeral`) generates
//     keys it never exposes, so a caller can only redeem what it issued
//     from a scope it already held.
//   * A party that can WRITE ONLY THE JOB STORE OR A GRANT STORE can do
//     nothing that widens a scope. It can delete a token (the work runs
//     anonymous, or the grant loses the consent the token carried — a fail
//     closed), corrupt one (the same), or copy a genuine token onto other
//     work — which the purpose binding refuses, because a token names the
//     job or grant it was issued for. It cannot write a token for a scope,
//     container or persistence the resolver did not produce. The records
//     around the token (a job's payload, a grant's own fields) are that
//     store's to keep; the token speaks only for the scope.
//
// Which paths remain carried rather than resolved is stated in the proofs
// residual (`model-input-scope-resolution`): a scheduled job's run and a
// restored publication consent re-mint through this carrier; everything
// else resolves per request.

/// What a carried-scope token was issued for (Phase 935). A token redeems
/// only for the purpose it was sealed with, so a genuine token copied onto
/// other work re-mints nothing.
[<RequireQualifiedAccess>]
type CarriedScopePurpose =
    /// A job the scheduler persisted, named by its scope id and job id.
    | ScheduledJob of scopeId: string * jobId: Guid
    /// A persisted grant: `kind` is the owning service's namespace for the
    /// grant and the party it speaks for, `grantId` the grant's identity.
    | Grant of kind: string * grantId: string

/// Rendering for `CarriedScopePurpose`.
[<RequireQualifiedAccess>]
module CarriedScopePurpose =

    /// The purpose's fields in a fixed order — the form it is sealed and
    /// compared in. A list rather than a joined string, so no field value
    /// can be read as a separator.
    let fields (purpose: CarriedScopePurpose) : string list =
        match purpose with
        | CarriedScopePurpose.ScheduledJob(scopeId, jobId) -> [ "job"; scopeId; jobId.ToString "N" ]
        | CarriedScopePurpose.Grant(kind, grantId) -> [ "grant"; kind; grantId ]

    /// A readable one-line form, for refusals and logs.
    let describe (purpose: CarriedScopePurpose) : string =
        fields purpose |> List.map (sprintf "'%s'") |> String.concat " / "

/// Why a carried-scope token re-minted nothing (Phase 935).
[<RequireQualifiedAccess>]
type CarriedScopeRefusal =
    /// The token is empty, or unsealed to something this carrier never
    /// writes.
    | TokenMalformed of reason: string
    /// The token did not unseal under this deployment's key ring: it was
    /// forged, altered, or sealed under a key the ring does not hold.
    | TokenNotIssuedHere
    /// The token is genuine but was issued for other work.
    | PurposeMismatch of issuedFor: string * presentedFor: string

/// Rendering for `CarriedScopeRefusal`.
[<RequireQualifiedAccess>]
module CarriedScopeRefusal =

    /// A sentence naming why the token re-minted nothing.
    let describe (refusal: CarriedScopeRefusal) : string =
        match refusal with
        | CarriedScopeRefusal.TokenMalformed reason -> sprintf "the carried-scope token is malformed: %s" reason
        | CarriedScopeRefusal.TokenNotIssuedHere ->
            "the carried-scope token was not issued by this deployment's key ring, or was altered after it was"
        | CarriedScopeRefusal.PurposeMismatch(issuedFor, presentedFor) ->
            sprintf
                "the carried-scope token was issued for %s and presented for %s; a token carries a scope for the work it was issued for only"
                issuedFor
                presentedFor

/// The platform's scope carrier (Phase 935): issues an opaque token for a
/// resolved scope and a purpose, and redeems a token back into that scope.
/// Constructed only by the platform — over the deployment's key ring when
/// composed, or over keys of its own (`ScopeCarrier.ephemeral`) — so no
/// caller can redeem a token sealed under key material it chose.
[<Sealed>]
type ScopeCarrier internal (protector: Lazy<IDataProtector>) =

    static let version = 1

    static let seal (storage: StorageScope) (purpose: CarriedScopePurpose) : string =
        use buffer = new MemoryStream()

        do
            use writer = new Utf8JsonWriter(buffer)
            writer.WriteStartObject()
            writer.WriteNumber("v", version)
            writer.WriteStartArray "purpose"

            for field in CarriedScopePurpose.fields purpose do
                writer.WriteStringValue field

            writer.WriteEndArray()
            writer.WriteStartObject "scope"
            writer.WriteString("id", storage.ScopeId)
            writer.WriteString("container", storage.Container)
            writer.WriteBoolean("persist", storage.Persist)
            writer.WriteEndObject()
            writer.WriteEndObject()

        Encoding.UTF8.GetString(buffer.ToArray())

    static let unseal (payload: string) : Result<string list * StorageScope, CarriedScopeRefusal> =
        try
            use document = JsonDocument.Parse payload
            let root = document.RootElement

            if root.GetProperty("v").GetInt32() <> version then
                Error(CarriedScopeRefusal.TokenMalformed "an unknown token version")
            else
                let purpose =
                    root.GetProperty("purpose").EnumerateArray()
                    |> Seq.map (fun e -> e.GetString())
                    |> List.ofSeq

                let scope = root.GetProperty "scope"

                Ok(
                    purpose,
                    {
                        ScopeId = scope.GetProperty("id").GetString()
                        Container = scope.GetProperty("container").GetString()
                        Persist = scope.GetProperty("persist").GetBoolean()
                    }
                )
        with ex ->
            Error(CarriedScopeRefusal.TokenMalformed ex.Message)

    /// Issue a token carrying `scope` for `purpose`. `Ok None` for the
    /// anonymous scope, which is never carried: work scheduled or consent
    /// given under it stays anonymous. `Error` when the key ring could not
    /// seal — the caller refuses the work rather than dropping its scope.
    member _.Issue(scope: ResolvedScope, purpose: CarriedScopePurpose) : Result<string option, string> =
        match scope.Storage with
        | None -> Ok None
        | Some storage ->
            try
                Ok(Some(protector.Value.Protect(seal storage purpose)))
            with ex ->
                Error(sprintf "the scope carrier could not seal a token: %s" ex.Message)

    /// Unseal a token presented for `purpose`: the resolver's scope it was
    /// issued with, or a typed refusal.
    member private _.Unseal(token: string, purpose: CarriedScopePurpose) : Result<StorageScope, CarriedScopeRefusal> =
        if String.IsNullOrWhiteSpace token then
            Error(CarriedScopeRefusal.TokenMalformed "the token is empty")
        else
            let unprotected =
                try
                    Ok(protector.Value.Unprotect token)
                with
                | :? CryptographicException
                | :? FormatException -> Error CarriedScopeRefusal.TokenNotIssuedHere

            match unprotected |> Result.bind unseal with
            | Error refusal -> Error refusal
            | Ok(sealedFor, storage) ->
                let presented = CarriedScopePurpose.fields purpose

                if sealedFor <> presented then
                    Error(
                        CarriedScopeRefusal.PurposeMismatch(
                            sealedFor |> List.map (sprintf "'%s'") |> String.concat " / ",
                            CarriedScopePurpose.describe purpose
                        )
                    )
                else
                    Ok storage

    /// Redeem a token presented for `purpose`: the scope it was issued for,
    /// re-minted, or a typed refusal naming why nothing was.
    member this.Redeem(token: string, purpose: CarriedScopePurpose) : Result<ResolvedScope, CarriedScopeRefusal> =
        this.Unseal(token, purpose) |> Result.map ResolvedScope.ofCarried

    /// Check a token presented for `purpose` WITHOUT re-minting: the scope
    /// id it carries, or a typed refusal. For a caller that must know a
    /// scope was resolved — a consent was given in it — but must not hold
    /// the scope itself.
    member this.Verify(token: string, purpose: CarriedScopePurpose) : Result<string, CarriedScopeRefusal> =
        this.Unseal(token, purpose) |> Result.map _.ScopeId

    /// `Redeem`, with every refusal read as the anonymous scope — the form
    /// a dispatch takes, where the work runs either way and must never run
    /// under a guess.
    member this.RedeemOrAnonymous(token: string option, purpose: CarriedScopePurpose) : ResolvedScope =
        match token with
        | None -> ResolvedScope.anonymous
        | Some token ->
            match this.Redeem(token, purpose) with
            | Ok scope -> scope
            | Error _ -> ResolvedScope.anonymous

/// Construction for `ScopeCarrier`.
[<RequireQualifiedAccess>]
module ScopeCarrier =

    /// The DataProtection application name the platform's key ring is
    /// shared under — the request pipeline's own.
    [<Literal>]
    let ApplicationName = "ToolUp.Platform"

    /// The protector purpose isolating carried-scope tokens from every
    /// other payload the ring seals.
    [<Literal>]
    let ProtectorPurpose = "ToolUp.Platform.CarriedScope.v1"

    /// A carrier over a DataProtection provider. Internal: a caller
    /// choosing the provider would choose the key material, and could then
    /// seal a scope the resolver never produced.
    let internal ofDataProtection (provider: IDataProtectionProvider) : ScopeCarrier =
        ScopeCarrier(lazy (provider.CreateProtector ProtectorPurpose))

    /// A carrier over the key ring `repository` persists — the deployment's
    /// ring when compose passes the `BlobXmlRepository` over its storage.
    /// The key manager is opened at first use, so building the carrier
    /// costs nothing at composition. Internal for the same reason as
    /// `ofDataProtection`.
    let internal ofKeyRepository (repository: IXmlRepository) : ScopeCarrier =
        ScopeCarrier(
            lazy
                (let services = ServiceCollection()
                 services.AddDataProtection().SetApplicationName ApplicationName |> ignore

                 services.Configure<KeyManagementOptions>(fun (o: KeyManagementOptions) ->
                     o.XmlRepository <- repository)
                 |> ignore

                 services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector
                     ProtectorPurpose)
        )

    /// A carrier whose keys are generated in memory and die with it: it
    /// redeems only what it issued, in this process. The default of a
    /// scheduler nothing has bound to the deployment's key ring, so a
    /// hand-constructed scheduler still re-mints within its own lifetime and
    /// runs a job anonymous after a restart — a fail closed.
    let ephemeral () : ScopeCarrier =
        ofDataProtection (EphemeralDataProtectionProvider())
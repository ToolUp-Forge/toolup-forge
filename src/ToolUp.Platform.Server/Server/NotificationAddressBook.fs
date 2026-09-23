module ToolUp.Platform.NotificationAddressBook

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── SDK-default implementations ─────────────────────────────────
//
// Phase 6f. Two implementations ship out-of-the-box:
//
//   * `NoOpNotificationAddressBook` — every lookup returns
//     `None` / `[]`. Registered as the default when no other impl is
//     supplied, so apps that haven't enabled transactional sinks pay
//     zero runtime cost. Sinks running against this fall back to
//     `SinkResult.Skipped "no_address_for_user"` per recipient,
//     which the dispatcher logs at Debug.
//
//   * `BlobBackedNotificationAddressBook` — looks up
//     `_platform/contacts/{scopeId}/{userId}.json` and round-trips
//     the persisted `UserContact` shape via `FableConverters`.
//     Apps populate the directory through their own user-management
//     flows (an admin UI, a CSV import tool, an authenticated
//     "save my email" flow); the SDK doesn't ship a write-side
//     surface in Phase 6f beyond this read-side default.
//
// Phase 6f.A widened both to `RecipientId`. The `User` arm is
// byte-for-byte what it was - same blob path, same shape, same failure
// semantics. The `External` arm is new, resolves through an
// optionally-composed `IExternalContactStore`, and is CONSENT-GATED: a
// contact with no live `OptInRecord` for the channel being resolved
// returns `None` even when its record holds a perfectly good address.
// That asymmetry is the whole point of the phase - an address is not
// permission to use it.
//
// Real production deployments swap in a directory-driven impl
// (LDAP, Okta, Azure AD) by registering a custom
// `INotificationAddressBook` against DI — same pattern as
// substituting `IBlobStorage` for cloud storage.

/// Reserved container name for SDK-platform blobs (membership data,
/// permissions, contact records). Same constant the rest of the SDK
/// reaches for via `BlobStorage` hard-coded paths; literalised here
/// to keep the file self-contained.
[<Literal>]
let private PlatformContainer = "_platform"

let private contactJsonOptions = FableConverters.create ()

/// Reserved blob name for a user's persisted contact record. The
/// container is `_platform`; this nested path keeps each scope's
/// contacts in their own logical folder so admin tooling (future) can
/// list-by-prefix.
let private contactBlobName (scopeId: string) (userId: string) = $"contacts/{scopeId}/{userId}.json"

/// `INotificationAddressBook` that knows about no contacts. The
/// SDK's safe default — registered automatically when the deployment
/// hasn't supplied a more useful implementation. Sinks running
/// against this `Skipped`-on-every-recipient; deployments that wired
/// up sinks but forgot the address book see `[Debug]` logs from the
/// dispatcher pointing at the gap.
type NoOpNotificationAddressBook() =
    interface INotificationAddressBook with
        member _.ResolveEmail(_, _) = async { return None }
        member _.ResolvePhone(_, _) = async { return None }
        member _.ResolvePushTokens(_, _) = async { return [] }
        member _.ResolveWhatsApp(_, _) = async { return None }

/// `INotificationAddressBook` backed by `IBlobStorage`. Reads
/// `_platform/contacts/{scopeId}/{userId}.json` per lookup. Missing
/// blobs return the empty contact (which surfaces as `None` /
/// `[]` from `Resolve*`); decode failures return the empty contact
/// AND log at `Warn` so a corrupt record doesn't masquerade as a
/// "no address" miss.
///
/// Stateless across calls (Phase 9c rule 4). A future caching layer
/// would wrap this rather than mutate it. Async at every boundary
/// (Phase 9c rule 2).
type BlobBackedNotificationAddressBook
    (storage: IBlobStorage, logger: ILogger option, externalContacts: IExternalContactStore option) =

    let logWarn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    let readContact (scopeId: string) (userId: string) : Async<UserContact> = async {
        let blobName = contactBlobName scopeId userId

        try
            let! payload = storage.Download(PlatformContainer, blobName)

            match payload with
            | Error _ ->
                // `Error` here covers both "not found" (the typical
                // case for a user with no registered contact) and
                // genuine storage failures. The default impl treats
                // them the same — empty contact, no exception. Real
                // deployments using a directory backend can
                // distinguish in their own implementation.
                return UserContact.empty userId
            | Ok bytes ->
                try
                    let json = Encoding.UTF8.GetString bytes
                    let contact = JsonSerializer.Deserialize<UserContact>(json, contactJsonOptions)

                    // Defensive null-check in case a corrupt JSON literal
                    // round-trips to a `null` reference (STJ will
                    // happily produce one for `"null"` payloads).
                    if isNull (box contact) then
                        return UserContact.empty userId
                    else
                        return contact
                with ex ->
                    logWarn
                        $"[NotificationAddressBook] decode failed scope=%s{scopeId} user=%s{userId}: %s{ex.GetType().Name}: %s{ex.Message}"

                    return UserContact.empty userId
        with ex ->
            logWarn
                $"[NotificationAddressBook] storage read failed scope=%s{scopeId} user=%s{userId}: %s{ex.GetType().Name}: %s{ex.Message}"

            return UserContact.empty userId
    }

    /// Read an external contact, if an external address book is
    /// composed at all. A deployment with `NoExternalContactStore`
    /// resolves every external recipient to `None` - the same answer a
    /// contact with no consent gets, and the correct one: with no store
    /// there is no consent record, and with no consent record there is
    /// no lawful basis.
    let readExternal (scopeId: string) (contactId: string) : Async<ExternalContact option> = async {
        match externalContacts with
        | None -> return None
        | Some store ->
            let! result = store.Get(scopeId, contactId)

            match result with
            | Ok contact -> return Some contact
            | Error ExternalContactError.NotFound -> return None
            | Error error ->
                logWarn
                    $"[NotificationAddressBook] external contact read failed scope=%s{scopeId} contact=%s{contactId}: %s{ExternalContactError.describe error}"

                return None
    }

    /// The consent gate. Yields the contact ONLY when it carries a live
    /// opt-in for `channel`; an expired opt-in reads exactly like an
    /// absent one.
    let readConsented
        (scopeId: string)
        (contactId: string)
        (channel: NotificationKind.SinkKind)
        : Async<ExternalContact option> =
        async {
            let! contact = readExternal scopeId contactId
            return contact |> Option.filter (ExternalContact.hasLiveOptIn DateTime.UtcNow channel)
        }

    /// The pre-Phase-6f.A shape: no external address book composed, so
    /// every `External` recipient resolves to nothing. An explicit
    /// secondary constructor rather than an optional parameter, because
    /// an optional parameter folds both forms into one widened
    /// constructor and the public-API approval gate reads that as the
    /// REMOVAL of the two-argument form.
    new(storage: IBlobStorage, logger: ILogger option) = BlobBackedNotificationAddressBook(storage, logger, None)

    interface INotificationAddressBook with
        member _.ResolveEmail(recipient, scopeId) = async {
            match recipient with
            | RecipientId.User userId ->
                let! contact = readContact scopeId userId
                return contact.Email
            | RecipientId.External contactId ->
                let! contact = readConsented scopeId contactId NotificationKind.SinkKind.Email

                let address =
                    contact
                    |> Option.bind _.OptionalEmailAddress
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)

                return
                    address
                    |> Option.map (fun address -> {
                        Address = address
                        DisplayName =
                            contact
                            |> Option.map _.DisplayName
                            |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    })
        }

        member _.ResolvePhone(recipient, scopeId) = async {
            match recipient with
            | RecipientId.User userId ->
                let! contact = readContact scopeId userId
                return contact.Phone
            | RecipientId.External contactId ->
                let! contact = readConsented scopeId contactId NotificationKind.SinkKind.Sms

                return
                    contact
                    |> Option.bind _.OptionalPhoneNumber
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.map (fun number -> { E164 = number })
        }

        member _.ResolvePushTokens(recipient, scopeId) = async {
            match recipient with
            | RecipientId.User userId ->
                let! contact = readContact scopeId userId
                return contact.PushTokens
            | RecipientId.External _ ->
                // An external contact has no device registration in the
                // shipped model: push is a channel you opt into from
                // inside an app you installed, and a recipient who never
                // installed the app has no token to hold. `[]` rather
                // than a raise keeps the sink's existing skip path the
                // one that handles it.
                return []
        }

        member _.ResolveWhatsApp(recipient, scopeId) = async {
            match recipient with
            | RecipientId.User _ ->
                // The persisted `UserContact` carries no WhatsApp
                // number (Phase 6f.B): a user recipient has nothing to
                // resolve until a user profile grows one. `None` is the
                // same skip every other no-address recipient takes.
                return None
            | RecipientId.External contactId ->
                let! contact = readConsented scopeId contactId NotificationKind.SinkKind.WhatsApp

                return
                    contact
                    |> Option.bind _.OptionalWhatsAppNumber
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
        }

/// Persist a `UserContact` for `(userId, scopeId)` so the blob-backed
/// address book picks it up on next lookup. Exposed as a helper
/// (rather than a write member on the interface) because Phase 6f
/// doesn't ship a ToolUp.Remoting admin surface — apps writing to the
/// address book do so server-side from their own user-profile flow.
/// Future phases may add an `IConfigStore`-style write API.
let saveContact (storage: IBlobStorage) (scopeId: string) (contact: UserContact) : Async<Result<unit, string>> = async {
    let json = JsonSerializer.Serialize(contact, contactJsonOptions)
    let bytes = Encoding.UTF8.GetBytes json
    let blobName = contactBlobName scopeId contact.UserId
    let! result = storage.Upload(PlatformContainer, blobName, bytes)

    return
        match result with
        | Ok _ -> Ok()
        | Error msg -> Error msg
}
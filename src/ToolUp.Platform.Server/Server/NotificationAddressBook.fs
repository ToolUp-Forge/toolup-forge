module ToolUp.Platform.NotificationAddressBook

open System.Text
open Newtonsoft.Json
open ToolUp.Remoting.Json
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
//     the persisted `UserContact` shape via `FableJsonConverter`.
//     Apps populate the directory through their own user-management
//     flows (an admin UI, a CSV import tool, an authenticated
//     "save my email" flow); the SDK doesn't ship a write-side
//     surface in Phase 6f beyond this read-side default.
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

let private contactJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

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
type BlobBackedNotificationAddressBook(storage: IBlobStorage, logger: ILogger option) =

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
                    let contact = JsonConvert.DeserializeObject<UserContact>(json, contactJsonSettings)

                    // Defensive null-check in case a corrupt JSON literal
                    // round-trips to a `null` reference (Newtonsoft will
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

    interface INotificationAddressBook with
        member _.ResolveEmail(userId, scopeId) = async {
            let! contact = readContact scopeId userId
            return contact.Email
        }

        member _.ResolvePhone(userId, scopeId) = async {
            let! contact = readContact scopeId userId
            return contact.Phone
        }

        member _.ResolvePushTokens(userId, scopeId) = async {
            let! contact = readContact scopeId userId
            return contact.PushTokens
        }

/// Persist a `UserContact` for `(userId, scopeId)` so the blob-backed
/// address book picks it up on next lookup. Exposed as a helper
/// (rather than a write member on the interface) because Phase 6f
/// doesn't ship a Fable.Remoting admin surface — apps writing to the
/// address book do so server-side from their own user-profile flow.
/// Future phases may add an `IConfigStore`-style write API.
let saveContact (storage: IBlobStorage) (scopeId: string) (contact: UserContact) : Async<Result<unit, string>> = async {
    let json = JsonConvert.SerializeObject(contact, contactJsonSettings)
    let bytes = Encoding.UTF8.GetBytes json
    let blobName = contactBlobName scopeId contact.UserId
    let! result = storage.Upload(PlatformContainer, blobName, bytes)

    return
        match result with
        | Ok _ -> Ok()
        | Error msg -> Error msg
}
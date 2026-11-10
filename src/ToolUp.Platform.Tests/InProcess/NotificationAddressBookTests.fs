module ToolUp.Platform.Tests.InProcess.NotificationAddressBookTests

open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Jobs
open ToolUp.Platform.Testing

/// Bind `INotificationAddressBookContract` to the SDK-default
/// `BlobBackedNotificationAddressBook` over a `LocalFileStorage`
/// rooted in a per-test-list temp directory. `populate` writes
/// through the same companion `saveContact` helper so the test
/// exercises the round-trip path the production code uses.
///
/// The contract pack's `factory ()` returns the same `book` instance
/// across assertions — tests use unique `scope-test-{guid}` ids so a
/// shared storage doesn't cross-pollute. This matches the
/// `INotificationChannelContract` binding style. The list has no teardown
/// hook, so the directory outlives it; the family attributes it.
let tests =
    let root = TempDir.create "tests-addrbook"

    let storage = LocalFileStorage.LocalFileStorage(root.Path) :> IBlobStorage

    let book =
        NotificationAddressBook.BlobBackedNotificationAddressBook(storage, None) :> INotificationAddressBook

    let populate (scopeId: string) (contact: UserContact) : Async<unit> = async {
        let! _ = NotificationAddressBook.saveContact storage scopeId contact
        return ()
    }

    INotificationAddressBookContract.tests "BlobBackedNotificationAddressBook" (fun () -> book) populate
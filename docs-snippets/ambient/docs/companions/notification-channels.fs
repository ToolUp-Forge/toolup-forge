// Ambient context for `docs/companions/notification-channels.md`.
//
// The page is a cross-cutting tour of the shipped notification
// companions, so nearly every block is an excerpt from a composition
// root it never shows in full: the deployment's `config`, the
// `addressBook` every transactional sink resolves recipients through,
// the `logger` each `create` takes as `ILogger option`, and the
// `secretStore` the API-keyed sinks read their vendor credential from.
// Declared here so the blocks compile exactly as a reader would copy
// them, with no `open`-ceremony added to the markdown.
//
// The page's own `open` lines stay in the markdown, because which
// package a companion lives in is precisely what this page teaches —
// and it is where the drift was: every one of them named
// `ToolUp.NotificationChannels.*` when the shipped modules are
// `ToolUp.Platform.NotificationChannels.*`.
open System.Net.Http
open ToolUp.Platform.Secrets

[<AutoOpen>]
module PageAmbient =

    // ─── The deployment's composition root ────────────────────────

    let config: ServerConfig = failwith "ambient"

    /// How a sink turns a `userId` into a vendor-neutral address. The
    /// first parameter of every shipped sink's `create`.
    let addressBook: INotificationAddressBook = failwith "ambient"

    /// Every sink's `create` ends in `ILogger option`; a deployment
    /// that wires no SDK logger passes `None`, which is what the page's
    /// blocks are showing.
    let logger: ILogger option = None

    /// Where an API-keyed sink (SendGrid / Twilio / WebPush) reads its
    /// vendor credential from, per call. Deliberately NOT taken by the
    /// SMTP sink — see the page's SMTP section.
    let secretStore: ISecretStore = failwith "ambient"

    // ─── The page's own hypothetical vendor sink ──────────────────

    /// "Writing a new sink" declares `module MyVendor.NotificationSink`
    /// — a file-level dotted module header, which a generated snippet
    /// file cannot carry (it already declares its own module), so that
    /// block stays `skip=fragment`. The "Wire" block right after it
    /// constructs the type that block would have produced, so the type
    /// is declared here. Page-local names throughout; the one SDK name
    /// involved is `INotificationSink` itself, which is implemented
    /// rather than mirrored — so its shape stays under the gate.
    type MyVendorSettings = {
        Endpoint: string
        FromAddress: string
    }

    type MyVendorEmailSink(settings: MyVendorSettings, secrets: ISecretStore, httpClient: HttpClient) =
        interface INotificationSink with
            member _.Kind = NotificationKind.SinkKind.Email
            member _.Provider = "MyVendor"
            member _.Send(_scopeId, _envelope) : Async<SinkResult> = failwith "ambient"

    /// The vendor settings and shared HTTP client the "Wire" block has
    /// in hand when it constructs the sink.
    let settings: MyVendorSettings = failwith "ambient"

    let httpClient: HttpClient = failwith "ambient"
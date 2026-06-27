module ToolUp.Platform.Tests.InProcess.SignedExportTests

open System.Collections.Concurrent
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.DataSubjectRequestApi
open ToolUp.Platform.DataSubjectRequestApiHandler

// ─── Phase 162 — signed compliance export bundle (server-side wiring) ───
//
// Covers the Platform.Server half of the signed-export feature: the
// fail-closed `SignedExportDepsValidator` preflight and the DSR handler's
// `DownloadSignedExport` (default-off behaviour + the signed path + the
// `ExportSigned` audit). The end-to-end "the JWS verifies with
// `DefaultArtefactVerifier`" round-trip lives in `ToolUp.ArtefactSigning.Tests`
// (Platform.Tests can't reference the signer/verifier — they sit a layer
// above; the bundle adapter that fills `IExportEnvelopeSigner` is there).

// ── fakes ───────────────────────────────────────────────────────────────

/// Fake export store: `Download` returns canned `Ready` bytes; the other
/// members are unused by `DownloadSignedExport` and stubbed minimally.
type private FakeExportStore(bytes: byte[]) =
    interface IBackgroundExportStore with
        member _.BeginExport(_, _) = async { return "ticket-1" }
        member _.GetStatus _ = async { return ExportStatus.Ready(int64 bytes.Length) }
        member _.Complete(_, _) = async { return () }
        member _.Fail(_, _) = async { return () }
        member _.Cancel _ = async { return () }
        member _.Download _ = async { return Result.Ok bytes }

/// Fake envelope signer: returns a canned detached-JWS signature triple.
type private FakeEnvelopeSigner() =
    interface IExportEnvelopeSigner with
        member _.SignEnvelope(_: byte[]) = async {
            return
                Result.Ok {
                    DetachedJws = "eyJhbGciOiJFUzI1NiJ9..AAAA"
                    SigningKeyId = "export-signing-v1"
                    SigningKeyUrl = "/_platform/signing-key/export-signing-v1"
                }
        }

let private admin: AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser "admin-user") with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

/// Recording audit callback so the `ExportSigned` emission can be asserted.
let private recordingAudit (sink: ConcurrentQueue<DsrAuditEvent>) : AuditOnDsr = fun ev -> async { sink.Enqueue ev }

let private mkApi (deps: DsrAsyncDeps option) (audit: AuditOnDsr) : IDataSubjectRequestApi =
    DataSubjectRequestApiHandler.create [] [] ErasurePolicy.Tombstone "team-x" admin.UserId admin audit deps

let private deps (signer: IExportEnvelopeSigner option) : DsrAsyncDeps = {
    Store = FakeExportStore("envelope-bytes"B)
    // DownloadSignedExport never touches the scheduler / notify path.
    Scheduler = Unchecked.defaultof<IJobScheduler>
    Notify = fun _ _ -> async { return () }
    Signer = signer
}

// ── validator ─────────────────────────────────────────────────────────────

let private cfg (dsr: DataSubjectRequestMode) = {
    ServerConfig.defaults with
        DataSubjectRequests = dsr
}

let private validate (config: ServerConfig) (services: IServiceCollection) : ValidationResult =
    let v =
        SignedExportDepsValidator.SignedExportDepsValidator(config, services) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private emptyServices () =
    ServiceCollection() :> IServiceCollection

let private servicesWithSigner () =
    let s = ServiceCollection()
    s.AddSingleton<IExportEnvelopeSigner>(FakeEnvelopeSigner()) |> ignore
    s :> IServiceCollection

[<Tests>]
let tests =
    testList "Phase 162 — signed export (server wiring)" [

        // ── fail-closed preflight ───────────────────────────────────────
        test "SignExports=true + no signer composed → Error (fail closed)" {
            let config =
                cfg (DataSubjectRequestMode.Enabled(DataSubjectRequestConfig.signedBackground ErasurePolicy.Tombstone))

            match validate config (emptyServices ()) with
            | Error msg ->
                Expect.stringContains msg "SignExports = true" "names the setting"
                Expect.stringContains msg "IExportEnvelopeSigner" "names the missing seam"
            | other -> failtestf "expected Error, got %A" other
        }

        test "SignExports=true + signer registered → Ok" {
            let config =
                cfg (DataSubjectRequestMode.Enabled(DataSubjectRequestConfig.signedBackground ErasurePolicy.Tombstone))

            Expect.equal (validate config (servicesWithSigner ())) Ok "a composed signer satisfies the gate"
        }

        test "SignExports=false (background) → Ok (no signer required)" {
            let config =
                cfg (DataSubjectRequestMode.Enabled(DataSubjectRequestConfig.background ErasurePolicy.Tombstone))

            Expect.equal (validate config (emptyServices ())) Ok "unsigned background export needs no signer"
        }

        test "DSR disabled → Ok" {
            Expect.equal (validate (cfg DataSubjectRequestMode.Disabled) (emptyServices ())) Ok "disabled DSR passes"
        }

        test "Validator metadata is well-formed" {
            let v =
                SignedExportDepsValidator.SignedExportDepsValidator(
                    cfg DataSubjectRequestMode.Disabled,
                    emptyServices ()
                )
                :> IConfigValidator

            Expect.equal v.Name "signed-export-deps" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }

        // ── DownloadSignedExport handler ────────────────────────────────
        testCaseAsync "DownloadSignedExport with no signer composed → not-enabled Error"
        <| async {
            let api = mkApi (Some(deps None)) noOpAuditCallback

            match! api.DownloadSignedExport "ticket-1" with
            | Result.Error msg -> Expect.stringContains msg "Signed DSR export is not enabled" "clear opt-in message"
            | Result.Ok _ -> failtest "must not produce a signed envelope without a composed signer"
        }

        testCaseAsync "DownloadSignedExport with a signer returns the envelope + signature and audits ExportSigned"
        <| async {
            let sink = ConcurrentQueue<DsrAuditEvent>()
            let api = mkApi (Some(deps (Some(FakeEnvelopeSigner())))) (recordingAudit sink)

            match! api.DownloadSignedExport "ticket-1" with
            | Result.Error e -> failtestf "signed download must succeed; got %s" e
            | Result.Ok signed ->
                Expect.equal signed.Envelope "envelope-bytes"B "envelope is the exact downloaded bytes"
                Expect.equal signed.Signature.SigningKeyId "export-signing-v1" "carries the signing key id"

                Expect.stringContains
                    signed.Signature.SigningKeyUrl
                    "/_platform/signing-key/"
                    "carries the public-key URL"

                let signedEvents = sink |> Seq.filter (fun e -> e.Kind = ExportSigned) |> Seq.toList

                Expect.equal signedEvents.Length 1 "exactly one ExportSigned audit event"
                let ev = signedEvents[0]
                Expect.isTrue (ev.Properties.ContainsKey "artefactSha256") "audit carries the artefact SHA-256"
                Expect.isFalse (ev.Properties.ContainsKey "bytes") "audit never carries the raw bytes"
                Expect.equal ev.Properties["keyId"] "export-signing-v1" "audit carries the key id"
        }

        testCaseAsync "DownloadSignedExport without async deps → async-not-enabled Error"
        <| async {
            let api = mkApi None noOpAuditCallback

            match! api.DownloadSignedExport "ticket-1" with
            | Result.Error msg -> Expect.stringContains msg "Async DSR export is not enabled" "async-off message"
            | Result.Ok _ -> failtest "must not produce a signed envelope with async export disabled"
        }
    ]
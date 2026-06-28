module ToolUp.Platform.Tests.Contracts.IEvidencePackGeneratorContract

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.Platform.IDataExporter

// Phase 187 — contract for the compliance evidence-pack generator. Proves
// the composition contract:
//   * the manifest is deterministic (fixed clock + data ⇒ identical bytes);
//   * the manifest is signed over exactly those bytes via the neutral
//     IExportEnvelopeSigner seam (the crypto itself is IArtefactSigner's
//     own contract — IArtefactSignerContract — not re-tested here);
//   * the classification sidecar matches IFieldClassifier output;
//   * audit + DSR segments are content-addressed in the manifest;
//   * the NoEvidencePackGenerator default is disabled.

let private fixedClock () =
    DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero)

let private sha256Hex (bytes: byte[]) : string =
    use sha = SHA256.Create()
    sha.ComputeHash bytes |> Array.map (fun b -> b.ToString("x2")) |> String.Concat

/// Fake event store answering ReadBySource from a fixed map.
type private FakeEventStore(bySource: Map<string, ModuleEvent list>) =
    interface IEventStore with
        member _.Write _ = async { return () }
        member _.ReadAll _ = async { return [] }
        member _.ReadByType(_, _) = async { return [] }

        member _.ReadBySource(_, sourceModule) = async {
            return bySource |> Map.tryFind sourceModule |> Option.defaultValue []
        }

        member _.ListScopes() = async { return [] }
        member _.Erase(_, _, _, _) = async { return Error(StoreUnreachable("fake", "n/a")) }

/// Fake DSR exporter returning a fixed segment list.
type private FakeExporter(name: string, segments: ExportSegment list) =
    interface IDataExporter with
        member _.Name = name
        member _.Export(_, _) = async { return segments }

/// Signer that records the exact bytes it was asked to sign, so the test
/// can assert the manifest (not some other payload) was signed.
type private RecordingSigner() =
    let mutable signed: byte[] option = None
    member _.Signed = signed

    interface IExportEnvelopeSigner with
        member _.SignEnvelope(envelope: byte[]) = async {
            signed <- Some envelope

            return
                Ok {
                    DetachedJws = "jws:" + Convert.ToBase64String envelope
                    SigningKeyId = "test-key"
                    SigningKeyUrl = "/_platform/signing-key/test-key"
                }
        }

let private auditEvents = [
    {
        Id = Guid "11111111-1111-1111-1111-111111111111"
        OccurredAt = DateTime(2026, 6, 28, 11, 0, 0, DateTimeKind.Utc)
        ScopeId = "team-1"
        SourceModule = "_platform.audit"
        EventType = "UserLoggedIn"
        Payload = "{}"
    }
]

let private classifications = [
    FieldClassification.create "Customer" "Email" Pii
    FieldClassification.create "Customer" "Balance" Financial
]

let private dsrSegment: ExportSegment = {
    Name = "entities/team-1.json"
    MimeType = "application/json"
    Body = System.Text.Encoding.UTF8.GetBytes "{\"subject\":\"user-9\"}"
}

let private request: EvidencePackRequest = {
    ScopeId = "team-1"
    AuditSourceModules = [ "_platform.audit" ]
    EntityNames = [ "Customer" ]
    SubjectUserId = Some "user-9"
    RequestedBy = "admin-1"
    Reason = "model-validation evidence MRM-2026-04"
}

let private makeGenerator (signer: IExportEnvelopeSigner option) : IEvidencePackGenerator =
    DefaultEvidencePackGenerator.createWithClock
        (FakeEventStore(Map.ofList [ "_platform.audit", auditEvents ]))
        (DefaultFieldClassifier.create classifications)
        [ FakeExporter("entities", [ dsrSegment ]) ]
        signer
        fixedClock

let private okPack label result =
    match result with
    | Ok pack -> pack
    | Error e -> failtestf "%s: %s" label (EvidencePackError.describe e)

let tests =
    testList "EvidencePackGenerator — IEvidencePackGenerator contract" [
        testCaseAsync "Manifest bytes are deterministic for a fixed clock + data"
        <| async {
            let gen = makeGenerator None
            let! r1 = gen.Generate request
            let! r2 = gen.Generate request
            let p1 = okPack "first generate" r1
            let p2 = okPack "second generate" r2
            Expect.equal p1.ManifestBytes p2.ManifestBytes "same inputs ⇒ identical signed bytes"
        }

        testCaseAsync "Signature is stamped over exactly the manifest bytes"
        <| async {
            let signer = RecordingSigner()
            let gen = makeGenerator (Some(signer :> IExportEnvelopeSigner))
            let! result = gen.Generate request
            let pack = okPack "signed generate" result

            Expect.isSome pack.Signature "a composed signer yields a signature"
            Expect.equal signer.Signed (Some pack.ManifestBytes) "signer signed exactly the canonical manifest bytes"
            Expect.equal pack.Signature.Value.SigningKeyId "test-key" "signature carries the key id"
        }

        testCaseAsync "No signer ⇒ unsigned pack, still Ok"
        <| async {
            let gen = makeGenerator None
            let! result = gen.Generate request
            let pack = okPack "unsigned generate" result
            Expect.isNone pack.Signature "no signer composed ⇒ no signature"
        }

        testCaseAsync "Classification sidecar matches IFieldClassifier output"
        <| async {
            let gen = makeGenerator None
            let! result = gen.Generate request
            let pack = okPack "generate" result

            let expected =
                classifications
                |> List.map (fun c -> {
                    EntityName = c.EntityName
                    FieldPath = c.FieldPath
                    Level = ClassificationLevel.name c.Level
                })
                |> List.sortBy (fun c -> c.EntityName, c.FieldPath)

            Expect.equal pack.Manifest.Classifications expected "sidecar mirrors the classifier tags"

            Expect.isTrue
                (pack.Manifest.Entries
                 |> List.exists (fun e -> e.Name = "classification/sidecar.json"))
                "sidecar is a content-addressed manifest entry"
        }

        testCaseAsync "Audit + DSR segments are content-addressed in the manifest"
        <| async {
            let gen = makeGenerator None
            let! result = gen.Generate request
            let pack = okPack "generate" result

            // Every manifest entry's Sha256 matches its segment's bytes.
            for entry in pack.Manifest.Entries do
                let segment = pack.Segments |> List.find (fun s -> s.Name = entry.Name)
                Expect.equal entry.Sha256 (sha256Hex segment.Body) $"entry {entry.Name} pins its segment hash"
                Expect.equal entry.SizeBytes segment.Body.Length $"entry {entry.Name} pins its segment size"

            Expect.isTrue
                (pack.Manifest.Entries
                 |> List.exists (fun e -> e.Name = "audit/_platform.audit.json"))
                "audit slice present"

            Expect.isTrue
                (pack.Manifest.Entries
                 |> List.exists (fun e -> e.Name = "dsr/entities/team-1.json"))
                "DSR export segment present (subject named)"
        }

        testCaseAsync "Manifest entries are sorted by Name (deterministic layout)"
        <| async {
            let gen = makeGenerator None
            let! result = gen.Generate request
            let pack = okPack "generate" result
            let names = pack.Manifest.Entries |> List.map _.Name
            Expect.equal names (List.sort names) "entries are name-sorted"
        }

        testCaseAsync "NoEvidencePackGenerator default is disabled"
        <| async {
            let gen = NoEvidencePackGenerator() :> IEvidencePackGenerator
            let! result = gen.Generate request

            match result with
            | Error GeneratorDisabled -> ()
            | other -> failtestf "expected Error GeneratorDisabled, got %A" other
        }
    ]
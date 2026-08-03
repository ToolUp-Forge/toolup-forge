module ToolUp.Platform.Tests.Contracts.IUploadValidatorContract

open Expecto
open ToolUp.AssetStore

// ─── Phase 186 — IUploadValidator contract pack ─────────────────
//
// Parametrised tests for any `IUploadValidator` that claims to
// cross-check a declared `Content-Type` against the bytes. The
// factory yields the validator plus two payloads whose leading bytes
// genuinely ARE what their names say — built as `byte[]` literals in
// code rather than committed binary fixtures, so the corpus stays
// greppable and carries no raw control bytes into the tree.
//
// The pack is deliberately paired: every refusal case has a CONTROL
// asserting a legitimate upload of the same shape still succeeds. A
// validator that refuses everything would satisfy the refusal cases
// alone, and "reject all uploads" is a failure mode with the same
// signature as "reject the spoofed ones".
//
// Bindings: `UploadValidationTests` binds the in-tree
// `SniffingUploadValidator`.

type UploadValidatorFixture = {
    /// The validator under test.
    Validator: IUploadValidator
    /// A payload whose bytes genuinely are a PNG.
    Png: byte[]
    /// A payload whose bytes genuinely are a JPEG.
    Jpeg: byte[]
}

let private run (fx: UploadValidatorFixture) (bytes: byte[]) (declared: string) = fx.Validator.Validate(bytes, declared)

let tests (name: string) (factory: unit -> UploadValidatorFixture) =

    testList $"{name} — IUploadValidator contract" [

        test "declares a stable non-empty Name (GP 12 rule 1 — identity by value)" {
            let fx = factory ()
            Expect.isNotEmpty fx.Validator.Name "Name is surfaced in ValidationUnavailable reasons"

            // Rule 1 again: the name is a value, so reading it twice
            // from the same instance cannot differ.
            Expect.equal fx.Validator.Name fx.Validator.Name "Name is stable across reads"
        }

        testCaseAsync "CONTROL — a PNG declared image/png is admitted"
        <| async {
            let fx = factory ()
            let! verdict = run fx fx.Png "image/png"
            Expect.equal verdict (Ok()) "a legitimate upload passes the seam untouched"
        }

        testCaseAsync "CONTROL — a JPEG declared image/jpeg is admitted"
        <| async {
            let fx = factory ()
            let! verdict = run fx fx.Jpeg "image/jpeg"
            Expect.equal verdict (Ok()) "a second legitimate type also passes"
        }

        testCaseAsync "a spoofed declared type is refused, naming BOTH the declared and the sniffed type"
        <| async {
            let fx = factory ()

            // The canonical abuse: the caller asserts image/jpeg, the
            // bytes are a PNG. `UploadRequest.create` cannot see this —
            // image/jpeg is on the accept-list, so the declared-metadata
            // checks pass and only the bytes disagree.
            let! verdict = run fx fx.Png "image/jpeg"

            match verdict with
            | Ok() -> failtest "a declared/actual mismatch was admitted — the seam trusted the caller"
            | Error(MimeMismatch(declared, sniffed)) ->
                Expect.equal declared "image/jpeg" "the rejection names what the caller claimed"
                Expect.equal sniffed "image/png" "the rejection names what the bytes actually are"
            | Error other -> failtestf "expected MimeMismatch, got %A" other
        }

        testCaseAsync "the mismatch is symmetric — JPEG bytes declared image/png are refused too"
        <| async {
            let fx = factory ()
            let! verdict = run fx fx.Jpeg "image/png"

            match verdict with
            | Error(MimeMismatch("image/png", "image/jpeg")) -> ()
            | other -> failtestf "expected MimeMismatch(image/png, image/jpeg), got %A" other
        }

        testCaseAsync "verdicts are stateless between invocations (GP 12 rule 4)"
        <| async {
            let fx = factory ()

            // Interleave a refusal and an admission on the SAME instance.
            // A validator that carried per-upload state between calls —
            // caching the last sniff, say — would drift here.
            let! first = run fx fx.Png "image/jpeg"
            let! second = run fx fx.Png "image/png"
            let! third = run fx fx.Png "image/jpeg"

            Expect.notEqual first (Ok()) "the spoof is refused"
            Expect.equal second (Ok()) "the legitimate upload between two refusals still passes"
            Expect.equal third first "the same input yields the same verdict on a re-run"
        }

        testCaseAsync "refusal is returned as data, never raised (GP 12 rule 3)"
        <| async {
            let fx = factory ()

            // Degenerate payloads a hostile caller can trivially send.
            // Whatever the validator concludes, it must CONCLUDE — an
            // escaping exception is a refusal the caller cannot type.
            for payload in [ [||]; [| 0uy |]; Array.create 3 0xFFuy ] do
                let! verdict = run fx payload "image/png"

                match verdict with
                | Ok()
                | Error _ -> ()
        }
    ]
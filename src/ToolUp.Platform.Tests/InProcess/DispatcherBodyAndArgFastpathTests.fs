module ToolUp.Platform.Tests.InProcess.DispatcherBodyAndArgFastpathTests

open System.IO
open Expecto

// ─── Phase 69m — Dispatcher body + argument-parse fastpath ──
//
// Closes the N+1-JSON-parse waste (F2) and the proxy stream re-read
// waste (F3) on the ToolUp.Remoting dispatcher. After Phase 69m:
//   * `parseArgumentArray` returns `JsonElement list` (Clone'd elements
//     survive the JsonDocument disposal); the per-arg deserialise calls
//     `JsonElement.Deserialize<'inp>` which walks the existing tokens —
//     no re-parse per argument.
//   * `InvocationProps<'impl>` carries `InputBytes: byte[] option`. The
//     Giraffe adapter populates it from the lazy body cache when an
//     upstream pre-flight stage materialised the bytes. The proxy parses
//     directly from those bytes (no second `StreamReader` walk of
//     `ctx.Request.Body`, no second string materialisation).
//   * Audit emission reuses the validation-parsed first-arg value when
//     both seams are armed for the same method — one
//     `parseFirstArgFromBody` call per request instead of two.
//
// Source plan: application-plans/toolup-remoting-hot-path-perf.md,
// Findings F2 + F3 + the audit-reuse refinement.
//
// These checks are textual — same shape as Phase 69l's gate pack. An
// integration-shape allocation-pin test (compose a multi-arg API,
// dispatch through a TestServer, assert one outer JsonDocument.Parse
// and zero StreamReader allocations on the cached-bytes path) is the
// right next step. The TestServer scaffold it needs now exists in this
// runner (`AuditBodyDisposalTests`, `PostDispatchBodyReadTests` dispatch
// through the real dispatcher on a TestServer); the allocation-pin test
// itself remains unwritten. Tracked as a follow-up TIDY-UP item
// alongside the Phase 69l integration-shape allocation-pin test.

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

[<Tests>]
let tests =
    testList "Phase 69m — Dispatcher body + arg-parse fastpath" [

        test "InvocationPropsInt.Arguments holds JsonElement (was string)" {
            let typesPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Types.fs")

            let contents = File.ReadAllText typesPath

            Expect.stringContains
                contents
                "Arguments: Choice<byte[], JsonElement> list"
                "InvocationPropsInt.Arguments must carry JsonElement (not raw text) so the \
                 per-arg deserialise path is a single JsonElement.Deserialize walk. \
                 Reverting to string would silently re-introduce N+1 JSON parses."
        }

        test "InvocationProps<'impl> carries InputBytes for cached body re-use" {
            let typesPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Types.fs")

            let contents = File.ReadAllText typesPath

            Expect.stringContains
                contents
                "InputBytes: byte[] option"
                "InvocationProps must carry InputBytes so the Giraffe adapter can hand the \
                 lazy body cache to the proxy. Without this field the proxy re-reads \
                 ctx.Request.Body and re-materialises the string."
        }

        test "parseArgumentArrayBytes exists (bytes-direct outer-array parse)" {
            let proxyPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Proxy.fs")

            let contents = File.ReadAllText proxyPath

            Expect.stringContains
                contents
                "parseArgumentArrayBytes"
                "parseArgumentArrayBytes is the bytes-direct sibling of parseArgumentArray. \
                 Without it the cached-bytes path would have to first allocate a string \
                 from the bytes, defeating half the F3 win."
        }

        test "Outer-array parse returns JsonElement list with Clone() per element" {
            let proxyPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Proxy.fs")

            let contents = File.ReadAllText proxyPath

            Expect.stringContains
                contents
                ": JsonElement list ="
                "parseArgumentArray must return JsonElement list — the signature pins the \
                 fastpath shape. Reverting to string list would silently regress N+1 parses."

            Expect.stringContains
                contents
                "Seq.map _.Clone()"
                "Each per-arg JsonElement must be Clone'd so it survives the parent \
                 JsonDocument's `use` scope. Without Clone the JsonElement values become \
                 invalid the moment the parent document disposes. (Asserts the `_.Clone()` \
                 lambda-shorthand form the SDK standardised on.)"
        }

        // Phase 783 moved the per-argument deserialise CALL out of Proxy.fs
        // and into the one decode seam in the STJ converter set
        // (`FableConverters.tryDeserialise`), so that a decode failure can be
        // caught in one place and refused by name instead of thrown. The
        // regression this pin guards is unchanged — the per-argument path must
        // consume the already-parsed `JsonElement` through the generic
        // `Deserialize<'T>` overload, never re-parse raw JSON text — so the
        // pin follows the code across the two files rather than being dropped.
        test "the per-argument deserialise takes JsonElement (no re-parse)" {
            let proxyPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Proxy.fs")

            let contents = File.ReadAllText proxyPath

            Expect.stringContains
                contents
                "argElement: JsonElement"
                "the per-arg deserialise must take JsonElement (not string). The previous \
                 signature took raw JSON text and re-parsed per call."

            Expect.stringContains
                contents
                "FableConverters.tryDeserialiseFor<'inp> recordName argElement stjOptions"
                "the proxy must hand the already-parsed ELEMENT to the decode seam (Phase 839's \
                 record-scoped `tryDeserialiseFor`, not the bare two-argument `tryDeserialise` \
                 the source generator's emitted code still calls). Passing raw text (or the \
                 element's RawText) would re-introduce the N+1 parse the fastpath removed, and \
                 would do it invisibly."

            let seamPath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "Json",
                    "SystemTextJsonConverter.fs"
                )

            Expect.stringContains
                (File.ReadAllText seamPath)
                "element.Deserialize<'T>(options)"
                "the seam must use the generic JsonElement.Deserialize overload so no \
                 re-parse happens, and so the happy path stays byte-identical rather than \
                 routing through the boxing by-Type overload. \
                 JsonSerializer.Deserialize<'T>(text, opts) would silently re-introduce \
                 the regression."
        }

        test "Multipart text section parses into JsonElement (not raw text)" {
            let proxyPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Proxy.fs")

            let contents = File.ReadAllText proxyPath

            Expect.stringContains
                contents
                "sectionDoc.RootElement.Clone()"
                "Multipart text sections must parse into a Clone'd JsonElement matching \
                 the outer-array path. Without this, the per-arg path in makeEndpointProxy \
                 would fail to type-match the Choice2Of2 arm."
        }

        test "the invoke stage hands the body's read bytes to the proxy" {
            let contents =
                File.ReadAllText(
                    Path.Combine(
                        repoRoot (),
                        "src",
                        "ToolUp.Platform.Server",
                        "Server",
                        "Remoting",
                        "Stages",
                        "Invoke.fs"
                    )
                )

            Expect.stringContains
                contents
                "ctx.Dispatch.Invoke ctx.Endpoint ctx.Parsed ctx.Body.Cached output"
                "the invoke stage must hand the body a stage already read (and the arguments a \
                 stage already parsed) to the proxy, so it neither re-reads the request body \
                 nor re-parses it."
        }

        test "both adapters reach the proxy through one dispatch" {
            let contents =
                File.ReadAllText(
                    Path.Combine(
                        repoRoot (),
                        "src",
                        "ToolUp.Platform.Server",
                        "Server",
                        "Remoting",
                        "Stages",
                        "Pipeline.fs"
                    )
                )

            Expect.stringContains
                contents
                "InputBytes = bytes"
                "the one `Dispatch` both adapters build carries the bytes the pipeline read; \
                 a second construction of the proxy's props would be a second place to forget them."
        }

        test "audit emission reuses the argument a stage already parsed" {
            let audit =
                File.ReadAllText(
                    Path.Combine(
                        repoRoot (),
                        "src",
                        "ToolUp.Platform.Server",
                        "Server",
                        "Remoting",
                        "Stages",
                        "Audit.fs"
                    )
                )

            let invoke =
                File.ReadAllText(
                    Path.Combine(
                        repoRoot (),
                        "src",
                        "ToolUp.Platform.Server",
                        "Server",
                        "Remoting",
                        "Stages",
                        "Invoke.fs"
                    )
                )

            Expect.stringContains
                audit
                "ctx.Parsed with"
                "the audit payload must read the argument dispatch decoded, never parse it again"

            Expect.stringContains
                invoke
                "match ctx.Parsed, mount.AuditEmitter, RequestContext.methodOf ctx with"
                "an audited method's argument is parsed ahead only when no stage has parsed it"
        }

        test "a validated method's parsed arguments are handed to dispatch" {
            let contents =
                File.ReadAllText(
                    Path.Combine(
                        repoRoot (),
                        "src",
                        "ToolUp.Platform.Server",
                        "Server",
                        "Remoting",
                        "Stages",
                        "Validate.fs"
                    )
                )

            Expect.stringContains
                contents
                "ctx.Dispatch.ParseFirst ctx.Endpoint bytes"
                "the validation stage must decode through the proxy's own argument path"

            Expect.stringContains
                contents
                "{ ctx with Parsed = ValueSome parsed }"
                "dispatch must receive what validation parsed, or the body is deserialised twice"
        }

        test "Exception path materialises body text from cached bytes when needed" {
            let proxyPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Proxy.fs")

            let contents = File.ReadAllText proxyPath

            Expect.stringContains
                contents
                "Some(Encoding.UTF8.GetString bytes)"
                "the fault path must materialise text from the body's bytes, so error \
                 reporting keeps the request-body context."
        }
    ]
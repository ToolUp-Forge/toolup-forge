// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

/// Phase 783 — a decode refusal: what the decoder expected, what it
/// actually found, and where it was when it found it.
///
/// The whole point is that the set is CLOSED and small. A decode failure
/// is not an open-ended message string that a client has to pattern-match
/// on; it is three fields a caller can branch on and a renderer can turn
/// into one sentence. Phase 785 rewrites the decoders themselves around
/// this record; Phase 787 states the totality theorem over it. Nothing
/// downstream may widen it without moving those two.
///
/// `Path` is OUTERMOST-FIRST — `[ "args[0]"; "count" ]` renders as
/// `args[0].count`. It is a list rather than a pre-joined string because
/// a refusal raised deep in a decoder is annotated on the way out (see
/// `DecodeError.at`), and joining at every frame would be both lossy and
/// quadratic. An empty path is legitimate: the value that failed WAS the
/// root.
///
/// Lives in `ToolUp.Platform.Core` rather than beside the server
/// dispatcher because all three tiers refuse with it — the MsgPack reader
/// (this assembly), the server's argument-parse stage
/// (`ToolUp.Platform.Server`) and the Fable client proxy
/// (`ToolUp.Platform.Client`) — and Core is the only tier whose source
/// ships under `fable/` in the nupkg (GP 10).
type DecodeError = {
    /// Field path, outermost first. Empty when the root value is what failed.
    Path: string list
    /// What the decoder required — a type name ("int"), a shape
    /// ("object"), or a structural expectation ("2 arguments").
    Expected: string
    /// What was actually there — a type name, a shape, or a byte/position
    /// description when the wire is binary.
    Found: string
}

/// Phase 783 — the carrier that gets a refusal out of a decoder whose
/// interior is still written in exceptions, to the one seam that converts
/// it back into a `Result`.
///
/// It is deliberately NOT part of the surface a caller is expected to
/// catch: every public seam this phase ships (`Reader.TryRead`,
/// `FableConverters.tryDeserialiseElement`, `InvocationResult.DecodeRefused`)
/// returns the refusal as data. The exception exists because the MsgPack
/// reader and the System.Text.Json converter set are both deeply recursive
/// over cached `Type -> obj` delegates, and threading a `Result` through
/// those signatures is exactly the interior rewrite Phase 785 owns. Until
/// then the exception is how a named refusal survives the descent —
/// which is still strictly better than the `failwithf` it replaces,
/// because the refusal arrives structured rather than as prose.
exception DecodeException of DecodeError

[<RequireQualifiedAccess>]
module DecodeError =

    /// A refusal at the root — no path.
    let create (expected: string) (found: string) : DecodeError = {
        Path = []
        Expected = expected
        Found = found
    }

    /// A refusal at a named path.
    let at (path: string list) (expected: string) (found: string) : DecodeError = {
        Path = path
        Expected = expected
        Found = found
    }

    /// Push one segment onto the FRONT of the path — the annotation a
    /// decoder applies as a refusal unwinds through it, so the outermost
    /// frame ends up first.
    let under (segment: string) (error: DecodeError) : DecodeError = {
        error with
            Path = segment :: error.Path
    }

    /// Render the path as a consumer reads it: `args[0].count`. Segments
    /// are dot-joined, which is the shape both System.Text.Json's own
    /// `$.a.b` reader paths and the Phase 69e validation violation paths
    /// already use. An indexer is carried INSIDE its segment
    /// (`"args[0]"`), never as a segment of its own, so the join needs no
    /// special case and a consumer splitting on `.` recovers exactly the
    /// list that was sent.
    let renderPath (path: string list) : string = path |> String.concat "."

    /// One sentence: `expected int at args[0].count, got string`. With an
    /// empty path the `at …` clause is dropped rather than rendered empty.
    let render (error: DecodeError) : string =
        if List.isEmpty error.Path then
            sprintf "expected %s, got %s" error.Expected error.Found
        else
            sprintf "expected %s at %s, got %s" error.Expected (renderPath error.Path) error.Found

    /// Raise the refusal through the `DecodeException` carrier. Used by
    /// decoder interiors that cannot yet return a `Result` (see the
    /// exception's own doc comment).
    let fail (error: DecodeError) : 'a = raise (DecodeException error)

    /// Raise a root refusal. The one-line replacement for a `failwithf`
    /// on a decode path.
    let failWith (expected: string) (found: string) : 'a = fail (create expected found)

    /// Raise a refusal at a named path.
    let failAt (path: string list) (expected: string) (found: string) : 'a = fail (at path expected found)
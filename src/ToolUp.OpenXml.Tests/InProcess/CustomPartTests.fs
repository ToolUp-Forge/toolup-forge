// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.OpenXml.Tests.InProcess.CustomPartTests

open System
open System.IO
open System.IO.Packaging
open Expecto
open ToolUp.OpenXml
open ToolUp.OpenXml.Tests

// A deliberately generic sidecar payload — no SDK-baked schema or
// content type; the caller owns PartUri / ContentType /
// RelationshipType verbatim.
let private treePart: CustomPart = {
    PartUri = "/myapp/tree.xml"
    ContentType = "application/vnd.myapp.doc-tree+xml"
    RelationshipType = "http://example.test/relationships/doc-tree"
    Content = "<tree><node id=\"1\">root</node></tree>"
}

let private metaPart: CustomPart = {
    PartUri = "/myapp/meta.xml"
    ContentType = "application/vnd.myapp.meta+xml"
    RelationshipType = "http://example.test/relationships/meta"
    Content = "<meta version=\"2\" />"
}

let private sampleModel () : DocModel =
    DocModel.ofBlocks [ Paragraph(ParagraphModel.create [ Run.plain "Body text" ]) ]

// `open ToolUp.OpenXml` shadows `Package` with the SDK's module, so
// the System.IO.Packaging type is referenced fully qualified here.
let private openPackage (bytes: byte[]) (act: System.IO.Packaging.Package -> unit) : unit =
    use stream = new MemoryStream(bytes)

    use package =
        System.IO.Packaging.Package.Open(stream, FileMode.Open, FileAccess.Read)

    act package

let private partUriOf (part: CustomPart) =
    PackUriHelper.CreatePartUri(Uri(part.PartUri, UriKind.Relative))

let tests =
    testList "CustomParts" [
        testCase "round-trip: a custom part survives emit → import byte-for-byte"
        <| fun () ->
            let imported = Import.fromBytes (Emit.toBytesWith [ treePart ] (sampleModel ()))

            Expect.equal imported.CustomParts [ treePart ] "the exact custom part is surfaced on import"

        testCase "back-compat: a plain document carries no custom part or relationship"
        <| fun () ->
            let bytes = Emit.toBytes (sampleModel ())
            let imported = Import.fromBytes bytes

            Expect.isEmpty imported.CustomParts "no custom parts surfaced"

            Expect.equal
                (imported.Model.Sections.Head.Blocks |> List.map Block.text)
                [ "Body text" ]
                "document body imports cleanly"

            openPackage bytes (fun package ->
                Expect.isFalse
                    (package.GetRelationships()
                     |> Seq.exists (fun r -> r.RelationshipType = treePart.RelationshipType))
                    "no stray custom root relationship"

                Expect.isFalse (package.PartExists(partUriOf treePart)) "no stray custom part")

        testCase "multiple custom parts all round-trip"
        <| fun () ->
            let imported =
                Import.fromBytes (Emit.toBytesWith [ treePart; metaPart ] (sampleModel ()))

            Expect.equal
                (imported.CustomParts |> List.sortBy _.PartUri)
                ([ treePart; metaPart ] |> List.sortBy _.PartUri)
                "both custom parts survive intact"

        testCase "re-emitting a part URI replaces it and dedupes its relationship"
        <| fun () ->
            let updated = {
                treePart with
                    Content = "<tree><node id=\"1\">replaced</node></tree>"
            }

            let bytes = Emit.toBytesWith [ treePart; updated ] (sampleModel ())
            let imported = Import.fromBytes bytes

            Expect.equal imported.CustomParts [ updated ] "last write wins; a single part remains"

            openPackage bytes (fun package ->
                let rootRels =
                    package.GetRelationships()
                    |> Seq.filter (fun r -> r.RelationshipType = treePart.RelationshipType)
                    |> Seq.length

                Expect.equal rootRels 1 "exactly one root relationship for the re-emitted URI")

        testCase "a document with a custom part is a valid OPC + OOXML package"
        <| fun () ->
            let bytes = Emit.toBytesWith [ treePart ] (sampleModel ())

            // OOXML schema validation is blind to the out-of-band part.
            let errors = Fixtures.validate bytes

            Expect.isEmpty
                (errors |> List.map (fun e -> sprintf "%s: %s" e.Id e.Description))
                "no OOXML validation errors"

            // The package opens via System.IO.Packaging — proving an
            // OPC-aware editor (Word, LibreOffice) sees a well-formed
            // part it will preserve.
            openPackage bytes (fun package ->
                let uri = partUriOf treePart
                Expect.isTrue (package.PartExists uri) "custom part present in the package")

        testCase "the content-type override and root relationship are present in the package"
        <| fun () ->
            let bytes = Emit.toBytesWith [ treePart ] (sampleModel ())

            openPackage bytes (fun package ->
                let uri = partUriOf treePart

                Expect.equal
                    (package.GetPart(uri).ContentType)
                    treePart.ContentType
                    "[Content_Types].xml carries the content-type override"

                match
                    package.GetRelationships()
                    |> Seq.tryFind (fun r -> r.RelationshipType = treePart.RelationshipType)
                with
                | Some rel ->
                    Expect.equal rel.TargetMode TargetMode.Internal "root relationship is Internal"

                    Expect.equal
                        (PackUriHelper.ResolvePartUri(rel.SourceUri, rel.TargetUri))
                        uri
                        "root relationship targets the custom part"
                | None -> failtest "expected a package-root relationship of the custom type")
    ]
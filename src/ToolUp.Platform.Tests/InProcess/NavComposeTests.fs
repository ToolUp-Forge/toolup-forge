// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NavComposeTests

open System
open System.IO
open Expecto
open ToolUp.PublicRendering
open ToolUp.PublicRendering.PublicRenderingCompose

// ─── Phase 100 — withNav / withTaxonomy compose ergonomics ──────────
//
// Record-level coverage of the compose helpers (the actual DI wiring is
// exercised through composePublicRendering + the existing tag-index /
// NavCatalog behavior).

let tests =
    testList "Phase 100 nav/taxonomy compose" [

        test "withTaxonomy flips the TaxonomyEnabled gate" {
            let app =
                PublicRenderingServerApp.create () |> PublicRenderingServerApp.withTaxonomy

            Expect.isTrue app.TaxonomyEnabled "taxonomy enabled"
            Expect.isFalse (PublicRenderingServerApp.create ()).TaxonomyEnabled "off by default (GP 11)"
        }

        test "withNavTree stores a pre-parsed nav tree" {
            let nav = [ NavTree.leaf "Home" "home"; NavTree.leaf "About" "about" ]

            let app =
                PublicRenderingServerApp.create () |> PublicRenderingServerApp.withNavTree nav

            Expect.equal app.Nav nav "nav tree stored on the record"
        }

        test "withNav parses a nav.yaml file into the nav tree" {
            let path =
                Path.Combine(Path.GetTempPath(), sprintf "nav-%s.yaml" (Guid.NewGuid().ToString("N")))

            File.WriteAllText(
                path,
                "- label: Home\n\
                 \x20 slug: home\n\
                 - label: Services\n\
                 \x20 slug: services"
            )

            try
                let app =
                    PublicRenderingServerApp.create () |> PublicRenderingServerApp.withNav path

                Expect.equal (List.length app.Nav) 2 "two top-level nodes parsed"
                Expect.equal (List.head app.Nav).Label "Home" "first node parsed"
            finally
                File.Delete path
        }

        test "withNav on a missing file registers an empty nav (no error)" {
            let app =
                PublicRenderingServerApp.create ()
                |> PublicRenderingServerApp.withNav "/no/such/nav.yaml"

            Expect.isEmpty app.Nav "missing file → empty nav, not a throw"
        }

        test "NavCatalog carries the nav tree" {
            let cat = { Nav = [ NavTree.leaf "x" "x" ] }
            Expect.equal (List.length cat.Nav) 1 "catalog holds the tree"
        }
    ]
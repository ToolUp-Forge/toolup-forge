# Phase 768 — The AI cookbook companions deliver their COOKBOOK.md to PackageReference consumers

**Applies to:** every consumer that references `ToolUp.AICookbooks.AgChart` and/or
`ToolUp.AICookbooks.AgGridEnterprise` as a **package** (`PackageReference`).
**Breaking:** no. No public surface moved; this is a packaging change only. The `content\` entry
each package has always carried is still there.
**Action required:** none to start working. A consumer that worked around the old packaging by
copying the markdown out of the package root itself should delete that workaround — the one-deletion
diff is below.

## What changes

Both companion prompt builders resolve their cookbook **beside their own assembly**
(`COOKBOOK.Community.md` / `COOKBOOK.Enterprise.md`, the `CookbookFileName` each module exposes).
Until this phase the packs shipped that markdown only under `content\`, the legacy
`packages.config` asset folder, which NuGet never delivers to a `PackageReference` project. So for
every package consumer both builders took their documented degradation path: one startup warning
each (`… not found on any candidate path; … guidance disabled.`) and a builder returning `""`. The
assistant silently lost the guidance the consumer had composed in.

Each pack now also carries the file as a **content file**:

```
contentFiles/any/any/COOKBOOK.Community.md     (ToolUp.AICookbooks.AgChart)
contentFiles/any/any/COOKBOOK.Enterprise.md    (ToolUp.AICookbooks.AgGridEnterprise)
```

declared in the nuspec with `buildAction="Content"` and `copyToOutput="true"`. NuGet adds it to
the consumer's build as a linked `Content` item, and the SDK copies it to the root of **both the
build output and the publish output** — the directory the companion assembly is copied to, which
is exactly the builder's assembly-relative candidate path. No property, no path, no configuration.

The `content\` entry is kept, so a consumer whose build still points at it (the workaround below)
keeps building until it deletes that workaround. A **project**-reference consumer is unchanged: it
still gets the file through the Content item's `CopyToOutputDirectory`, as before.

Both package dependencies are packed with `exclude="Build,Analyzers"`, which leaves content files
flowing transitively: a consumer referencing only `ToolUp.AICookbooks.AgGridEnterprise` also
receives the Community cookbook through its `ToolUp.AICookbooks.AgChart` dependency.

The contract is pinned end to end by `src/ToolUp.AICookbooks.Tests/PackageReferenceDeliveryTests.fs`:
it packs each companion at a throwaway version into a private scratch feed, restores it into a
throwaway consumer, builds and publishes that consumer, and asserts that the cookbook lands beside
the companion assembly in both outputs and that the builder reads it to non-empty guidance with no
warning. Reverting either `.fsproj` to the `content\`-only packaging turns that test red.

## Consumer follow-up — delete the copy-out workaround

A consumer that hit the degradation may have worked around it in its server project:
`GeneratePathProperty="true"` on both `PackageReference`s, plus an `ItemGroup` of two `Content`
items copying `$(PkgToolUp_AICookbooks_*)\content\COOKBOOK.*.md` into its output. **Once it
re-pins both packages past this cut**, drop that workaround.

The diff is one deletion — the copy `ItemGroup` (and any comment explaining it):

```diff
-  <ItemGroup>
-    <Content Include="$(PkgToolUp_AICookbooks_AgChart)\content\COOKBOOK.Community.md"
-             Link="COOKBOOK.Community.md"
-             CopyToOutputDirectory="PreserveNewest"
-             CopyToPublishDirectory="PreserveNewest"
-             Visible="false" />
-    <Content Include="$(PkgToolUp_AICookbooks_AgGridEnterprise)\content\COOKBOOK.Enterprise.md"
-             Link="COOKBOOK.Enterprise.md"
-             CopyToOutputDirectory="PreserveNewest"
-             CopyToPublishDirectory="PreserveNewest"
-             Visible="false" />
-  </ItemGroup>
```

`GeneratePathProperty="true"` on the two `PackageReference`s becomes inert once nothing reads
`$(PkgToolUp_AICookbooks_*)`; removing the attribute is optional tidy-up in the same commit, not a
second required change.

**Delete it after the re-pin, not before.** Against a package cut before this phase the deletion
brings the degradation back. And do not keep the workaround alongside a post-768 package: both
would put a file of the same name at the same output path, which is exactly the duplicate-copy shape
the distinct `CookbookFileName`s exist to avoid.

## Verification

1. Re-pin both packages past this cut and delete the `ItemGroup` above.
2. `dotnet build` and `dotnet publish` the server: `COOKBOOK.Community.md` and
   `COOKBOOK.Enterprise.md` sit beside `ToolUp.AICookbooks.AgChart.dll` /
   `ToolUp.AICookbooks.AgGridEnterprise.dll` in both outputs.
3. Start the server: neither `AgChartAICookbook: … not found on any candidate path` nor the
   Enterprise equivalent is logged, and the composed system prompt carries the two authoring
   headings.

## Rollback

Restore the deleted `ItemGroup` (and `GeneratePathProperty`, if removed). The packages still carry
the `content\` entry it reads, so the workaround works against both old and new cuts — but not at
the same time as the new content-file delivery; pin back to a pre-768 package if you restore it.

## SDK adoption

`consumer_facing: true` — the adoption matrix gains a row for this phase. A consumer carrying the workaround
adopts by deleting it (above); a consumer without one has nothing to do.

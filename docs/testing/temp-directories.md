# Temp directories in tests

**The rule: a fixture that creates a directory owns its deletion. Use `TempDir`.**

```fsharp skip=fragment
open ToolUp.Platform.Testing

testCase "the store survives a restart" <| fun () ->
    use dir = TempDir.create "jobstore"
    let storage = LocalFileStorage.LocalFileStorage(dir.Path) :> IBlobStorage
    // … the directory and everything under it is deleted when `dir` is disposed
```

`TempDir` ships in `ToolUp.Platform.Testing` (`Testing/TempDir.fs`), so a consumer's tests get it too.

- `TempDir.create "<family>"` makes `%TEMP%/toolup-<family>-<guid>` and answers an `IDisposable` with
  `.Path`, `.File "<name>"` (a path inside it, not created) and `.Sub "<name>"` (a sub-directory, created).
- Disposing it deletes the directory recursively. A file handle the code under test has not released yet
  (a SQLite pool, a watcher, a scanner) makes the first attempt fail on Windows, so the delete retries a
  bounded number of times; a directory already gone is not an error; any other failure surfaces after the
  last attempt, because a leak that is swallowed is a leak nobody fixes.
- `TempDir.withDir "<family>" (fun path -> async { … })` scopes a directory to an async body, for the
  shapes where a `use` binding would end before the work does.
- `TempDir.familyOf "<directory name>"` inverts the naming, so a census of the temp root attributes each
  directory to the fixture family that made it.

**Keep the family.** The family is the fixture's short name (letters, digits, `-`, `.`, `_`) and it stays
in the directory name on purpose: when a run leaves something behind, the family says which pack did it.
A test file converted to `TempDir` keeps the prefix it used before (`toolup-jobsched-tests-…` became the
family `jobsched-tests`).

**Nothing in a test pack calls `Path.GetTempPath()` itself.** The exceptions are named where they stand:
a test that asserts something about the temp root, a path that must NOT exist and is never created, and
a contract factory whose signature hands back a store with no disposal hook (the directory outlives the
factory; those sites say so in a comment and are the next sweep's, by changing the contract's factory to
answer an `IDisposable`).

## The gate proves it

`VerifyAll` runs every pack and leg under a temp root of its own (it points `TEMP`, `TMP` and `TMPDIR` at
`%TEMP%/toolup-gate-<guid>` before anything runs, and deletes that root when the driver exits), so a run
leaves nothing in the machine's temp directory and a sibling session's directories are never mistaken for
this run's. After the packs, the `GateReconcile` leg censuses the run's root: every `TempDir`-shaped
directory left there is printed by family and removed, and a family outside `tempLeakAllowance` in
`Build.fs` fails the leg, naming it. The allowance holds the contract-factory families, each with its
reason, and only shrinks: under the full lane a listed family that left nothing behind fails the leg too.

The Build pack's `TestDoubleCensusTests` holds the sources to the same rule: no file under
`src/ToolUp.Platform.Tests` calls `Path.GetTempPath()` outside its named exceptions.

## Why

A census of the Platform test pack (2026-11-10, before the sweep) found 153 `Path.GetTempPath()` sites in
113 files, with private helpers (`tempDir`, `newTempDir`, `freshDir`, …) re-implemented per file and most
directories never deleted, so every gate run grew the machine's temp root. After the sweep the pack holds 8
sites in 8 files, each a named exception in `TestDoubleCensusTests`. A part pack
(`ToolUp.Platform.Tests.Infra` and the rest of the family) may not reference the Testing package, which
reaches the umbrella server tier, so it compiles `Testing/TempDir.fs` (BCL-only) by source link.

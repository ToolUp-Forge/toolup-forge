// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.TestDoubleCensusTests

open System.IO
open System.Text.RegularExpressions
open Expecto

// ─── The Platform packs use the doubles the Testing package ships ─────
//
// `ToolUp.Platform.Testing` ships a secret store, a recording audit log,
// a recording metrics sink, the loggers and `TempDir`, and the SDK's own
// Platform packs read the same doubles a consumer reads. This grep holds
// the sources under `src/ToolUp.Platform.Tests` to it: no file there
// declares a double under a shipped double's local name, and none calls
// `Path.GetTempPath()` itself outside the named exceptions below.

let private platformTests =
    Path.Combine(GateReconcileTests.repoRoot, "src", "ToolUp.Platform.Tests")

let private sources () =
    Directory.EnumerateFiles(platformTests, "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun f ->
        let p = f.Replace('\\', '/')
        not (p.Contains "/bin/" || p.Contains "/obj/"))
    |> Seq.map (fun f -> Path.GetRelativePath(platformTests, f).Replace('\\', '/'), File.ReadAllText f)
    |> List.ofSeq

/// A local double declared under a name the Testing package's doubles
/// replaced.
let private localDouble =
    Regex(
        @"^\s*(?:type|and)\s+(?:private\s+|internal\s+)?(InMemorySecretStore|RecordingAuditLog|SilentLogger|CapturingLogger|RecordingLogger|RecordingMetricsSink)\b",
        RegexOptions.Multiline
    )

/// The local doubles a pack still declares, each with why it cannot take
/// the shipped one yet. A file leaves this map when it is swept; an entry
/// whose file no longer declares the double fails the census.
let private localDoubleExceptions: Map<string, string> =
    Map.ofList [
        "InProcess/TransientFaultPolicyTests.fs",
        "compiled only by the Infra part pack, which may not reference the Testing package and cannot link Fakes.fs (it reaches the Scope part)"
    ]

let private tempPathCall = Regex(@"\bGetTempPath\s*\(")

/// The files that still call `Path.GetTempPath()`, each with why. Every
/// other fixture directory is a `TempDir`.
let private tempPathExceptions: Map<string, string> =
    Map.ofList [
        "InProcess/BuildIdentityEndpointTests.fs", "the assertion is that a response does not leak the temp root itself"
        "InProcess/ExtractorTests.fs", "a golden mismatch leaves its rendering on disk to be diffed, on purpose"
        "InProcess/LocalSecretFilePermissionsValidatorTests.fs", "a path that must not exist; nothing is created"
        "InProcess/MultiSiteTests.fs", "a content root that must not exist; nothing is created"
        "InProcess/OcrProviderTests.fs", "a tessdata directory that must not exist; nothing is created"
        "InProcess/PublicApiApprovalTests.fs", "a project directory that must not exist; nothing is created"
        "InProcess/SecretStoreAtRestPostureValidatorTests.fs",
        "the store is built only to classify its at-rest posture; it never touches the directory"
        "InProcess/ServerConfigSnapshotTests.fs",
        "a mismatch writes the received snapshot for the operator to diff; it must outlive the run"
    ]

let tests =
    testList "the Platform packs use the doubles the Testing package ships" [
        test "no Platform test file declares a double the Testing package ships" {
            let found =
                sources ()
                |> List.choose (fun (file, text) ->
                    let names = localDouble.Matches text |> Seq.map _.Groups[1].Value |> List.ofSeq

                    if List.isEmpty names || localDoubleExceptions.ContainsKey file then
                        None
                    else
                        Some(sprintf "%s declares %s" file (String.concat ", " names)))

            Expect.isEmpty
                found
                "use Fakes.TestSecretStore, TestAuditLog, Fakes.TestMetricsSink, Logger.silent or Logger.recording (docs/platform/testing-conventions.md)"
        }

        test "no Platform test file calls Path.GetTempPath outside the named exceptions" {
            let found =
                sources ()
                |> List.filter (fun (file, text) ->
                    tempPathCall.IsMatch text && not (tempPathExceptions.ContainsKey file))
                |> List.map fst

            Expect.isEmpty found "a fixture directory is a TempDir (docs/testing/temp-directories.md)"
        }

        test "every named exception is still needed" {
            let byFile = sources () |> Map.ofList

            let stale = [
                for KeyValue(file, _) in localDoubleExceptions do
                    match byFile.TryFind file with
                    | Some text when localDouble.IsMatch text -> ()
                    | _ -> yield file + " (local double)"
                for KeyValue(file, _) in tempPathExceptions do
                    match byFile.TryFind file with
                    | Some text when tempPathCall.IsMatch text -> ()
                    | _ -> yield file + " (GetTempPath)"
            ]

            Expect.isEmpty stale "an exception whose file no longer needs it leaves the map"
        }

        test "the census reads the pack it guards" {
            Expect.isGreaterThan (sources ()).Length 400 "src/ToolUp.Platform.Tests holds the Platform pack's sources"
        }
    ]
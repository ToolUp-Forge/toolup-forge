module ToolUp.Platform.Tests.InProcess.HostCapabilityNegotiationTests

open Expecto
open ToolUp.Platform

// ─── Phase 270 — hosted-tree capability/version negotiation tests ─────
//
// The negotiation gate fails LOUD and EARLY (at mount) when a hosted tree
// needs a capability the host lacks, or a higher protocol version than the
// host advertises — a structured `HostCapabilityMismatch` naming the gap,
// never a console warning after a partial render. This pack pins:
//   * matched sets negotiate clean;
//   * a required capability the host lacks fails, naming it;
//   * a below-minimum version fails, naming both versions;
//   * a host advertising a Phase 266 registered id satisfies a tree that
//     requires it;
//   * a view that declares no required set (the four-built-in default)
//     always negotiates clean (GP 11).

let private clipboard = HostCapability.ClipboardWrite

let tests =
    testList "HostCapabilityNegotiation (Phase 270)" [

        testCase "matched sets negotiate clean"
        <| fun _ ->
            let required = HostCapabilitySet.defaultRequired
            let host = HostCapabilitySet.host []

            match HostCapabilityNegotiation.negotiate required host with
            | Ok() -> ()
            | Error m -> failtestf "matched sets must mount clean; got: %s" (HostCapabilityMismatch.describe m)

        testCase "the default required set (four built-ins) always negotiates clean (GP 11)"
        <| fun _ ->
            // A view that declares nothing uses defaultRequired; any host
            // (which always advertises the four built-ins) satisfies it.
            match HostCapabilityNegotiation.negotiate HostCapabilitySet.defaultRequired (HostCapabilitySet.host []) with
            | Ok() -> ()
            | Error m -> failtestf "the built-in default must always mount; got: %s" (HostCapabilityMismatch.describe m)

        testCase "a required capability the host lacks fails, naming the missing id"
        <| fun _ ->
            let required =
                HostCapabilitySet.requires HostCapabilitySet.CurrentVersion [ clipboard ]

            let host = HostCapabilitySet.host [] // only the four built-ins

            match HostCapabilityNegotiation.negotiate required host with
            | Error(HostCapabilityMismatch.MissingCapabilities missing) ->
                Expect.contains missing (CapabilityId.value clipboard) "the missing capability id is named"
            | Error other -> failtestf "expected a MissingCapabilities mismatch; got: %A" other
            | Ok() -> failtest "a missing capability must fail negotiation, not mount broken"

        testCase "a host advertising the registered capability satisfies a tree that requires it"
        <| fun _ ->
            let required =
                HostCapabilitySet.requires HostCapabilitySet.CurrentVersion [ clipboard ]

            let host = HostCapabilitySet.host [ clipboard ] // registered the built-in

            match HostCapabilityNegotiation.negotiate required host with
            | Ok() -> ()
            | Error m -> failtestf "a host advertising the id must satisfy; got: %s" (HostCapabilityMismatch.describe m)

        testCase "a below-minimum version fails, naming both versions"
        <| fun _ ->
            let required = {
                Version = HostCapabilitySet.CurrentVersion + 1
                Capabilities = HostCapabilitySet.builtInCapabilities
            }

            let host = HostCapabilitySet.host [] // advertises CurrentVersion

            match HostCapabilityNegotiation.negotiate required host with
            | Error(HostCapabilityMismatch.VersionTooLow(req, hostVersion)) ->
                Expect.equal req (HostCapabilitySet.CurrentVersion + 1) "required version reported"
                Expect.equal hostVersion HostCapabilitySet.CurrentVersion "host version reported"
            | Error other -> failtestf "expected a VersionTooLow mismatch; got: %A" other
            | Ok() -> failtest "a below-minimum host version must fail negotiation"

        testCase "the version check precedes the capability check"
        <| fun _ ->
            // Both a version gap AND a missing capability — the version gap is
            // reported first (fail-fast on the coarser incompatibility).
            let required =
                HostCapabilitySet.requires (HostCapabilitySet.CurrentVersion + 1) [ clipboard ]

            let host = HostCapabilitySet.host []

            match HostCapabilityNegotiation.negotiate required host with
            | Error(HostCapabilityMismatch.VersionTooLow _) -> ()
            | other -> failtestf "version mismatch must be reported before capability gaps; got: %A" other

        testCase "describe names the gap for both mismatch kinds"
        <| fun _ ->
            let versionMsg =
                HostCapabilityMismatch.describe (HostCapabilityMismatch.VersionTooLow(2, 1))

            Expect.stringContains versionMsg "version" "version mismatch description names the version"

            let capsMsg =
                HostCapabilityMismatch.describe (HostCapabilityMismatch.MissingCapabilities [ "host.file.read" ])

            Expect.stringContains capsMsg "host.file.read" "capability mismatch description names the missing id"
    ]
module ToolUp.Platform.Tests.InProcess.PeerAuthPostureTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.PeerBearerAuthMiddleware

// ─── Phase 317 — peer-auth posture advisory ──────────────────────────
//
// Two peer-auth substrates coexist "on different prefixes", and until
// this phase nothing checked that they did. `PeerRoutePrefixes` entries
// are ordinary case-insensitive `StartsWith` prefixes, so `"/peer/"` —
// the most natural name to reach for — claims the whole `/peer/v1/`
// namespace the signed-JWT host serves, and the static-bearer gate then
// runs AHEAD of it.
//
// This pack pins three things:
//
//   1. **The ladder is discriminated, rung by rung.** Every posture the
//      classifier can return has a case here, and each flagged rung is
//      paired with a control whose config differs in exactly one field.
//      A classifier that collapsed rungs — or returned a constant —
//      fails several of these at once rather than none.
//   2. **The overlap predicate has boundaries.** `"/peerish/"` must not
//      be mistaken for `"/peer/"`, a prefix INSIDE the namespace must
//      be caught, an empty prefix (which matches every path) must be
//      caught, and case must not matter — because the runtime gate the
//      classification models is case-insensitive.
//   3. **The advisory says what is wrong and which lever fixes it**, and
//      the two flagged rungs do not share a verdict: the reason text is
//      asserted, not merely its existence. Counting warnings would pass
//      against a classifier that always returned the same posture.

/// Captures `Warn` lines so the advisory is asserted as an emitted log
/// line, not merely as a classification.
type private RecordingLogger() =
    let warnings = ResizeArray<string>()
    member _.Warnings = List.ofSeq warnings

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Add message
        member _.Error(_, _) = ()

/// A config differing from the defaults in exactly the two fields the
/// classification reads, so every case below is a one- or two-field
/// delta and nothing incidental can explain a verdict.
let private configFor (substrate: PeerSubstrateMode) (prefixes: string list) = {
    ServerConfig.defaults with
        PeerSubstrate = substrate
        PeerRoutePrefixes = prefixes
}

// ─── 1. The ladder ───────────────────────────────────────────────────

let ladderTests =
    testList "Phase 317 — peer-auth posture ladder" [

        test "neither substrate composed — no peer surface at all" {
            Expect.equal
                (auditPeerAuthPosture (configFor NoPeerSubstrate []))
                NoPeerAuthSurface
                "a deployment exposing no cross-deployment surface has no posture to compare"
        }

        test "signed-JWT substrate alone is the strongest rung" {
            Expect.equal
                (auditPeerAuthPosture (configFor EnabledPeerSubstrate []))
                SignedPeerAuthOnly
                "per-call minting + exp/aud + host-verified delegation, and no weaker gate in front of it"
        }

        test "static bearer on its own prefix is a legitimate rung, not a defect" {
            Expect.equal
                (auditPeerAuthPosture (configFor NoPeerSubstrate [ "/api/peer/echo" ]))
                (StaticBearerOnly [ "/api/peer/echo" ])
                "the bearer flavour is the right tool for operator-controlled internal callers; it is not being deprecated"
        }

        test "both substrates on disjoint prefixes is the documented coexistence" {
            Expect.equal
                (auditPeerAuthPosture (configFor EnabledPeerSubstrate [ "/api/peer/echo" ]))
                (BothSubstratesDisjoint [ "/api/peer/echo" ])
                "the bearer flavour guards its own routes and /peer/v1/* is served untouched — exactly what the SDK documents"
        }

        test "THE DEFECT — a bearer prefix covering /peer/ while the host serves" {
            Expect.equal
                (auditPeerAuthPosture (configFor EnabledPeerSubstrate [ "/peer/" ]))
                (StaticBearerShadowsSignedPeer [ "/peer/" ])
                "the static-bearer gate runs ahead of the router, so it decides who reaches the signed-JWT host"
        }

        test "CONTROL — the identical prefix with the host NOT serving is latent, not shadowing" {
            // One field different from the case above. Without this pair
            // the shadow verdict would pass against a classifier that
            // never read `PeerSubstrate` at all.
            Expect.equal
                (auditPeerAuthPosture (configFor NoPeerSubstrate [ "/peer/" ]))
                (StaticBearerOnReservedNamespace [ "/peer/" ])
                "nothing is shadowed while the peer substrate is off — the hazard is what enabling it later would do silently"
        }

        test "the shadow verdict carries ONLY the shadowing prefixes" {
            // A classifier that echoed `PeerRoutePrefixes` wholesale
            // would pass every case above and fail here — and an
            // operator reading the advisory would be sent to audit a
            // prefix that is doing nothing wrong.
            let posture =
                auditPeerAuthPosture (configFor EnabledPeerSubstrate [ "/api/peer/echo"; "/peer/v1/" ])

            Expect.equal
                posture
                (StaticBearerShadowsSignedPeer [ "/peer/v1/" ])
                "the disjoint prefix is not part of the finding — only the one that reaches the namespace"
        }
    ]

// ─── 2. The overlap predicate ────────────────────────────────────────

let overlapTests =
    testList "Phase 317 — signed-peer namespace overlap" [

        test "a prefix SHORTER than the namespace swallows it" {
            Expect.isTrue (shadowsSignedPeerNamespace "/peer") "'/peer' matches every path under '/peer/v1/'"
            Expect.isTrue (shadowsSignedPeerNamespace "/") "a root prefix claims literally everything"
        }

        test "a prefix INSIDE the namespace claims part of it" {
            Expect.isTrue
                (shadowsSignedPeerNamespace "/peer/v1/ledger")
                "a prefix under the namespace still gates dispatches the signed-JWT host owns"
        }

        test "an empty prefix matches every path, so it shadows too" {
            // `String.StartsWith ""` is true, which is exactly how
            // `PeerRouteRegistry.isPeerRoute` behaves — an empty entry
            // silently turns the whole deployment into a peer route.
            Expect.isTrue
                (shadowsSignedPeerNamespace "")
                "an empty prefix is the widest possible one, not the narrowest"
        }

        test "case does not matter, because the runtime gate is case-insensitive" {
            Expect.isTrue
                (shadowsSignedPeerNamespace "/PEER/V1/")
                "the classification must agree with PeerRouteRegistry.isPeerRoute, which compares OrdinalIgnoreCase"
        }

        test "CONTROL — a prefix that merely LOOKS similar does not overlap" {
            // The boundary a naive `Contains` or a prefix-trimmed
            // comparison gets wrong. Without this, a predicate that
            // returned `true` for anything starting '/peer' would pass
            // every positive case above.
            Expect.isFalse
                (shadowsSignedPeerNamespace "/peerish/")
                "'/peerish/' shares a stem with the namespace and claims no path in it"

            Expect.isFalse
                (shadowsSignedPeerNamespace "/api/peer/echo")
                "a nested peer-ish route is disjoint from '/peer/v1/'"

            Expect.isFalse
                (shadowsSignedPeerNamespace "/peer/v2/")
                "a sibling version namespace is not the one the host serves"
        }
    ]

// ─── 3. The advisory ─────────────────────────────────────────────────

let advisoryTests =
    testList "Phase 317 — the startup advisory" [

        test "the advisory names the offending prefix, the symptom and the lever" {
            let advisory =
                peerAuthPostureAdvisory (StaticBearerShadowsSignedPeer [ "/peer/" ])
                |> Option.defaultWith (fun () -> failtest "the shadow posture must produce an advisory")

            Expect.stringContains advisory "/peer/" "an advisory that does not name the prefix cannot be acted on"

            Expect.stringContains
                advisory
                PeerNameHeader
                "the symptom is that a typed peer client sends no X-Peer-Name and is 401'd before dispatch"

            Expect.stringContains
                advisory
                RejectionReason.MissingPeerNameHeader
                "the operator should be able to grep the audit rejections this produces"

            Expect.stringContains
                advisory
                "/api/peer/echo"
                "the lever is a prefix off the namespace, and the advisory must show one"
        }

        test "every silent rung really is silent" {
            let silent = [
                NoPeerAuthSurface
                SignedPeerAuthOnly
                StaticBearerOnly [ "/api/peer/echo" ]
                BothSubstratesDisjoint [ "/api/peer/echo" ]
                StaticBearerOnReservedNamespace [ "/peer/" ]
            ]

            for posture in silent do
                Expect.isNone
                    (peerAuthPostureAdvisory posture)
                    $"an advisory that fires on a correct configuration is one operators learn to ignore (%A{posture})"
        }

        test "advisePeerAuthPosture emits exactly one Warn on the shadowing composition" {
            let logger = RecordingLogger()
            advisePeerAuthPosture logger (configFor EnabledPeerSubstrate [ "/peer/" ])

            Expect.equal logger.Warnings.Length 1 "one line per start, not per request"

            Expect.stringContains
                logger.Warnings[0]
                "peer-auth-posture"
                "the advisory carries a stable grep handle, matching the peer-audience-binding / peer-transport-tls lines"
        }

        test "CONTROL — the same composition on a disjoint prefix logs nothing" {
            // One field different. Without this pair, "a warning was
            // emitted" would pass against an advisory that fired on
            // every peer-enabled deployment.
            let logger = RecordingLogger()
            advisePeerAuthPosture logger (configFor EnabledPeerSubstrate [ "/api/peer/echo" ])

            Expect.isEmpty logger.Warnings "the documented coexistence must stay quiet"
        }

        test "CONTROL — the same prefix with the peer substrate off logs nothing" {
            let logger = RecordingLogger()
            advisePeerAuthPosture logger (configFor NoPeerSubstrate [ "/peer/" ])

            Expect.isEmpty
                logger.Warnings
                "warning about a collision with a host this deployment does not run is a warning about someone else's composition"
        }

        test "GP 11 / GP 13 — neither auth path changed and the advisory never refuses" {
            // The whole phase is docs + one classified log line. A
            // shadowing composition still starts, and the middleware's
            // own behaviour is untouched: the static-bearer gate still
            // matches exactly the prefixes it did before.
            let config = configFor EnabledPeerSubstrate [ "/peer/" ]
            let logger = RecordingLogger()

            advisePeerAuthPosture logger config

            Expect.isTrue
                (PeerRouteRegistry.isPeerRoute
                    config.PeerRoutePrefixes
                    (Microsoft.AspNetCore.Http.PathString "/peer/v1/ledger"))
                "the runtime gate is unchanged — the advisory describes it, it does not alter it"

            Expect.isFalse
                (PeerRouteRegistry.isPeerRoute
                    config.PeerRoutePrefixes
                    (Microsoft.AspNetCore.Http.PathString "/api/things"))
                "and a non-peer path is still untouched"
        }

        test "a default composition is classified, and silent" {
            Expect.equal
                (auditPeerAuthPosture ServerConfig.defaults)
                NoPeerAuthSurface
                "the overwhelming majority of deployments compose neither substrate and must see nothing"

            Expect.isNone (peerAuthPostureAdvisory NoPeerAuthSurface) "zero cost for the common case (GP 13)"
        }
    ]
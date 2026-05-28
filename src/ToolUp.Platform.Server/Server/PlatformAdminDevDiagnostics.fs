module ToolUp.Platform.PlatformAdminDevDiagnostics

open ToolUp.Platform

// ─── Phase 4b dev convenience — /dev/inspect contributor ──────────────
//
// Surfaces the Platform Admin substrate's current state in the
// `/dev/inspect` panel. Saves operators the round-trip of inspecting
// `data/_platform/platform-admins.json` + `runtime-config.json` by
// hand when debugging "why isn't the Platform Admin sidebar group
// appearing?" or "is the PlatformKnowledgeBase toggle actually on?"
//
// **Caller scope.** Returns deployment-wide data (admin list,
// runtime config) rather than the caller's role state. The caller's
// role is already surfaced in the AccessContext panel — this
// contributor complements it with the SDK-internal state that
// drives the role gate. Same GP-4 carve-out as the existing
// `HealthMonitorUI` and SSE-trace contributors: `/dev/inspect` is
// debug-only and `EnableDevEndpoints`-gated, so cross-team
// visibility is acceptable.
//
// **Performance.** `Contribute` issues two store reads:
//   - `IPlatformAdminStore.ListPlatformAdmins` (one blob read,
//     ~tens of ms on `LocalFileStorage`)
//   - `IPlatformRuntimeConfigStore.Snapshot` (sync, in-memory cell)
// Comfortably under the 50ms guidance.

/// Wire-shape rendered into the `Contributors["Platform Admin"]`
/// panel of the `DevDiagnosticsReport`. F# records serialise cleanly
/// through `JsonConvert.SerializeObject` so the JSON view + the HTML
/// view both pick up the structure.
type private PlatformAdminPanel = {
    /// Userids holding `PlatformRole.PlatformAdmin`. Empty when no
    /// admin has been bootstrapped yet — admin-gated endpoints will
    /// refuse all callers in that state.
    Admins: string list
    /// Cardinality, surfaced separately so the HTML view can render
    /// `(N admins)` without re-counting.
    AdminCount: int
    /// Current runtime `PlatformKnowledgeBase` mode (the `Snapshot`
    /// value the retrieval pipeline reads). Reflects any runtime
    /// override applied via `PlatformAdminApi.SetPlatformKnowledgeBase`,
    /// not just the boot-time `ServerConfig` default.
    PlatformKnowledgeBase: string
}

let private modeToString =
    function
    | EnabledPlatformKnowledgeBase -> "Enabled"
    | NoPlatformKnowledgeBase -> "Disabled"

/// Default contributor. Registered as a DI singleton in `compose`
/// alongside the existing SSE-trace contributor.
type PlatformAdminContributor(adminStore: IPlatformAdminStore, runtimeConfig: IPlatformRuntimeConfigStore) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let! admins = adminStore.ListPlatformAdmins()
            let mode = runtimeConfig.Snapshot()

            let panel: PlatformAdminPanel = {
                Admins = admins |> List.sort
                AdminCount = admins.Length
                PlatformKnowledgeBase = modeToString mode
            }

            return "Platform Admin", box panel
        }
module ToolUp.Platform.Tests.Support.CacheReset

// ─── Module-level-cache reset registry ──────────────────────────────
//
// Some SDK substrate modules carry a process-lifetime `let mutable`
// cache or registration list. Under Expecto's default parallel-within-
// testList execution, sibling tests collide on these caches and produce
// false-fails. The pattern + audit checklist + when each strategy
// applies is documented in
// `toolup-forge/docs/platform/testing-conventions.md`.
//
// Tests that mutate any (b)-class module-level cache call
// `CacheReset.invalidateAll` at setup. The registry below knows the
// small finite set of cache-bearing modules. Adding a new (b)-class
// module means appending one line here AND adding a matching
// `__internal_resetForTests` to that module's source file.

/// Reset every (b)-class module-level cache to its initial state.
///
/// Call at the top of any `testCaseAsync` that mutates a module-level
/// cache, OR wrap the consuming `testList` in
/// `testSequencedGroup "uses shared cache"` and call once at setup.
///
/// Returns `Async<unit>` so it composes naturally inside
/// `testCaseAsync` blocks; the actual resets are synchronous.
let invalidateAll () : Async<unit> = async {
    // (b) — PendingInviteStore.cache: 30s TTL cache of the pending-
    // invite blob; read on every middleware sign-in resolve.
    ToolUp.Platform.Teams.PendingInviteStore.__internal_resetForTests ()

    // (b) — FileManagement.pendingPostSaveHooks: companion-registered
    // post-save hooks drained by `compose`. Tests that bypass `compose`
    // need an explicit reset to start clean.
    ToolUp.Platform.FileManagement.__internal_resetForTests ()
}
module ToolUp.Platform.Tests.InProcess.DefaultSubjectResolverTests

open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Tests.Contracts

// ─── DefaultSubjectResolver — Phase 66 Stream D.1 ────────────────────
//
// Binds the SDK's default `ISubjectResolver` to the shared
// `ISubjectResolverContract` pack. The factory supplies the impl's
// private substrate (a fresh `IMemoryCache` + `InMemoryNotificationChannel`
// per resolver) so the contract's "one fresh resolver per test" promise
// holds — each gets its own cache, so no active-team answer leaks across
// scenarios. External resolver impls (a grain, an actor) bind the same
// pack from their own test file with their own factory.

let private factory: ISubjectResolverContract.ResolverFactory =
    fun surfaces teamStore ->
        let cache = new MemoryCache(MemoryCacheOptions()) :> IMemoryCache
        let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
        new DefaultSubjectResolver.DefaultSubjectResolver(surfaces, teamStore, cache, notifications) :> ISubjectResolver

let tests = ISubjectResolverContract.tests "DefaultSubjectResolver" factory
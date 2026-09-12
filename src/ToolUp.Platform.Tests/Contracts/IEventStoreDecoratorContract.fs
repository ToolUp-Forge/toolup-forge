module ToolUp.Platform.Tests.Contracts.IEventStoreDecoratorContract

open System
open Expecto
open ToolUp.Platform

// ─── IEventStoreDecorator contract pack — Phase 9u ───────────────────
//
// Parametrised laws for any `IEventStore` decorator that self-describes
// through `IEventStoreDecorator`. Every implementation binds this pack
// against itself; the three first-party decorators do so in
// `InProcess/EventStoreChainValidatorTests.fs`.
//
// **What the laws are actually protecting.** The declaration is only
// worth anything if it is TRUE of the object that makes it.
// `EventStoreChain.describe` walks the composed chain by following
// `InnerStore`, and `EventStoreChainValidator` refuses boot on what it
// finds — so a decorator whose `InnerStore` is not the store it actually
// writes through does not merely mis-report itself, it makes the boot
// guard and the `/dev/inspect` panel confidently describe a chain that
// does not exist. That is a worse failure than having no declaration at
// all, because it is one an operator would believe. Laws 4-7 exist to
// make it impossible to ship.
//
// **Coverage:**
//   1. Identity by value — `DecoratorName` non-empty and stable across
//      reads (GP 12 rule 1: the walk keys off the string, so a name that
//      varies per read is not an identity).
//   2. `DecoratorPosition` within the reserved bounds, and stable.
//   3. `DecoratorPurpose` non-empty — the `/dev/inspect` panel renders it
//      verbatim, and an empty cell teaches the reader nothing.
//   4. `InnerStore` is the store the decorator was handed, by REFERENCE,
//      and the same reference on every read.
//   5. `InnerStore` is never the decorator itself — a self-reference is
//      the cycle `describe`'s depth cap exists to survive, and every
//      `Write` would recurse until the stack exhausts.
//   6. `Write` reaches the declared inner store. Whatever else a
//      decorator does to a write, the store it NAMES is the store the
//      event lands in.
//   7. Reads pass through the declared inner store unchanged. Decoration
//      is a write-side concern; a decorator that filtered reads would
//      make two callers of the same chain disagree about history.
//   8. `EventStoreChain.describe` sees the decorator as the outermost
//      link over the given inner store — the walk and the declaration
//      agree, which is the property everything downstream assumes.

/// Build the decorator under test around a supplied inner store. The
/// pack hands in its own recording store, so an implementation's other
/// constructor dependencies (a dispatcher, a scheduler lookup, an enqueue
/// callback) are closed over by the binding rather than modelled here.
type EventStoreDecoratorFactory = IEventStore -> IEventStore

/// Records every write and answers reads from a fixed script, so laws 6
/// and 7 can tell "reached the declared inner store" from "reached
/// something".
type private RecordingEventStore(scripted: ModuleEvent list) =
    let written = ResizeArray<ModuleEvent>()
    member _.Written = written |> List.ofSeq

    interface IEventStore with
        member _.Write(evt) = async { written.Add evt }
        member _.ReadAll(_) = async { return scripted }
        member _.ReadByType(_, _) = async { return scripted }
        member _.ReadBySource(_, _) = async { return scripted }
        member _.ListScopes() = async { return [ "scripted-scope" ] }
        member _.Erase(_, _, _, _) = async { return Result.Ok(Unchecked.defaultof<ErasureSummary>) }

let private anEvent () = {
    Id = Guid.NewGuid()
    OccurredAt = DateTime.UtcNow
    ScopeId = "_platform"
    SourceModule = "_platform.audit"
    EventType = "ContractProbe"
    Payload = "{}"
}

let tests (name: string) (factory: EventStoreDecoratorFactory) =

    /// The decorator plus the recording store it was handed, as the
    /// declared-vs-actual pair every law below compares.
    let build () =
        let inner = RecordingEventStore [ anEvent () ]
        let decorated = factory (inner :> IEventStore)

        let declaring =
            match box decorated with
            | :? IEventStoreDecorator as d -> d
            | _ -> failtestf "%s does not implement IEventStoreDecorator" name

        inner, decorated, declaring

    testList $"{name} — IEventStoreDecorator contract" [

        testCase "1. DecoratorName is non-empty and stable across reads"
        <| fun _ ->
            let _, _, declaring = build ()

            Expect.isFalse
                (String.IsNullOrWhiteSpace declaring.DecoratorName)
                "the walk and every refusal message key off this string"

            Expect.equal declaring.DecoratorName declaring.DecoratorName "identity by value — stable across reads"

        testCase "2. DecoratorPosition sits within the reserved bounds and is stable"
        <| fun _ ->
            let _, _, declaring = build ()
            let position = declaring.DecoratorPosition

            Expect.isGreaterThanOrEqual
                position
                EventStoreChain.InnermostPosition
                "position must not undercut the reserved innermost bound"

            Expect.isLessThanOrEqual
                position
                EventStoreChain.OutermostPosition
                "position must not exceed the reserved outermost bound"

            Expect.equal position declaring.DecoratorPosition "a position that varies per read cannot order a chain"

        testCase "3. DecoratorPurpose is non-empty"
        <| fun _ ->
            let _, _, declaring = build ()

            Expect.isFalse
                (String.IsNullOrWhiteSpace declaring.DecoratorPurpose)
                "the /dev/inspect chain panel renders this verbatim"

        testCase "4. InnerStore is the supplied store, by reference, on every read"
        <| fun _ ->
            let inner, _, declaring = build ()

            Expect.isTrue
                (Object.ReferenceEquals(declaring.InnerStore, inner))
                "a declared inner store that is not the wrapped one makes the whole chain walk a fiction"

            Expect.isTrue
                (Object.ReferenceEquals(declaring.InnerStore, declaring.InnerStore))
                "the walk reads InnerStore once per link and must get the same store each time"

        testCase "5. InnerStore is never the decorator itself"
        <| fun _ ->
            let _, decorated, declaring = build ()

            Expect.isFalse
                (Object.ReferenceEquals(declaring.InnerStore, decorated))
                "a self-referential InnerStore cycles the walk and recurses every Write until the stack exhausts"

        testCase "6. Write reaches the declared inner store"
        <| fun _ ->
            let inner, decorated, _ = build ()
            let evt = anEvent ()
            decorated.Write evt |> Async.RunSynchronously

            Expect.equal
                (inner.Written |> List.map _.Id)
                [ evt.Id ]
                "the store a decorator NAMES must be the store the event lands in"

        testCase "7. Reads pass through the declared inner store unchanged"
        <| fun _ ->
            let inner, decorated, _ = build ()
            let expected = (inner :> IEventStore).ReadAll "_platform" |> Async.RunSynchronously
            let actual = decorated.ReadAll "_platform" |> Async.RunSynchronously

            Expect.equal
                (actual |> List.map _.Id)
                (expected |> List.map _.Id)
                "decoration is write-side; a read-filtering decorator would make two callers disagree about history"

            let scopes = decorated.ListScopes() |> Async.RunSynchronously
            Expect.equal scopes [ "scripted-scope" ] "ListScopes passes through too"

        testCase "8. describe sees the decorator as the outermost link over the supplied store"
        <| fun _ ->
            let _, decorated, declaring = build ()
            let chain = EventStoreChain.describe decorated

            match chain.Links with
            | [ link ] ->
                Expect.equal link.Name declaring.DecoratorName "the walk reports the declared name"
                Expect.equal link.Position (Some declaring.DecoratorPosition) "and the declared position"
                Expect.equal link.Purpose (Some declaring.DecoratorPurpose) "and the declared purpose"
            | other -> failtestf "expected exactly one walked link over an undecorated store, got %A" other

            Expect.equal chain.InnerStoreName "RecordingEventStore" "the walk terminates at the supplied store"
            Expect.isFalse chain.Truncated "a single decorator cannot truncate the walk"
    ]
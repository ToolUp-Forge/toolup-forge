// Ambient context for `docs/platform/client-remoting-proxies.md`.
//
// The page teaches a CONVENTION, so every block is an excerpt from the
// `*.Client` companion a reader is writing: an illustrative `IFooApi`
// contract, the module-level `fooApi` value the first block constructs,
// and the companion's Elmish `Msg`. None of them are SDK surface — they
// are what the reader's own module supplies. A block that declares its
// own `fooApi` shadows this one, which is why they sit in an auto-opened
// module.
open ToolUp.Elmish
open ToolUp.Remoting.Client

[<AutoOpen>]
module PageAmbient =

    /// A companion's ToolUp.Remoting contract. A record of functions is
    /// the shape `Remoting.buildProxy` requires.
    type IFooApi = {
        GetFoo: unit -> Async<Result<string, string>>
    }

    /// The hosting module's Elmish message type.
    type Msg = FooLoaded of Result<string, string>

    let fooApi: IFooApi = failwith "ambient"
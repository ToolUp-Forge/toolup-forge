# The module shape

A ToolUp domain module is four files. HelloWorld's module is the minimal example.

## SharedTypes

Defines the API record that crosses the client/server boundary:

```fsharp
type HelloWorldApi = { Echo: string -> Async<string> }
```

## Server

Owns the domain logic as a pure routine:

```fsharp
let echoRoutine (request: string) : string = ...
```

The composition root — not the module — owns the HTTP wiring, so the same
routine can be tested in isolation and reused.

## ClientModel / ClientView

The Elmish model, update, and Feliz view for the module's UI. These are
source-injected into a client project via the module's `.Client.props`.

## Why four files

The split keeps domain logic (Server) separate from presentation (ClientView)
and shared contracts (SharedTypes), so each compiles for the right target and
the shell handles all wiring. Consumers add modules by dropping in these four
files and registering the module in their composition root.

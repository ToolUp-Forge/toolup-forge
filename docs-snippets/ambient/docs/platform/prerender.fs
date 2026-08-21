// Ambient context for `docs/platform/prerender.md`.
//
// The route list is declared in the page's first block and read from the
// FAKE-seam block below it, the way a reader who scrolled past it would.
// A block that declares its own `prerenderRoutes` shadows this one, which
// is why the declaration sits in an auto-opened module.
[<AutoOpen>]
module PageAmbient =

    let prerenderRoutes: PrerenderRoute list = failwith "ambient"
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// A scripted XMLHttpRequest for the packs that run the real proxy under
/// Node, which has no XMLHttpRequest of its own. Installed once per page
/// on first use (one global, shared by every pack that installs it); each
/// request is answered with the scripted status and body, and recorded.
module ToolUp.AI.Client.Tests.XhrStub

open Fable.Core
open ToolUp.Remoting.Client

[<Emit("""(() => {
    if (globalThis.__xhrStub) return;
    const stub = { calls: [], status: 200, body: '' };
    globalThis.__xhrStub = stub;
    globalThis.XMLHttpRequest = class {
        constructor() { this.readyState = 0; this.status = 0; this.responseText = ''; this.headers = {}; }
        open(method, url) { this.method = method; this.url = url; }
        setRequestHeader(key, value) { this.headers[key] = value; }
        abort() { }
        send(body) {
            stub.calls.push({ method: this.method, url: this.url, body });
            this.status = stub.status;
            this.responseText = stub.body;
            this.readyState = 4;
            queueMicrotask(() => { if (this.onreadystatechange) this.onreadystatechange(); });
        }
    };
})()""")>]
let private installJs () : unit = jsNative

/// Select the XHR transport (`fetch` is the default) and install the stub.
let install () : unit =
    Http.useTransport Http.Transport.Xhr
    installJs ()

/// Answer every later request with `status` and `body`.
[<Emit("(globalThis.__xhrStub.status = $0, globalThis.__xhrStub.body = $1, undefined)")>]
let scriptResponse (status: int) (body: string) : unit = jsNative

/// How many requests have been sent since the stub was installed.
[<Emit("globalThis.__xhrStub.calls.length")>]
let callCount () : int = jsNative

/// The URL of the `i`th request.
[<Emit("globalThis.__xhrStub.calls[$0].url")>]
let callUrl (i: int) : string = jsNative

/// The body of the `i`th request.
[<Emit("globalThis.__xhrStub.calls[$0].body")>]
let callBody (i: int) : string = jsNative
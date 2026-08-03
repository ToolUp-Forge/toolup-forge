// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
//
// Node leg of the cross-runtime federation conformance harness.
//
// The harness writes the generated TypeScript client next to this file as
// `client.ts`, starts a loopback receiver, and runs:
//
//   node [--experimental-strip-types] driver.mjs <baseUrl> <token> <callerPeerId>
//
// The driver performs a FIXED sequence of calls. The receiver scripts its
// replies by request ordinal, so the sequence below and the harness's
// script are two halves of one contract — change one and you must change
// the other. Each leg's outcome is reported; nothing is asserted here.
// Interpreting the report is the harness's job, in F#, where the corpus
// and the live receiver are both in reach.
//
// The client is imported by shape rather than by name so this driver does
// not encode the generator's class-naming rule a second time.

const [baseUrl, token, callerPeerId] = process.argv.slice(2);

const module_ = await import("./client.ts");

const ClientClass = Object.values(module_).find(
  (exported) => typeof exported === "function" && exported.name.endsWith("Client"),
);

if (!ClientClass) {
  throw new Error("the generated module exports no *Client class");
}

const client = new ClientClass(baseUrl, token, callerPeerId);

const legs = [];

/** Run one leg, recording either its value or the failure it raised. */
async function leg(name, call) {
  try {
    legs.push({ name, ok: true, value: await call() });
  } catch (error) {
    legs.push({ name, ok: false, error: String(error && error.message ? error.message : error) });
  }
}

// 1. Capability handshake against the live receiver.
await leg("capabilities", () => client.capabilities());

// 2. An immediate call dispatched by the live receiver.
await leg("immediate", () => client.PlaceOrder("order-42", { Quantity: 3 }));

// 3. The same call, into a handler the receiver fails — a structured
//    PeerError arriving on a 200, which the client must raise.
await leg("handlerError", () => client.PlaceOrder("boom", { Quantity: 1 }));

// 4-6. The same call three more times, answered with corpus bytes: the
//      specified success response, the specified unauthorized failure,
//      and a document that is not well-formed at all.
await leg("corpusResult", () => client.PlaceOrder("order-42", { Quantity: 3 }));
await leg("corpusError", () => client.PlaceOrder("order-42", { Quantity: 3 }));
await leg("corpusMalformed", () => client.PlaceOrder("order-42", { Quantity: 3 }));

// 7-9. The long-running poll leg, answered with each of the three
//      terminal states the corpus pins, in the corpus's own order.
await leg("pollPending", () => client.pollReconcileLedger("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
await leg("pollCompleted", () => client.pollReconcileLedger("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
await leg("pollFailed", () => client.pollReconcileLedger("7c9e6679-7425-40de-944b-e07fc1f90ae7"));

process.stdout.write(JSON.stringify({ runtime: "node", legs }));

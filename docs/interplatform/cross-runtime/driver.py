# SPDX-License-Identifier: Apache-2.0
# Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
#
# Python leg of the cross-runtime federation conformance harness. The twin
# of `driver.mjs` — same fixed call sequence, same report shape, so the
# harness interprets both through one code path and any divergence it
# finds is a divergence between the two GENERATED CLIENTS rather than
# between two hand-written drivers.
#
# The harness writes the generated client next to this file as `client.py`
# and runs:
#
#   python driver.py <baseUrl> <token> <callerPeerId>

import importlib
import inspect
import json
import sys

base_url, token, caller_peer_id = sys.argv[1], sys.argv[2], sys.argv[3]

module = importlib.import_module("client")

client_class = next(
    value
    for name, value in vars(module).items()
    if inspect.isclass(value) and name.endswith("Client")
)

client = client_class(base_url, token, caller_peer_id)

legs = []


def leg(name, call):
    """Run one leg, recording either its value or the failure it raised."""
    try:
        legs.append({"name": name, "ok": True, "value": call()})
    except Exception as error:  # noqa: BLE001 - the failure IS the observation
        legs.append({"name": name, "ok": False, "error": str(error)})


# 1. Capability handshake against the live receiver.
leg("capabilities", lambda: client.capabilities())

# 2. An immediate call dispatched by the live receiver.
leg("immediate", lambda: client.PlaceOrder("order-42", {"Quantity": 3}))

# 3. The same call, into a handler the receiver fails — a structured
#    PeerError arriving on a 200, which the client must raise.
leg("handlerError", lambda: client.PlaceOrder("boom", {"Quantity": 1}))

# 4-6. The same call three more times, answered with corpus bytes: the
#      specified success response, the specified unauthorized failure, and
#      a document that is not well-formed at all.
leg("corpusResult", lambda: client.PlaceOrder("order-42", {"Quantity": 3}))
leg("corpusError", lambda: client.PlaceOrder("order-42", {"Quantity": 3}))
leg("corpusMalformed", lambda: client.PlaceOrder("order-42", {"Quantity": 3}))

# 7-9. The long-running poll leg, answered with each of the three terminal
#      states the corpus pins, in the corpus's own order.
leg("pollPending", lambda: client.poll_ReconcileLedger("7c9e6679-7425-40de-944b-e07fc1f90ae7"))
leg("pollCompleted", lambda: client.poll_ReconcileLedger("7c9e6679-7425-40de-944b-e07fc1f90ae7"))
leg("pollFailed", lambda: client.poll_ReconcileLedger("7c9e6679-7425-40de-944b-e07fc1f90ae7"))

sys.stdout.write(json.dumps({"runtime": "python", "legs": legs}))

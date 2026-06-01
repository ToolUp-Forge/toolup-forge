#!/bin/sh
# ─── ToolUp.Platform — Docker HEALTHCHECK wrapper (Phase 16b) ─────────
#
# Probes the Phase 9k `/health` Liveness endpoint with a bounded
# timeout. Exits 0 on a 2xx response, 1 on anything else. The Docker
# HEALTHCHECK directive interprets exit 0 as healthy, exit 1 as
# unhealthy; the surrounding `--interval` / `--retries` policy in the
# Dockerfile decides how many consecutive failures flip the container
# state.
#
# `TOOLUP_HEALTHCHECK_URL` overrides the default probe target so a
# deployment exposing a different port or a sidecar-resolved hostname
# can re-point the check without rebuilding. Defaults to the in-image
# `http://localhost:5000/health`.

set -eu

PROBE_URL=${TOOLUP_HEALTHCHECK_URL:-http://localhost:5000/health}
TIMEOUT_SECONDS=${TOOLUP_HEALTHCHECK_TIMEOUT:-5}

# `--max-time` caps the whole curl invocation; `--fail` flips a 4xx /
# 5xx response into a non-zero exit; `--silent --show-error` keeps the
# output noise-free except when a probe actually errors (visible in
# `docker inspect`'s Health.Log).
exec curl --silent --show-error --fail \
    --max-time "${TIMEOUT_SECONDS}" \
    --output /dev/null \
    "${PROBE_URL}"

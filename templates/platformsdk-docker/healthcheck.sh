#!/bin/sh
# Generated from ToolUp.Hosts.Docker / dotnet new platformsdk-docker.
# Bounded-timeout wrapper around the Phase 9k /health Liveness probe.

set -eu

PROBE_URL=${TOOLUP_HEALTHCHECK_URL:-http://localhost:5000/health}
TIMEOUT_SECONDS=${TOOLUP_HEALTHCHECK_TIMEOUT:-5}

exec curl --silent --show-error --fail \
    --max-time "${TIMEOUT_SECONDS}" \
    --output /dev/null \
    "${PROBE_URL}"

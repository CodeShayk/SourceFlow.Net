#!/usr/bin/env bash
#
# Launches LocalStack via Docker and runs AWS integration tests locally.
#
# Usage:
#   ./run-integration-tests.sh                         # run all tests, stop container after
#   ./run-integration-tests.sh --keep                  # keep container running after tests
#   ./run-integration-tests.sh --filter "Name~SqsStandard"  # run subset of tests
#   ./run-integration-tests.sh --configuration Release       # use Release build
#
set -euo pipefail

CONTAINER_NAME="sourceflow-localstack"
LOCALSTACK_PORT=4566
LOCALSTACK_ENDPOINT="http://localhost:${LOCALSTACK_PORT}"
HEALTH_URL="${LOCALSTACK_ENDPOINT}/_localstack/health"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"

KEEP_RUNNING=false
FILTER=""
CONFIGURATION="Debug"

# --- Parse arguments ---
while [[ $# -gt 0 ]]; do
    case "$1" in
        --keep)       KEEP_RUNNING=true; shift ;;
        --filter)     FILTER="$2"; shift 2 ;;
        --configuration) CONFIGURATION="$2"; shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

# --- Helper functions ---

step() { printf "\n\033[36m>> %s\033[0m\n" "$1"; }
ok()   { printf "\033[32m%s\033[0m\n" "$1"; }
warn() { printf "\033[33m%s\033[0m\n" "$1"; }
err()  { printf "\033[31m%s\033[0m\n" "$1"; }

check_docker() {
    if ! docker info >/dev/null 2>&1; then
        err "Docker is not running. Please start Docker and try again."
        exit 1
    fi
}

check_localstack_healthy() {
    curl -sf "$HEALTH_URL" >/dev/null 2>&1
}

wait_for_localstack() {
    local max_attempts=${1:-30}
    local delay=${2:-3}
    echo "Waiting for LocalStack to become healthy..."
    for ((i=1; i<=max_attempts; i++)); do
        if check_localstack_healthy; then
            ok "LocalStack is healthy!"
            curl -s "$HEALTH_URL" | python3 -m json.tool 2>/dev/null || curl -s "$HEALTH_URL"
            return 0
        fi
        echo "  Attempt $i/$max_attempts - not ready yet..."
        sleep "$delay"
    done
    err "LocalStack did not become healthy in time."
    return 1
}

wait_for_services() {
    local services=("sqs" "sns" "kms")
    local max_attempts=20
    local delay=3
    echo "Waiting for services: ${services[*]}..."
    for ((i=1; i<=max_attempts; i++)); do
        local all_ready=true
        local health
        health=$(curl -sf "$HEALTH_URL" 2>/dev/null) || { all_ready=false; }
        if $all_ready; then
            for svc in "${services[@]}"; do
                local status
                status=$(echo "$health" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('services',{}).get('$svc',''))" 2>/dev/null || echo "")
                if [[ "$status" != "available" && "$status" != "running" ]]; then
                    all_ready=false
                    break
                fi
            done
        fi
        if $all_ready; then
            ok "All services ready!"
            return 0
        fi
        echo "  Attempt $i/$max_attempts - services not all ready..."
        sleep "$delay"
    done
    err "Services did not become ready in time."
    return 1
}

# --- Main ---

step "Checking Docker"
check_docker
ok "Docker is running."

ALREADY_RUNNING=false

# Check for existing container
existing=$(docker ps --filter "name=$CONTAINER_NAME" --format "{{.Names}}" 2>/dev/null || true)
if [[ "$existing" == "$CONTAINER_NAME" ]]; then
    step "Found existing LocalStack container '$CONTAINER_NAME'"
    if check_localstack_healthy; then
        ok "Container is healthy - reusing it."
        ALREADY_RUNNING=true
    else
        warn "Container exists but not healthy. Removing and recreating..."
        docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
    fi
else
    # Check if port is in use by another container
    if docker ps --format "{{.Ports}}" 2>/dev/null | grep -q ":${LOCALSTACK_PORT}->"; then
        if check_localstack_healthy; then
            ok "LocalStack is responding on port $LOCALSTACK_PORT - reusing it."
            ALREADY_RUNNING=true
        else
            err "Port $LOCALSTACK_PORT is in use but not responding as LocalStack."
            echo "Please free port $LOCALSTACK_PORT and try again."
            exit 1
        fi
    fi
fi

if ! $ALREADY_RUNNING; then
    step "Starting LocalStack container"
    docker run -d \
        --name "$CONTAINER_NAME" \
        -p "${LOCALSTACK_PORT}:${LOCALSTACK_PORT}" \
        -e "SERVICES=sqs,sns,kms" \
        -e "DEBUG=1" \
        -e "EAGER_SERVICE_LOADING=1" \
        -e "SKIP_SSL_CERT_DOWNLOAD=1" \
        localstack/localstack:latest

    ok "Container started."
fi

step "Waiting for LocalStack health"
if ! wait_for_localstack; then
    warn "Dumping container logs for diagnostics:"
    docker logs "$CONTAINER_NAME" 2>&1 | tail -30
    exit 1
fi

step "Waiting for AWS services"
if ! wait_for_services; then
    warn "Dumping container logs for diagnostics:"
    docker logs "$CONTAINER_NAME" 2>&1 | tail -30
    exit 1
fi

step "Setting environment variables"
export AWS_ACCESS_KEY_ID="test"
export AWS_SECRET_ACCESS_KEY="test"
export AWS_DEFAULT_REGION="us-east-1"
export AWS_ENDPOINT_URL="$LOCALSTACK_ENDPOINT"

echo "  AWS_ACCESS_KEY_ID     = $AWS_ACCESS_KEY_ID"
echo "  AWS_SECRET_ACCESS_KEY = $AWS_SECRET_ACCESS_KEY"
echo "  AWS_DEFAULT_REGION    = $AWS_DEFAULT_REGION"
echo "  AWS_ENDPOINT_URL      = $AWS_ENDPOINT_URL"

step "Running integration tests"
test_args=(
    test "$PROJECT_DIR"
    --configuration "$CONFIGURATION"
    --logger "console;verbosity=normal"
    -- "RunConfiguration.TestSessionTimeout=600000"
)

if [[ -n "$FILTER" ]]; then
    test_args+=(--filter "$FILTER")
fi

set +e
dotnet "${test_args[@]}"
TEST_EXIT=$?
set -e

if ! $KEEP_RUNNING && ! $ALREADY_RUNNING; then
    step "Stopping LocalStack container"
    docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
    ok "Container removed."
else
    echo ""
    warn "LocalStack container '$CONTAINER_NAME' is still running."
    echo "  Stop it with: docker rm -f $CONTAINER_NAME"
fi

echo ""
if [[ $TEST_EXIT -eq 0 ]]; then
    ok "All tests passed!"
else
    err "Some tests failed (exit code: $TEST_EXIT)."
fi

exit $TEST_EXIT

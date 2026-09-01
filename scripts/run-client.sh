#!/bin/bash
#
# Runs the NBomber client once, against one server, with the parameters given on the command line.
#
# This script holds no experiment knowledge. It does not know which parameters
# belong together or which comparisons are meaningful,
# nor does it validate the load parameters: the client already rejects a wrong value and the servers
# reject it a second time on arrival. Repeating those checks here would only add a third copy that
# could become misaligned.
#
# What this script does is the quality of life work around a test:
#   - infers a report folder name from the parameters and creates it, because the client requires
#     the directory passed to --output-dir to be already existing
#   - prints the matching run-server.ps1 command
#   - warns when the requested load would saturate the link or exhaust the client's ephemeral ports
#
# Coordination: this script probes the server's port and starts
# by itself once the port answers, so the two sides can be started in either order and there is no
# reaction time window to miss.
#
# Both banchmark configuration use this same script with the same parameters. The loopback benchmark passes --affinity
# on both sides so client and server get a different set of cores; the LAN benchmark is simply not passed --affinity.

set -euo pipefail

HOST=""
PROTOCOL=""
TLS="off"
LABEL=""
OUTPUT_DIR="$HOME/Reports"
LAUNCHER="${LAUNCHER:-$(dirname "$0")/../NBombLauncher/bin/Release/net10.0/NBombLauncher.dll}"
# An array because --report-format is documented as repeatable and the client accepts several
# values, so assigning a scalar here would have kept only the last requested.
REPORT_FORMATS=()
DRY_RUN=false
SERVER_AFFINITY=""

# Advisory variables. None of these have effect on the tests, they only decide whether a warning prints.
LINK_MBPS=1000          # 1 GbE. Set to the speed the link negotiated.
EPHEMERAL_PORTS=16384   # Windows default range. After Configure-TcpStack.ps1 -Mode test: 64511.
TIME_WAIT=240           # Windows default seconds. After Configure-TcpStack.ps1 -Mode test: 30.
                        # not less because Windows 10 and later clamp TcpTimedWaitDelay below 30 s.

# Load parameters. Empty means not passed by the user: the option is left off the command line so
# the client's own default applies.
PAYLOAD=""
INTENSITY=""
KIND=""
EXECUTION=""
OPS_PER_SESSION=""
PROFILE=""
RPS=""
DURATION=""
MAX_RPS=""
STEPS=""
STEP_DURATION=""
WARMUP=""
TIMEOUT=""
REPEAT=""
START_DELAY=""
AFFINITY=""

usage() {
  cat <<'EOF'
Usage:
  run-client.sh --host <host> --protocol <http1|http2|websocket|grpc> [options]

Required:
  --host            Host name or address of the server under test.
  --protocol        http1 | http2 | websocket | grpc.

Run options:
  --tls             Transport security: on | off. Default off.
  --label           Report folder name, shared with the server side. Default inferred from the parameters.
  --output-dir      Report root folder. Default ~/Reports.
  --launcher        Path to NBombLauncher.dll.
  --report-format   html | csv | md | txt. Repeatable. Default: html and csv.
  --affinity        Processor affinity mask for the client, e.g. 0xFFFF0000. Loopback benchmark only.
  --server-affinity Mask to print in the run-server.ps1 line, e.g. 0x0000FFFF. Loopback benchmark only.
                    Not passed to the client beacuse it only shapes the command to copy to the server.
  --dry-run         Print the commands without running an actual test.
  -h, --help        This message.

Advisory options (they don't change the test's behaviour, only whether a warning is printed):
  --link-mbps        Link speed in Mbit/s. Default 1000.
  --ephemeral-ports  Client ephemeral port range size. Default 16384 (untuned Windows).
  --time-wait        Seconds a closed port stays in TIME_WAIT. Default 240 (untuned Windows).

Load parameters, passed straight through to the client. Anything not set keeps the client's default:
  --payload            null | small | medium | large | extreme.
  --intensity          null | low | medium | high | extreme.
  --kind               cpu | IO.
  --execution          blocking | async.
  --ops-per-session    Operations per session. Multiplies the offered load: see the note below.
  --profile            load | stress.
  --rps                Arrival rate in sessions per second.
  --duration           Duration of the load generation in seconds.
  --max-rps            Final plateau rate, stress profile.
  --steps              Number of plateaus, stress profile.
  --step-duration      Seconds per plateau, stress profile.
  --warmup             on | off. Default on.
  --timeout            Operation timeout in seconds.
  --repeat             Repetitions of the test configuration in one server session.
  --start-delay        Seconds to wait after the readiness probe and before starting the load.

Note on --rps and --ops-per-session:
  --rps injects sessions per second, so the server sees "--rps x --ops-per-session operations per
  second", while the number of connections per second stays at --rps. To compare different session
  lengths hold the operation rate constant: select a target and set --rps = target / --ops-per-session.
  Holding --rps fixed instead leaves the connection rate identical in every test.

Examples:
  run-client.sh --host 192.168.1.10 --protocol http1 --payload null --intensity null --rps 2000 --duration 60
  run-client.sh --host localhost --protocol grpc --tls on --ops-per-session 16 --rps 125 --duration 60
  run-client.sh --host localhost --protocol websocket --affinity 0xFFFF0000 --repeat 3
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --host)              HOST="$2"; shift 2 ;;
    --protocol)          PROTOCOL="$2"; shift 2 ;;
    --tls)               TLS="$2"; shift 2 ;;
    --label)             LABEL="$2"; shift 2 ;;
    --output-dir)        OUTPUT_DIR="$2"; shift 2 ;;
    --launcher)          LAUNCHER="$2"; shift 2 ;;
    --report-format)     REPORT_FORMATS+=("$2"); shift 2 ;;
    --affinity)          AFFINITY="$2"; shift 2 ;;
    --server-affinity)   SERVER_AFFINITY="$2"; shift 2 ;;
    --link-mbps)         LINK_MBPS="$2"; shift 2 ;;
    --ephemeral-ports)   EPHEMERAL_PORTS="$2"; shift 2 ;;
    --time-wait)         TIME_WAIT="$2"; shift 2 ;;
    --dry-run)           DRY_RUN=true; shift ;;
    --payload)           PAYLOAD="$2"; shift 2 ;;
    --intensity)         INTENSITY="$2"; shift 2 ;;
    --kind)              KIND="$2"; shift 2 ;;
    --execution)         EXECUTION="$2"; shift 2 ;;
    --ops-per-session)   OPS_PER_SESSION="$2"; shift 2 ;;
    --profile)           PROFILE="$2"; shift 2 ;;
    --rps)               RPS="$2"; shift 2 ;;
    --duration)          DURATION="$2"; shift 2 ;;
    --max-rps)           MAX_RPS="$2"; shift 2 ;;
    --steps)             STEPS="$2"; shift 2 ;;
    --step-duration)     STEP_DURATION="$2"; shift 2 ;;
    --warmup)            WARMUP="$2"; shift 2 ;;
    --timeout)           TIMEOUT="$2"; shift 2 ;;
    --repeat)            REPEAT="$2"; shift 2 ;;
    --start-delay)       START_DELAY="$2"; shift 2 ;;
    -h|--help)           usage; exit 0 ;;
    *) echo "Unknown argument: $1. Use --help for the list of options." >&2; exit 1 ;;
  esac
done

[[ -n "$HOST" ]]     || { echo "--host is required" >&2; exit 1; }
[[ -n "$PROTOCOL" ]] || { echo "--protocol is required" >&2; exit 1; }
[[ -f "$LAUNCHER" ]] || { echo "Launcher not found at '$LAUNCHER'" >&2; exit 1; }

# --tls and --protocol are the two values this script interprets itself rather than passing
# through, so they are validated. An unchecked --tls true would
# run in plaintext inside a folder named for TLS on and the two halves of the test would disagree
# about what was measured.
[[ "$TLS" == "on" || "$TLS" == "off" ]] || {
  echo "Invalid --tls value: '$TLS'. Expected on or off." >&2; exit 1;
}

case "$PROTOCOL" in
  http1|http2|websocket|grpc) ;;
  *) echo "Invalid --protocol value: '$PROTOCOL'. Expected http1, http2, websocket or grpc." >&2; exit 1 ;;
esac

# An unset parameter leaves the client's default in force.
cmd=(dotnet "$LAUNCHER" --host "$HOST" --protocol "$PROTOCOL" --tls "$TLS")
[[ -n "$PAYLOAD" ]]         && cmd+=(--payload "$PAYLOAD")
[[ -n "$INTENSITY" ]]       && cmd+=(--intensity "$INTENSITY")
[[ -n "$KIND" ]]            && cmd+=(--kind "$KIND")
[[ -n "$EXECUTION" ]]       && cmd+=(--execution "$EXECUTION")
[[ -n "$OPS_PER_SESSION" ]] && cmd+=(--ops-per-session "$OPS_PER_SESSION")
[[ -n "$PROFILE" ]]         && cmd+=(--profile "$PROFILE")
[[ -n "$RPS" ]]             && cmd+=(--rps "$RPS")
[[ -n "$DURATION" ]]        && cmd+=(--duration "$DURATION")
[[ -n "$MAX_RPS" ]]         && cmd+=(--max-rps "$MAX_RPS")
[[ -n "$STEPS" ]]           && cmd+=(--steps "$STEPS")
[[ -n "$STEP_DURATION" ]]   && cmd+=(--step-duration "$STEP_DURATION")
[[ -n "$WARMUP" ]]          && cmd+=(--warmup "$WARMUP")
[[ -n "$TIMEOUT" ]]         && cmd+=(--timeout "$TIMEOUT")
[[ -n "$REPEAT" ]]          && cmd+=(--repeat "$REPEAT")
[[ -n "$START_DELAY" ]]     && cmd+=(--start-delay "$START_DELAY")
[[ -n "$AFFINITY" ]]        && cmd+=(--affinity "$AFFINITY")

# Under `set -u` expanding an empty array is an
# error in bash versions before 4.4. This script has to run the bash version available.
if (( ${#REPORT_FORMATS[@]} > 0 )); then
  for format in "${REPORT_FORMATS[@]}"; do cmd+=(--report-format "$format"); done
fi

# The label must be recognisable in the report tree and reused as the server-side counter label so
# the two halves of a test can be paired afterwards.
if [[ -z "$LABEL" ]]; then
  LABEL="$PROTOCOL-tls_$TLS"
  [[ -n "$PAYLOAD" ]]         && LABEL+="-p$PAYLOAD"
  [[ -n "$INTENSITY" ]]       && LABEL+="-i$INTENSITY"
  [[ -n "$KIND" ]]            && LABEL+="-k$KIND"
  [[ -n "$EXECUTION" ]]       && LABEL+="-x$EXECUTION"
  [[ -n "$OPS_PER_SESSION" ]] && LABEL+="-n$OPS_PER_SESSION"
  [[ -n "$PROFILE" ]]         && LABEL+="-$PROFILE"
fi

REPORT_DIR="$OUTPUT_DIR/$LABEL"

# The client could be a Windows .NET process even when this script runs under Git Bash and it cannot
# resolve a POSIX path: "/c/Users/<user>/Reports" is not interpreted as a directory, so
# the test stops at the --output-dir validator with an error message that points to the directory rather than
# the path. This matters on the loopback benchmark where both halves run on the
# Windows machine and this script is therefore launched from Git Bash. cygpath exists only on
# Git Bash and Cygwin, so its absence is the condition where no translation is wanted.
REPORT_DIR_ARG="$REPORT_DIR"
if command -v cygpath >/dev/null 2>&1; then
  REPORT_DIR_ARG="$(cygpath -w "$REPORT_DIR")"
fi

cmd+=(--label "$LABEL" --output-dir "$REPORT_DIR_ARG")

# How long the server has to recor to cover the client load generation window. It is overestimated: the
# client records the window of its measured phase in run-info.txt. Overrecording costs only a few kilobytes
# while underrecording loses the test accuracy.
record_seconds() {
  local warm=5
  [[ "$WARMUP" == "off" ]] && warm=0

  local reps="${REPEAT:-1}"
  local delay="${START_DELAY:-5}"
  local per_rep

  if [[ "${PROFILE:-load}" == "stress" ]]; then
    local steps="${STEPS:-8}" step_duration="${STEP_DURATION:-30}"
    per_rep=$(( steps * step_duration + (steps - 1) * 5 + warm + 10 ))
  else
    per_rep=$(( ${DURATION:-60} + warm + 10 ))
  fi

  echo $(( reps * per_rep + delay + 60 ))
}

# Advisory only. The response size multiplied by the operation rate sets a throughput ceiling because
# past that point every protocol converges because of the network constraint.
# Parameters are never altered, only a warning is printed. The byte scale mirrors OperationsHandler.PayloadSizes.
# It is duplicated here as it is the only way for a script to know the response size.
warn_bandwidth() {
  local bytes peak_ops required capacity threshold n

  # Loopback does not saturates a network.
  # The machine's own NIC is always running, so the server counter would record a column of
  # unrelated values unless it is declined: hence the -NetworkAdapter none in the suggested
  # command line above, explained in this note.
  if [[ "$HOST" == "localhost" || "$HOST" == "127.0.0.1" || "$HOST" == "::1" ]]; then
    echo "  NOTE: loopback run. No link ceiling applies and loopback traffic crosses no adapter,"
    echo "        so the suggested server command declines the network counter with"
    echo "        -NetworkAdapter none rather than recording a column of random data."
    echo
    return 0
  fi

  case "${PAYLOAD:-small}" in
    null)    bytes=0       ;;
    small)   bytes=1000    ;;
    medium)  bytes=10000   ;;
    large)   bytes=100000  ;;
    extreme) bytes=1000000 ;;
    *) return 0 ;;   # Unrecognised value.
  esac
  (( bytes == 0 )) && return 0

  if [[ "${PROFILE:-load}" == "stress" ]]; then peak_ops="${MAX_RPS:-1000}"; else peak_ops="${RPS:-10}"; fi
  [[ "$peak_ops" =~ ^[0-9]+$ ]] || return 0

  # The rate is in sessions per second; the bytes are for a single operation.
  n="${OPS_PER_SESSION:-1}"
  [[ "$n" =~ ^[0-9]+$ ]] || n=1
  peak_ops=$(( peak_ops * n ))

  required=$(( bytes * peak_ops ))
  capacity=$(( LINK_MBPS * 125000 ))
  threshold=$(( capacity * 70 / 100 ))

  if (( required > threshold )); then
    echo "  WARNING: $peak_ops ops/s x $bytes bytes = $(( required / 1000000 )) MB/s requested,"
    echo "           above 70% of a ${LINK_MBPS} Mbit/s link ($(( capacity / 1000000 )) MB/s)."
    echo "           This result risks being network bound rather than server bound."
    echo
  fi
}

# Advisory only. Every session opens and closes one connection and
# the port is held in TIME_WAIT. Past ports/TIME_WAIT
# connections per second the client runs out of ephemeral ports and reports connection failures
# that look like a server fault but are not.
warn_ephemeral_ports() {
  local sessions sustainable

  if [[ "${PROFILE:-load}" == "stress" ]]; then sessions="${MAX_RPS:-1000}"; else sessions="${RPS:-10}"; fi
  [[ "$sessions" =~ ^[0-9]+$ ]] || return 0

  sustainable=$(( EPHEMERAL_PORTS / TIME_WAIT ))

  if (( sessions > sustainable )); then
    echo "  WARNING: $sessions connections/s requested, but $EPHEMERAL_PORTS ephemeral ports with a"
    echo "           ${TIME_WAIT}s TIME_WAIT sustain only about $sustainable/s."
    echo "           Expect connection failures on the CLIENT that look like server faults."
    echo "           Either raise --ops-per-session (fewer connections for the same server work), or run"
    echo "           Configure-TcpStack.ps1 -Mode test and pass --ephemeral-ports 64511 --time-wait 30."
    echo
  fi
}

SERVER_CMD=".\\run-server.ps1 -Protocol $PROTOCOL -Tls $TLS -RecordSeconds $(record_seconds) -Label $LABEL"
# The server mask is not passed to the client, which has its own --affinity: it only completes the
# command line printed for the other half of the test.
[[ -n "$SERVER_AFFINITY" ]] && SERVER_CMD+=" -Affinity $SERVER_AFFINITY"

# On loopback the machine's NIC is up and would be detected. This is also a printed suggestion.
case "$HOST" in
  localhost|127.0.0.1|::1) SERVER_CMD+=" -NetworkAdapter none" ;;
esac

echo
echo "=== $LABEL ==="
echo
echo "  Start the server with:"
echo
echo "    $SERVER_CMD"
echo
echo "  The client waits for that port to answer, so it can be started in either order."
echo

warn_bandwidth
warn_ephemeral_ports

if [[ "$DRY_RUN" == true ]]; then
  echo "  Client command (not executed):"
  printf '    '
  printf '%q ' "${cmd[@]}"
  printf '\n\n'
  exit 0
fi

mkdir -p "$REPORT_DIR"
"${cmd[@]}"

echo
echo "Report under $REPORT_DIR_ARG"
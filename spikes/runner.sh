#!/bin/bash
# Game_CLIENT_RUNNER for the spikes (Linux/Proton): preload the spike host, then run the client as usual.
# Put this script, spikehost.so, agent.js, an empty inbox.js and an overlay/ folder together in a folder inside the
# LocalExile checkout (the Proton container does not see /tmp), then start the client only through live.ps1:
#   Game_CLIENT_RUNNER=/path/to/runner.sh pwsh scripts/dev/live.ps1
D="$(cd "$(dirname "$0")" && pwd)"
export NATIVEMODS_IMAGE="$(readlink -f "$1")"
export NATIVEMODS_AGENT="$D/agent.js" NATIVEMODS_INBOX="$D/inbox.js" NATIVEMODS_LOG="$D/out.log" NATIVEMODS_OVERLAY="$D/overlay"
export LD_PRELOAD="$D/spikehost.so${LD_PRELOAD:+:$LD_PRELOAD}"
exec "${NATIVEMODS_RUNNER:-umu-run}" "$@"

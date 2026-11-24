#!/usr/bin/env bash
set -euo pipefail
out=${1:?usage: test-preview.sh BUILD_DIRECTORY}
app="$out/Usage Lite.app/Contents/MacOS/UsageLite"
fixture=$(mktemp -d)
app_pid=
trap 'if [[ -n $app_pid ]]; then kill "$app_pid" 2>/dev/null || true; fi; rm -rf "$fixture"' EXIT

python3 - "$fixture" <<'PY'
import datetime as dt
import json
import pathlib
import sqlite3
import sys

home = pathlib.Path(sys.argv[1]); codex = home / '.codex'
sessions = codex / 'sessions'; sessions.mkdir(parents=True)
now = dt.datetime.now(dt.timezone.utc)
ts = int(now.timestamp())
with sqlite3.connect(codex / 'logs_2.sqlite') as conn:
    conn.execute('CREATE TABLE logs(ts INTEGER, ts_nanos INTEGER, id INTEGER PRIMARY KEY, feedback_log_body TEXT)')
    limits = {'limit_id': 'codex', 'primary': {'window_minutes': 10080, 'used_percent': 24, 'resets_at': ts + 86400}}
    event = {'type': 'codex.rate_limits', 'rate_limits': limits}
    conn.execute('INSERT INTO logs VALUES (?, 0, 1, ?)', (ts, 'websocket event: ' + json.dumps(event)))

profile = home / 'Library' / 'Application Support' / 'Claude'; profile.mkdir(parents=True)
history = {'version': 2, 'samples': [{'t': ts * 1000, 'org': 'fixture', 'u': {'fh': 10, 'sd': 95}}]}
(profile / 'plan-usage-history.json').write_text(json.dumps(history))
PY

"$app" --smoke-fixture "$fixture"
"$app" --export-previews "$out/ui"
for name in dual-quota weekly-only no-records; do
    test -s "$out/ui/$name.png"
    sips -g pixelWidth -g pixelHeight "$out/ui/$name.png"
done

# Start the actual menu bar app as well as exercising the offscreen renderer.
"$app" &
app_pid=$!
sleep 4
kill -0 "$app_pid"
kill "$app_pid"
wait "$app_pid" || true
app_pid=

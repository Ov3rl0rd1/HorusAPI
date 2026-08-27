#!/bin/sh
# Lightweight scheduler: sleeps most of the time, wakes to run one backup. Matches the
# repo's other sidecar (nginx/sync-releases.sh) rather than pulling in cron.
set -eu

: "${BACKUP_ENABLED:=false}"
: "${BACKUP_INTERVAL:=86400}"   # seconds between runs (default: daily)

if [ "$BACKUP_ENABLED" != "true" ]; then
    echo "[backup] disabled (BACKUP_ENABLED != true) — idling."
    while true; do sleep 3600; done
fi

echo "[backup] enabled; interval=${BACKUP_INTERVAL}s remote=${RCLONE_REMOTE:-unset}"
while true; do
    echo "[backup] run starting $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if /usr/local/bin/backup.sh; then
        echo "[backup] run ok"
    else
        echo "[backup] run FAILED (exit $?)" >&2
    fi
    sleep "$BACKUP_INTERVAL"
done

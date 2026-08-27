#!/bin/sh
# One backup run: dump → encrypt → upload → prune. Streams throughout, so peak memory
# is tiny (the DB is a few MB); safe on a 1 GB / 1 core / 20 GB box.
#
# The dump is encrypted to an age RECIPIENT (public key). The matching private key is
# NEVER on this server — decrypt/restore happens off-box, so a server compromise cannot
# read the backups. Only ciphertext ever leaves the machine.
set -eu

: "${AGE_RECIPIENT:?set AGE_RECIPIENT (age1... public key)}"
: "${RCLONE_REMOTE:?set RCLONE_REMOTE (e.g. r2:horus-backups)}"
: "${PGDATABASE:=horus}"

TS=$(date -u +%Y%m%dT%H%M%SZ)
DOW=$(date -u +%u)   # 1..7 (7 = Sunday)
DOM=$(date -u +%d)   # 01..31

# Grandfather-father-son: month roll on the 1st, week roll on Sunday, else daily.
TIER=daily
[ "$DOW" = "7" ]  && TIER=weekly
[ "$DOM" = "01" ] && TIER=monthly

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
FILE="horus-${TS}.dump.age"
OUT="$TMP/$FILE"

# Custom-format (-Fc), compressed (-Z6) dump, encrypted on the fly.
pg_dump -Fc -Z6 | age -r "$AGE_RECIPIENT" -o "$OUT"
SIZE=$(wc -c < "$OUT")
echo "[backup] dumped+encrypted ${SIZE} bytes → ${TIER}/${FILE}"

rclone copyto "$OUT" "${RCLONE_REMOTE}/${TIER}/${FILE}" --s3-no-check-bucket
echo "[backup] uploaded ${TIER}/${FILE}"

# Keep the newest N in each tier; filenames sort chronologically (ISO timestamp).
prune() {
    tier=$1; keep=$2
    files=$(rclone lsf "${RCLONE_REMOTE}/${tier}/" 2>/dev/null | sort || true)
    count=$(printf '%s\n' "$files" | grep -c . || true)
    if [ "$count" -gt "$keep" ]; then
        remove=$((count - keep))
        printf '%s\n' "$files" | grep . | head -n "$remove" | while read -r f; do
            rclone deletefile "${RCLONE_REMOTE}/${tier}/${f}" && echo "[backup] pruned ${tier}/${f}"
        done
    fi
}
prune daily   "${BACKUP_RETENTION_DAILY:-7}"
prune weekly  "${BACKUP_RETENTION_WEEKLY:-4}"
prune monthly "${BACKUP_RETENTION_MONTHLY:-6}"

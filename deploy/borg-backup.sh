#!/usr/bin/env bash
# Nightly Borg snapshot of /var/lib/ydl to the Hetzner Storage Box.
# Prereqs (see deploy/README.md):
#   - borgbackup installed (`apt install borgbackup`)
#   - ssh keypair installed and trusted by the Storage Box account
#   - repo initialised once:  borg init --encryption=repokey-blake2 "$BORG_REPO"
#   - /etc/borg-ydl.env provides BORG_REPO, BORG_PASSPHRASE, BORG_RSH
#
# Runs via systemd timer borg-ydl.timer @ 03:00 UTC daily.

set -euo pipefail

# shellcheck disable=SC1091
source /etc/borg-ydl.env

# Use the SQLite ".backup" pipe rather than copying the live file. WAL means the live file
# can be mid-checkpoint; ".backup" gives a consistent snapshot whether the API container is
# running or not. Write to a temp file the same partition so the rename is atomic.
SNAPSHOT="/var/lib/ydl/ydl.sqlite.snapshot"
sqlite3 /var/lib/ydl/ydl.sqlite ".backup '${SNAPSHOT}'"

ARCHIVE="ydl-$(date -u +%Y%m%d-%H%M)"

borg create                                  \
    --compression zstd,3                     \
    --stats                                  \
    "${BORG_REPO}::${ARCHIVE}"               \
    "${SNAPSHOT}"                            \
    /var/lib/ydl/instruments.json

rm -f "${SNAPSHOT}"

# Retention: 7 daily, 4 weekly, 6 monthly. Older snapshots are pruned automatically.
borg prune                                   \
    --keep-daily   7                         \
    --keep-weekly  4                         \
    --keep-monthly 6                         \
    "${BORG_REPO}"

borg compact "${BORG_REPO}"

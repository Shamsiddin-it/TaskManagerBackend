#!/usr/bin/env bash
#
# Daily backup of REL-001 and REL-002: a logical dump of PostgreSQL and a mirror of the MinIO bucket.
#
# Run at 02:00 UTC from the host's scheduler, not from the application: a backup that depends on the
# application being healthy is unavailable exactly when it is needed.
#
#   BACKUP_DIR              where dumps are written; must be off the application host (REL-004)
#   CONNECTION_STRING       libpq URI for the backup account — read-only, and not the app's (REL-004)
#   MINIO_ALIAS             `mc` alias of the live bucket, e.g. live/mentortaskflow
#   MINIO_BACKUP_ALIAS      `mc` alias of the replica
#   RETAIN_DAILY            daily copies to keep (default 30, per REL-001)
#   RETAIN_MONTHLY          monthly copies to keep (default 6)

set -Eeuo pipefail

BACKUP_DIR="${BACKUP_DIR:?BACKUP_DIR is required}"
CONNECTION_STRING="${CONNECTION_STRING:?CONNECTION_STRING is required}"
RETAIN_DAILY="${RETAIN_DAILY:-30}"
RETAIN_MONTHLY="${RETAIN_MONTHLY:-6}"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
day_of_month="$(date -u +%d)"
target="${BACKUP_DIR}/daily"

# The first of the month is kept separately, so pruning the dailies cannot take the last copy that
# spans a longer incident (REL-001).
if [[ "${day_of_month}" == "01" ]]; then
  target="${BACKUP_DIR}/monthly"
fi

mkdir -p "${target}"

dump="${target}/mentortaskflow-${stamp}.dump"

echo "[backup] pg_dump -> ${dump}"

# Custom format: pg_restore can then restore selectively and in parallel, which is what keeps the
# 4-hour RTO reachable on a database of the PERF-001 profile.
pg_dump --format=custom --compress=6 --no-owner --no-privileges \
        --file="${dump}" "${CONNECTION_STRING}"

# The dump is verified before anything is pruned. An unreadable dump that replaced a readable one is
# worse than no backup at all, because it is believed.
echo "[backup] verifying the dump is readable"
pg_restore --list "${dump}" > /dev/null

sha256sum "${dump}" > "${dump}.sha256"

if [[ -n "${MINIO_ALIAS:-}" && -n "${MINIO_BACKUP_ALIAS:-}" ]]; then
  # REL-002: versioning on the live bucket protects against overwrite, the mirror against losing the
  # volume. `--remove` is deliberately absent — a deletion in the live bucket must not propagate.
  echo "[backup] mc mirror ${MINIO_ALIAS} -> ${MINIO_BACKUP_ALIAS}"
  mc mirror --overwrite "${MINIO_ALIAS}" "${MINIO_BACKUP_ALIAS}"
else
  echo "[backup] MinIO aliases not set; object storage was not mirrored" >&2
fi

echo "[backup] pruning: ${RETAIN_DAILY} daily, ${RETAIN_MONTHLY} monthly"

prune() {
  local directory="$1" keep="$2"

  [[ -d "${directory}" ]] || return 0

  # Sorted newest first; everything past the retention count goes, dumps and checksums together.
  find "${directory}" -maxdepth 1 -name 'mentortaskflow-*.dump' -printf '%T@ %p\n' \
    | sort -rn | tail -n "+$((keep + 1))" | cut -d' ' -f2- \
    | while read -r old; do
        echo "[backup] removing ${old}"
        rm -f "${old}" "${old}.sha256"
      done
}

prune "${BACKUP_DIR}/daily" "${RETAIN_DAILY}"
prune "${BACKUP_DIR}/monthly" "${RETAIN_MONTHLY}"

echo "[backup] done: ${dump}"

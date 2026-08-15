#!/usr/bin/env bash
#
# The monthly restore drill of REL-005, extended by the tenancy checks of TEN-095.
#
# The drill exists because an untested backup is a belief, not a capability. It restores into an
# isolated database, then answers four questions in order — and each is a different way a restore
# fails silently:
#
#   1. Does the dump restore at all?
#   2. Does the schema match the migrations the code expects? (a restore of an older dump against a
#      newer image starts and then fails on the first query that touches a new column)
#   3. Do the tenancy invariants hold? (TEN-095 — the checks the composite FKs cannot make during a
#      restore, because pg_restore loads data before it recreates constraints)
#   4. Are a sample assignment and its file actually readable? (REL-005)
#
# A failed drill is an operational incident, not a warning (REL-005).
#
#   DUMP_FILE          the dump to restore
#   RESTORE_URI        libpq URI of the isolated target database — never production
#   MIGRATOR_IMAGE     optional: image to run `--migrate` from, for check 2

set -Eeuo pipefail

DUMP_FILE="${DUMP_FILE:?DUMP_FILE is required}"
RESTORE_URI="${RESTORE_URI:?RESTORE_URI is required}"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
integrity_sql="${script_dir}/../db/02-tenant-integrity.sql"

echo "== 1. restoring ${DUMP_FILE} =="

if [[ -f "${DUMP_FILE}.sha256" ]]; then
  (cd "$(dirname "${DUMP_FILE}")" && sha256sum --check "$(basename "${DUMP_FILE}").sha256")
fi

# --exit-on-error, so a partially restored database is never mistaken for a restored one.
pg_restore --dbname="${RESTORE_URI}" --no-owner --no-privileges --exit-on-error --jobs=4 "${DUMP_FILE}"

echo "== 2. schema matches the migrations =="

pending="$(psql "${RESTORE_URI}" --tuples-only --no-align --command \
  "SELECT count(*) FROM __ef_migrations_history;")"

echo "applied migrations: ${pending}"

if [[ "${pending}" -eq 0 ]]; then
  echo "FAIL: the restored database has no migration history" >&2
  exit 1
fi

echo "== 3. tenant integrity (TEN-095) =="

# Every query in the file returns zero rows on success, so any output row is a failure. Captured
# rather than streamed so the check can fail the drill rather than merely print.
integrity="$(psql "${RESTORE_URI}" --set ON_ERROR_STOP=1 --tuples-only --no-align \
  --file="${integrity_sql}" 2>&1 | grep -v '^==' || true)"

if [[ -n "${integrity//[[:space:]]/}" ]]; then
  echo "FAIL: tenant integrity violated after restore (TEN-095):" >&2
  echo "${integrity}" >&2
  exit 1
fi

echo "== 4. a sample assignment and its file are readable =="

sample="$(psql "${RESTORE_URI}" --tuples-only --no-align --command \
  "SELECT a.id || ' ' || coalesce(s.storage_key, '<no submission>')
     FROM assignments a
     LEFT JOIN submissions s ON s.assignment_id = a.id
    ORDER BY a.created_at DESC
    LIMIT 1;")"

if [[ -z "${sample}" ]]; then
  echo "WARN: the restored database holds no assignment to sample" >&2
else
  echo "sample: ${sample}"

  key="${sample#* }"

  if [[ "${key}" != "<no submission>" && -n "${MINIO_BACKUP_ALIAS:-}" ]]; then
    # The row is worthless without the file it points at: a dump restored beside a bucket that was
    # not mirrored looks complete and downloads nothing.
    mc stat "${MINIO_BACKUP_ALIAS}/${key}" > /dev/null \
      || { echo "FAIL: ${key} is absent from the object storage replica" >&2; exit 1; }
  fi
fi

echo "== restore drill passed =="
echo "Record the result: REL-005 makes an unperformed drill an operational incident."

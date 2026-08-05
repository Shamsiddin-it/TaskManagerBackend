-- Three database roles with non-overlapping privileges (TZ 12.6, DEPLOY-008, DEPLOY-009).
--
--   mentortaskflow_app        — the application. No DDL. From Phase 1 onward, UPDATE and DELETE are
--                               revoked on the append-only tables (task_events, audit_logs,
--                               user_category_history, user_branch_history).
--   mentortaskflow_migrator   — migrations and bootstrap. Full DDL.
--   mentortaskflow_retention  — the retention job. DELETE only on tables with a bounded lifetime
--                               (TZ 27.5), no DDL.
--
-- Passwords here are development fixtures. In Staging and Production the roles are created by
-- operations with secrets from the secret manager (SEC-010).

CREATE ROLE mentortaskflow_app       WITH LOGIN PASSWORD 'mentortaskflow_dev';
CREATE ROLE mentortaskflow_migrator  WITH LOGIN PASSWORD 'mentortaskflow_dev';
CREATE ROLE mentortaskflow_retention WITH LOGIN PASSWORD 'mentortaskflow_dev';

GRANT CONNECT ON DATABASE mentortaskflow TO
    mentortaskflow_app, mentortaskflow_migrator, mentortaskflow_retention;

-- The migrator owns the schema so that every object it creates is grantable by it.
ALTER SCHEMA public OWNER TO mentortaskflow_migrator;

GRANT USAGE ON SCHEMA public TO mentortaskflow_app, mentortaskflow_retention;

-- Default privileges apply to tables created later by the migrator, so Phase 1 migrations do not
-- have to re-grant. The per-table REVOKE for append-only tables is part of the migration itself.
ALTER DEFAULT PRIVILEGES FOR ROLE mentortaskflow_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mentortaskflow_app;

ALTER DEFAULT PRIVILEGES FOR ROLE mentortaskflow_migrator IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO mentortaskflow_app;

-- Retention receives SELECT everywhere; DELETE is granted per table by the migration that
-- introduces it, never wholesale.
ALTER DEFAULT PRIVILEGES FOR ROLE mentortaskflow_migrator IN SCHEMA public
    GRANT SELECT ON TABLES TO mentortaskflow_retention;

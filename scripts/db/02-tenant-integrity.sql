-- Tenant integrity checks of TEN-095, run after every restore drill (REL-005).
--
-- A restore that brings the process up and answers /health/ready has proved that the schema is
-- readable — not that the tenancy model survived. These three queries are what «successful restore»
-- means for a multi-tenant installation: a dump restored to an inconsistent point, or a partial
-- restore of one organization, breaks exactly here and nowhere else visible.
--
-- Each query returns zero rows on success. Any row is a failed restore (TEN-095), not a warning.
--
-- Usage:
--   psql "$CONNECTION_STRING" --set ON_ERROR_STOP=1 -f scripts/db/02-tenant-integrity.sql

\echo '== TEN-095.1: exactly one head office per organization =='

-- BRN-005 and ux_branches_single_head_office. Zero is as wrong as two: an organization with no head
-- office has no zone for organization-level reports and no target for organization-level users.
SELECT o.id            AS organization_id,
       o.slug          AS organization_slug,
       count(b.id)     AS head_offices
FROM organizations o
LEFT JOIN branches b
       ON b.organization_id = o.id
      AND b.is_head_office
GROUP BY o.id, o.slug
HAVING count(b.id) <> 1;

\echo '== TEN-095.2: no row references a nonexistent organization =='

-- Every tenant-scoped table, checked one by one rather than through a generated loop: the list is
-- Приложение M's, and a table added without its organization_id should fail to appear here in review.
SELECT 'branches' AS table_name, b.id AS row_id, b.organization_id
FROM branches b LEFT JOIN organizations o ON o.id = b.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'categories', c.id, c.organization_id
FROM categories c LEFT JOIN organizations o ON o.id = c.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'users', u.id, u.organization_id
FROM users u LEFT JOIN organizations o ON o.id = u.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'assignments', a.id, a.organization_id
FROM assignments a LEFT JOIN organizations o ON o.id = a.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'submissions', s.id, s.organization_id
FROM submissions s LEFT JOIN organizations o ON o.id = s.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'reviews', r.id, r.organization_id
FROM reviews r LEFT JOIN organizations o ON o.id = r.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'task_events', t.id, t.organization_id
FROM task_events t LEFT JOIN organizations o ON o.id = t.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'audit_logs', al.id, al.organization_id
FROM audit_logs al LEFT JOIN organizations o ON o.id = al.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'notification_outbox', n.id, n.organization_id
FROM notification_outbox n LEFT JOIN organizations o ON o.id = n.organization_id WHERE o.id IS NULL
UNION ALL SELECT 'ai_summaries', ai.id, ai.organization_id
FROM ai_summaries ai LEFT JOIN organizations o ON o.id = ai.organization_id WHERE o.id IS NULL;

\echo '== TEN-095.3: no cross-scope row survived the restore =='

-- The composite FKs of 12.2a make these rows impossible while the constraints are enforced. They
-- become possible during a restore, because pg_restore loads data before it recreates constraints —
-- and a restore that ended with a constraint failure leaves the data in place and the constraint off.
-- This query finds what that would have left behind.
SELECT 'categories.branch' AS relation, c.id AS row_id
FROM categories c
JOIN branches b ON b.id = c.branch_id
WHERE b.organization_id <> c.organization_id
UNION ALL SELECT 'users.branch', u.id
FROM users u JOIN branches b ON b.id = u.branch_id
WHERE b.organization_id <> u.organization_id
UNION ALL SELECT 'users.category', u.id
FROM users u JOIN categories c ON c.id = u.category_id
WHERE c.organization_id <> u.organization_id OR c.branch_id <> u.branch_id
UNION ALL SELECT 'assignments.category', a.id
FROM assignments a JOIN categories c ON c.id = a.category_id
WHERE c.organization_id <> a.organization_id OR c.branch_id <> a.branch_id
UNION ALL SELECT 'assignments.assignee', a.id
FROM assignments a JOIN users u ON u.id = a.assigned_to_id
WHERE u.organization_id <> a.organization_id
UNION ALL SELECT 'submissions.assignment', s.id
FROM submissions s JOIN assignments a ON a.id = s.assignment_id
WHERE a.organization_id <> s.organization_id OR a.branch_id <> s.branch_id
UNION ALL SELECT 'reviews.submission', r.id
FROM reviews r JOIN submissions s ON s.id = r.submission_id
WHERE s.organization_id <> r.organization_id OR s.branch_id <> r.branch_id
UNION ALL SELECT 'ai_summaries.branch', ai.id
FROM ai_summaries ai JOIN branches b ON b.id = ai.branch_id
WHERE b.organization_id <> ai.organization_id;

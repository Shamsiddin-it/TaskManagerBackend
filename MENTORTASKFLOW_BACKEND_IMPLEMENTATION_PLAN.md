# MentorTaskFlow — Backend Implementation Plan (.NET 10 + PostgreSQL)

**Источник требований:** `MentorTaskFlow_TZ_v2.2.md` (Final после архитектурного аудита, 02.08.2026)
**Область:** только backend. Frontend (React SPA) — вне рамок этого плана.
**Дата плана:** 03.08.2026

---

## 0. Решения по стеку и отклонения от ТЗ

ТЗ в разделе 1 фиксирует «ASP.NET Core 9 / EF Core 9». Заказчик требует .NET 10. Отклонения зафиксированы явно:

| # | ТЗ | Решение | Обоснование |
|---|---|---|---|
| 1 | ASP.NET Core 9, EF Core 9 | **.NET 10, EF Core 10, Npgsql.EFCore.PostgreSQL 10** | Требование заказчика; совпадает со стеком Falaq (`net10.0`, Npgsql 10.0.0, EF Design 10.0.0) — проверенная связка |
| 2 | `Guid.CreateVersion7()` «в .NET 9» | Оставляем как есть | API доступен с .NET 9, в .NET 10 сохранён |
| 3 | React 18 + Vite frontend | **Вне scope** | Backend-only. Пункты DoD 2, 9, 12 (frontend, frontend-тесты, loading/empty/error) не применяются; вместо них — OpenAPI + integration-тесты |
| 4 | `Microsoft.Extensions.TimeZoneInfo` compat-пакет (14.2) | Такого пакета нет. Используем `TimeZoneInfo` + Linux-образы (`DEPLOY-011`), локально на Windows — `AppContext.SetSwitch("System.Globalization.UseNls", false)` / ICU | Формулировка ТЗ ошибочна, требование по существу (IANA tzdata) выполняется Linux-контейнером |
| 5 | Hangfire | **Сохраняем.** Проверено на фазе 0: `Hangfire.AspNetCore 1.8.24` + `Hangfire.PostgreSql 1.21.1` собираются на `net10.0`. Обязателен пин `Newtonsoft.Json ≥ 13.0.4` — транзитивно тянется уязвимая 11.0.1 | [ADR-002](./docs/ADR-002-background-job-scheduler.md) |
| 6 | `ADR-001-organization-branch-isolation.md`, `MentorTaskFlow_TZ_v2.2_AUDIT.md` | **Получены и прочитаны** (05.08.2026). Подтверждают план; режим миграции — A (новая БД) | ADR-001 §5 |
| 7 | `Microsoft.AspNetCore.OpenApi` | **Не используется.** OpenAPI генерируется только Swashbuckle | Его source-generator рассчитан на объектную модель Microsoft.OpenApi 2.x, а 2.0.0 несёт GHSA-v5pm-xwqc-g5wc (High). Пакет убран, а не порог аудита ослаблен (`SEC-014`). **Уточнено в фазе 2:** первоначальный пин на `Microsoft.OpenApi 3.9.0` оказался неверным — Swashbuckle падает с `MissingMethodException` на `IOpenApiRequestBody.get_Content`, как только появляется endpoint с телом запроса. Правильное решение — исправленная линия 2.x: `Microsoft.OpenApi 2.11.0` + `Swashbuckle 10.2.3`, аудит NuGet чист |
| 8 | `Category.Name` — «3–50 символов» (10.2) | **Минимум снижен до 2.** Верхняя граница 50 и `varchar(50)` сохранены | **Противоречие внутри ТЗ.** Раздел 2 перечисляет направления как «C#, Python, Go, Frontend, Design», фикстура тестов изоляции 31.9 построена на категории `C#`. `C#` и `Go` — 2 символа. С минимумом 3 система не может смоделировать собственный пример. Требует подтверждения заказчика |
| 9 | `TEN-048` — перечень действий с допустимым `BranchId IS NULL` | **Добавлено `audit.read`** (14 вместо 13) | **Противоречие внутри ТЗ.** `AUD-023` требует писать в запись чтения `branchFilter: "<uuid>\|all"`, а `TEN-033` делает режим «Все филиалы» умолчанием для Organization Admin без заголовка. Но `audit.read` отсутствует в `TEN-048`, поэтому такое чтение физически невозможно записать — филиала нет. Альтернативы хуже: приписать чтение произвольному филиалу — фальсифицировать журнал; запретить чтение — убрать возможность, которую Приложение D.7 прямо даёт. `audit.read` при этом **не** принудительно обнуляется: чтение Branch Admin несёт его филиал |
| 10 | Приоритет `CONCURRENCY_CONFLICT` относительно доменных 409 | **Проверка токена перенесена вперёд доменного перехода.** `ConcurrencyTokenAccessor.Expect` сравнивает предъявленный токен с текущим сразу после загрузки строки и бросает 409 `CONCURRENCY_CONFLICT`, не дожидаясь `SaveChanges`. Проверка EF по `OriginalValue` остаётся — она закрывает гонку между сравнением и записью | **Пробел в ТЗ, найден интеграционным тестом фазы 8.** `ASN-007` объявляет несовпадение токена конфликтом, но порядок относительно guard'ов статуса (`ASN-004`, `ASN-021`) нигде не задан. При отложенной проверке повтор устаревшего запроса возвращал `ASSIGNMENT_INVALID_STATUS_TRANSITION`: формально верно, но `FE-011` открывает диалог перезагрузки именно по `CONCURRENCY_CONFLICT`, поэтому клиент оставался с устаревшей копией — той самой, из-за которой запрос и не прошёл. Изменение общее для всех сущностей с токеном, не только для Assignment |
| 11 | Composite FK 11 и 12 Приложения M (`fk_assignments_assignee_scope`, `fk_assignments_assigner_scope`) | **Заменены триггером `trg_assignments_executor_scope`** на `assignments`, срабатывающим на INSERT и на изменение `assigned_to_id`/`assigned_by_id`. Триггер поднимает SQLSTATE `23503` с теми же именами ограничений, поэтому прямой INSERT по-прежнему отклоняется БД под именем из ТЗ, а трансляция в 409 `CROSS_SCOPE_REFERENCE` не изменилась. Существование и `ON DELETE RESTRICT` остались обычными FK | **Взаимоисключающие требования ТЗ, найдено интеграционным тестом фазы 9.** FK ссылаются на `users(id, organization_id, branch_id, category_id)` — ровно те поля, которые меняют `change-category` и `change-branch`. Scope Assignment при этом immutable и намеренно **не** перепривязывается (`TEN-018`, `BRN-049`). Без `ON UPDATE` UPDATE строки users отклоняется, как только на неё ссылается хоть один Assignment: переводы разделов 15.2 и 39.6 не выполнимы ни для кого, кто сделал хотя бы одну задачу. `ON UPDATE CASCADE` хуже — он переписал бы филиал и категорию всей истории вслед за человеком, то есть ретроактивно перенёс бы завершённую работу между филиалами вопреки `TEN-018`, `BRN-049` и `ANA-014`. Охраняемое отношение — факт **создания**, а не вечный инвариант; триггер выражает именно это. Требует подтверждения заказчика |

**Проверка перед стартом (фаза 0):** ТЗ ссылается на разделы 24–25, 28–31, Приложения A/B/G–K/N — они прочитаны частично. Перед каждой фазой соответствующие разделы вычитываются полностью; расхождения с этим планом фиксируются в PR.

---

## 1. Структура решения

По образцу Falaq (Clean Architecture, модульный монолит, без MediatR/AutoMapper — плоские сервисы + record-команды):

```
MentorTaskFlow.sln
src/
  MentorTaskFlow.Domain          -> ничего не ссылается
  MentorTaskFlow.Contracts       -> ничего (DTO запросов/ответов)
  MentorTaskFlow.Application     -> Domain, Contracts
  MentorTaskFlow.Infrastructure  -> Application, Domain
  MentorTaskFlow.Api             -> Application, Infrastructure, Contracts
tests/
  MentorTaskFlow.UnitTests           -> домен, автомат, формулы метрик, BDA
  MentorTaskFlow.IntegrationTests    -> Testcontainers (PostgreSQL + MinIO), 40 isolation-тестов
  MentorTaskFlow.ArchitectureTests   -> TEST-SEC-001/021/022/023, TEST-ASN-020
```

Отклонение от Falaq: там один проект `Falaq.Tests`. Здесь три — потому что `TEST-TEN-040` требует прямых INSERT в БД в обход приложения, а архитектурные тесты (`TEST-SEC-021/022`) должны падать быстро в CI без поднятия контейнеров.

**Внутри Application/Infrastructure — деление по модулям**, как в Falaq (`Orders/`, `Payments/`): `Tenancy/`, `Identity/`, `Users/`, `Categories/`, `Schedule/`, `Assignments/`, `Submissions/`, `Reviews/`, `Notifications/`, `Telegram/`, `Analytics/`, `Ai/`, `Common/`.

---

## 2. Git-процесс

Как в Falaq (проверено на 21 фазе, 24 PR):

```
ветка:   claude/phase-<N>-<slug>
коммит:  Phase <N>: <краткое описание>
PR:      в main, мержит заказчик
```

Репозиторий ещё не инициализирован (`Is a git repository: false`). Фаза 0 включает `git init`, `.gitignore`, `.dockerignore`, первый push и создание remote на GitHub.

**Правила работы (перенесены из `FALAQ_CODEX_IMPLEMENTATION_PLAN.md`, раздел «Global Rules»):**

1. Одна фаза = одна ветка = один PR. Не смешивать модули.
2. Перед правкой — читать существующий код, переиспользовать сложившиеся паттерны.
3. Не переименовывать проекты/namespace/сущности вне задачи фазы.
4. `dotnet build` + `dotnet test` после каждой фазы; PR не открывается на красной сборке.
5. Каждый endpoint — явная authorization policy (`SEC-001`) и явный scope-фильтр в handler (`SEC-030`).
6. Каждая мутация — структурированный ответ либо ProblemDetails с `code` (`API-021`).
7. Инвариант, выразимый ограничением БД, выражается им, а не только сервисом (`DoD-4`, `TEN-023`).
8. Никаких секретов в `appsettings.json` (`SEC-010`).

**После каждой фазы** — отчёт: что сделано, какие Requirement ID закрыты, какие тесты добавлены, что осталось.

---

## 3. Ключевые архитектурные точки ТЗ (что нельзя пропустить)

| Тема | Требование |
|---|---|
| Иерархия | `Organization → Branch → Category`; 6 уровней изоляции строго по порядку (9.1) |
| Роли | Ровно 3: Admin / Lead / Mentor + `AdminScope ∈ {Organization, Branch}` (`TEN-011`) |
| Scope-поля | `organization_id` / `branch_id` / `category_id` физически во всех бизнес-таблицах, immutable snapshot (`TEN-018`) |
| Cross-scope | Запрещён **composite FK** на уровне PostgreSQL, не только валидацией (12.2a, `TEN-023`) |
| Isolation-коды | Чужой Organization/Branch/Category/Mentor → **404 `RESOURCE_NOT_FOUND`**, побайтово одинаково (`TEN-006`) |
| Concurrency | PostgreSQL `xmin` как concurrency token, клиенту — opaque Base64Url (11.6) |
| Именование БД | `snake_case`, `EFCore.NamingConventions`, `ux_/ix_/ck_/fk_` (11.1) |
| PK | UUID v7, генерация в приложении (11.3) |
| Enum | `varchar` + CHECK, `HasConversion<string>()`; нативные PG enum запрещены (11.4) |
| Удаление | Все FK `ON DELETE RESTRICT`, каскадов нет; деактивация вместо удаления (11.7) |
| Append-only | `task_events`, `audit_logs`, `user_category_history`, `user_branch_history` — `REVOKE UPDATE, DELETE` у роли приложения (12.6) |
| Роли БД | Три непересекающиеся: `mentortaskflow_app` / `_migrator` / `_retention` (12.6, `DEPLOY-009`) |
| Автомат | 17 переходов, только через domain-методы, `private set` на `Status` (13.3, 10.6.5) |
| Транзакция | Переход + TaskEvent + Outbox — **одна транзакция** (`ASN-023`) |
| RLS | В Release 1.0 **не используется** (`TEN-030`) |
| Ошибки | RFC 9457 ProblemDetails + стабильный `code`, 54 кода (Приложение C) |

---

## 4. План по фазам

Опорная разбивка — раздел 33 ТЗ, но разбита мельче под PR-размер и переупорядочена по фактическим зависимостям данных.

> **Критично (`TEN-019`):** фаза 1 (Tenancy foundation) обязана быть завершена **до** любой бизнес-логики. Добавление арендной границы постфактум неизбежно пропускает scope в части запросов — это главный вывод аудита версии 2.2.

---

### Фаза 0 — Foundation
`claude/phase-0-foundation`

**Цель:** пустой каркас, который собирается, поднимается и проходит CI.

**Состав:**
- `git init`, `.gitignore`, `.dockerignore`, remote на GitHub.
- Solution + 5 src-проектов + 3 test-проекта, ссылки строго по разделу 1.
- `docker-compose.yml` (dev): `postgres:16`, `minio`, `mailhog`, `mtf-api`.
- `docker-compose.prod.yml` + `Dockerfile` (multi-stage, Linux, `DEPLOY-011`).
- Контейнеры `mtf-api` / `mtf-worker` / `mtf-migrator` — один образ, разные режимы (`DEPLOY-013`).
- `EFCore.NamingConventions` + `UseSnakeCaseNamingConvention()` (`DEPLOY-001`).
- `DbContext` без сущностей, connection string через `ConnectionStrings__DefaultConnection`.
- ProblemDetails-middleware (RFC 9457, поле `code`), каталог кодов как `static class ErrorCodes` (Приложение C, 54 значения).
- `X-Correlation-Id` middleware (`API-007`), Serilog JSON с обязательными полями (`OBS-001`).
- Health checks `/health/live`, `/health/ready` (`OBS-003`, `OBS-004`).
- OpenAPI 3.1 + Swagger; строгая десериализация `UnmappedMemberHandling.Disallow` (`API-005`).
- `IValidateOptions` для конфигурации — отказ старта при отсутствии обязательного значения (`DEPLOY-015`).
- GitHub Actions CI: build → unit → integration (Testcontainers) → arch → публикация OpenAPI (`DEPLOY-027`).
- **ADR-002: Hangfire vs собственный планировщик** — проверка совместимости с .NET 10, решение зафиксировано письменно.

**Тесты:** smoke — приложение стартует, `/health/live` = 200, unhandled exception → 500 `INTERNAL_ERROR` без stack trace (`API-025`).

**Готово когда:** `docker compose up` поднимает окружение, CI зелёный, Swagger открывается.

---

### Фаза 1 — Tenancy foundation ⚠️ блокирующая
`claude/phase-1-tenancy-foundation` · ТЗ фаза 0.5

**Цель:** арендная граница существует раньше любой бизнес-логики.

**Состав:**
- Сущности `Organization` (10.17), `Branch` (10.18), `User` (10.1) — только схема и инварианты, без endpoint'ов.
- `UserBranchHistory` (10.19), `UserCategoryHistory` (10.13) — таблицы append-only.
- Все CHECK версии 2.2: `ck_users_scope_shape`, `ck_users_role_admin_scope`, `ck_users_role_category`, `ck_organizations_slug_format`, `ck_branches_code_format` (12.2, `USER-023` — ровно 4 допустимых сочетания).
- Уникальные индексы 12.1a, включая `ux_branches_single_head_office (organization_id) WHERE is_head_office = true` (`BRN-021`) и `ux_*_id_scope` — цели composite FK (`TEN-020`).
- `ICurrentUserContext` и `IBranchContext` (38.3, `TEN-011a`, `TEN-012a`).
- Middleware `X-MTF-Branch-Id`: 403 `SCOPE_OVERRIDE_FORBIDDEN` для BA/Lead/Mentor при **любом** присутствии заголовка (`TEN-032`); 404 для чужой Organization (`TEN-032a`); валидация на каждом запросе (`TEN-035a`).
- EF Core Global Query Filters по `OrganizationId`/`BranchId` + базовая спецификация репозитория, запрещающая голый `DbSet<T>` (`SEC-031`).
- Заголовок ответа `X-MTF-Effective-Branch-Id` (`API-027`).
- Три роли БД (`app`/`migrator`/`retention`) + `REVOKE UPDATE, DELETE` на append-only таблицы (12.6) — SQL в миграции.
- `xmin` concurrency token: маппинг + Base64Url-кодек + 409 `CONCURRENCY_CONFLICT` с `currentConcurrencyToken` (11.6, `API-026`).
- Первая миграция.

**Уточнение состава по итогам реализации.** Фаза 1 создаёт также таблицы `Category` и `CategorySettings` — без единого endpoint'а и без бизнес-правил (они в фазе 5). Причина: composite FK `fk_users_category_scope` (`USER-024`) и `fk_user_category_history_scope` ссылаются на `categories`. Отложить их означало бы оставить `User.CategoryId` без FK до фазы 5 — ровно тот постфактум-scope, который запрещает `TEN-019`.

**Тесты:**
- Arch: `TEST-SEC-031` (DbContext не покидает Infrastructure), immutability scope-полей, отсутствие concurrency token у append-only сущностей.
- Integration (Testcontainers PostgreSQL): `TEST-TEN-031`, `TEST-TEN-032`, `TEST-DB-010`, часть `TEST-TEN-040` — composite FK, существующие после фазы 1; фильтры запросов (`TEST-TEN-002`, `TEST-TEN-003`, `TEST-TEN-008`, `TEST-TEN-010`).
- Middleware: `TEST-TEN-007`, `TEST-TEN-012`, `TEST-TEN-013`.

> `TEST-TEN-001`…`TEST-TEN-006`, `TEST-TEN-009`, `TEST-TEN-011` требуют endpoint'ов и закрываются в фазах 4–6 вместе с ними. Оставшиеся 15 composite FK из `TEST-TEN-040` добавляются в фазах, создающих свои таблицы (DoD 3a).

**Готово когда:** cross-branch связь физически невозможна на уровне БД, подтверждено прямыми INSERT в обход приложения.

---

### Фаза 2 — Identity & bootstrap
`claude/phase-2-identity` · ТЗ фаза 1 (часть)

**Состав:**
- `RefreshToken` (10.10), `UserSecurityToken` (10.12).
- JWT: HS256, TTL 15 мин, claims `sub/role/admin_scope/org_id/branch_id/category_id/tv/jti` (`AUTH-003`); **отсутствующий claim не сериализуется** (`AUTH-031`).
- `AUTH-032`: middleware отклоняет токен, чей набор claims не совпадает ни с одной строкой матрицы `AUTH-031` → 401, без указания нарушенного claim.
- Refresh: cookie `mtf_rt` (HttpOnly, Secure, SameSite=Strict, `Path=/api/v1/auth`, host-only), ротация, token family, reuse detection → отзыв всей family (`AUTH-007`, `AUTH-008`).
- CSRF double-submit + валидация `Origin` — только на `refresh`/`logout` (`AUTH-012`).
- `TokenVersion` + `IMemoryCache` TTL 30 с (`AUTH-026`…`AUTH-029`, `AUTH-034` — полная таблица триггеров).
- Пароли: PBKDF2-HMAC-SHA256 100k (ASP.NET Identity `PasswordHasher`), политика 12–128 + список 10 000 распространённых (`AUTH-013`).
- Flow: `set-password` (TTL 24 ч), `forgot-password` (всегда 202, `AUTH-015`), `reset-password` (TTL 30 мин). **Сгенерированные пароли не создаются никогда** (`AUTH-019`).
- Lockout 5 попыток / 15 мин (`AUTH-024`); rate limiting (`SEC-007`, Приложение L.2).
- `POST /auth/login`, `/refresh`, `/logout`, `/change-password`, `/forgot-password`, `/reset-password`, `/set-password`, `GET /auth/me` — контракт профиля раздела 16.9 (`AUTH-037`, `AUTH-038`).
- **Bootstrap** (`mtf-migrator`): 6 INSERT в одной транзакции (Organization + HeadOffice Branch + Organization Admin + SetPassword-токен + Outbox + AuditLog), идемпотентность, 6 переменных окружения (32.6, `DEPLOY-022`…`DEPLOY-033`).
- Security headers, HSTS, CORS с `AllowCredentials` только для `app.<domain>` (`SEC-006`, `SEC-008`, `SEC-009`).

**Тесты:** unit (состав claims по `AUTH-031`, TokenVersion, политика паролей, PBKDF2, опаковые токены), integration (login/lockout/деактивация, rotation, reuse detection, CSRF, полный цикл паролей, идемпотентность bootstrap).

**Перенесено в фазу 3 (таблиц ещё не существует).** `DEPLOY-030` требует в транзакции bootstrap шести INSERT; реализованы четыре — Organization, HeadOffice Branch, Organization Admin, `SetPassword`-токен. Записи `NotificationOutbox` (приглашение) и `AuditLog` (`bootstrap.provision`) добавляются в фазе 3 вместе с этими таблицами. Ссылка установки пароля пока выводится в лог контейнера `mtf-migrator`, что `DEPLOY-024` прямо разрешает. По той же причине отложены записи AuditLog для `auth.refresh_reuse_detected` (`AUTH-008`) и для отклонённого `SCOPE_OVERRIDE_FORBIDDEN` (`TEN-032`).

**Найдено при реализации.** `Program.cs` и `AddInfrastructure` читали конфигурацию энергично (`.Get<T>()` до `builder.Build()`), поэтому источники, добавленные позже, игнорировались: ключ подписи JWT оказывался пустым, а строка подключения — из `appsettings.json` вместо переданной. Исправлено на отложенное разрешение через `IConfigureOptions<JwtBearerOptions>` и перегрузку `AddDbContext` с `IServiceProvider`. Дефект проявился бы и в production при подстановке секретов late-bound провайдером, а не только в тестах.

---

### Фаза 3 — Authorization, AuditLog, Outbox-схема
`claude/phase-3-authorization-audit` · ТЗ фаза 1 (часть)

**Цель:** cross-cutting инфраструктура до появления бизнес-модулей.

**Состав:**
- Authorization policies по 4 типам пользователей; **на каждом endpoint явная policy** (`SEC-001`).
- Реализация 6-уровневой проверки (9.1) как переиспользуемого пайплайна; порядок 1→6 обязателен (`TEN-001`).
- Приоритет кодов `ORGANIZATION_INACTIVE` → `BRANCH_INACTIVE` → `CATEGORY_INACTIVE` (`TEN-008`).
- 409 `CROSS_SCOPE_REFERENCE` + метрика `cross_scope_reference_rejected_total` + AuditLog `security.cross_scope_rejected` (9.7).
- `AuditLog` (10.14): `OrganizationId` обязателен всегда, `BranchId` nullable только для списка `TEN-040`, `ActorAdminScope`; типизированный `Metadata` (jsonb + `MetadataSchemaVersion`).
- `IAuditWriter` — scope берётся из `IBranchContext`, не из аргументов (`TEN-022`).
- `NotificationOutbox` (10.15) — **только схема + `IOutboxWriter`**; доставка в фазе 12. Причина: `ASN-023` требует записи Outbox в одной транзакции с бизнес-событием, значит writer нужен раньше воркера.
- `GET /admin/audit-log` со scope-фильтром + запись `audit.read` (`AUD-002`, `TEN-041`).
- Redaction: запрет записи токенов, паролей, presigned URL (`SEC-021`, `AUD-022`).

**Тесты:** `TEST-SEC-001` (нет endpoint'а без policy), `TEST-TEN-018` (scope AuditLog), append-only на уровне прав БД.

**Закрыты переносы из фазы 2.** Bootstrap доведён до полных шести INSERT `DEPLOY-030` (добавлены `NotificationOutbox` с приглашением и `AuditLog` `bootstrap.provision`). Добавлены записи аудита `auth.refresh_reuse_detected` (`AUTH-008`) и `security.scope_override_rejected` (`TEN-032`, `AUD-020`).

**Найдено при реализации.**
1. Новый тест `EndpointPolicyTests` сразу поймал два endpoint'а фазы 2 (`GET /auth/me`, `POST /auth/change-password`) с голым `[Authorize]` без policy — прямое нарушение `SEC-001` и `TEN-035`. Исправлено.
2. `BootstrapProvisioner` открывал транзакцию через `BeginTransactionAsync` при включённом `EnableRetryOnFailure`. EF Core такое запрещает: retrying execution strategy не может переиграть транзакцию, которой не владеет. **Bootstrap упал бы и в production**, не только в тестах — прежние тесты этого не ловили, потому что собирали `DbContext` вручную без retry. Обёрнуто в `CreateExecutionStrategy().ExecuteAsync(...)`; change tracker очищается внутри делегата, иначе повтор вставил бы сущности дважды.

---

### Фаза 4 — Organization & Branch management
`claude/phase-4-branch-management` · ТЗ раздел 39.1–39.4

**Состав:**
- `GET/PUT /organization` (`ORG-003`, `ORG-004`) — минимальный состав для не-OA.
- `GET /branches` — только Organization Admin, для остальных 403 (`BRN-006`).
- `POST /branches`, `GET /branches/{id}`, `PUT /branches/{id}` — редактирование **только** OA (`BRN-002`); BA/Lead/Mentor — только собственный филиал, Lead/Mentor получают `BranchSummaryDto` (`BRN-008`, `BRN-009`).
- `activate` / `deactivate` — `confirmActiveUsers`, 409 `BRANCH_HAS_ACTIVE_USERS`, поведение деактивированного филиала (таблица `BRN-031`).
- `make-head-office` — транзакция «снять → установить» с `SELECT … FOR UPDATE` по `organizations` (`BRN-035`), 409 `HEAD_OFFICE_REQUIRED` / `HEAD_OFFICE_DEACTIVATION_FORBIDDEN`.
- Валидация `TimeZoneId` по IANA tzdata (`BRN-010`).
- `CreateBranchRequest` **без** поля `isHeadOffice` (`API-031`).

**Тесты:** `TEST-TEN-033` (параллельная смена HeadOffice → ровно один), `TEST-TEN-032`, уникальность `code`/`normalizedName` под гонкой, видимость профиля по ролям, concurrency token.

**Добавлено сверх плана.** Механизм concurrency token (`API-020`, `API-026`) реализован здесь, а не отложен: это первые мутации, которым он нужен. `xmin` — shadow property, поэтому доступ к нему централизован в `ConcurrencyTokenAccessor`; клиенту уходит Base64Url, а не число, чтобы его нельзя было «услужливо» инкрементировать. Также добавлен `ITimeZoneCatalog` (`BRN-010`): Windows-идентификаторы вроде `Central Asia Standard Time` отклоняются, потому что на Linux-контейнерах их не существует и расчёт дедлайнов молча свалился бы в UTC.

---

### Фаза 5 — Categories & CategorySettings
`claude/phase-5-categories` · ТЗ 15.3, 39.4

> **Переставлена местами с Users** относительно первой редакции плана. Lead и Mentor обязаны иметь `CategoryId` (`USER-023`), а composite FK `fk_users_category_scope` требует существующей записи в `categories`. Создавать пользователей раньше категорий невозможно.

**Состав:**
- CRUD Category; уникальность `UNIQUE (branch_id, normalized_name)` — **не глобальная** (`CAT-021`).
- `CategorySettings` создаётся в одной транзакции с Category; `TimeZoneId` наследуется от `Branch.TimeZoneId` (`CAT-014`, `CAT-023`).
- `DefaultDueTimeLocal` (default `23:59`), `DefaultAssignmentDueDays` 1–60, `DeadlineReminderHours` 1–168, `AllowLateSubmission`.
- `activate` / `deactivate` + `confirmActiveUsers` (`CAT-003`).
- Правила `CAT-015` (изменение настроек не пересчитывает существующие дедлайны) и `CAT-016` (`AllowLateSubmission` действует немедленно).
- `X-MTF-Branch-Id` обязателен для OA при `POST /categories` (`CAT-025`).

**Добавлено сверх плана.** `ITenantStateGuard` — переиспользуемая проверка цепочки Organization → Branch → Category с приоритетом кодов `TEN-008`. Понадобилась здесь впервые (запись в контуре деактивированного филиала или категории), но нужна каждой последующей фазе, поэтому вынесена в общий слой сразу, а не продублирована.

**Тесты:** `TEST-TEN-008`, `TEST-TEN-010` (одноимённые категории — разные записи), приоритет `BRANCH_INACTIVE` над `CATEGORY_INACTIVE`, `confirmActiveUsers`, диапазоны настроек, аудит смены часового пояса с прежним и новым значением.

---

### Фаза 6 — Users management
`claude/phase-6-users` · ТЗ 15.1, 39.5

**Состав:**
- `GET /users`, `GET /users/{id}`, `POST /users`, `PATCH /users/{id}`.
- Матрица «кто кого создаёт» (`USER-031`): **Branch Admin не может создать Branch Admin** — 403 + AuditLog.
- Серверное определение scope при создании (`USER-032`); `X-MTF-Branch-Id` обязателен для OA при создании BA/Lead/Mentor и запрещён при создании OA.
- `activate` / `deactivate` / `change-role` (включая смену `AdminScope`, `USER-033`) / `resend-invitation`.
- Единственный активный Lead в категории — partial unique index + 409 `ACTIVE_LEAD_ALREADY_EXISTS` (`USER-003`).
- Приглашение без раскрытия UUID, только человекочитаемые названия (`USER-034`).
- Уведомления `CategoryWithoutLead`, `BranchWithoutAdmin` — enqueue в Outbox (`USER-005`, `USER-036`).

**Отложено:** `change-category` и `change-branch` — фаза 9 (блокировки зависят от Assignment).

**Найдено при реализации.**
1. Запись аудита об **отклонённой** попытке создания не сохранялась: `EnsureMayCreate` добавлял её в change tracker и сразу бросал 403, а `SaveChanges` уже никто не вызывал. `USER-031` требует, чтобы попытка Branch Admin создать Branch Admin была видна в журнале — теперь она коммитится отдельно, до распространения 403.
2. Аудит брал филиал из scope запроса. Organization Admin в режиме «Все филиалы» филиала не имеет, поэтому любая branch-scoped операция над пользователем падала с `VALIDATION_FAILED` на `ck_audit_logs_branch_scope`. Приложение D.2 помечает `X-MTF-Branch-Id` обязательным только для `POST /users` — у остальных операций филиал определяется самим пользователем. Добавлено `AuditEntry.BranchId` для явной атрибуции.
3. **Скрытая гонка в смене главного офиса (фаза 4).** Код полагался на то, что EF Core выпустит два UPDATE в порядке изменения сущностей. Такой гарантии нет: при обратном порядке `ux_branches_single_head_office` срабатывал на легитимной операции. Раньше это выглядело как редкая нестабильность теста. Переписано на два явных SQL-оператора в порядке, предписанном `BRN-035`.

---

### Фаза 7 — Schedule (Topic, TopicAssignment)
`claude/phase-7-schedule` · ТЗ фаза 2, раздел 15.4

**Состав:**
- CRUD Topic: `UNIQUE (category_id, day_number)`, partial unique по `planned_date` среди активных (`TOPIC-010`).
- CRUD TopicAssignment: типы `Presentation/ClassTask/HomeTask`, `IsRequired`, composite FK на Topic (`TPL-005`).
- Удаление при наличии ссылок → 409 `RESOURCE_IN_USE`, вместо него деактивация.
- Mentor — только чтение своей категории (`TOPIC-004`).

**Добавлено сверх Приложения D.4.** Endpoint'ы `activate` / `deactivate` для Topic и TopicAssignment. `TOPIC-012` и `TPL-002` требуют деактивацию как альтернативу отказу в удалении, но каталог endpoint'ов не содержит ни одной операции, достающей до `IsActive`. Возможность предписана, маршрут — нет; сделан по той же конвенции action-эндпоинтов, что у филиалов и категорий (`API-008`).

**Тесты:** read-only для Mentor, уникальность `DayNumber` и `PlannedDate` (включая освобождение даты архивной темой), прошедшая дата принимается, 409 `RESOURCE_IN_USE` при удалении связанной темы, наследование scope шаблоном, отказ в деактивированной категории.

---

### Фаза 8 — Assignment lifecycle & TaskEvent
`claude/phase-8-assignment-lifecycle` · ТЗ фаза 3

**Ключевая фаза. Состав:**
- Сущность `Assignment` (10.6) со всеми полями дат, `LastEventSequence`, immutable snapshot scope (10.6.4).
- Конечный автомат: 9 статусов, 17 переходов (13.3), `private set` на `Status`, domain-методы `CreateDraft/CreateSuggestion/Publish/AcceptSuggestion/Reassign/Submit/StartReview/Approve/RequestRework/MarkOverdue/Cancel`.
- `TaskEvent` (10.9): `SequenceNumber` через `Assignment.LastEventSequence + 1` под `FOR UPDATE` (12.4), `CorrelationId`, типизированный `Metadata` per EventType (Приложение F, 12 типов).
- Вычисление `InitialDueAt` с `DefaultDueTimeLocal` + правила DST (14.2, 14.3): несуществующее локальное время → первый момент после разрыва; неоднозначное → более позднее.
- Endpoints: `POST /assignments/drafts`, `PUT`, `publish`, `accept-suggestion`, `reassign`, `cancel`, `start-review`, `GET /assignments/{id}/history`.
- Переназначение только до первой Submission (10.6.3) → 409 `REASSIGN_NOT_ALLOWED`.
- Admin — **только** `GET` и force-cancel с причиной, в пределах своего контура (`ASN-025`).
- Транзакция «переход + TaskEvent + Outbox» (`ASN-023`).
- 409 `ASSIGNMENT_TERMINAL` имеет приоритет над `ASSIGNMENT_INVALID_STATUS_TRANSITION` (`ASN-021`).

**Тесты:** `TEST-ASN-020` (arch: нет прямого присвоения `Status` вне домена), полная таблица переходов Приложения B, `TEST-EVT-001` (нет смены статуса без TaskEvent), DST-кейсы.

**Выполнено.** 488 тестов зелёные (211 unit / 40 arch / 237 integration).

Замечания по реализации:
- **Composite FK на исполнителя — сырым SQL, не через модель.** `HasPrincipalKey` по `ux_users_id_scope` заставлял EF сгенерировать `AlterColumn ... nullable: false` на `users.branch_id` и `users.category_id`, что уничтожило бы `USER-023` (Organization Admin без филиала). FK на модели убран; `fk_assignments_assignee_scope` и `fk_assignments_assigner_scope` создаются `migrationBuilder.Sql` поверх уже существующего уникального индекса. Проверка на уровне БД покрыта прямым INSERT'ом в обход приложения (`TEST-TEN-014`).
- **`ix_assignments_overdue_scan` намеренно не начинается со scope-полей.** Единственный запрос системы, который обязан пройти поперёк всех арендаторов, — сканер просрочки; tenant-leading индекс заставил бы его читать по индексу на каждую организацию отдельно.
- **DST-тесты используют Europe/Berlin.** Asia/Dushanbe не переводит часы с 1991 года, то есть на нём ни одно из двух правил 14.3 не воспроизводится. `ResolveGap` шагает по минуте вперёд, а не прибавляет час: разрыв не везде часовой (Lord Howe — 30 минут).
- **Найден и исправлен приоритет `CONCURRENCY_CONFLICT`** — см. отклонение 10 в разделе 0.

---

### Фаза 9 — User transfers
`claude/phase-9-user-transfers` · ТЗ 15.2, 39.6, 39.7

Выделена в отдельную фазу, потому что блокировки перевода (`USER-012`, `BRN-038`) требуют существующего Assignment.

**Состав:**
- `POST /users/{id}/change-category` — в пределах того же Branch (`USER-037`), 409 `CATEGORY_CHANGE_BLOCKED` со списком блокирующих задач.
- `POST /users/{id}/change-branch` — **только** Organization Admin (`BRN-036`, `USER-030`); 409 `BRANCH_CHANGE_BLOCKED`; ответ содержит только id блокирующих задач без имён и названий (`BRN-039`).
- Транзакционный состав: смена поля + `UserCategoryHistory`/`UserBranchHistory` + `TokenVersion++` + отзыв RefreshToken + инвалидация security-токенов (для branch) + AuditLog + уведомление `UserBranchChanged`.
- Ownership-исключение из Global Query Filter (`USER-016`) и его ограничение арендной границей (`USER-017`): ownership **никогда** не пробивает Organization/Branch.

**Тесты:** `TEST-USER-011`, `TEST-TEN-025`, `TEST-TEN-029`, `TEST-TEN-034`.

**Выполнено.** 22 интеграционных теста фазы; всего 273 зелёных (211 unit / 40 arch / 22 из 258 integration).

Замечания по реализации:
- **Composite FK 11 и 12 Приложения M сделали переводы невозможными — переписаны на триггер.** См. отклонение 11 в разделе 0. Найдено интеграционным тестом «завершённая задача не блокирует перевод».
- **Блокировка строки пользователя реализована с обеих сторон.** Перевод берёт `FOR UPDATE` по `users`, создание Assignment — `FOR SHARE` (сценарии 11 и 20 Приложения K). Только создание задачи и было незакрытым входом: любой уже существующий нетерминальный Assignment сам блокирует перевод, поэтому `publish` и `reassign` дополнительной блокировки не требуют — ТЗ в сценариях 8 и 10 их и не требует.
- **Авторизация выполняется до блокировки.** Иначе ожидание на `FOR UPDATE` стало бы наблюдаемым: запрос по чужому пользователю висел бы до конца чужой транзакции, а это ровно тот побочный канал, который закрывает 404 из `TEN-006`.
- **Третье условие `BRN-038` (незавершённое Review, начатая загрузка Submission) пока не проверяется** — сущностей не существует до фаз 10 и 11. Разрыв узкий, но не пустой: идущее ревью означает Assignment в `InReview`, который ловится проверкой исполнителя, а ревьюер — активный Lead категории, который ловится проверкой Lead. Условие добавляется вместе с этими фазами.
- **`user.change_branch` пишется тремя записями аудита** (`TEN-049`): organization-level запись с `BranchId = NULL` плюс `user.left_branch` и `user.joined_branch` в оба филиала, без указания филиала-контрагента.

---

### Фаза 10 — Submissions & file storage
`claude/phase-10-submissions` · ТЗ фаза 4 (часть), раздел 17

**Состав:**
- MinIO (S3 API), bucket закрыт от анонимного доступа, presigned URL TTL 10 мин (`SEC-013`).
- `StorageKey` = `submissions/{organizationId}/{branchId}/{categoryId}/{assignmentId}/{submissionId}{ext}` (`SUB-009`); клиенту не возвращается никогда (`SUB-008`).
- Порядок валидации 0a→12 строго по таблице 17.2 — файл попадает в storage **только** после шагов 1–9.
- Ограничение размера по фактически прочитанным байтам, не по `Content-Length` (`SUB-012`…`SUB-015`).
- PDF: `%PDF-` + `%%EOF`, без разбора структуры (`SUB-016`).
- PPTX: OPC-валидация (5 проверок) + защита от ZIP bomb (7 лимитов, таймаут 5 с) (`SUB-017`, `SUB-018`).
- SHA-256: неуникальный индекс; дубль **внутри того же Assignment** → 409 `SUBMISSION_DUPLICATE_CONTENT` (`SUB-026`…`SUB-029`).
- `VersionNumber` под `SELECT … FOR UPDATE` по Assignment, `READ COMMITTED` (12.5).
- `IsLate` по правилу 14.5 (учитывает 15-минутный лаг джоба просрочки).
- Guard `AllowLateSubmission` — проверка **до** загрузки в MinIO (13.5).
- `download-url` / `preview-url` с полной повторной проверкой уровней 1–5 (`SEC-004`); для OA обязателен `X-MTF-Branch-Id` (`TEN-065`).
- PPTX без preview в Release 1.0 (17.5).

**Тесты:** `TEST-TEN-017` (соответствие StorageKey scope), ZIP bomb, переименованный ZIP как PPTX, гонка параллельных загрузок.

**Выполнено.** 574 теста зелёные (240 unit / 45 arch / 289 integration), из них 30 интеграционных и 27 модульных — по этой фазе.

Замечания по реализации:
- **AWS SDK, а не клиент MinIO.** ТЗ говорит «S3-совместимое хранилище (MinIO)», а `SEC-017` требует задавать `Content-Disposition` на каждый запрос — `ResponseHeaderOverrides` в presigned URL здесь первоклассная возможность.
- **Схема presigned URL приводится к настроенной.** Резолвер эндпоинтов SDK v4 строит presigned URL по `https` независимо от схемы `ServiceURL`; против MinIO по HTTP клиент падает с ошибкой TLS-фрейминга. Схема в подпись SigV4 не входит (подписываются хост, путь, query и signed headers), поэтому переписывание безопасно и сделано явно в `S3FileStorage.Normalise`.
- **Файл выгружается на диск, а не в память.** 50 МБ на каждую параллельную загрузку в managed heap ушли бы в LOH. Временный файл открывается с `DeleteOnClose` и исчезает вместе с запросом, как бы запрос ни закончился.
- **Лимиты ZIP проверяются до чтения содержимого.** Размеры берутся из центрального каталога и не считаются доверенными: `[Content_Types].xml` дополнительно ограничен по объёму перед открытием. Вся проверка контейнера идёт под таймаутом — крафтовый каталог делает дорогим само перечисление, чего ни один лимит на запись не ловит.
- **`sha256_hash` — явное имя колонки.** Конвенция превращает `Sha256Hash` в `sha256hash`, а CHECK ссылается на `sha256_hash`; расхождение ломает миграцию, а не запрос.
- **`submissions` не попадает в `REVOKE` из 12.6.** Список раздела нормативен и таблицу не включает; `SUB-021` к тому же оставляет `DELETE` роли retention. Неизменяемость обеспечена отсутствием пути обновления и архитектурным тестом.
- **Третье условие `BRN-038` по-прежнему закрыто наполовину.** «Начатая загрузка Submission» не является состоянием: загрузка атомарна, промежуточной записи не существует. Остаётся только незавершённое Review — добавляется в фазе 11.

---

### Фаза 11 — Review
`claude/phase-11-review` · ТЗ фаза 4 (часть), раздел 15.7

**Состав:**
- `POST /submissions/{id}/reviews`: `Approved` / `NeedsRework` (comment 10–3000 + `reworkDueAt > now`).
- `UNIQUE (submission_id)` — один Review на Submission (`REV-005`).
- Только для последней Submission (`REV-004`) → 409 `REVIEW_NOT_LATEST_SUBMISSION`.
- Только из статуса `InReview` (`REV-003`).
- `CurrentDueAt = ReworkDueAt` при `NeedsRework`; `TaskEvent(ReviewNeedsRework)` с `previousCurrentDueAt`/`newCurrentDueAt`.
- `SELF_REVIEW_FORBIDDEN` — defensive invariant (`REV-022`), реализуется несмотря на недостижимость в Release 1.0.
- Review immutable, в том числе для Admin (`REV-020`).

**Выполнено.** 613 тестов зелёные (251 unit / 50 arch / 312 integration), из них 23 интеграционных, 11 модульных и 5 архитектурных — по этой фазе.

Замечания по реализации:
- **Composite FK 15 Приложения M — снова триггер.** `fk_reviews_reviewer_scope` ссылается на `users(id, organization_id, branch_id, category_id)`, то есть на те же поля, что меняет перевод, при immutable записи Review. Буквальный FK запретил бы переводить любого тимлида, вынесшего хоть одно решение. Сделано так же, как в фазе 9: обычный FK на существование плюс триггер `trg_reviews_reviewer_scope` с тем же SQLSTATE и тем же именем ограничения. Покрыто двумя тестами — прямой INSERT отклоняется БД, а переводы тимлида после ревью проходят.
- **Найден дефект: `reworkDueAt` при одобрении молча отбрасывался.** `Review.Approve` не принимает этот параметр, поэтому доменная проверка не могла сработать, и запрос проходил с 201 вместо 400 из строки 10 Приложения F. Клиент считал бы дедлайн установленным. Проверка добавлена в сервис, где форма запроса ещё видна.
- **Токен передаётся по Assignment, а не по Submission.** Submission append-only и токена не имеет; решение меняет статус и дедлайн задачи — именно на этой строке и происходит конфликт.
- **Удалён дублирующий `ux_submissions_id_scope`.** EF объявляет для scope-кортежа собственный alternate key под FK от Review; ручной уникальный индекс по тем же четырём колонкам оказался его точной копией.
- **Третье условие `BRN-038` закрыто окончательно — дописывать нечего.** «Незавершённое Review» не является отдельным состоянием: решение выносится атомарно, промежуточной записи нет. Идущая проверка означает Assignment в `InReview`, а вынести решение может только активный тимлид категории, которого `USER-013` и так блокирует безусловно. Условие покрыто существующими проверками, отдельного кода не требует.

---

### Фаза 12 — Notifications & Outbox delivery
`claude/phase-12-notifications` · ТЗ фаза 5, раздел 18

**Состав:**
- Worker: `FOR UPDATE SKIP LOCKED`, батч 50, интервал 30 с (`NTF-011`).
- Lease 5 мин + восстановительный проход каждые 5 мин (`NTF-012`).
- Retry 5 попыток, backoff 1м→5м→15м→1ч→6ч; классификация временная/постоянная (`NTF-013`).
- Статусы `Pending/Processing/Sent/DeadLetter` — статуса `Failed` нет.
- `DeduplicationKey` с обязательным префиксом `{organizationId}:{branchId|"org"}:` (`NTF-015`, `TEN-043`) + `ON CONFLICT DO NOTHING`.
- Защита от рекурсии DeadLetter: `IsSystemAlert=true`, цепочка обрывается на первом уровне, дедуп `deadletter-alert:{yyyy-MM-dd-HH}` (`NTF-020`…`NTF-023`).
- Email-канал (SMTP), шаблоны на русском, без токенов/URL/PII в payload (`NTF-017`).
- Политики каналов `Both` / `TelegramPreferred` / `EmailOnly`; skip при отсутствии `TelegramChatId` — не ошибка (`NTF-001`).
- 19 событий Приложения E со scope-правилами (`TEN-042`, `TEN-044`, `TEN-045`).
- `GET /admin/notifications`, `POST /admin/notifications/{id}/retry` со scope-ограничением (`TEN-046`, `TEN-047`).

**Тесты:** `TEST-TEN-019` (одноимённые события разных филиалов не подавляют друг друга).

**Выполнено.** 678 тестов зелёные (273 unit / 64 arch / 341 integration), из них 29 интеграционных, 22 модульных и 8 архитектурных — по этой фазе.

Замечания по реализации:
- **Воркер — `BackgroundService`, а не задача Hangfire.** Outbox — непрерывно вычерпываемая очередь, а не событие по расписанию; Hangfire подключается в фазе 14 для задач, у которых расписание действительно есть. Цикл регистрируется всегда и сам останавливается, если процесс не воркер: принадлежность фоновой обработки — вопрос конфигурации, а не композиции (`DEPLOY-013`).
- **Найден дефект: ключ дедупликации не включал канал.** До этой фазы шаблон был `{org}:{branch}:{остальное}`, и при политике `Both` вторая строка схлопывалась с первой — каждый получатель получал ровно один из двух каналов, выбранный порядком вставки. Ключ теперь строится централизованно по полному шаблону `NTF-015`; вызывающий код передаёт только дискриминатор.
- **Найден дефект в собственном алерте о dead letter:** проверка существования шла по базовому ключу, а вставка — по ключу с идентификатором администратора, поэтому второй пакет падал на уникальном индексе. Окно в час проверяется теперь по тому ключу, который реально пишется.
- **`ON CONFLICT DO NOTHING` заменён предварительной проверкой ключа.** Строка outbox — отслеживаемая сущность в одной транзакции с бизнес-событием (`NTF-009`), поэтому конфликт уникального индекса откатил бы и само событие. Проверка смотрит и в таблицу, и в change tracker; уникальный индекс остаётся финальной защитой на гонку, которую проверка не закрывает.
- **Строки Telegram пока перепланируются, а не уходят в dead letter.** Канал появляется в фазе 13; `NTF-001` и так не даёт создать их без привязки, а отбрасывать их значило бы терять уведомления ровно в момент подключения канала.
- **Классификация SMTP-ошибок намеренно узкая.** Постоянной считается только 5xx про самого получателя или отправителя; 5xx «сервер на обслуживании» остаётся временной, иначе все уведомления этого окна были бы молча выброшены.

---

### Фаза 13 — Telegram
`claude/phase-13-telegram` · ТЗ фаза 6, раздел 19

**Состав:**
- Dev — long polling; Staging/Prod — **только** webhook с `X-Telegram-Bot-Api-Secret-Token`, сравнение constant-time (`TG-002`).
- `TelegramBindToken`: 256 бит, TTL 15 мин, в БД только SHA-256, `FixedTimeEquals` (`TG-011`, `TG-012`).
- `POST /telegram/bind-token`, `GET /telegram/status`, `DELETE /telegram/binding`, `POST /telegram/webhook`.
- Один `TelegramChatId` — один User (`TG-008`).
- Бот не выполняет бизнес-действий (`TG-004`).
- Классификация ошибок провайдера (19.4); `403 bot was blocked` **не обнуляет** `TelegramChatId`.
- Feature flag `Features:Telegram` — при `false` bind-эндпоинты возвращают 404 (4.1).

**Выполнено.** 708 тестов зелёные (282 unit / 64 arch / 362 integration), из них 21 интеграционный и 9 модульных — по этой фазе.

Замечания по реализации:
- **Флаг называется `Telegram:Enabled`, а не `Features:Telegram`.** Остальные настройки интеграции лежат в той же секции, и держать переключатель отдельно от того, чем он управляет, — приглашение включить одно без другого.
- **401 вебхука пишется напрямую.** Любой штатный способ вернуть 401 из `[ApiController]` прикладывает тело ProblemDetails, а `TG-002` требует ответа без тела: тело сообщает пробующему, что эндпоинт существует и что заголовок ему важен. Поймано тестом.
- **Лимит попыток `/start` считается в памяти по хэшу chat_id.** Журнал аудита для этого не годится: он намеренно не хранит chat_id — это персональные данные получателя, которые разбору привязки не нужны (`AUD-022`). Следствие названо прямо: при нескольких репликах бюджет получается на процесс, то есть 20 × реплик. Принято — лимит здесь эшелонированная защита, а невозможность перебора обеспечивают 256 бит токена (`TG-011`).
- **`TelegramChatId` не обнуляется при `403 bot was blocked`** (`19.4`): пользователь может разблокировать бота, а обнуление молча и навсегда перевело бы его на почту.
- **`BoundAt` выводится из `UsedAt` погашенного токена**, а не хранится отдельной колонкой на пользователе: момент привязки уже записан, а второе поле пришлось бы держать в согласии с первым.
- **`TooManyRequestsException` переехал в Application.** Сервисы применяют лимиты на актора, которых транспортный limiter не видит (`TG-014`).

---

### Фаза 14 — Background jobs & scheduler
`claude/phase-14-scheduler` · ТЗ фаза 7, раздел 20

**Состав:**
- 7 задач таблицы 20.1, все с `DisableConcurrentExecution`.
- Авто-генерация: recurring job на каждое уникальное `CategorySettings.TimeZoneId`, cron `0 6 * * *` (`SCH-001`); перерегистрация при изменении настроек.
- Цепочка выборки Organization → Branch → Category → Topic → TopicAssignment → Mentor, обрыв на первом неактивном звене (`SCH-002`, `TEN-050`).
- Идемпотентность: `AutoGenerationKey` со scope + partial unique index + `ON CONFLICT DO NOTHING` (`SCH-009`, `SCH-023`).
- **BDA** (не round-robin): min нетерминальных → min последний `AssignedAt` (null = самый ранний) → min `User.Id`; загрузка считается **только внутри Branch+Category** (`TEN-051`, `SCH-013`).
- Джоб просрочки: условный `UPDATE … WHERE status = @expected`, батчи по 200, `overdue_at = COALESCE(...)` (`SCH-007`, `SCH-019`).
- Напоминания: ключ дедупа включает значение дедлайна (`NTF-004`).
- `OrphanObjectCleanupJob` — по одному scope-префиксу за проход; при рассогласовании scope **не удалять**, а поднимать security-алерт (`TEN-067`, `TEN-068`).
- Retention (27.5) под ролью `mentortaskflow_retention`.
- Hangfire Dashboard **не публикуется** ни одному пользователю приложения (`TEN-059`).
- Feature flag `Features:Scheduler`.

**Тесты:** `TEST-SCH-004` (детерминизм BDA), `TEST-TEN-020`, `TEST-TEN-021` (Mentor чужого филиала не выбирается никогда).

---

### Фаза 15 — Analytics
`claude/phase-15-analytics` · ТЗ фаза 8, раздел 21

**Состав:**
- 10 метрик таблицы 21.2 с точными формулами и «датой отнесения».
- Границы периода: локальные даты → полуоткрытый интервал UTC (`ANA-002`).
- Медиана через `percentile_cont(0.5) WITHIN GROUP` (`ANA-008`).
- Деление на ноль → `null`, не 0 (`ANA-004`); отрицательные длительности исключаются + счётчик (`ANA-007`).
- `FirstPassApprovalRate` — 3 условия одновременно (21.3); `OverdueRate` — DISTINCT Assignment (21.4).
- Правило ≥5 менторов внутри фактического scope, без обхода объединением филиалов (`ANA-012`, `TEN-072`); allowlist фильтров для Mentor (`ANA-013`).
- **Группировка по `(BranchId, CategoryId)`, никогда по `Category.Name`** (`TEN-071`) — дефект уровня Critical при нарушении.
- `GET /reports/personal`, `/reports/team`, `/reports/branches` (только OA).
- `isPartialPeriod`, `isCrossBranchAggregate`, `periodTimeZoneId` в ответе (`ANA-006`, `TEN-071`, `TEN-074`).

**Тесты:** `TEST-ANA-003` (две формулировки OverdueRate дают одинаковый результат), `TEST-TEN-005`, `TEST-TEN-010`.

---

### Фаза 16 — AI summary
`claude/phase-16-ai-summary` · ТЗ фаза 9, раздел 22

**Состав:**
- `IAiSummaryProvider` в Application, `AnthropicSummaryProvider` в Infrastructure (`AI-001`).
- Модель `claude-sonnet-5`, лимиты Приложения L.6, retry 2× (2 с, 6 с), общий бюджет 90 с.
- **Field allowlist** как основной механизм минимизации; regex — только дополнительный (`AI-006`).
- Запрет передачи UUID Organization/Branch, `Slug`, `Code`, `Address` (`TEN-079`).
- Защита от prompt injection: `<system_instructions>` / `<untrusted_data>`, вырезание имитаций разделителей (`AI-014`…`AI-016`).
- `CacheKey` с обязательными `organizationId` + `branchId` (`AI-009`, `TEN-076` — иначе прямая утечка между филиалами).
- `force=true` не чаще 1 раза в сутки → 429 `AI_REGENERATION_LIMIT`.
- AI — **optional**-зависимость `/health/ready`, никогда не переводит сервис в Unhealthy (`AI-019`).
- Feature flag `Features:AiSummary`; при отключении метрики остаются доступны (`TEST-AI-002`).

**Тесты:** `TEST-TEN-030` (кэш не отдаёт отчёт чужого филиала), недоступность провайдера не ломает `/reports/*`.

---

### Фаза 17 — Hardening & production readiness
`claude/phase-17-hardening` · ТЗ фаза 10, разделы 28–32

**Состав:**
- Полный прогон **всех 40** isolation-тестов `TEST-TEN-001`…`TEST-TEN-040` — без waiver'ов (`TEST-014`).
- Архитектурные тесты `TEST-SEC-021`, `TEST-SEC-022`, `TEST-SEC-023`.
- Нагрузочный тест профиля `PERF-001`, проверка p95 ≤ 300 мс; каждый списочный запрос обязан использовать tenant-ведущий индекс (`TEN-029`, `PERF-006`).
- Метрики Prometheus, включая 10 tenant-метрик с labels `organization`/`branch`/`category`; **высококардинальные labels запрещены** (30.4).
- Алерты 30.3 + 30.4.
- Бэкапы, тест восстановления, RPO 24 ч / RTO 4 ч; восстановление — на уровне всей Organization (29.1).
- Сканирование зависимостей и образов; блокировка сборки при Critical (`SEC-014`).
- Production deployment: `mtf-migrator` → `mtf-api`/`mtf-worker` → smoke `/health/ready` (`DEPLOY-029`).
- Итоговая проверка Приложения J: каждый Requirement ID имеет реализацию и минимум один проходящий тест.

**Готово когда:** выполнены `TEST-011`, `TEST-012`, `TEST-014`; `cross_scope_reference_rejected_total = 0` по итогам приёмочного прогона.

---

## 5. Граф зависимостей

```
0 Foundation
└─ 1 Tenancy ⚠ блокирующая (TEN-019)
   └─ 2 Identity ─ 3 Authz/Audit/Outbox-схема
      └─ 4 Branches
         └─ 5 Categories
            └─ 6 Users
               └─ 7 Schedule
            └─ 8 Assignment + TaskEvent
               ├─ 9 User transfers
               ├─ 10 Submissions ─ 11 Review
               ├─ 12 Notifications ─ 13 Telegram
               ├─ 14 Scheduler
               └─ 15 Analytics ─ 16 AI summary
                                  └─ 17 Hardening
```

Фазы 0–8 строго последовательны. После фазы 11 ветки 12/13, 14, 15/16 независимы и могут идти параллельно.

---

## 6. Definition of Done фазы (адаптирован под backend-only)

Из раздела 35 ТЗ, с исключением frontend-пунктов:

1. ✅ Реализован backend.
2. ~~Frontend~~ — вне scope.
3. ✅ Проверки авторизации всех применимых уровней 9.1 (Organization → Branch → Category → ownership → Role/AdminScope → состояние).
3a. ✅ Для tenant-scoped сущности: scope-колонки, composite FK, tenant-ведущий индекс, явный scope-фильтр в handler, строка в Приложении N.
4. ✅ Валидация; инвариант, выразимый ограничением БД, выражен им.
5. ✅ TaskEvent и/или AuditLog.
6. ✅ NotificationOutbox — если применимо по Приложению E.
7. ✅ Unit-тесты.
8. ✅ Integration-тесты (Testcontainers) — если затронуты БД/storage/провайдер.
9. ~~Frontend-тесты~~ — вне scope.
10. ✅ Миграция БД по правилу expand → migrate → contract (`DEPLOY-019`).
11. ✅ Обновлён OpenAPI; каждый endpoint описан по `API-028` (13 обязательных полей).
12. ~~loading/empty/error~~ — вне scope.
13. ✅ Given/When/Then-сценарии раздела 31.8 проходят.
14. ✅ Наблюдаемость: структурированные логи с `CorrelationId`, метрика для асинхронного/фонового/внешнего.
15. ✅ Обновлено Приложение L при добавлении переменных окружения.
16. ✅ Security review: нет Critical/High, нет секретов в логах.

---

## 7. Открытые вопросы к заказчику

| # | Вопрос | Статус | Влияние |
|---|---|---|---|
| 1 | GitHub-репозиторий | ✅ `github.com/Shamsiddin-it/TaskManagerBackend` | — |
| 2 | ADR-001 и отчёт аудита | ✅ Получены 05.08.2026, прочитаны, план подтверждён | — |
| 3 | Hangfire vs собственный планировщик | ✅ Hangfire, [ADR-002](./docs/ADR-002-background-job-scheduler.md) | — |
| 4 | Домен для `app.<domain>` / `api.<domain>` | ⏳ Домена пока нет | Фаза 2. Не блокирует: cookie host-only (`Domain` не выставляется, `AUTH-010`), CORS и ссылки в письмах — из конфигурации. Домен подставляется при деплое одной переменной |
| 5 | Anthropic API key и бюджет | ⏳ Открыт | Фаза 16. До ответа реализуется провайдер + заглушка за feature flag `Features:AiSummary` |
| 6 | **Минимальная длина `Category.Name`** | ⏳ Требует решения | Раздел 10.2 требует 3–50, но раздел 2 и фикстура 31.9 используют `C#` и `Go` (2 символа). Реализовано с минимумом **2**; если заказчик подтвердит 3, придётся переименовать категории в примерах ТЗ и в тестовой фикстуре |
| 7 | **Список 10 000 распространённых паролей** (`AUTH-013`) | ⏳ Требует решения | Механизм готов: `ICommonPasswordCatalog` + embedded-ресурс, подмена файла не требует изменений кода. Сейчас в нём **затравка**, а не полный корпус: вендоринг стороннего списка (SecLists `rockyou-10000` или аналог) — решение о зависимости, которое принимает заказчик. Нужно подтвердить источник и лицензию |
| 8 | **Composite FK на исполнителя, постановщика и ревьюера против переводов пользователей** (ограничения 11, 12 и 15 Приложения M) | ⏳ Требует решения | Приложение M требует FK на `users(id, organization_id, branch_id, category_id)`, а разделы 15.2 и 39.6 меняют ровно эти поля при immutable scope Assignment и Review. Одновременно выполнить оба требования нельзя. Реализовано триггером с теми же именами ограничений и тем же SQLSTATE — прямой INSERT отклоняется БД, переводы работают. Если заказчик предпочтёт сохранить FK буквально, придётся запретить перевод любого пользователя, у которого есть хотя бы одна задача **в любом статусе**, что делает таблицу последствий в 15.2 и раздел 39.7 недостижимыми |

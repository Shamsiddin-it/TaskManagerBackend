# MentorTaskFlow — Backend

Backend системы контроля учебных задач между тимлидами и менторами в организации с филиалами.

**.NET 10 · ASP.NET Core · PostgreSQL 16 · EF Core 10 · Clean Architecture (модульный монолит)**

| Документ | Назначение |
|---|---|
| [`MentorTaskFlow_TZ_v2.2.md`](./MentorTaskFlow_TZ_v2.2.md) | Техническое задание — контракт реализации |
| [`ADR-001-organization-branch-isolation.md`](./ADR-001-organization-branch-isolation.md) | Архитектурное решение по изоляции Organization/Branch |
| [`MentorTaskFlow_TZ_v2.2_AUDIT.md`](./MentorTaskFlow_TZ_v2.2_AUDIT.md) | Отчёт архитектурного аудита |
| [`MENTORTASKFLOW_BACKEND_IMPLEMENTATION_PLAN.md`](./MENTORTASKFLOW_BACKEND_IMPLEMENTATION_PLAN.md) | План реализации по фазам |
| [`docs/ADR-002-background-job-scheduler.md`](./docs/ADR-002-background-job-scheduler.md) | Планировщик фоновых задач на .NET 10 |

---

## Структура решения

```
src/
  MentorTaskFlow.Domain          сущности, инварианты, конечный автомат — не ссылается ни на что
  MentorTaskFlow.Contracts       DTO запросов/ответов, каталог кодов ошибок — не ссылается ни на что
  MentorTaskFlow.Application     сценарии, абстракции — Domain + Contracts
  MentorTaskFlow.Infrastructure  EF Core, PostgreSQL, внешние провайдеры — Application + Domain
  MentorTaskFlow.Api             контроллеры, middleware, композиция
tests/
  MentorTaskFlow.UnitTests           домен, автомат, формулы метрик
  MentorTaskFlow.IntegrationTests    Testcontainers: PostgreSQL, MinIO, изоляция арендаторов
  MentorTaskFlow.ArchitectureTests   правила слоёв и scope-фильтров (TEST-SEC-021…023)
```

Направление зависимостей проверяется автоматически — см. `LayeringTests`.

---

## Запуск

### Docker Compose

```bash
docker compose up --build
```

| Сервис | Адрес |
|---|---|
| API | http://localhost:5000 |
| Swagger | http://localhost:5000/swagger |
| Liveness | http://localhost:5000/health/live |
| Readiness | http://localhost:5000/health/ready |
| PostgreSQL | localhost:5432 |
| MinIO / консоль | http://localhost:9000 · http://localhost:9001 |
| MailHog | http://localhost:8025 |

### Локально без Docker

Нужны запущенные PostgreSQL и MinIO.

```bash
dotnet run --project src/MentorTaskFlow.Api
```

### Тесты

```bash
dotnet test MentorTaskFlow.sln
```

Интеграционные тесты поднимают PostgreSQL через Testcontainers — требуется запущенный Docker.

### Миграции

```bash
dotnet ef migrations add MigrationName --project src/MentorTaskFlow.Infrastructure --startup-project src/MentorTaskFlow.Api --output-dir Persistence/Migrations
```

---

## Конфигурация

Переменные окружения используют разделитель `__` (ASP.NET Core). Полный реестр значений и лимитов — Приложение L технического задания. Шаблон — [`.env.example`](./.env.example).

Ключевые:

```
ConnectionStrings__DefaultConnection
Database__MigrateOnStartup          # true только в Development (DEPLOY-016)
Cors__AllowedOrigins__0             # точный origin SPA; wildcard запрещён (SEC-006)
AUTH__JWT_SIGNING_KEY               # ≥256 бит, только из secret manager (SEC-010)
Ai__Enabled                         # false — метрики работают, блок резюме отсутствует (AI-018)
Ai__ApiKey                          # секрет; обязателен при Ai__Enabled=true
```

Приложение выполняет валидацию конфигурации при старте: отсутствие или некорректность обязательного значения приводит к отказу старта, а не к работе с небезопасным умолчанием (`DEPLOY-015`).

---

## Что нужно знать перед первым PR

Требования ниже не являются рекомендациями — они проверяются тестами и блокируют merge.

1. **Изоляция филиалов — граница безопасности, а не UI-фильтр.** Шесть уровней проверок выполняются строго по порядку (ТЗ 9.1). Чужая Organization, чужой Branch, чужая Category, чужой объект и несуществующий объект возвращают **побайтово одинаковый** 404 (`TEN-006`).
2. **Global Query Filter не является достаточной защитой.** Каждый handler tenant-scoped сущности содержит явный scope-фильтр (`SEC-030`).
3. **Инвариант, выразимый ограничением БД, выражается им.** Application-валидация никогда не единственная защита (`TEN-023`).
4. **`OrganizationId`, `BranchId`, `CategoryId`, `Role`, `AdminScope` не принимаются от клиента** ни в теле, ни в query string (`SEC-003`). Строгая десериализация отклоняет такие поля с 400.
5. **Каждый endpoint имеет явную authorization policy** (`SEC-001`).
6. **Коды ошибок стабильны.** `code` не меняет смысл между версиями; коды, подтверждающие существование чужих объектов, добавлять запрещено (Приложение C).

---

## Процесс

Одна фаза плана = одна ветка = один PR в `main`.

```
ветка:   claude/phase-<N>-<slug>
коммит:  Phase <N>: <описание>
```

Definition of Done фазы — раздел 6 плана реализации.

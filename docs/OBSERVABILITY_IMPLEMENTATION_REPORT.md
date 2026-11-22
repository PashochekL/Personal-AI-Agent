# Наблюдаемость — 23 ноября 2026

Ветка `feature/observability-integration`, база `529f969` из develop после
объединения PR #14 с переносом диалогов в Telegram. Из сохранённых локальных
изменений перенесены OpenTelemetry, JSON-логи и подключение существующего
Langfuse через OTLP. Исходный stash сохранён отдельно.

## Результат

- `src/ai_agent/observability/`: настройки, регистрация SDK и экспортёров,
  трассировка выполнения задач и вызовов модели, метрики длительности/токенов.
- Core публикует диагностику инструментов через стандартные ActivitySource/Meter.
  Core не зависит от инфраструктурного модуля наблюдаемости.
  `filesystem.send_to_telegram` распознаётся как встроенный инструмент.
- API записывает W3C traceparent при принятии задачи; nullable-столбец добавляется
  совместимо через `ADD COLUMN IF NOT EXISTS`. Worker продолжает трассу после
  очереди и перезапуска; replay не меняет исходный контекст. HTTP DTO не изменены.
- JSON-логи задач содержат TraceId/SpanId при включённом tracing; служебные
  сообщения хостинга с локальными путями и адресами подавлены.
- Экспорт выключен по умолчанию. Поддержаны общий OTLP Collector и Langfuse,
  включая параллельную отправку. Заголовки авторизации изолированы между
  экспортёрами, redirect запрещён. Сетевые ошибки экспортёра не меняют результат
  задачи; экспорт ограничен размером очереди и таймаутом.
- Метаданные не включают пользовательские тексты, содержимое файлов, команды,
  аргументы инструментов или секреты. Сессии псевдонимизированы; custom/MCP-имена
  инструментов сведены к категориям. Дочерние spans получают корреляцию.
- Опциональный Docker Collector закреплён версией 0.165.0 и digest манифеста,
  проверенного в registry. Профиль не публикует порты и работает в agent_internal.
  Windows и Unix launcher запускают его по явной настройке локального endpoint.

Новые NuGet-зависимости: OpenTelemetry.Extensions.Hosting и
OpenTelemetry.Exporter.OpenTelemetryProtocol 1.21.0. README.md,
docs/ARCHITECTURE.md и src/ai_agent/shared/ не изменялись.

## Проверки

| Проверка | Результат |
| --- | --- |
| ObservabilityTests | 33 проверки, в том числе настоящий OTLP/HTTP protobuf на локальный приёмник и диагностика инструмента отправки файла в Telegram |
| BackendTests/run-integration.ps1 | 167 проверок с настоящими PostgreSQL/pgvector, Redis, Worker и Core; продолжение трассы после перезапуска, связь HTTP → Worker → Core → модель/инструмент, меню Telegram и переходы A → B → A с включённым экспортом |
| Dockerfile.tests | Полный Linux-набор пройден: контракты, Core, автономная память, CLI, Telegram, наблюдаемость и четыре Unix launcher-набора |
| BackendTests/run-container-smoke.ps1 | Production backend/CLI, Compose, авторизация, очередь, сохранение ошибки шлюза, ответ, usage и context пройдены в отдельном временном проекте |
| JSON-логи Worker | Проверены записи с TraceId/SpanId и отсутствие синтетических текстов запросов и ключей |
| windows-launcher-check.ps1 | Пройдено, включая автоматический запуск локального Collector |
| personal-telegram-launcher-check.ps1 | 6 проверок персонального запуска, повторного использования и переноса настройки токена |
| Collector validate | Официальный закреплённый образ подтвердил config/otel-collector.yaml в одноразовом контейнере без сети |
| Docker Compose config | Профиль observability проверен с синтетическими обязательными значениями |
| ContractTests на Windows | 39 проверок пройдено |
| Сборки API и Shared на Windows | Пройдены, 0 предупреждений и ошибок |
| git diff --check | Пройдено |

CoreTests выполняется в Linux Docker, включая проверки символических ссылок;
Windows-прогон CoreTests в этом этапе не запускался. Тесты не отключались.
Автономный MemoryTests без специальной тестовой БД пропускает SQL-часть;
pgvector проверен отдельно интеграционным backend-набором.

Модель, содержимое диалогов и ключи в тестах синтетические. Живые пользователи,
платные запросы и реальный проект Langfuse не использовались. Интеграционные
контейнеры удалены тестовым скриптом. Collector не запускался рядом с живым ботом.

## Оставшиеся действия

Настройка: [OBSERVABILITY.md](OBSERVABILITY.md). Endpoint и ключи настоящего
проекта Langfuse локально не настроены; доставка в него не подтверждена.
После задания настоящих ключей владелец проверяет одну трассу в своём Langfuse
с вызовами модели и инструмента.
Сам сервер Langfuse не разворачивается этим изменением; локальный Collector
выводит метаданные в логи, не предоставляет UI и постоянное хранилище.

Денежные бюджеты, обязательный аудит, трассировка эмбеддингов, прямой пример
SandboxConsole и визуализация подробного прогресса в Telegram остаются отдельными
задачами. Телеметрия может теряться при недоступности приёмника и не является
гарантией выполнения или основанием для автоматического повтора действия.

Предлагаемый коммит: `Подключить OpenTelemetry и Langfuse для задач Дамира`.

## Затронутые файлы

- `.env.example`, `damir`, `damir-launcher.ps1`, `docker-compose.yml`,
  `docker/Dockerfile.tests`, `config/otel-collector.yaml`.
- `src/ai_agent/observability/Observability.csproj`, `ObservabilityOptions.cs`,
  `AgentTelemetry.cs`, `ObservedLlmClient.cs`, `TelemetryRegistration.cs`.
- `src/ai_agent/core/CoreDiagnostics.cs`, `src/ai_agent/core/ToolRegistry.cs`.
- `src/ai_agent/interfaces/api/Api.csproj`, `BackendApplication.cs`,
  `AgentWorker.cs`, `ConsoleOperations.cs`, `PostgresTaskStore.cs`.
- `tests/ObservabilityTests/`, `tests/BackendTests/TelemetryChecks.cs`,
  `tests/BackendTests/Program.cs`, `tests/BackendTests/run-integration.ps1`,
  `tests/LaunchTests/telegram.sh`, `tests/windows-launcher-check.ps1`.
- `CONTRIBUTING.md`, `docs/BACKEND.md`, `docs/CONTRACTS.md`, `docs/INSTALL.md`,
  `docs/OBSERVABILITY.md`, этот отчёт.

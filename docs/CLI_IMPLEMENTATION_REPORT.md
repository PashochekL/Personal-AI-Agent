# Перевод CLI на общий backend — 21 ноября 2026

Рабочая ветка: `feature/cli-backend`, база — `6b8222a` из develop.
Отчёт подготовлен перед коммитом и публикацией ветки.

## Результат

Обычный `damir` запускает HTTP-клиент. Приём, очередь, блокировка сессии,
история, инструменты и Agent Loop обслуживаются backend и Core.
Сохранены выбор модели и навыка, многострочный ввод, счётчики и сжатие.
Добавлены именованные API-сессии, восстановление квитанций, отмена и просмотр
задач. Модель, навык и операция записываются с задачей и участвуют в проверке
Idempotency-Key. Служебные операции выполняются в очереди сессии.

Launcher создаёт собственный backend без публикации порта и передаёт CLI только
адрес, ключ backend и имя сессии. GitHub MCP подключается к backend через
существующий адаптер Core. При завершении убираются собственные backend,
песочница, MCP-контейнер и IPC-тома. Пользовательские базы не удаляются.

Добавлены совместимые столбцы client_name у API-сессий и selection у задач;
прежние API-запросы остаются допустимыми. README.md, docs/ARCHITECTURE.md и
src/ai_agent/shared/ не менялись. Новых NuGet-зависимостей нет.

## Проверки

| Проверка | Результат |
| --- | --- |
| ContractTests | 39 проверок пройдено |
| Сборка Shared | Успешно, без предупреждений и ошибок |
| Сборки API и CLI | Успешно |
| CliTests | 20 проверок пройдено |
| BackendTests/run-integration.ps1 | 61 проверка пройдена |
| windows-launcher-check.ps1 | Пройдено на Windows |
| Dockerfile.tests | Контракты, Core, 41 автономная проверка памяти, CLI и Unix launcher пройдены |
| BackendTests/run-container-smoke.ps1 | Production backend и CLI-контейнеры, очередь, usage и context пройдены |
| git diff --check | Пройдено |

Интеграция использует настоящие PostgreSQL/pgvector и Redis в отдельных
временных контейнерах; ответы модели синтетические. Проверены выбор модели
и полных инструкций навыка, сериализация сессии, изоляция scope, отмена,
таймаут, перезапуск, потеря Redis-уведомлений, сохранение истории, сжатие,
повторная отправка и восстановление квитанции при исчезнувшем навыке.
Настоящий процесс CLI также проверен при недоступном каталоге моделей.

Автономный MemoryTests в Linux явно пропускает собственные SQL-проверки
без специальной переменной окружения; реальная pgvector-память проверена
отдельно в интеграционной композиции backend. Контейнеры тестов удалены
их скриптами. Запросов к платной модели не выполнялось.

Один параллельный локальный запуск сборки Shared завершился ошибкой
нехватки файла подкачки. Отдельный повтор после завершения других проверок
успешен; изменение кода для обхода этой ошибки не потребовалось.

## Ограничения и действия команды

- Первый переход с демонстрационной консоли создаёт отдельную API-историю.
  Прежняя история остаётся в БД; автоматической миграции к API-сессии нет.
- Ключ и тело неясной отправки сохраняются в памяти CLI до его закрытия.
  После перезапуска доступны последняя серверная задача и её результат;
  постоянное локальное хранилище отправок пока не реализовано.
- Подробные события инструментов пока не передаются по HTTP.
- Живые входы ChatGPT/GitHub и платная генерация не проверялись этим этапом.
- Telegram и OpenTelemetry/Langfuse остаются дальнейшими этапами.

Для ручной проверки: запустить Docker Desktop и `damir` из нужной папки,
проверить сообщения, /models, /skill, /tokens, /context, /compress и повторный
запуск той же сессии. Затем команда может провести ревью и открыть PR в develop.
Рекомендуемое название коммита: `Перевести консоль Дамира на общий сервер с очередью задач`.
Инструкция пользователя: [CLI.md](CLI.md).

## Изменённые и добавленные файлы

- `CONTRIBUTING.md`
- `damir`
- `damir-launcher.ps1`
- `docker-compose.yml`
- `docker/Dockerfile.backend`
- `docker/Dockerfile.cli`
- `docker/Dockerfile.tests`
- `docs/BACKEND.md`
- `docs/CLI.md`
- `docs/CLI_IMPLEMENTATION_REPORT.md`
- `docs/CONTRACTS.md`
- `docs/INSTALL.md`
- `docs/SANDBOX.md`
- `src/ai_agent/interfaces/api/AgentWorker.cs`
- `src/ai_agent/interfaces/api/Api.csproj`
- `src/ai_agent/interfaces/api/BackendApplication.cs`
- `src/ai_agent/interfaces/api/BackendOptions.cs`
- `src/ai_agent/interfaces/api/ConsoleOperations.cs`
- `src/ai_agent/interfaces/api/CoreAgentFactory.cs`
- `src/ai_agent/interfaces/api/PostgresTaskStore.cs`
- `src/ai_agent/interfaces/cli/BackendClient.cs`
- `src/ai_agent/interfaces/cli/Cli.csproj`
- `src/ai_agent/interfaces/cli/Program.cs`
- `tests/BackendTests/BackendTests.csproj`
- `tests/BackendTests/CliCompositionChecks.cs`
- `tests/BackendTests/CompositionChecks.cs`
- `tests/BackendTests/Program.cs`
- `tests/BackendTests/container-gateway.ps1`
- `tests/BackendTests/run-container-smoke.ps1`
- `tests/CliTests/CliTests.csproj`
- `tests/CliTests/Program.cs`
- `tests/LaunchTests/github-auth.sh`
- `tests/LaunchTests/memory-workspace.sh`
- `tests/windows-launcher-check.ps1`

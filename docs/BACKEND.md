# Общий backend и очередь задач

Реализован следующий этап после памяти: ASP.NET Core 10 API, Task Service,
StackExchange.Redis и Agent Worker. API и Worker запускаются в одном процессе,
но разделены на компоненты. Agent Loop остаётся только в Core. Общие модели
`Session`, `AgentTask`, `AgentResult`, `IAgent` и `IContextStore` не изменены.

## Запуск

После `damir login`, с запущенным Docker Desktop, из папки нужного проекта:

```powershell
damir backend
```

Из корня этого репозитория: `.\damir.cmd backend` на Windows или
`./damir backend` на Linux/macOS. Запуск из корня использует `workspace`, как
демонстрационная консоль. HTTP доступен только на `127.0.0.1:8080`. Остановка —
Ctrl+C. `DAMIR_BACKEND_PORT` меняет порт, `DEFAULT_MODEL` выбирает модель перед
запуском. Launcher поднимает БД, Redis, Gateway, память и отдельную песочницу;
при выходе убирает только собственную песочницу и её IPC-том. Базы остаются.

Для опубликованного HTTP-порта backend подключён к отдельной bridge-сети
`backend_http`, к БД, Redis и Gateway — через `agent_internal`, а к сервису
интернет-поиска — через `search_internal`. Порт публикуется
только на loopback. Shell-песочница по-прежнему запускается без сети.

При первом запуске launcher создаёт ключ в `.uv-cache/damir-backend-key`.
Этот файл игнорируется Git; ключ не выводится в терминал. Можно передать свой
`DAMIR_BACKEND_API_KEY` (24–512 символов без пробелов); тогда используется он.
Ключ backend отличается от ключа LiteLLM.

Пример из второго PowerShell-терминала (путь `$repo` замени своим):

```powershell
$repo = 'D:\programming\Personal-AI-Agent'
$headers = @{ Authorization = 'Bearer ' + (Get-Content -Raw "$repo\.uv-cache\damir-backend-key").Trim() }
$base = 'http://127.0.0.1:8080'
$session = Invoke-RestMethod "$base/sessions" -Method Post -Headers $headers
$body = @{ session_id = $session.session_id; user_message = 'Привет! Какие инструменты тебе доступны?' } | ConvertTo-Json
$headers['Idempotency-Key'] = [guid]::NewGuid().ToString('N')
$task = Invoke-RestMethod "$base/tasks" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
Invoke-RestMethod "$base/tasks/$($task.task_id)" -Headers $headers
```

Последнюю команду повторяют, пока статус не станет терминальным. Для отмены:

```powershell
Invoke-RestMethod "$base/tasks/$($task.task_id)/cancel" -Method Post -Headers $headers
```

## HTTP

JSON использует snake_case и строковые статусы shared. Все маршруты кроме
`/health/live` требуют `Authorization: Bearer <ключ backend>`.

| Метод и путь | Результат |
| --- | --- |
| `POST /sessions` | 201, новая `Session`; ID генерирует сервер |
| `GET /sessions/{id}` | 200 `Session` или 404 |
| `GET /sessions/{id}/messages` | 200, сохранённые сообщения, включая tool calls и continuation |
| `POST /tasks` | 202 `AgentTask`, тело `{ "session_id": "...", "user_message": "..." }` |
| `GET /tasks/{id}` | 200 `AgentTask` со статусом и результатом |
| `POST /tasks/{id}/cancel` | 202, актуальная задача после запроса отмены |
| `GET /health/live` | 200, процесс обслуживает HTTP |
| `GET /health/ready` | 200 при доступных PostgreSQL/Redis и активном Worker, иначе 503 |

Для `POST /tasks` рекомендуется `Idempotency-Key`: сохраняй один ключ для
повторов одного сообщения в одной сессии. Повтор вернёт ту же задачу; другой
текст с тем же ключом — 409. Без ключа повторная отправка создаёт новую задачу.
Если ответ потерялся, повтори запрос с исходным ключом: результат записи в БД
уже мог сохраниться. Неизвестные поля и пустой текст отклоняются с 400; лимит
сообщения — 32000 символов, тела — 128 КиБ, незавершённых задач на сессию — 100
(при превышении — 429). Неправильный Content-Type — 415.

## Хранение и выполнение

PostgreSQL — источник истины: `damir_api_sessions` хранит принадлежность сессий,
`damir_api_tasks` — порядок приёма, статусы, ключи повторов, отмену и результаты.
История и пересказы используют существующий `PostgresContextStore`. Старые
таблицы и диалоги демонстрационной консоли не мигрируют и не удаляются.

Диспетчер периодически находит ожидающие задачи в SQL и отправляет уведомление
о сессии в Redis. Короткая дедупликация ограничивает число уведомлений. Worker
читает очередь и атомарно забирает следующую PENDING-задачу по порядку приёма.
Тексты запросов и ответы в Redis не передаются. Потерянное уведомление восстанавливает
повторный просмотр SQL; при недоступном Redis задача принимается в PostgreSQL
и остаётся PENDING. Перезапуск Redis не теряет принятые задачи; его очередь
намеренно не хранится на диске.

По умолчанию два обработчика выполняют разные сессии параллельно. PostgreSQL
advisory lock сериализует целый ход одной сессии, включая историю и инструменты;
используется тот же ключ блокировки, что и у демонстрационной консоли. Worker
держит отдельную блокировку owner/project: второй backend для той же пары
откажется запускаться. Это первоначальный режим с одним активным Worker;
несколько процессов Worker и отдельное масштабирование API пока не реализованы.

Переходы: PENDING → RUNNING → COMPLETED/FAILED/CANCELLED. Отмена PENDING сразу
даёт CANCELLED; отмена RUNNING передаётся через CancellationToken, а статус
меняется после завершения агента. Терминальный результат неизменяем.
Общий таймаут по умолчанию — 300 секунд, настраивается
`DAMIR_TASK_TIMEOUT_SECONDS` (1–3600); количество обработчиков —
`DAMIR_BACKEND_WORKERS` (1–8).

После аварийного завершения новый Worker помечает прежние RUNNING как FAILED
с сообщением о прерванной работе. **Начатые задачи автоматически не повторяются**:
инструмент мог выполнить действие до записи результата. PENDING продолжаются.
При штатной остановке активным операциям передаётся отмена. Потеря соединения
блокировки останавливает Worker. Отмена кооперативная: нельзя обещать остановку
инструмента, игнорирующего токен, или откат уже совершённых действий. При
сетевом разделении также нет гарантии мгновенной остановки внешней операции.

## Память, доступ и границы этапа

Один backend обслуживает одного доверенного владельца и один workspace.
Ключ авторизует доступ к настроенным `DAMIR_MEMORY_OWNER` и
`DAMIR_MEMORY_PROJECT`; запрос не может выбирать owner, project или путь
workspace. При чужом ID возвращается 404. Это локальный API, без регистрации
пользователей, TLS и публичного размещения.

Production-композиция использует LiteLLM, Core, файловые инструменты, `web.search`, shell
через отдельную песочницу, AGENTS.md/skills, compaction и долговременную память.
Системный текст Дамира вынесен в `config/damir-persona.txt` и общий с консолью.
При одинаковом owner/project долговременные факты общие для API и консоли;
API создаёт собственные сессии. Модель задаётся на старте, выбор модели через
HTTP пока отсутствует. GitHub MCP пока подключён только в демонстрационной
консоли. Логи backend содержат ID и события задач, а не тела запросов или ключи;
OpenTelemetry/Langfuse и учёт расходов backend ещё не подключены.

Обычный `damir` пока остаётся прямой демонстрационной консолью для Core.
Следующий шаг — переключить CLI на HTTP backend, затем подключить Telegram
через те же маршруты. Два готовых пользовательских интерфейса этим этапом
ещё не заявляются.

Без Docker-композиции backend можно запустить через
`dotnet run --project src/ai_agent/interfaces/api/Api.csproj`, настроив
`DATABASE_URL` (строка подключения Npgsql), `REDIS_URL` (конфигурация
StackExchange.Redis), `DAMIR_BACKEND_API_KEY`, `DAMIR_MEMORY_PROJECT`,
`AGENT_WORKSPACE`, `LITELLM_BASE_URL`, `LITELLM_API_KEY` и при включённой памяти
`MEMORY_DATABASE_URL`. Для shell необходим Linux-контейнер песочницы с IPC.

## Проверка

```powershell
pwsh -NoProfile -File tests/BackendTests/run-integration.ps1
```

Скрипт создаёт отдельные PostgreSQL/pgvector и Redis на случайных loopback-портах,
использует только синтетические данные и удаляет свои контейнеры после проверки.
Проверяются HTTP, авторизация, повторы, порядок сессии, параллельность разных
сессий, отмена, таймаут, потеря уведомления, недоступный Redis, перезапуск,
общая история и production-композиция Core с памятью между двумя сессиями,
наличием `web.search` и загрузкой инструкций и навыков из `.agents`.
Ответы модели в этих проверках заданы тестовым HTTP-сервером; платных запросов
к провайдеру и загрузки Ollama нет.

Проверка production-контейнера и Compose в отдельном временном проекте:
`pwsh -NoProfile -File tests/BackendTests/run-container-smoke.ps1`.
Она также собирает образ backend, проверяет HTTP с ключом и сохранение ошибки
недоступного Gateway, затем удаляет только собственные контейнеры и тома.

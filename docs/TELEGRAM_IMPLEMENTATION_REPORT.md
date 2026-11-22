# Telegram и изоляция аккаунтов — 22 ноября 2026

Исторический отчёт об отдельном административном режиме. Персональный перенос
диалога консоли по `/tg` и актуальные проверки описаны в
[TELEGRAM_HANDOFF_REPORT.md](TELEGRAM_HANDOFF_REPORT.md).

Ветка: `feature/telegram`, база `5387aee` из develop.
Реализация предназначена для ревью и PR в develop.

## Реализовано

Один Telegram-бот принимает личные текстовые сообщения от 1–32 разрешённых ID.
На каждый аккаунт launcher поднимает отдельный backend с собственным ключом,
owner/project, workspace-томом и shell-песочницей без сети. Получатель выбирает
сервер по реальному отправителю, проверяет `/identity` и хранит отдельное
состояние аккаунта. Чужие сессии, задачи, память и файлы недоступны.

PostgreSQL, Redis, шлюз моделей и поиск остаются общей доверенной инфраструктурой.
Администратор Docker/БД и получатель Telegram имеют технический доступ к данным.
Папки хоста и общий GitHub-токен владельца аккаунтам не подключаются.
Начальные рабочие тома пустые. Agent Loop остаётся в Core.

Общий HTTP-клиент вынесен из CLI в `interfaces/http`. Бот поддерживает сессии,
модель, навык, usage, контекст, сжатие, статус и отмену. Приём и доставка идут
независимо; ошибки одного backend после старта не блокируют другие аккаунты.
Повтор принятого сообщения использует прежний Idempotency-Key.
Транспортное состояние сохраняется атомарно с блокировкой второго получателя.

Launcher принимает список ID через запятую или `TELEGRAM_ALLOWED_USER_IDS`,
сохраняет список и позволяет заменить его при следующем запуске. Старое
`TELEGRAM_ALLOWED_USER_ID` совместимо с одним ID. Удаление ID прекращает приём
и доставку после перезапуска, но сохраняет его данные. Повторное добавление
восстанавливает личное состояние. Ключи хранятся локально вне Git; временный
манифест удаляется при остановке. Токен вводится скрыто. На Windows он сохраняется
локально с шифрованием DPAPI для текущего пользователя и компьютера; повторный
запуск использует токен и список ID без вопросов. Для смены данных:
`.\damir.cmd telegram -ConfigureTelegram`. На macOS токен сохраняется через
`git credential-osxkeychain` в «Связке ключей», отдельной записью на копию
репозитория; ID также используются без вопросов. Смена данных на Mac:
`./damir telegram --configure`. На Linux токен не сохраняется, ID подхватываются
автоматически. Токены из окружения не записываются в локальное хранилище.

Из прежнего общего состояния переносится только позиция обновлений.
Старые незавершённые отправки требуют `damir telegram-drain` с прежним одним ID
и проектом; новые сообщения этот режим не принимает. Прежние общие данные
не назначаются новым аккаунтам. README.md, ARCHITECTURE.md и shared не изменены.
Новых NuGet-зависимостей нет.

## Проверки

| Проверка | Результат |
| --- | --- |
| BackendTests/run-integration.ps1 | 90 проверок пройдено |
| TelegramTests | 54 проверки пройдено в Linux; 56 в Windows с диагностикой отсутствующего манифеста |
| CliTests | 20 проверок пройдено в Linux |
| ContractTests | 39 проверок пройдено в Windows и Linux |
| Shared.csproj | Сборка успешна, 0 предупреждений и ошибок |
| Dockerfile.tests | Ранее пройдены Core, 41 автономная проверка памяти, CLI, Telegram и три Unix launcher; после добавления проверки macOS-настроек образ повторно не собирался |
| windows-launcher-check.ps1 | Пройдено, включая два ID, отдельные ключи, сохранение токена через Windows DPAPI, повторный запуск без вопросов, смену/восстановление настроек и отказ некорректному списку до Docker |
| LaunchTests/telegram-settings.sh | Пройдено в Git Bash на Windows с поддельными Keychain, вводом и Docker; живую работу со «Связкой ключей» на Mac нужно проверить отдельно |
| TelegramTests/run-isolation-smoke.ps1 | Пройдено: два реальных backend/shell, разные workspace/IPC, отсутствие чужих файлов, выполнение через Core |
| TelegramTests/run-container-smoke.ps1 | Compose, сборка, права на том и отказ без токена проверены без сети |
| git diff --check | Пройдено |

Интеграционные проверки используют настоящие PostgreSQL/pgvector, Redis,
Worker и Core. Проверены одинаковый ключ памяти у разных owner, чтение из
новой сессии только своей памяти, отдельные рабочие файлы, запрет обхода пути,
чужого API-ключа, чтения истории/usage/задач, отмены и отправки в чужую сессию.
Проверена доставка через TelegramRouter на правильный ID после реального Core.

Docker-проверка выполняет shell.execute через модельный адаптер, очередь,
Core и отдельные IPC-песочницы. После записи файлов обоих аккаунтов повторный
запрос первого по-прежнему не видит файл второго. Проверяются реальные mounts,
network_mode none и отсутствие публикации портов в production-конфигурации.

Модель и Telegram в тестах синтетические. Платных запросов, живых сообщений
и использования личного токена нет. Автономный MemoryTests явно пропускает
SQL-часть без собственной тестовой БД; реальный pgvector проверен backend-тестами.
Временные контейнеры и тома проверок удаляются самими скриптами.

## Осталось владельцу и команде

После сообщения об ошибке первого живого запуска обнаружен старый путь launcher:
Compose создал пустой каталог вместо отсутствующего файла backend-профилей.
В текущем launcher применяется изолированная схема. Загрузка профилей теперь
явно объясняет отсутствие файла, а bind.create_host_path=false исключает
создание каталога. Образ Telegram и Windows launcher повторно проверены.
Живой бот при диагностике не запускался, токен и сообщения не читались.

Владелец подтвердил получение ответа от живого бота в одном аккаунте.
Осталось проверить живые диалоги двух аккаунтов и настоящее сохранение токена
в «Связке ключей» macOS. Для изменения доступа — остановка и запуск настройки:
`damir telegram -ConfigureTelegram` на Windows или `damir telegram --configure`
на macOS. Инструкция: [TELEGRAM.md](TELEGRAM.md).

Медиа, загрузка файлов из Telegram, кнопки, изменение доступа командами бота,
отдельные лимиты стоимости и публичная регистрация не реализованы.
На аккаунт запускаются два контейнера; хост должен иметь достаточно ресурсов.
Первоначальная инициализация требует всех настроенных backend.

При 100 недоставленных квитанциях новые сообщения переполненного аккаунта
пропускаются с записью в журнал. При потере подтверждения sendMessage часть
ответа может прийти повторно; это не повтор выполнения агента.
Отмена и исключение ID не откатывают действия инструментов.
Данные исключённых ID сохраняются; их удаления и переноса интерфейс не делает.

Тема изменений: Telegram с изоляцией аккаунтов и сохранением настроек.
После оставшихся живых проверок — ревью и PR в develop по решению владельца.

## Изменённые файлы

HTTP-клиент перенесён из `interfaces/cli/BackendClient.cs` в `interfaces/http/BackendClient.cs`;
Git распознаёт перенос при фиксации. Список затронутых файлов:
- `.env.example`
- `CONTRIBUTING.md`
- `damir`
- `damir-launcher.ps1`
- `docker/Dockerfile.backend`
- `docker/Dockerfile.cli`
- `docker/Dockerfile.shell-sandbox`
- `docker/Dockerfile.telegram`
- `docker/Dockerfile.tests`
- `docker-compose.yml`
- `docs/BACKEND.md`
- `docs/CLI.md`
- `docs/CONTRACTS.md`
- `docs/INSTALL.md`
- `docs/SANDBOX.md`
- `docs/TELEGRAM.md`
- `docs/TELEGRAM_IMPLEMENTATION_REPORT.md`
- `scripts/telegram-isolated.ps1`
- `scripts/telegram-isolated.sh`
- `scripts/telegram-settings.sh`
- `src/ai_agent/interfaces/api/BackendApplication.cs`
- `src/ai_agent/interfaces/cli/BackendClient.cs`
- `src/ai_agent/interfaces/cli/Cli.csproj`
- `src/ai_agent/interfaces/cli/Program.cs`
- `src/ai_agent/interfaces/http/BackendClient.cs`
- `src/ai_agent/interfaces/http/Http.csproj`
- `src/ai_agent/interfaces/telegram/Program.cs`
- `src/ai_agent/interfaces/telegram/Telegram.csproj`
- `src/ai_agent/interfaces/telegram/TelegramApi.cs`
- `src/ai_agent/interfaces/telegram/TelegramBot.cs`
- `src/ai_agent/interfaces/telegram/TelegramOptions.cs`
- `src/ai_agent/interfaces/telegram/TelegramRouter.cs`
- `src/ai_agent/interfaces/telegram/TelegramState.cs`
- `tests/BackendTests/BackendTests.csproj`
- `tests/BackendTests/CliCompositionChecks.cs`
- `tests/BackendTests/CompositionChecks.cs`
- `tests/BackendTests/container-gateway.ps1`
- `tests/BackendTests/IsolatedAccountsChecks.cs`
- `tests/BackendTests/TelegramCompositionChecks.cs`
- `tests/CliTests/Program.cs`
- `tests/LaunchTests/telegram.sh`
- `tests/LaunchTests/telegram-settings.sh`
- `tests/TelegramTests/AllowedUsersChecks.cs`
- `tests/TelegramTests/Program.cs`
- `tests/TelegramTests/RouterChecks.cs`
- `tests/TelegramTests/run-container-smoke.ps1`
- `tests/TelegramTests/run-isolation-smoke.ps1`
- `tests/TelegramTests/TelegramTests.csproj`
- `tests/windows-launcher-check.ps1`

# Перенос диалога в Telegram — 22 ноября 2026

Ветка публикации: `feature/telegram-dialogs`, целевая ветка PR: `develop`.
Незавершённые изменения наблюдаемости из исходной рабочей папки в этот PR не входят.
Корневой README, ARCHITECTURE.md и shared не изменены.

## Что изменено

- `damir telegram` запускает локальную консоль со ссылкой на собственного бота; `/tg` переносит текущий разговор. Одноразовая ссылка автоматически сохраняет фактический Telegram ID в PostgreSQL.
- Токен вводится один раз: Windows сохраняет его через DPAPI в `%LOCALAPPDATA%\Damir`, macOS — в Keychain, Linux — в Secret Service при наличии `secret-tool`. Старый зашифрованный файл Windows переносится после успешного чтения. Первая настройка сохраняет диалог, модель и навык на время перезапуска.
- Консоль и Telegram используют общие backend/Worker/Core, историю и рабочую папку. Новые запросы принимает один интерфейс; переключение сериализуется с приёмом задач и отклоняется при активном ответе. Кнопки «На компьютер» и «Продолжить здесь» переключают подтверждённый разговор в пределах одного запуска. Повторный `/tg` прерывает ожидающий опрос Telegram перед созданием ссылки.
- «Команды» открывает сжатие, оценку контекста, расход токенов, состояние ответа, модель, навык, историю и текущий проект. Служебные действия проходят через Worker/Core, не становятся сообщениями LLM и не расходуют навык следующего обычного хода.
- Рабочая папка совпадает с папкой запуска; домашняя папка и корень диска отклоняются до Docker. Разные пути получают отдельные проекты и сессии; возврат к прежнему пути восстанавливает историю. Новый запуск или выбор другой сессии требует свежей ссылки. Старые сообщения, кнопки и ссылки не активируют новый проект.
- `filesystem.send_to_telegram` проходит проверку инструкций Core и сохраняет снимок файла проекта в PostgreSQL. Получатель отправляет документы после ответа: до 20 МиБ на файл, три ожидающих файла, загрузка HTTP по одному. Подтверждение снимает файлы с очереди.
- Запрос и параметры сохраняются до отправки backend. Потеря квитанции и перезапуск сохраняют ключ повтора, текст, модель, навык, операцию и позицию доставки. Прежний административный режим сохранён как `damir telegram-isolated`. Новых NuGet-зависимостей нет.

## Изменённые файлы

| Область | Файлы |
| --- | --- |
| Запуск | `damir`, `damir-launcher.ps1`, `scripts/telegram-settings.sh`, `docker-compose.yml`, `docker/Dockerfile.cli` |
| API | `BackendApplication.cs`, `BackendOptions.cs`, `PostgresTaskStore.cs`, `CoreAgentFactory.cs`, `ConsoleOperations.cs`, новые `TelegramCompanionRoutes.cs`, `TelegramCompanionStore.cs`, `TelegramCompanionSelection.cs`, `TelegramFileTool.cs` в `src/ai_agent/interfaces/api/` |
| Консоль и HTTP | `interfaces/cli/Program.cs`, `Cli.csproj`, `interfaces/http/BackendClient.cs` |
| Telegram | `Program.cs`, `TelegramApi.cs`, `TelegramBot.cs`, `TelegramRouter.cs`, новые `TelegramCompanion.cs`, `TelegramCompanionCommands.cs` в `interfaces/telegram/` |
| Тесты | `tests/BackendTests/{Program,CliCompositionChecks,CompositionChecks,TelegramCompositionChecks,CompanionChecks,TelegramMenuChecks,WorkspaceHandoffChecks}.cs`, `tests/TelegramTests/Program.cs`, три сценария `tests/LaunchTests/`, `tests/windows-launcher-check.ps1`, новый `tests/personal-telegram-launcher-check.ps1` |
| Документация | `docs/TELEGRAM.md`, новые `TELEGRAM_ISOLATED.md` и этот отчёт, `TELEGRAM_IMPLEMENTATION_REPORT.md`, `CLI.md`, `BACKEND.md`, `CONTRACTS.md`, `INSTALL.md`, `SANDBOX.md` |

## Проверки

Результаты относятся к отделённому составу Telegram-изменений:

- BackendTests с временными PostgreSQL/Redis, синтетическими Gateway и Telegram: 154 проверки, включая меню и переходы A → B → A.
- TelegramTests: 57 проверок; CliTests: 20; ContractTests: 39.
- Сборка BackendTests вместе с API, CLI, Telegram и зависимостями, отдельная сборка Shared: без ошибок и предупреждений.
- Windows launcher: пройден, включая запуск из Windows PowerShell 5.1.
- Персональный launcher: 6 проверок — первая настройка без ID, повторное использование, запуск со ссылкой, другая копия проекта, перенос настройки и запрет широких папок.
- Четыре Unix-сценария `tests/LaunchTests/*.sh`: пройдены в Linux-контейнере без сети с исходниками только для чтения и подменными внешними сервисами.

Использованы локальный кэш NuGet и `NuGetAudit=false`: аудит зависимостей не
проверен, настройки проектов не изменены. Из-за ограничения памяти сборка
выполнялась последовательно с `DOTNET_PROCESSOR_COUNT=2`. Первая попытка сборки
завершилась нехваткой памяти; один интеграционный прогон прервался при сбое
Docker Desktop. Повторные сборка и полный прогон прошли.
Настоящие Bot API, личные данные и платные модели в тестах не использовались.

## Ограничения и действия команды

Нужны включённый компьютер, Docker и открытый Дамир. Один бот привязывается к
одному Telegram-аккаунту и допускает одного активного получателя. Для нескольких
изолированных пользователей сохранён [административный режим](TELEGRAM_ISOLATED.md).

Первую ссылку нельзя пересылать до привязки. После аварии аренда получателя
истекает через 60 секунд. Смена привязанного Telegram-аккаунта через интерфейс
пока не реализована. Входящие вложения и голос не поддерживаются. Telegram
показывает один чат для всех папок и последние 12 сообщений в обзоре; полная
история сохраняется в БД. Потеря подтверждения Telegram может привести к повтору
последней части или файла. На Linux без Secret Service токен требуется при
каждом запуске launcher.

Команде следует проверить переходы консоль → Telegram → консоль и A → B → A
на собственном боте по [TELEGRAM.md](TELEGRAM.md) и выполнить ревью PR в `develop`.
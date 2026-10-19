# ContactMirror 0.6.0: проверка доставки

Дата: 20 октября 2026. Пользователь разрешил merge в main, выпуск релиза и обновление локальной установки. **Статус: подготовка; публикация и установка ещё не выполнены.**

## Исходный код и план

Исходная ветка `feat/mcp-runtime`: feature `960132d`, совместимость с main `17575a8`, аудит rebase `925e76e`; база `e40eb5b` (main 0.5.0). Выпускается MINOR 0.6.0 с 38 MCP tools, shared runtime, защитой черновиков и раздельными правами на папки/local/Google. Product runtime после rebase не меняется; release metadata и Native test harness готовятся отдельно. Curated notes: [releases/0.6.0.md](releases/0.6.0.md).

Required advisory reviewer `/root/mcp_spec_review` работает procedural no-mutation при writable sandbox: не меняет файлы, Git, внешнее состояние, не запускает сборки/тесты. До публикации требуется закрыть Native gate, закрепить exact merged SHA и проверить свежий Production output. `Publish-Release.ps1` используется только для dry-run plan; его стандартные notes не публикуются.

## Текущие проверки

| Проверка | Статус | Evidence |
| --- | --- | --- |
| Release solution build | PASS до release stamp; свежая 0.6.0 validation выполняется | `chat-artifacts/release-0.6.0/build.log` |
| Core | Выполняется | `TestResults/Release060/core/core.trx` |
| Google/storage | Ожидает Core | `TestResults/Release060/google/google.trx` |
| MCP | Ожидает доменные suites; fresh Synthetic payload | `tests/ContactMirror.McpTests/bin/Release/net10.0/TestResults/release-060-mcp.trx` |
| Headless | Ожидает MCP | `tests/ContactMirror.UiTests.Headless/bin/Release/net10.0/TestResults/release-060-headless.trx` |
| Native | **BLOCKED: Windows LockApp, keyboard foreground недоступен** | `release-060-native-source-before.trx`, `release-060-native-focus-fixed.trx`, `release-060-native-input-ready.trx` в Native TestResults |
| Synthetic package | PASS | `artifacts/releases/Synthetic-0.6.0-preflight/build-0.6.0-mcp.validation1.json` |
| Production/package/public updater | Не выполнены | План после exact main merge |
| Локальная установка | Не выполнена; остаётся 0.5.0 | Preservation baseline ниже |

Для протокольных проверок fresh Synthetic Desktop/MCP staging скопирован в `artifacts/mcp-validation/app`; старый payload сохранён соседним каталогом. Сборки, упаковка и suites выполняются последовательно. Исторические 521 PASS/1 unresolved от 15 октября не считаются свежими результатами выпуска.

## Native blocker и диагностика

Первый preflight `--no-build` использовал диагностические binaries, оставшиеся после прошлого сеанса; он исключён из acceptance. После свежей обычной сборки исходный тест снова упал на focus assertion. Before video: `chat-artifacts/editor/editor-before-20261019-221352-989/contact-editor.mp4`; TRX `release-060-native-source-before.trx`.

Уточнён Native harness: перед глобальными клавишами проверяется actual foreground HWND; настоящий `Automation.FocusedElement()` ожидается с process guard, исходной паузой 80ms и всеми пятью прежними assertions. Timeout возвращает null. При отказе активации допускается одиночный click по поднятому caption собственного тестового HWND, temporary topmost снимается в finally; размеры активной записи не меняются. Продуктовый focus trap не меняется. Advisory source review исправил потенциальный premature PASS и обход process guard; обе находки закрыты по source, acceptance требует actual PASS.

Новый precondition не прошёл ещё до клавиатурного ввода. Read-only Win32 probe выявил foreground process **LockApp** при Session1: Windows заблокирована. Это объясняет отсутствие корректной предпосылки keyboard input в текущем запуске, но не доказывает причину всех исторических failures или исправление продукта. Пользователю отправлен запрос разблокировать Windows. Гейт не ослабляется и не объявляется PASS.

## Сохранность локальной установки

До обновления: installed ProductVersion `0.5.0+0ca08593c877e629054c8075c0568bb691a8dcf3`; Production App ID `ContactMirror.Desktop`. Сохранён baseline SHA256/size **1017** файлов contacts/groups/photos в пользовательской папке и **3** protected settings/vault файлов в LocalAppData. Папки разработки `.git`/`.venv` в snapshot не входят. Evidence: `chat-artifacts/release-0.6.0/preservation-before.json` (ignored, не публикуется).

После сборки Production необходимо проверить effective OAuth ClientId против установленного пакета без печати значений. Перед установкой проверить public feed/download через настоящий `VelopackUpdateBackend` с изолированным cache и `applied=false`; затем установить опубликованный Setup и отдельно проверить версию, hashes, обычный запуск и сохранность данных/settings/vault. Реальные Google-записи не выполнять.

## Review / Stop

Pre-release plan пригоден после закрытия обязательного Native gate. **Текущий STOP: NEEDS-FIX / внешнее состояние Windows.** Ни готовность к stable publication, ни успешный merge/release/install пока не заявляются. Автоматический downgrade приложения не поддерживается; исправление выпускается следующей версией, ручная переустановка — отдельный rollback path. OAuth Testing и отсутствие подписи установщика сохраняются.

# ContactMirror 0.6.0: проверка доставки

Дата: 20 октября 2026. Пользователь разрешил merge в main, выпуск релиза и обновление локальной установки. **Статус: подготовка; публикация и установка ещё не выполнены.**

## Исходный код и план

Исходная ветка `feat/mcp-runtime`: feature `960132d`, совместимость с main `17575a8`, аудит rebase `925e76e`; база `e40eb5b` (main 0.5.0). Дополнения: Native harness `85c79c4`, release metadata `e0fdf1e`. Подготовлен [draft PR #7](https://github.com/Kibnet/ContactMirror/pull/7), MERGEABLE/CLEAN при проверке. Готовится MINOR 0.6.0 с 38 MCP tools, shared runtime, защитой черновиков и раздельными правами на папки/local/Google. Product runtime после rebase не меняется. Curated notes: [releases/0.6.0.md](releases/0.6.0.md).

Required advisory reviewer `/root/mcp_spec_review` работает procedural no-mutation при writable sandbox: не меняет файлы, Git, внешнее состояние, не запускает сборки/тесты. До публикации требуется проверить поведение диалога доступным UI runner, закрепить exact merged SHA и проверить свежий Production output. `Publish-Release.ps1` используется только для dry-run plan; его стандартные notes не публикуются.

После вопроса пользователя «А headless тесты тебе зачем даны?» пересмотрен выбор runner. Безусловный Native gate был избыточным: appautomation предписывает FlaUI при доступном interactive Windows desktop; центральный UI profile требует релевантные UI-тесты. Поведение приложения проверяется Headless; недоступность Win32 input не объявляется ни продуктовым дефектом, ни Native PASS. Историческое падение Native сохранено ниже.

## Текущие проверки

| Проверка | Статус | Evidence |
| --- | --- | --- |
| Release solution build | PASS, 0 warnings/errors, версия 0.6.0 | `chat-artifacts/release-0.6.0/build.log` |
| Core | 174/174, 0 skip, 1m36s | `TestResults/Release060/core/core.trx` |
| Google/storage | 185/185, 0 skip, 30s | `TestResults/Release060/google/google.trx` |
| MCP | 44/44, 0 skip, 1m56s; fresh Synthetic payload | `tests/ContactMirror.McpTests/bin/Release/net10.0/TestResults/release-060-mcp.trx` |
| Headless | 103/103, 0 fail/skip; после keyboard coverage повторно 103/103, 2m05s | `tests/ContactMirror.UiTests.Headless/bin/Release/net10.0/TestResults/release-060-headless-keyboard-full.trx`; прежний `release-060-headless.trx` сохранён |
| Native | **BLOCKED: Windows LockApp, keyboard foreground недоступен** | `release-060-native-source-before.trx`, `release-060-native-focus-fixed.trx`, `release-060-native-input-ready.trx` в Native TestResults |
| Synthetic package | PASS | `artifacts/releases/Synthetic-0.6.0-preflight/build-0.6.0-mcp.validation1.json` |
| Production candidate package | PASS, локальный кандидат из unmerged `e0fdf1e`, не release | `artifacts/releases/Production-0.6.0-candidate/build-0.6.0.json`, `chat-artifacts/release-0.6.0/candidate/package-verification.json` |
| Effective OAuth/account continuity | PASS, локальное чтение vault, без Google-запросов | `chat-artifacts/release-0.6.0/candidate/oauth-continuity.json` |
| Merge/public release/public updater | Не выполнены | План после Headless keyboard PASS и exact main merge |
| Локальная установка | Не выполнена; остаётся 0.5.0 | Preservation baseline ниже |

Для протокольных проверок fresh Synthetic Desktop/MCP staging скопирован в `artifacts/mcp-validation/app`; старый payload сохранён соседним каталогом. Сборки, упаковка и suites выполняются последовательно. Исторические 521 PASS/1 unresolved от 15 октября не считаются свежими результатами выпуска.

Свежий итог без Native: **506 PASS, 0 fail/skip**. Headless PNG `chat-artifacts/ui/20261019-222823-728/mcp-photo-refreshed.png` и `chat-artifacts/ui/20261019-222824-024/mcp-settings-rendered.png` открыты и просмотрены: новое имя/фото отображаются в карточке и таблице, панель прав/папок/config доступна. Это rendered-state evidence, не Native keyboard acceptance и не UX-исследование.

## Локальный Production-кандидат

При чистом tracked tree `e0fdf1e42a760527b542691c161667942f6cd129` собран отдельный output `artifacts/releases/Production-0.6.0-candidate`. ProductVersion — `0.6.0+e0fdf1e42a760527b542691c161667942f6cd129`, App ID `ContactMirror.Desktop`, канал `win`, RID `win-x64`. Это **unmerged candidate**, который не публиковался и не устанавливался. После слияния требуется новая сборка из exact merged SHA в другом свежем output; кандидат не подменяет release provenance.

`Build-Release.ps1` подтвердил compiled profile/shared binaries, а настоящий `VelopackUpdateBackend.VerifyPackageFile` — full SHA256/size, nuspec/identity/version/channel/RID/mainExe и assembly hash. Full SHA256: `E403FD680AD521F1F7A1DCC46E88C688A0B28C56C2DC3B6D5D54E98DC94E702B`; assembly hash совпал со staging. В ZIP 290 entries; MCP exe/dll/docs и DataGrid присутствуют; PDB, test binaries, DPAPI/vault payload отсутствуют. Publisher OAuth и GitHub source проверены без вывода значений credentials.

Dry-run `Publish-Release.ps1` вернул ровно пять assets: full nupkg, Setup, portable ZIP, `releases.win.json`, `SHA256SUMS.txt`, pinned target `e0fdf1e...`, `published=false`. Standard notes, созданные скриптом в output, не предназначены для публикации; использовать curated `docs/releases/0.6.0.md`.

`OAuthClientOptions.LoadForRuntime(packaged:true)` выбрал publisher для старой установки и кандидата. Effective ClientId одинаков; оба `GoogleAccountService.GetAccountAsync` локально распознали тот же сохранённый аккаунт. Vault hash не изменился. Идентификатор, адрес аккаунта, токены и секреты не печатались/не сохранялись в evidence. Это continuity локального подключения, а не live Google OAuth/sync validation.

## Native blocker и диагностика

Первый preflight `--no-build` использовал диагностические binaries, оставшиеся после прошлого сеанса; он исключён из acceptance. После свежей обычной сборки исходный тест снова упал на focus assertion. Before video: `chat-artifacts/editor/editor-before-20261019-221352-989/contact-editor.mp4`; TRX `release-060-native-source-before.trx`.

Уточнён Native harness: перед глобальными клавишами проверяется actual foreground HWND; настоящий `Automation.FocusedElement()` ожидается с process guard, исходной паузой 80ms и всеми пятью прежними assertions. Timeout возвращает null. При отказе активации допускается одиночный click по поднятому caption собственного тестового HWND, temporary topmost снимается в finally; размеры активной записи не меняются. Продуктовый focus trap не меняется. Advisory source review исправил потенциальный premature PASS и обход process guard; обе находки закрыты по source. Для заявления Native PASS по-прежнему нужен фактический успешный Native run.

Новый precondition не прошёл ещё до клавиатурного ввода. Read-only Win32 probe выявил foreground process **LockApp** при Session1: Windows заблокирована. Evidence `input-desktop-probe.json` (22:21Z) и `input-desktop-recheck.json` (22:29Z) в `chat-artifacts/release-0.6.0/` подтверждают состояние. Это объясняет отсутствие корректной предпосылки keyboard input в текущем запуске, но не доказывает причину всех исторических failures или исправление продукта. Первоначально запрошено разблокирование, затем выбран доступный Headless runner. Native остаётся непроверенным, его assertions не ослаблены. Before MP4 проверен через ffprobe: H.264, 1250×876, 66.6s; это failing test evidence, не успешная приёмка.

### Проверка клавиатуры в Headless

Существующий `Main_window_close_Escape_stays_with_draft_then_Discard_closes_without_any_file_write` дополнен реальными Avalonia `Window.KeyPress/KeyRelease`: естественный начальный focus без `.Focus()` на ожидаемой кнопке; Tab ×5 и Shift+Tab ×5, после каждого фокус только на одной из трёх кнопок и все три посещены; Ctrl+S сохраняет modal/dirty и байты файла; Escape возвращает к черновику; повторное закрытие/Discard закрывает окно без записи. Product source не менялся.

Targeted run: `dotnet run --project tests/ContactMirror.UiTests.Headless -c Release -- --treenode-filter '/*/*/*/Main_window_close_Escape_stays_with_draft_then_Discard_closes_without_any_file_write' --maximum-parallel-tests 1 --report-trx --report-trx-filename release-060-headless-keyboard.trx` — **1/1 PASS**, 0 skip, 8.5s. Полный affected Headless suite: та же команда без filter с `--no-build` и filename `release-060-headless-keyboard-full.trx` — **103/103 PASS**, 0 fail/skip, 2m05s. PNG `chat-artifacts/ui/20261019-224835-212/editor-window-close-decision.png` открыт: диалог и три решения видны, фон заблокирован. Видео fallback: заблокированный interactive desktop; actual Headless keyboard assertions/TRX/PNG заменяют недоступную запись Native. Win32 foreground, системный Alt+F4, UIA keyboard input и native file dialogs этим прогоном не проверены.

## Сохранность локальной установки

До обновления: installed ProductVersion `0.5.0+0ca08593c877e629054c8075c0568bb691a8dcf3`; Production App ID `ContactMirror.Desktop`. Сохранён baseline SHA256/size **1017** файлов contacts/groups/photos в пользовательской папке и **3** protected settings/vault файлов в LocalAppData. Папки разработки `.git`/`.venv` в snapshot не входят. Evidence: `chat-artifacts/release-0.6.0/preservation-before.json` (ignored, не публикуется).

После подготовки и чтения account continuity hashes всех 1017 файлов и трёх protected settings/vault снова совпали; установленная версия осталась 0.5.0. Evidence: `preservation-after-preparation.json`, `updated=false`. Никакая установка не выдаётся за выполненную.

Перед установкой требуется проверить public feed/download через настоящий `VelopackUpdateBackend` с изолированным cache и `applied=false`; затем установить опубликованный Setup и отдельно проверить версию, hashes, обычный запуск и сохранность данных/settings/vault. Реальные Google-записи не выполнять. Probe source подготовлен в ignored `chat-artifacts/release-0.6.0/public-update-probe`; public mode не запускался, поскольку release 0.6.0 ещё не опубликован.

## Review / Stop

Первоначальный STOP был NEEDS-FIX из-за избыточного требования Native. После уточнения runner продуктовый сценарий принят по actual Headless keyboard evidence, affected full suite103/103 PASS; системная Native-интеграция остаётся ограничением проверки. Можно продолжать авторизованную доставку. До проверки exact merged package/public updater/установки их успешность не заявляется. Автоматический downgrade приложения не поддерживается; исправление выпускается следующей версией, ручная переустановка — отдельный rollback path. OAuth Testing и отсутствие подписи установщика сохраняются.

Первый procedural advisory reviewer прочитал четыре свежих TRX (506 PASS/0 fail/skip), manifest/package/OAuth/preservation proof и probe source; независимо сверил size/SHA256 трёх binary assets и feed, открыл два новых Headless PNG и прочитал live PR #7 (OPEN/DRAFT, MERGEABLE/CLEAN). Новых замечаний к подготовке не было; первоначальный полный STOP оставлен NEEDS-FIX из-за Native. Affected source re-review нового Headless keyboard flow — PASS: нет ослабления проверок или принудительного expected focus. Reviewer подтвердил применимость Headless для поведения Avalonia при недоступном interactive desktop. Исторический Native failure не заменён на PASS; future exact-main rebuild/public updater/install proof по-прежнему требуются.

Affected final review: reviewer прочитал actual targeted1/1 и full103/103 TRX (0 fail/skip), осмотрел PNG диалога и подтвердил пять ролей в scope поведения Avalonia/подготовки delivery. **STOP PASS для авторизованного merge**; product behavior blocker закрыт actual Headless evidence. Native/Win32 integration остаётся явной непроверенной границей. Создан hash-verified backup1017 contact/group/photo файлов и3 protected settings/vault в LocalAppData вне репозитория; credentials не публикуются.

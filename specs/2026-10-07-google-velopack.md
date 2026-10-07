# ContactMirror: рабочее подключение Google и установка/обновление Velopack

## 0. Метаданные

- Фаза: EXEC; exact approval нового scope получен 2026-10-07. История SPEC/review ниже сохранена.
- Тип: delivery-task; expanded, large/high-risk — OAuth, доступ к контактам, конфигурация издателя, установка и перезапуск программы.
- Владелец продукта: пользователь; implementation/review coordination: root.
- Stack: central AGENTS → routing-matrix, creator-vibe-lens, model-behavior-baseline, tool-execution-baseline, collaboration-baseline, quest-governance, quest-mode, testing-baseline, spec-linter, spec-rubric, review-loops. Profiles: dotnet-desktop-client, product-system-design, ui-automation-testing; context testing-dotnet. Применимых local overrides не обнаружено.
- Canonical template: `C:/Users/Kibnet/.codex/agents/templates/specs/_template.md`.
- Поверхность: Codex desktop, Windows/PowerShell; behavior baseline каталога GPT-6 Astra. Фактическая модель/tier не меняются и не выводятся из названия baseline. Model eval: не применимо, приложение не использует LLM.
- Дата проверок: 2026-10-07, Europe/Moscow. Candidate v0.2.0; контрольное обновление до v0.2.1, с отличимой версией в UI. Git-каталога/remote у проекта нет.
- Связь: `2026-10-06-contactmirror-v1.md` и `docs/VALIDATION.md`; новые требования не отменяют сохранность формата v1 и sync/recovery инвариантов.

## 1. Overview / Цель

Исходное поручение: «Добавь нормальную поддержку гугл, сейчас же не работает. и настрой установку и обновление через velopack».

Success means: обычный установленный ContactMirror подключает настоящий Google через кнопку и системный браузер; получает контакты в выбранную папку и возвращает проверенную правку специально созданного тестового контакта. Setup устанавливает программу; следующий пакет находится, скачивается, применяется и запускает новую версию с сохранёнными настройками, привязкой аккаунта и контактными файлами.

Output: исходники и тесты, настроенная конфигурация Desktop OAuth издателя в сборке, Windows x64 Setup/Velopack portable/full+delta packages/feed, воспроизводимый release script, UI обновления и отчёт actual live/install/restart checks. Подготовленный установщик, установленная копия, локальная проверка обновления и доступный другим людям update feed отмечаются отдельно.

Stop: не выдавать demo/HTTP fixtures/TestVelopackLocator за настоящий OAuth или установленное обновление. Если нет доступа к Google Console/consent либо источнику выпуска, завершить независимые части и назвать точную невыполненную проверку. Готовый локальный пакет без publisher OAuth не считается исправленным входом Google. Существующие пользовательские контакты не используются для destructive smoke.

## 2. Текущее состояние (AS-IS)

- v0.1 имеет real People API gateway, OAuth PKCE/state/nonce/JWT, DPAPI vault, JSON/SQLite sync и demo. Последнее recorded evidence — 165 Release tests PASS и ordinary/demo EXE smoke; это результаты 2026-10-06, сегодня suites не перезапускались.
- `GoogleAccountService.SignInAsync` останавливается при `!options.IsConfigured`. Проверены environment Client ID/secret presence и обычные appsettings paths: ни одной конфигурации нет. `%LocalAppData%/ContactMirror` также отсутствует. Значения credentials не читались/не печатались.
- Проверка текущего callback механизма: временный `HttpListener` на 127.0.0.1/random port успешно запущен и закрыт без elevation. Это не OAuth login, но отсутствие administrator rights не воспроизвело предполагаемый callback blocker.
- Настройки и vault сейчас находятся в `%LocalAppData%/ContactMirror`; JSON/SQLite/backup — в выбранной папке. `DesktopPreferences.OAuthPath` указывает на внешний developer JSON, который может исчезнуть; publisher configuration не поставляется.
- Velopack отсутствует в csproj/tool manifest/Program. Нет App Version/release source/release pipeline. Portable ZIP не является Velopack installer или update evidence.
- Пользователь ответил: Google Cloud проект и Desktop client нужно подготовить. Источник updates задан optional вопросом; ответа пока нет. Отсутствует выбранный repository/HTTPS URL.
- `gh.exe` доступен; gcloud не обнаружен. CUA inventory обнаружил Chrome/IAB, но Chrome provider вернул HTML вместо JSON при listing. Это browser tooling failure, а не Google denial и не доказательство отсутствия авторизации.

## 3. Проблема

Доставленный локальный candidate не содержит необходимой OAuth identity приложения и цепочки выпуска. В результате главный сценарий пользователя обрывается до доступа Google, а установка и последующие обновления отсутствуют.

## 4. Цели дизайна

- Обычный пользователь нажимает «Подключить Google»; Google Cloud/JSON-настройки принадлежат издателю.
- Сохранить все действующие категории People API, фото/ярлыки/starred, preview, confirmations и recovery.
- Один владелец updater lifecycle на всё приложение; UI показывает его действительное состояние.
- Обновление не прерывает синхронизацию и не заменяет пользовательские данные.
- Локальная установка и upgrade воспроизводимы без опубликованного release; publisher build fail-closed при отсутствующей конфигурации.

## 5. Non-Goals

- Изменение файлового формата контактов, автоматическая синхронизация, новые платформы, корпоративный каталог/Other contacts.
- Покупка сертификата, принятие новых Google соглашений от имени человека, платная инфраструктура и массовая OAuth verification в текущем локальном scope.
- Публичная публикация исходников/release или production consent audience без отдельного конкретного поручения. Подготовка средств выпуска входит в scope; исполнение публикации зависит от выбранного адресата.
- Изменения/удаления существующих контактов человека для тестов. Контрольные данные имеют отдельный account/workspace либо явно перечисленные новые ContactMirror test resources.

## 6. Предлагаемое решение (TO-BE)

### 6.1 Ответственности

| Компонент | Ответственность |
| --- | --- |
| GoogleAccountService/OAuthClientOptions | External browser OAuth, validated credentials, explicit configuration readiness и понятные auth errors |
| ApplicationConfiguration / ApplicationPaths | Publisher OAuth/update source/version, developer override, неизменный data root вне install root |
| IApplicationUpdateService + adapter | Один UpdateManager, состояния/check/download/apply, source/version identity, concurrency/cancellation |
| Desktop composition / VM | Связать sync activity и updater, дать пользователю check/download/restart и feedback без блокировки UI |
| Program.Main | Velopack bootstrap первой операцией, fast hooks без UI/Google/чтения workspace |
| release scripts / local tool manifest | Одна команда self-contained publish → validated staging → vpk pack → manifest/hash verification |
| live/install validation | Actual publisher-configured app, Google readback и настоящий controlled installed upgrade |

### 6.2 Детальный дизайн

#### Google

1. После exact approval подготовить один Google Cloud проект ContactMirror, включить People API, Google Auth Platform branding/audience/data access и Desktop OAuth client. Первое использование — Testing audience с выбранным аккаунтом. Организация, support/developer email и аккаунт берутся из явно выбранной Google Console identity, не из личного профиля/догадки.
2. Требуемые scopes: `openid`, `email`, `https://www.googleapis.com/auth/contacts`. Не добавлять Drive/Gmail/Directory. Public audience/verification — следующий release stage; Testing ограничения и повторная авторизация показаны честно.
3. Publisher config генерируется в staging `appsettings/oauth-client.json` из конкретного созданного клиента и включается в Setup/portable. Это identity desktop-приложения, не пользовательские access/refresh tokens; клиент desktop не может хранить client_secret как confidential server secret. Персональные tokens остаются только в DPAPI vault и исключаются из staging/logs/artifacts.
4. Developer JSON остаётся optional advanced override. Import копирует валидную конфигурацию в стабильный per-user config directory вне install root; сохраняется stable path, а не ссылка на Downloads/папку предыдущей сборки. При недоступном/invalid override показать исправление и переключение к publisher, без молчаливой смены клиента/привязки.
5. Publisher source автоматически доступен после первой установки. При отсутствии/invalid publisher config обычный вход не обещает работу: в developer build объяснить настройку, release script отказать в выпуске Google-enabled package.
6. Сохранить callback state/PKCE/nonce/issuer/audience/signature/scope и refresh account binding. Callback cancellation/timeout/посторонний запрос не оставляют listener/process/UI занятными. Не объявлять фикс callback до воспроизведения новой ошибки.
7. Улучшить bounded structured ошибки: отсутствующая конфигурация, отказ пользователя, disabled People API, app/testing access blocked, scope missing, token invalid_grant/401. Не показывать raw OAuth tokens/полные ответные payload; retry после неясной mutation по прежнему запрещён.
8. Live smoke: обычный EXE → пользователь завершает Google login/consent → read all → preview → download в новую папку. Новый контакт с явным test marker → Apply create → targeted readback; file phone/custom Unicode edit → upload/readback; Google edit этого же контакта → download; photo/label/star и редкие категории проверяются на поддерживаемом Google fixture с registry mapping. Повторный scan должен стать no-op после ожидаемой propagation. Созданные resources фиксируются по ID; cleanup касается только перечисленного disposable fixture и не обходится без требуемого tooling confirmation.

#### OAuth configuration precedence

| Runtime | Порядок выбора | Invalid / mismatch |
| --- | --- | --- |
| Production Setup / managed portable | Explicit managed per-user override → packaged publisher | Выбранный invalid override останавливает login и предлагает исправить/явно вернуться к publisher; без silent fallback |
| Raw developer run / legacy ZIP | Explicit managed override → environment pair → exe-local JSON → CWD-local JSON → publisher | У первого присутствующего источника проверяется целая конфигурация; не смешивать ID/secret разных источников, invalid fail-closed |
| Compile-time validation profiles | Immutable profile policy; IntegrationValidation — publisher, Validation — OAuth disabled | Production/runtime arguments не меняют profile identity или разрешение OAuth |

Production игнорирует environment и CWD/exe-local developer files, включая старые `OAuthPath` за пределами managed directory; legacy setting предлагает явный валидируемый импорт, но не молча загружается. Raw developer путь сохраняет существующие источники в указанном порядке. UI показывает source label (publisher / managed override / developer environment / developer file), без client_secret; смена ClientId требует reauthentication, текущий vault не перезаписывается до успешной привязки. Tests покрывают присутствие конкурирующих источников, invalid higher-priority source, old external OAuthPath, client mismatch и production startup из постороннего cwd.

#### Velopack

1. Закрепить Velopack SDK и vpk **1.2.0** (stable, official registry verified 2026-10-07). Локальный dotnet-tools manifest; не менять глобальный resolver/tool cache.
2. `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` — первой операцией Main, ровно один раз. Обычная и demo startup идут после; install/update/uninstall fast hooks быстро завершаются без запуска Avalonia/OAuth/workspace. Сверить реально доступные API/CLI flags закреплённой версии.
3. Stable production PackId **ContactMirror.Desktop**, main exe **ContactMirror.exe**, RID **win-x64**, channel **win**, self-contained .NET 10. App/version/package/feed versions берутся из одного `Version` input; candidate0.2.0 и контрольный0.2.1. Продуктовые версии не выводятся из даты сборки/старого AssemblyVersion1.0.0.
4. Критический storage контракт: install root `%LocalAppData%/ContactMirror.Desktop` не совпадает с существующим data root `%LocalAppData%/ContactMirror`. Contacts/workspace также вне install root. Запрет выбора папки зеркала внутри RootAppDir через validated path policy, включая import/restore/history; нельзя потерять зеркало при замене current. Старый data root не переименовывается и не очищается при uninstall.
5. Updater source immutable на одну операцию. Runtime publisher config: `{kind: github|https, url, channel: win}`. Поддержать GithubSource без встроенного пользовательского GitHub token и SimpleWebSource HTTPS. Local file/loopback source используется только в контрольном workflow, не подменяет remote readiness. Без configured URL — явное NotConfigured, без fictitious update-success.
6. Один application-scoped update service: `NotConfigured/Unsupported/Idle/Checking/Available/Downloading/ReadyToRestart/Applying/Error`; snapshot содержит running installed version, target version, progress, checked time и безопасную ошибку. Параллельные запросы check/download объединяются или отклоняются через один gate; backend один, ViewModels проецируют общий snapshot. Смена source отменяет/сбрасывает прежний snapshot и не применяет пакет от прежнего feed.
7. Manual «Проверить обновления» и unobtrusive startup check для installed runtime; startup check не блокирует вход/первый экран и не скачивает/не перезапускает автоматически. «Скачать» → «Установить и перезапустить» после осознанного действия. Закрытие обычным X не запускает WaitExitThenApply; autostart application сохраняет explicit apply policy.
8. Apply/restart разрешён только когда нет auth/sync/restore/cleanup и активного account/workspace operation. Activity guard на общую composition, а не только IsBusy отдельного VM. Не запускать принудительное обновление чужой работающей копии; installed-runtime single-instance/activity coordination предотвращает потерю active journal. Demo restart сохраняет режим; обычный restart не превращается в demo.
9. Raw dotnet/обычный legacy ZIP: app продолжает запускаться; updater показывает Unsupported с действием получить Setup, не выдает NotInstalledException как поломку Google. Velopack-managed portable проверяется отдельно по locator semantics и не приравнивается к legacy ZIP. Download cancellation, invalid feed, wrong channel/package/RID, corrupted hash, unavailable disk и delta→full fallback имеют tests/error states; downgrade не включается автоматически.
10. Release script с явными Version/OAuthClientPath/UpdateSource/Output parameters: restore pinned tool → publish → включить publisher config/docs/notices → исключить tokens/state/test assemblies/debug artifacts → pack. Не маскировать отсутствие publisher config флагом «Google ready». Output: Setup.exe, Velopack portable, full.nupkg, feed/assets; второй pack с доступным previous full дает delta. Проверить содержимое и feed/version/hash. Public upload выполняется только отдельно после review; GitHub требует выбранного repo, которого сейчас нет.
11. Настоящий installed upgrade проверяется двумя изолированными packaging profiles. **ContactMirror.Validation**: synthetic demo, реальный DPAPI с synthetic payload, OAuth запрещён; для повреждённых пакетов, отмены и busy/restart. **ContactMirror.IntegrationValidation**: ordinary app с настоящим publisher OAuth и выбранным тестовым аккаунтом; для actual Google login и сохранения account/vault после actual update. У каждого immutable compile-time PackId и data root `%LocalAppData%/ContactMirror.Validation.Data` либо `ContactMirror.IntegrationValidation.Data`, вне соответствующего install root; profiles нельзя включить произвольным production runtime argument. Release script принимает явный ValidationProfile; production default не меняется. Отличия profiles — identity, paths и local feed; auth/sync/updater code общий. Fixture paths/mode сохраняются при fast hooks и restart; не зависят от cwd или временной environment variable.
12. Для каждого profile: actual Setup0.2.0 → установленный EXE → UI check/download0.2.1 → apply/restart → новая UI/assembly/locator version, PID и executable path → сохранённые settings/workspace/backup. Integration profile дополнительно проверяет настоящий OAuth vault, account binding, read-only Google readback и следующий no-op без повторного consent. Synthetic profile доказывает DPAPI mechanics, но не заменяет integration proof. Точный installer invocation/custom install-directory options проверяются по `--help` и source до запуска; TestVelopackLocator только дополняет. Production payload/config/identity/hooks инспектируются отдельно; isolated install не называется production deployment. Созданные test registration/shortcuts/paths учитываются и очищаются только в пределах fixture; credential cleanup не экспортирует токены. Если реальный запуск, consent, upgrade или cleanup невозможен — соответствующий AC остаётся incomplete.

#### Update handoff / second launch

Normal-instance mutex identity состоит из Windows user SID и immutable app identity; процесс держит его от composition до exit. Fast hooks проходят Velopack bootstrap до normal gate и не создают account/workspace services. Second normal launch активирует существующее окно либо сообщает о занятом обновлении, не начинает второй sync.

Один process mutex не защищает промежуток после exit старой версии. Поэтому перед SDK apply под атомарным exclusive activity lease записывается durable `Applying` marker вне install root: identity, source generation, ожидаемая версия/hash, old PID+creation time и одноразовый handoff nonce. Все normal launches проверяют marker до composition; обычный shortcut launch при Applying не открывает Google/workspace, даже если process mutex уже свободен. SDK restart получает отдельный resume argument с nonce, сохраняя demo/profile mode; аргумент не является Google credential. Только restarted installed EXE с matching identity/nonce/target locator+assembly version и после завершённого Velopack bootstrap может atomically consume marker и разрешить normal composition. API передачи restart arguments и порядок запуска child после apply проверяются по pinned SDK source перед реализацией; при отсутствии такого контракта применение блокируется до реализации эквивалентного проверяемого handoff.

Lease до вызова SDK не снимается обычным `finally`, позволяющим начать sync: после durable marker состояние terminal Applying; old process либо передаёт управление SDK, либо возвращается в recovery state без новых account/workspace операций. При проверенном отказе до запуска updater marker снимается и lease возвращается в idle. При ambiguous failure/crash marker остаётся; recovery screen работает без auth/sync и не очищает его только по timeout. Отсутствие active owned updater доказывается по exact install-root Update.exe path, Windows owner, PID+creation time/parent lineage; PID reuse или неизвестный статус не считается завершением. Возврат к старой версии возможен только после доказанного завершения updater и проверки целостности текущего пакета; иначе предложить конкретный repair/Setup action с отдельным подтверждением. Не завершать чужие процессы и не отключать marker через обычный runtime argument.

Race tests и actual synthetic installed scenario запускают второй экземпляр до marker, между old-process exit и child restart, во время apply и после успешного consume; ни один второй экземпляр не выполняет auth/sync. Дополнительно проверяются updater launch failure, old-process crash, неверный nonce/identity/version, PID reuse, restart-mode persistence и безопасный recovery. Это обязательная часть AC6; только process-mutex test её не закрывает.

### 6.3 User-Observable Scenarios

| Сценарий | Наблюдаемый результат | Evidence / AC |
| --- | --- | --- |
| Установить впервые | Setup → обычное окно, версия0.2.0, подключение Google без developer JSON | installed path/native PNG; AC1/4 |
| Подключить Google | Системный браузер → выбранный аккаунт в приложении → настоящий contact preview | live login/download/readback; AC1/2 |
| Исправить отказ | Cancel/denied/disabled API объяснён; повторный вход доступен | fixtures + native state; AC3 |
| Править файл | Preview upload, Google readback и next no-op, другие контакты сохранены | live targeted fixture; AC2 |
| Обновить | Найдена0.2.1 → progress → явный restart → действительно новая версия | installed native upgrade; AC5/6 |
| Обновление во время sync | Сообщение о занятой синхронизации, перезапуска нет | CPU/remote activity fake + native; AC6 |
| Сохранить данные | После upgrade тот же data root, OAuth binding, workspace/backup и следующий no-op | persistence hashes/restart; AC7 |
| Нет сети/пакет повреждён | Понятный update error, прежняя версия продолжает работать | contract/package corruption; AC8 |

### 6.4 Visual planning artifact / State Matrix

Embedded wireframe ниже — local-only planning, не runtime screenshot. Оставить существующий main screen, новую область поместить в настройки/«О программе», не смешивать sync Apply с обновлением.

```text
Первый запуск: ContactMirror 0.2.0     [Настройки]
  Контакты остаются в выбранной папке
  [Подключить Google] → системный браузер → account + [Выбрать папку]
  advanced: конфигурация разработчика (не обязательна для publisher build)

Настройки → Обновления
  Установленная версия 0.2.0       Канал: стабильный
  [Проверить обновления]
  → Доступна0.2.1 + краткие изменения [Скачать]
  → Скачано 64% [Остановить загрузку]
  → Готово [Установить и перезапустить] [Позже]
  → если sync занят: «Завершите синхронизацию перед перезапуском»
  → ошибка: действие исправить/повторить, текущая версия видна
```

| State/trigger | Переход | Disabled/error/concurrent case |
| --- | --- | --- |
| Missing publisher / login click | NotConfigured, advanced action | Нет вызова браузера и dummy client |
| Configured / login | AwaitingBrowser → Connected | Cancel/timeout → Idle, listener закрыт |
| Idle / check | Checking → Idle или Available | Одна проверка; network error не блокирует sync |
| Available / download | Downloading → ReadyToRestart | Cancel/Hash error → safe retry, прежняя app версия |
| Ready / restart | Applying → new process/version | Active sync/auth/another instance → disabled, без forced kill |
| Unmanaged runtime / updates | Unsupported + Setup action | Остальные функции работают |

### 6.5 Decision Ledger

| Решение | Owner | Выбор / основание | Confidence | Needs user before local EXEC |
| --- | --- | --- | ---: | --- |
| Подготовить Google Cloud проект и client | user | Ответ «Нужно подготовить проект и клиент» получен; resource creation только после SPEC gate | 1.0 | Нет, scope известен |
| Google account/support email/test account | user | Выбрать из фактической Console; не угадывать | 1.0 | Нет для local code; да перед dependent Cloud/consent/live шагом |
| PackId/storage roots | agent | ContactMirror.Desktop отдельно от сохранённого ContactMirror data root | 0.95 | Нет |
| Check/download/apply UX | agent | Check автоматически/вручную; download/restart явно, с activity guard | 0.95 | Нет |
| Remote release source/provider/URL | user | Вопрос задан; адаптеры Github+HTTPS и local validation независимы от ответа | 0.9 | Нет для local code; да перед configured remote release |
| Публичное создание repo/публикация | user | Текущий запрос не задаёт audience/repo/visibility; отдельный concrete delivery outcome | 1.0 | Нет для local code; да перед публикацией |
| Google consent/credential action | user/runtime | Нужный approval/handoff соблюдается перед конкретной кнопкой, согласно tool policy | 1.0 | Не блокирует spec/local code |
| Controlled installed test | agent | Compile-time synthetic и integration validation identities/data roots; actual Google consent отдельно, имена/пути/cleanup в отчёте | 0.9 | Нет для изолированной установки; выбранный test account нужен перед live шагом |

### 6.6 Runtime / Config / Data Contract Matrix

| Contract | Source of truth / изменение | Compatibility / verification |
| --- | --- | --- |
| OAuth client | Конкретный Desktop client, publisher staging config / managed override | ClientId mismatch не использует старые tokens; actual login + package config check |
| Tokens | Existing DPAPI user vault вне install root | Не экспортировать/не мигрировать по новым путям без need; hash/binding after restart |
| Desktop settings | Existing data root, additive fields | Старый JSON читается с defaults; atomic save не создаёт empty settings; reload tests |
| Workspace | Existing JSON/schema1/SQLite/backup, вне RootAppDir | Current format/read-only/source/recovery tests; installer root rejected |
| App version | Один release Version → assembly/info/pack/feed | Two distinct builds + UI/exe/version after restart |
| Update source | Publisher kind/url/channel + stable advanced settings | Validated HTTPS/github; no embedded PAT; unsupported/missing visible |
| Install runtime | Actual Velopack locator | Program.Run exactly once, hooks exit fast, standard Setup launch |

## 7. Бизнес-правила / Алгоритмы

- Existing sync planner, arrays-as-category, source separation и ambiguous mutation journal сохраняются.
- Нельзя применять обновление, если sync activity уже началась; нельзя начинать sync после получения update apply lease до restart. Guard должен быть атомарным, проверка UI IsBusy недостаточна.
- Update metadata «доступно» не доказывает download; download не доказывает apply; apply process return не доказывает новый installed runtime. Каждое состояние проверяется отдельно.
- Config/source changes invalidate prepared update; package identity/channel/version проверяются до download/apply. Update version строго выше running version, automatic downgrade=false.
- Contacts/snapshots/tokens не уходят на update host: запросы содержат только update metadata/packages. Ошибки/logs редактируются без PII/token payloads.

## 8. Точки интеграции и триггеры

- Program.Main: Velopack before Avalonia. App.CreateViewModel/composition: shared configuration/activity/updater.
- Initialize/open settings/manual check: общий update service. UI snapshot dispatch безопасен для UI thread; network/IO выполняются вне UI.
- Sync/Auth/restore/cleanup и update restart: shared activity coordinator, leases снимаются в finally; обычный close при busy sync использует безопасную остановку и ожидает journal checkpoint.
- release script: version/config staging/pack validation. Cloud настройка даёт publisher JSON input, не делает ручную настройку обязанностью всех пользователей.

## 9. Модель данных / состояния

- Контактные файлы и schemaVersion неизменны. Desktop preferences расширяются additive update settings/managed OAuth configuration path.
- Updater snapshot transient; pending packages определяются locator/Velopack, не cached UI bool. Log bounded/redacted; previous check time informational.
- ApplicationPaths и immutable compile-time fixture data roots отделены от install RootAppDir. Production default не изменяется; updater restart воспроизводит тот же profile без runtime обхода.

## 10. Миграция / Rollout / Rollback

- Existing portable user запускает Setup и продолжает с тем же data root/папкой; перенос токенов между Windows users не обещается. Если ClientId меняется после publisher client creation, потребуется явный повторный вход.
- Developer override импортируется атомарно, old config references сохраняются до verified import; отказ импорта не стирает прежнюю working configuration.
- New packages не включают settings/state/workspace/vault. Install/uninstall validation проверяет сохранность production data и fixture cleanup.
- Нельзя объявлять автоматический rollback после успешного update; контрольный возврат возможен через предыдущий Setup/дополнительный rescue release с новой версией, в том же compatible data contract. Не downgrading SQLite/schema.
- Cloud resource IDs и configuration в отчёте; project/client не удаляются автоматически как cleanup. Public release/consent verification идут после конкретного решения пользователя и требуемых Google шагов.

## 11. Тестирование и критерии приёмки

| AC | Проверяемое требование | Automated / actual evidence |
| --- | --- | --- |
| AC1 | Publisher-configured ordinary app входит в реальный Google без developer JSON | Config precedence/package completeness, OAuth fixture + actual browser/login/account/native evidence |
| AC2 | Actual Google↔JSON cycle, supported fields/photo/group/star, next no-op | Existing registry/contract coverage + live targeted fixture/readback report, Google normalization accounted |
| AC3 | Cancel/denied/invalid client/disabledAPI/invalidgrant/401 не оставляют UI занятным и дают действие | New structured error/callback tests + Headless/native state |
| AC4 | Setup и Velopack portable собираются; hook invocation быстро завершается без UI | Program-hook process check, packaging manifest/files, actual controlled Setup launch |
| AC5 | Actual installed0.2.0 обнаруживает/скачивает0.2.1 и restart подтверждает новую version/path/PID | Local real packages/feed + actual installed UI upgrade; TestLocator supplement only |
| AC6 | Update не перезапускает active sync/auth и concurrent operations имеют одного owner | Race/activity/coalescing tests, native ReadyToRestart/busy scenario |
| AC7 | После actual integration upgrade настоящий OAuth vault/account binding, settings/workspace/backup сохранены; mirror inside install root запрещён | Actual Google-enabled IntegrationValidation Setup0.2.0→0.2.1, vault hash/binding/read-only readback/no-op; synthetic DPAPI supplement и root/reparse policy tests |
| AC8 | Corrupt/missing feed/package/hash/channel/RID и network/cancel не повреждают current app | Backend fixtures + real package corrupt/local outage + inspect actual error UI |
| AC9 | Raw developer launch остаётся usable; demo отделён; version visible и UI accessible | Existing+new Headless/FlaUI, themes/small window/native PNG; Unsupported updater state |
| AC10 | Один воспроизводимый release invocation, payload без tokens/PII/test artifacts; remote readiness отдельно | pack/output/feed/hash inspection; configured remote check после выбора и публикации |

Обязателен полный текущий набор Core/Application, Google/Store, Headless и native FlaUI после targeted regressions: меняются startup/security/config/storage integration. Тесты новых updater contracts идут в отдельный xUnit project либо существующий suite с явным ownership. 10k/performance rerun только если activity/async changes затрагивают планирование; не сокращать прежние guards и не повторять успешные проверки без новой причины.

```powershell
dotnet build ContactMirror.sln -c Release -m:1
dotnet test tests/ContactMirror.Tests -c Release --no-build
dotnet test tests/ContactMirror.GoogleTests -c Release --no-build
dotnet run --project tests/ContactMirror.UiTests.Headless -c Release --no-build -- --maximum-parallel-tests 1
dotnet run --project tests/ContactMirror.UiTests.FlaUI -c Release --no-build -- --maximum-parallel-tests 1
pwsh -File scripts/Build-Release.ps1 -Version 0.2.0 -OAuthClientPath <prepared-client-json> -UpdateSource <selected-source>
# Повторить pack0.2.1 с previous full → controlled install/upgrade smoke
```

Пути scripts и new tests здесь planned. Native/live результаты сохраняются в ignored chat-artifacts/TestResults. UI video: записать after для новых settings/update сценариев; baseline existing onboarding при пропорциональном безопасном recorder. Live OAuth consent/реальные контакты не записывать в общий публичный video. Если безопасная запись не поддерживается harness или чувствительные данные не скрываются, указать объективный fallback и inspected synthetic native PNG/logs с конкретной командой. До EXEC это план, не PASS.

Stop rules: targeted red должен воспроизвести claimed bug, а не absent credentials/network failure; исправления → targeted → full mandatory → actual packaged/login/install verification → post-EXEC review. Failure по tool access выделяется отдельно. Без actual login/upgrade AC1/5 остаются incomplete, even if all unit suites PASS.

## 12. Риски и edge cases

| Риск / likely objection | Механизм / mitigation | Disposition |
| --- | --- | --- |
| «Снова демо, а Google не работает» | Current config absent; publisher input + actual login/readback обязательны | mitigated by AC1/2; credentials/access dependency explicit |
| «Каждому знакомому надо создавать Cloud проект» | Publisher config включён в Setup; advanced developer override optional | mitigated |
| «Обновление стерло настройки/контакты» | PackId совпадает с нынешним LocalAppData data root или выбран current folder | separate PackId/root + forbidden mirror root + persistence hashes |
| «В настройках одно, автоматически скачивается другое» | Два UpdateManager или source changed mid-download | one owner + immutable operation/source generation |
| «Установка есть, обновление только притворяется» | TestLocator/download-only masquerades actual upgrade | standard Setup + actual new exe/version/PID |
| OAuth testing/verification и policy restrictions | Cloud audience/admin/review может блокировать login остальных | Honest readiness state; public release separate, no bypass |
| Credential consent/Console требует human action | Новые permission/credential grants и agreements имеют tool at-action rules | Prepare concrete screen, handoff/confirmation exactly at required action |
| Ошибка native tooling | Chrome listing failure/Windows input/installer blockers | Прямая ошибка + permitted alternate read path; no fabricated installed/live evidence |

Rework prevention: исходные Google/install/update сценарии сохранены; каждый связан с AC; publisher/test source decisions явны; relevant UX/domain/testing/architecture/security reviews обязательны; отсутствие Cloud account/URL не скрывается за локальным PASS.

## 13. План выполнения

1. Review этой SPEC и exact approval. Выбор external identities/feed уточняется независимо от local code.
2. Startup/config/activity/updater contracts и targeted tests; publisher release pipeline и stable storage paths.
3. Настройка Google Console/client с выбранной identity и соблюдением требуемого human consent; publisher-ready build.
4. UI/settings/errors/update progress; full local suites, inspected native synthetic visuals/video или objective fallback.
5. Actual Google targeted fixture cycle и оба controlled installed profiles0.2.0→0.2.1, version/data/restart proofs; integration credential preservation не заменяется synthetic proof.
6. Post-EXEC review, локальные artifacts. Public remote upload only after concrete destination/scope decision, затем отдельно verify feed/download на installed runtime.

## 14. Открытые вопросы

- Нужен выбранный Google account/support email и разрешённый test account/resource scope перед Console/consent/live работой. Предыдущий ответ определил необходимость нового проекта/client; личный профиль не заменяет выбор identity.
- Источник updates задан вопросом; pending. Для local implementation/controlled upgrade нужны только generic backend и local feed, URL не блокирует local EXEC. Перед remote configured release нужен реальный owner/repo или HTTPS endpoint и конкретное разрешение публикации.
- Ни один ответ «нужно подготовить» не заменяет exact SPEC→EXEC phrase или требуемый runtime at-action consent.

## 15. Соответствие профилю

- Desktop: startup bootstrap/fast hooks, no UI blocking, stable AutomationIds, full build/test и actual installed state.
- Product system design: outcome и owner boundaries, shared updater, config/data matrix, migration/rollback, actual evidence levels.
- UI automation: existing Headless/FlaUI расширяются; embedded wireframe/state matrix, inspected screenshots + safe video/fallback.
- Tool/Quest: only current SPEC mutated до approval; no Google/installer/release side effects сейчас.

## 16. Таблица изменений файлов

| Planned paths | Изменения | Причина |
| --- | --- | --- |
| src/ContactMirror.Desktop/Program.cs, App.axaml.cs, csproj | Bootstrap/Velopack package/shared composition/version | Installed lifecycle |
| src/ContactMirror.Application/Updates/*, activity contracts | Updater API/state и shared operation guard | Один owner, безопасный restart |
| src/ContactMirror.Infrastructure/Updates/*, Configuration/* | Adapter, source/config/path policy | Source/runtime isolation |
| Google/OAuthClientOptions.cs, GoogleAccountService.cs, GoogleContactsGateway.cs | Managed/publisher readiness и bounded auth/API errors по verified failures | Рабочий обычный вход |
| Desktop settings/VM/axaml | Updates panel, version/readiness/error actions | UX target wireframe |
| .config/dotnet-tools.json, scripts/Build-Release.ps1, release config schema | pinned vpk, staging/pack/version/feed validation | Reproducible installer/update |
| tests/*, controlled install/live harness | Config/race/hooks/packages + actual scenario proofs | AC1–10 |
| docs/GOOGLE_SETUP.md, docs/RELEASE.md, docs/VALIDATION.md, README | Настоящий publisher/setup workflow/results | Instructions agree with implementation |
| appsettings/publisher config in staging | Реальный Desktop client и выбранный updates source | Не фальшивый placeholder |

## 17. Было → стало

| Область | Было | Стало |
| --- | --- | --- |
| Google | Unconfigured demo-capable binary | Publisher-configured ordinary entry + actual live test |
| First user setup | Own Cloud project/JSON | Кнопка + браузер, publisher делает Cloud setup |
| Install | Unmanaged ZIP | Setup/managed portable and installed runtime evidence |
| Updates | Нет | Check/download/explicit safe restart + full/delta/feed |
| Data preservation | Data root есть, installer collision не учтён | Separate stable app/data roots + upgrade/uninstall safety checks |

## 18. Альтернативы и компромиссы

- Оставить BYO OAuth единственным способом: быстрее, но не выполняет easy-connect outcome; оставить только advanced fallback.
- Web OAuth proxy/backend: добавляет сервер/секреты/передачу контактов; Desktop OAuth достаточен и сохраняет local-only data.
- MSI/ручной ZIP update: не отвечает прямому запросу Velopack; SDK+CLI stable выбран согласно user instruction.
- Silent automatic restart: меньше кликов, но риск прервать sync; выбраны background check и explicit download/apply.
- PackId ContactMirror: привычно, но совпадает с data root и uninstall может удалить credentials/settings; выбран ContactMirror.Desktop.

## 19. Quality gate и review

### SPEC Linter Result

| № | Критерий | Статус / evidence |
| --- | --- | --- |
| 1 | Outcome | PASS §1/6.3 |
| 2 | AS-IS | PASS §2, runtime config booleans и callback check |
| 3 | Root problem | PASS §3 |
| 4 | Design goals | PASS §4 |
| 5 | Boundaries | PASS §5, phases §1/14 |
| 6 | Responsibilities | PASS §6.1 |
| 7 | Integration points | PASS §8 |
| 8 | Algorithms | PASS §6.2/7 |
| 9 | Errors/recovery | PASS §6.2/6.4/12 |
| 10 | Performance | PASS async/throttle, existing10k scope retained, tests §11 |
| 11 | State/data | PASS §6.6/9 |
| 12 | Compatibility | PASS §10 |
| 13 | Rollback | PASS §10, no fake automatic rollback |
| 14 | AC | PASS §11 |
| 15 | AC evidence | PASS actual vs fixture boundaries §6.3/11 |
| 16 | Commands/stop | PASS §11 |
| 17 | Execution dependencies | PASS §13 |
| 18 | Decisions | PASS §6.5/14, local EXEC vs dependent external stages explicit |
| 19 | Form/scale | PASS §0 expanded/high-risk |
| 20 | Profile | PASS §15 |

### SPEC Rubric Result

| Критерий | Балл | Evidence |
| --- | ---: | --- |
| Outcome/boundaries | 5 | Ordinary Google login + actual installed upgrade, §1 |
| AS-IS | 5 | Config missing/callback PASS/source read, §2 |
| Concrete design | 5 | Publisher/config/shared updater/storage/version/pack, §6 |
| Safety/migration | 5 | Separate roots/activity/DPAPI/fixture/rollback, §7/10 |
| Verifiability | 5 | Actual AC1/5, full suites и typed error negatives, §11 |
| Autonomy | 2 | External Google identity/consent/update URL dependency explicit |

Итого27/30; числовой балл не закрывает pending review/exact approval/live gates.

### Role-Based Review Result

| Role | Applicability | Review question | Verdict |
| --- | --- | --- | --- |
| Business analyst/domain | applicable | Google работает для ordinary installed user, не только demo? | PASS design: publisher input + actual integration scenario, не runtime claim |
| UX/designer | applicable | Publisher login/settings/update/busy/error flow понятен? | PASS design: wireframe §6.4, source label и actionable error states |
| Tester | applicable | Actual login/installed upgrade доказываются отдельно от fixtures? | PASS plan: AC1/5/7 actual, race AC6 и precedence negatives обязательны |
| Developer/architect | applicable | Одна updater ownership/activity/config/version identity? | PASS design: один owner, durable handoff, immutable profile/source, единый Version |
| Delivery/security | applicable | Install/data roots, credential grants, release URL и публикация ясны? | PASS design: roots вне install, explicit config precedence, grants/publication отдельны |

### Post-SPEC Review

- Статус: **PASS на уровне SPEC** после fix-and-re-review, 2026-10-07. Можно запрашивать exact approval нового local EXEC; это не выполненный login/install/update.
- Scope reviewed: эта expanded SPEC, предыдущая v0.1 SPEC и recorded docs/VALIDATION boundary; central stack из §0, desktop/product/UI/testing profiles, planned files §16, вопросы Google identity/feed §14.
- Review surface: отдельный `/root/spec_review` + root. Child sandbox фактически `danger-full-access`, технического read-only enforcement нет; это **adversarial fallback**, не technically read-only independent review. Reviewer выполнил только чтение/spec/source/official web verification, без мутаций; residual — запрет технически не enforced.
- Scope/Evidence pass: source/config inventory, Program/Main/desktop composition/preferences, OAuth options/account/gateway/vault, tool manifest/csproj/data paths; presence booleans и callback-start result; source docs §19. Старые 165 tests не перезапускались и не стали evidence нового EXEC. Git status fatal подтверждает отсутствие .git, поэтому diff сейчас недоступен; единственная authoring mutation — текущая SPEC.
- Contract pass: исходные Google + Setup/update outcomes сопоставлены с §6.3/11; v0.1 portable scope и его отдельные external boundaries учтены. Формат1/read-only categories/recovery сохраняются, public deployment исключён из local claims. Все обязательные документы decision/scenarios/matrix/objections/roles заполнены.
- Adversarial risk pass: install root/data collision, developer source overriding publisher, second launch в gap old-exit→apply, synthetic credentials вместо real upgrade, source/version mismatch, consent/provider/tooling dependencies и unsafe restart рассмотрены. Обнаруженные in-scope design gaps исправлены ниже.
- Role-Based pass: пять применимых ролей в таблице выше; reviewer итоговые verdicts PASS по design после targeted re-review. Программное поведение не объявлено проверенным.
- Fix and re-review: AC7 усилен actual Google-enabled IntegrationValidation upgrade; OAuth precedence matrix и handoff marker/recovery/race acceptance добавлены. Reviewer перечитал исправленные поверхности и соответствующие AC6/7; HIGH/MEDIUM closed, новых находок нет. Root проверил их согласованность с state/guard/config/paths и official1.2.0 restartArgs API.
- Stop decision: **PASS SPEC**; новый код, Cloud resources, пакеты и установка ещё не выполнялись. Exact approval нужен сейчас; account/consent и выбранный remote endpoint нужны только перед зависимыми внешними шагами.

Evidence inspected: `specs/2026-10-06-contactmirror-v1.md`, `docs/VALIDATION.md`, `src/ContactMirror.Desktop/{Program,App.axaml,DesktopPreferences,MainWindow.axaml,MainWindow.axaml.cs,MainWindowViewModel}.cs/axaml`, Desktop csproj, Directory.Build.props, `.config/dotnet-tools.json`, `.gitignore`, Infrastructure Google options/account/gateway/vault, existing Demo data path, official docs §19. Root static check: sections0–20 exactly once;4 balanced fences;10 AC rows;5 role rows. Callback preflight только listener start/close; credentials/content не выводились.

Depth checklist:

- Scope drift/unrelated changes: Google/publisher/install/update в текущем поручении; исходники и прежние artifacts не менялись, только текущая SPEC. Public release не добавлен молча.
- Acceptance criteria: AC1–10 имеют test/evidence mapping, AC1/5/7 требуют actual; simulated proof отдельно.
- Scenarios/decisions/objections: §6.3/6.5/12 заполнены, адресуют ordinary entry/easy-connect/data safety/actual upgrade.
- Validation evidence: для текущей фазы проверены AS-IS/design/источники и structure; EXEC suites/live/install остаются планом, no-evidence/no-pass применяется отдельно к ним.
- Unsupported claims: pin/source/lifecycle проверены по primary docs; Google live success и clean-machine/public readiness не утверждаются.
- Regression/edge cases: precedence, legacy path migration, root/reparse, source invalidation, apply handoff/second launch/PID reuse, cancel и ambiguous recovery включены.
- Comments/docs/changelog: planned delivery docs §16 должны отражать фактические readiness и source; текущие README/scripts не менялись.
- Hidden contract change: startup/update-host requests/shared activity/path rejection/preferences migration названы явно; schema1 неизменна.
- Manual-review challenge: реальные tokens после upgrade или второй shortcut во время замены могли бы опровергнуть поверхностный PASS; добавлены integrated fixture и durable handoff с actual race evidence.

| Severity | Area | Finding | Required action | Status |
| --- | --- | --- | --- | --- |
| HIGH | restart/concurrency | Process mutex освобождается до завершения updater; second launch может начать sync | Durable Applying handoff + target/nonce validation, recovery и actual race tests | fixed; targeted reviewer re-review PASS |
| MEDIUM | OAuth configuration | Старый env/CWD/local client может перекрыть publisher; precedence не задан | Production/raw table, managed legacy import, source label и invalid/mismatch tests | fixed; targeted reviewer re-review PASS |
| MEDIUM | acceptance/evidence | Synthetic/noOAuth upgrade не доказывает сохранность real Google vault/account | Compile-time IntegrationValidation с real login→actual upgrade→vault/readback/no-op | fixed; reviewer re-review PASS |

No-findings justification: после исправлений новых находок в изменённых контрактах не выявлено; проверены конкретные counterexamples выше и соответствующие AC/state/paths. Это отсутствие дополнительных SPEC defects, не утверждение runtime correctness.

- Fixed before continuing: три design gaps в таблице.
- Checks rerun: только relevant SPEC passes/AC6–7 и configuration tests plan; успешные source/preflight проверки не повторялись без причины.
- Needs human: exact SPEC phrase; позднее фактическая Console identity/test account, required at-action consent и remote destination/publication scope.
- Residual risks: Cloud/browser access и Google Testing/verification могут блокировать actual evidence; updater handoff требует pinned SDK sequencing proof и настоящего race smoke; remote URL пока не выбран. Ни одна зависимость не скрывается за unit PASS.

### Post-EXEC Review

Статус: **ASK-HUMAN** на границе публичной публикации. Локальная реализация и runtime-проверки0.2.4 завершены; full PASS не выставлен до разрешённой публикации и проверки удалённого feed. Финальный adversarial re-review локальных evidence: PASS. Исправление имени portable launcher в документации упаковано в0.2.5 без изменения runtime-поведения.

2026-10-07, подтверждённое evidence:

- Google project `contactmirror-desktop-20261007`, People API, Desktop client и Testing audience подготовлены с отдельными разрешениями. Настоящий OAuth вход сохранён в DPAPI.
- Обе отдельно разрешённые установленые копии Synthetic и Integration прошли 0.2.0 → 0.2.2; новый PID/версия, сохранность prefs/vault и отсутствие Applying marker подтверждены. Integration остаётся подключённой без повторного consent.
- Экспорт 1003 контактов +5 ярлыков завершён; JSON проверены. Следующая ручная проверка не предложила отправок/скачиваний/конфликтов/удалений, но одна запись заблокирована: google.person отредактирован после экспорта (LastWrite15:32:48, baseline отличается по четырём names fields). Это не complete no-op; правка не перезаписывалась. Complete no-op отдельно подтверждён live fixture. `chat-artifacts/installed-google/{status,manual-check}.png`.
- По отдельному разрешению создан ровно один тестовый контакт и один ярлык; Google↔JSON поля/фото/star/phone/biography и next-noop PASS. Оба ресурса удалены, cleanup подтверждён. `chat-artifacts/google/live-fixture/fixture-journal.json`.
- По прямому уточнению пользователя Google→папка Apply сохраняет показанный снимок без повторного чтения Google. Remote write/deletion/recovery и локальный drift сохраняют проверки. Повторная SPEC approval для порученного исправления не требуется.
- Пользователь дополнительно поручил ускорить файловое сохранение: вместо полного ScanAsync на каждый ReadLocal — session identity index, точечное чтение и обновление собственных изменений. External edits, duplicate IDs, переименования, фото, root/reparse и durable CAS сохраняют прежнюю защиту. Native Windows notifications дополняются RWH read-cache leases и одним постоянным issuing thread; при отсутствии поддержки выполняется полный безопасный reread. Приёмка — реальный disk export100/1000, ноль Google Apply запросов, next-noop и строгие mapped/open-writer/index regressions — выполнена.
- После ускорения: полная сборка0warnings, Core53/53, Google141/141, Headless13/13, Native5/5 PASS. Index+Safety44/44 включают mapped before/after и calling Thread exit. Настоящий synthetic disk export100/1000:16,852/76,651с, Apply0Google requests/mutations, nextPrepare0entries. TRX TestResults/GoogleVelopack; chat-artifacts/disk-performance/results.json.
- Delta ZIP не совпал со строгим full SHA256; bounded single original-full fallback исправлен и проверен actual установленными обновлениями. Проверка не ослаблена.
- Source reviews прежних5 MEDIUM, stale-token429 и snapshot export закрыты. Новые index/native edge cases (mapped writers, issuing-thread exit, IOCP, exclusive editor) исправлены и прошли targeted re-review и строгие регрессии. Reviewer writable sandbox — adversarial fallback, technical readonly independence не заявляется.
- Пользователь выбрал GitHub Releases и `Kibnet/ContactMirror`. Финальный Production0.2.4 payload/source/profile/size/SHA256 проверен, publication script default-plan PASS. Создание публичного repo/source push/release ещё не разрешены и не выполнены.
- До local commit Git history отсутствовала; baseline `chat-artifacts/before-google-velopack`, файловый diff и source inspection сохраняют прежнее evidence. Signing и OAuth production verification не выполнены.

Дополнительные installed evidence0.2.4:

- Synthetic0.2.3 → 0.2.4: второй экземпляр запущен после выхода старого процесса при существующем Applying marker. Защитное окно не допускает auth/sync; новый SDK PID/версия и marker consume подтверждены. Preferences/vault и21 workspace файл совпали SHA256 до/после. `chat-artifacts/installed-synthetic/update-0.2.4/`.
- Integration0.2.2 → 0.2.4: настоящий Google Prepare выполнялся при ReadyToRestart; restart отключён до завершения. Новый PID/версия, прежний аккаунт и vault,2096 workspace файлов совпали до/после. Ручной Google readback новой0.2.4 без нового consent:1003 контакта+5 ярлыков, прежняя blocked google.person запись сохранена. `chat-artifacts/installed-google/{update-0.2.4,read-after-0.2.4}/`.
- Повреждённый реальный package0.2.3 отвергнут установленной0.2.2, UI ошибки просмотрен, текущая DLL неизменна; feed восстановлен в finally. `chat-artifacts/installed-synthetic/corrupt-0.2.3/`.
- Промежуточный0.2.3 с разным написанием одного локального feed вызвал fail-closed source identity guard. Recovery через существующий UI подтвердил target hash и остановку процессов; source в старом validation fixture приведён к канонической записи того же каталога. Build-Release использует GetFullPath для file source. Runtime source/nonce/hash правила сохранены. Финальный0.2.4 race PASS; прежний0.2.3 timeout не объявляется успешным.
- Native PNG этих сценариев просмотрены. Synthetic видео до0.2.2/после0.2.4: `chat-artifacts/ui-video/{before-client,after-client}.mp4`,12с, целевая частота15fps (средняя14,83/14,10), одинаковые2656×1846, только окно, без аудио/реальных контактов. До: ручная проверка до no-op; после: состояние после проверки. Разное время нажатия и начальное состояние исключают performance claim. Baseline пространственно обрезан к client area, raw сохранён.

Acceptance-to-Test Matrix, фактическое закрытие:

| AC | Disposition | Evidence / граница |
| --- | --- | --- |
| AC1 | Локально проверен | Actual installed Google login, publisher profile, readback новой0.2.4; Google Testing audience остаётся ограничением |
| AC2 | Проверен | Один разрешённый Google↔JSON fixture: fields/photo/star/label/phone/biography/no-op; cleanup verified |
| AC3 | Проверен | Google141 + Headless13, error/cancel/retry и callback fixture; actual OAuth consent |
| AC4 | Проверен | Production0.2.4 Setup/portable payload/profile/hook gates; отдельно разрешённые actual validation Setup installations |
| AC5 | Проверен | Actual installed0.2.0→0.2.2 обеих копий, Integration0.2.2→0.2.4, Synthetic0.2.3→0.2.4 с новым PID/version |
| AC6 | Проверен | Shared owner/race tests + actual gap race Synthetic и actual busy Google guard |
| AC7 | Проверен с явной границей | Preferences/vault/21 и2096 workspace SHA256 equality; Google readback после upgrade. Полная пользовательская книга имеет одну blocked metadata правку; complete no-op доказан live fixture |
| AC8 | Проверен | Backend error/network/channel/RID/hash tests и actual corrupted package rejection/current DLL unchanged |
| AC9 | Проверен | Headless13/FlaUI5, обычные окна/Synthetic PNG/video, raw/demo separation; README launcher исправлен |
| AC10 | Локально проверен; remote pending | Production0.2.4 package/feed/hash/private payload gates и default publication plan; remote feed проверяется после отдельного разрешения публикации |

Full review-loop audit (root, перед финальным reviewer re-review):

- Scope reviewed / Evidence inspected: approved SPEC, центральный stack, review-loops.md, owner profiles; файловый diff `chat-artifacts/source-diff.json` относительно частичного baseline `before-google-velopack`; Google gateway/account/vault, coordinator, FileWorkspaceStore/native journals/leases/thread, updater/handoff/activity, Desktop composition/UI, scripts/build/publish и ReleaseSmoke/GoogleSmoke/Performance; README/GOOGLE_SETUP/RELEASE/VALIDATION, TRX53/141/13/5, index44, live fixture journal, disk results, installed SHA maps/PNG/video, Production build0.2.4/default plan. Git отсутствует; файлы вне baseline не считаются автоматически новыми изменениями.
- Scope/Evidence pass: runtime source проверен отдельно от actual UI/install/live proof; private токены/контакты и screenshots остаются chat-artifacts, payload gate не допускает их в релиз.
- Contract pass: §6.3 scenarios entry/login/error/file-edit/update/busy/preservation/corruption соответствуют matrix выше; Decision Ledger источника/roots/explicit restart исполнен. Expected objections §12 закрыты actual login/cycle/upgrade и safety tests; публичная доступность ограничена Testing и отсутствием релиза.
- Adversarial risk pass: late duplicate UUID/mapped writer, поток завершился без writer, file handle IOCP lifetime, cancellation/free native memory, rename/root/photo/CAS, stale token429, delta reconstruction hash, source mismatch, второй shortcut в exit/apply gap, потеря recovery marker, populated-book preservation и ошибочные no-op claims проверены. Строгие проверки не заменены закрытием writer/ослаблением hash.
- Role-Based pass: domain — actual Google cycle и cached export; UX — inspected error/busy/guard/recovery/ordinary states; tester — раздельные full/actual/fixture evidence и negative native regressions; architect — shared owner, durable handoff, conservative index fallback; delivery/security — isolated roots, DPAPI/payload/profile/source gate, Testing/unsigned/publication граница. Все локальные условия проверены; remote delivery ожидает человека.
- Fix and re-review: прежние source findings исправлены, targeted index44 и full212 PASS; actual gap/busy/preservation/corrupt evidence закрыли прошлые missing-validation findings. Build canonical file source и README launcher исправлены, package0.2.4 собран после этих изменений. Последующий reviewer проверяет именно оставшиеся evidence/docs, исходные успешные tests без причины не повторяются.
- Depth checklist: scope drift — только порученные Google/Velopack и ускорение, pre-existing uncaptured файлы помечаются отдельно; AC/scenarios/objections — matrix выше; validation — actual/fixture/install уровни раздельны; unsupported claims — нет complete user-book no-op/public Google/production install/46min→76sec equivalent benchmark; regression — перечисленные native safety/races; docs/comments/changelog — русский factual release4, корректный launcher; hidden contract — cached pure-download по прямому поручению, guard для remote writes сохранён; manual challenge — populated-book SHA и actual retained OAuth доказаны, source mismatch не скрыт.
- No-findings justification: финальный reviewer targeted re-review подтвердил локальные runtime-гейты0.2.4 и отсутствие новых HIGH/MEDIUM. PNG/manifest7 assets/hash maps21/2096/video metadata проверены. Source/full suites/live fixture ранее проверены и не повторялись без причины. Невыполненная remote acceptance не закрывается этим утверждением.
- Stop decision: **ASK-HUMAN** только для создания публичного repository/публикации, затем remote check. Signing/OAuth Production verification и Production installation не заявлены выполненными. Полный PASS до обязательной remote проверки запрещён.

| Severity | Area | Finding | Required action | Status |
| --- | --- | --- | --- | --- |
| HIGH | AC6 installed evidence | Gap race и busy guard не были завершены в прошлом review | Actual Synthetic gap и Integration Google busy run | fixed;0.2.4 evidence re-review PASS |
| HIGH | AC7 preservation | Раннее обновление пустой папки не доказывало сохранность книги | Filled workspace SHA maps и retained Google readback | fixed;21/2096 файлов и vault/settings совпали, re-review PASS |
| MEDIUM | AC8 / visual evidence | Corrupt package и before/after video отсутствовали | Actual corrupted full rejection и inspected safe visual material | fixed; re-review PASS с честной границей разных состояний видео |
| LOW | README / portable | ContactMirror.Desktop.exe отсутствует в корне portable | Указать реальный ContactMirror.exe и упаковать docs fix новой версией | fixed; immutable4 сохранён, package5 root launcher/embedded README/profile/payload/hash/default plan проверены root |
| authorization | AC10 remote delivery | URL выбран, создание публичного repo/release не разрешено | Получить отдельное разрешение, затем проверить remote feed | ASK-HUMAN; user «закоммить» разрешает только local commit |

Пользователь отдельно поручил «Закоммить». Обнаружен уже инициализированный пустой Git master без remote/commits. Первый local commit включает весь source tree, docs/spec/tests/scripts; actual OAuth JSON, DPAPI vault, пользовательские контакты, builds/chat-artifacts/TRX исключены .gitignore и staged content scan. История исходного проекта до этого коммита отсутствует; filesystem baseline остаётся частичным evidence прежнего состояния. Удалены лишние пустые строки EOF для git diff --check, это не semantic code change. Production0.2.5 profile/payload/feed/hash/default plan9 assets и portable root launcher/точное совпадение embedded README проверены root; actual installed validation по runtime0.2.4 остаётся отдельным уровнем evidence. Публикация не выполнялась.

### Проверенные первичные источники

Проверены2026-10-07. Quotes не копируются в продукт; APIs/CLI flags при EXEC сверяются с pin1.2.0.

- Google [Desktop OAuth](https://developers.google.com/identity/protocols/oauth2/native-app), [People API](https://developers.google.com/people), [sensitive verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification).
- Velopack [NuGet1.2.0](https://www.nuget.org/packages/Velopack/1.2.0), [C# entry point/pack](https://docs.velopack.io/getting-started/csharp), [lifecycle/update](https://docs.velopack.io/integrating/overview), [sources](https://docs.velopack.io/integrating/update-sources), [preserved files](https://docs.velopack.io/integrating/preserved-files), [testing](https://docs.velopack.io/integrating/testing), [distribution/feed](https://docs.velopack.io/distributing/overview), [self hosting/draft](https://docs.velopack.io/distributing/self-hosting), [Windows CLI](https://docs.velopack.io/reference/cli/content/vpk-windows).
- [UpdateManager API generated from1.2.0](https://docs.velopack.io/reference/cs/Velopack/UpdateManager): `ApplyUpdatesAndRestart(VelopackAsset?, string[]? restartArgs)` поддерживает handoff arguments; до вызова требуется сохранить состояние. Actual sequencing/race proof остаётся обязательным EXEC test.

## Approval

Получена exact phrase **«Спеку подтверждаю»** 2026-10-07 для описанного нового EXEC. Google identity/consent и конкретная remote публикация остаются зависимостями соответствующих шагов; повторного подтверждения этого local scope не требуется.

## 20. Журнал действий агента

| Фаза/событие | Решение/evidence | Остаток / следующий шаг | Решение человека | Артефакты |
| --- | --- | --- | --- | --- |
| SPEC / диагноз | Source/config inventory: OAuth missing, callback startup PASS безadmin; Velopack отсутствует; Git отсутствует | Подготовить review-ready design и external identity/source plan | Запрос рабочего Google и Velopack | Только текущая SPEC |
| SPEC / external dependency | Не подменять publisher setup BYO для каждого пользователя | Console выбраннаяidentity; actual login/upgrade обязательны | Нужно подготовить Google project/client | Текущая SPEC; optional updates source question pending |
| SPEC / draft | Canonical0–20, shared updater/activity, storage collision устранён дизайном, AC1–10 | Separate adversarial review и fixes before approval | Нового exact approval нет | Эта SPEC |
| SPEC / review и fixes | Adversarial fallback + root full passes; actual integration credential proof, precedence и durable update handoff уточнены; targeted re-review PASS | Запросить exact approval; затем local code и выбранные external steps | Google project/client требуется; updates source pending | Review audit §19, исходники не менялись |

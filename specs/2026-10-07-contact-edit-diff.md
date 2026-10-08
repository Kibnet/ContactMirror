# Разница по полям и обновление всего контакта, включая фото

## 0. Метаданные

- Профиль: delivery-task, dotnet-desktop-client + ui-automation-testing; context testing-dotnet.
- Владелец реализации: root; reviewer только читает. Масштаб medium, expanded: UI + доверенная привязка Google + локальная запись/recovery.
- Центральный каталог: `C:\Users\Kibnet\.codex\agents\AGENTS.md`; canonical template `templates/specs/_template.md`. Stack: routing-matrix, creator-vibe-lens, model-behavior-baseline, tool-execution-baseline, collaboration-baseline, quest-governance/quest-mode, testing-baseline, testing-dotnet, desktop/UI profiles, spec-linter/spec-rubric/review-loops. Локальных AGENTS.override.md нет.
- Поверхность Codex desktop Windows/PowerShell, danger-full-access, approvals never; модель/effort не переключаются. Optimization baseline каталога GPT-6 Astra, фактический model ID host в этой SPEC не проверялся. Model eval не применим: меняется обычное .NET приложение.
- Исходный commit `cfc57bf`, master без remote, чистый worktree перед SPEC. Runtime installed validation0.2.4, последний подготовленный Production0.2.5.
- Фаза EXEC разрешена: пользователь подтвердил эту SPEC фразой «Спеку подтверждаю»2026-10-08. Исторические записи SPEC ниже сохраняют состояние на момент ревью.
- Publisher OAuth, updater и публичная публикация из предыдущей задачи не меняются. Работа с реальными контактами ниже только read-only; существующий контакт не отправляется автоматически.

## 1. Overview / Цель

Исходное поручение: после исправления имени в файле программа замечает изменение, но не показывает поля/значения и не позволяет отправить его в Google.

Уточнение пользователя2026-10-08: требуется обновление **всего контакта, включая фото**, если данные изменены. Имя — исходный repro, а не граница решения.

Success means: пользователь видит точные редактируемые правки «было / в файле / в Google» для всех supported writable категорий контакта, ярлыков, избранного и фотографии. Может исправить служебный раздел, сохранив **все** свои правки и фото. Следующая ручная проверка предлагает «В Google» для изменившихся частей; одно применение выбранного плана отправляет все выбранные изменения контакта, в том числе новое изображение. Readback подтверждает результат, следующий Prepare не предлагает повторной отправки. Неизменённые поля/фото сохраняются: whole-contact update не означает безусловную замену всего Person JSON.

Output: исправленный coordinator/preview/UI, регрессии, актуальное описание редактирования и inspected screenshots/video на вымышленных данных. Stop: mandatory tests и исходный UI cycle должны пройти; unit-only или один красивый экран не закрывают пользовательский сценарий.

## 2. Текущее состояние (AS-IS)

Read-only исследование фактического `ContactMirrorData` и SQLite `entities` через mode=ro/query_only подтвердило: изменены `data.names[0].familyName`, `data.names[0].unstructuredName`, а также четыре пути `google.person.names[0]`: familyName/unstructuredName/displayName/displayNameLastFirst. Значения/имена человека в SPEC не копируются.

- `SyncCoordinator.ReadOnlyModified/BlockedEntity` сравнивает весь local.google с state.Baseline.google. `BuildContact` при несовпадении возвращает один Blocked `$entity` до расчёта обычных полей.
- `EntityEntry` не задаёт Before; у blocked entry Local содержит полный ContactDocument, Google — raw Person (разные формы).
- `MainWindowViewModel.OnSelectedEntryChanged` выводит большие JSON в один TextBox. Нет перечня leaf changes, отдельного объяснения служебных изменений и repair action. В низком окне открытая панель настроек сокращает доступное место details.
- `SyncEntry.IsSelectable` запрещает Blocked. Это необходимая защита, но существующий UX оставляет пользователю ручное редактирование/поиск backup.
- Обычное `data.names` уже поддерживается реестром/шлюзом. Ошибка текущего случая — ранний metadata block и отсутствие восстановления/diff, а не отсутствующая writable категория names.
- `JsonSemantics` сравнивает массивы как мультимножества; порядок/дубли важны при объяснении изменений. FileWorkspaceStore имеет backup, CAS, guard path/root/photo, журнал операций. ApplicationActivity сериализует UI operations и updater restart.

## 3. Проблема

Приложение умеет обнаружить нарушение служебного снимка, но не помогает сохранить и отправить полезные правки контакта. Whole-document JSON без baseline скрывает, какие поля пользователь действительно исправил.

## 4. Цели дизайна

Понятная разница по значениям; безопасный путь из blocked в обычный upload; один owner планов/записей; повторное использование backup/CAS/journal; отсутствие автоматической записи в Google при исправлении локального снимка; сохранение формат1 и пользовательских данных.

## 5. Non-Goals

Полный редактор контактов, изменение Google identity/PROFILE данных, автоматическое принятие служебных правок, автоматический upload, переустройство формата файлов/движка конфликтов/updater, фоновый sync, установка/публикация/новый live fixture. Нет прямой записи в текущий пользовательский контакт в ходе разработки.

## 6. Предлагаемое решение (TO-BE)

### 6.1 Распределение ответственности

- Core: calculated preview details/семантические различия, названия вложенных полей и значения; не принимает решения о записи.
- Application SyncCoordinator: передаёт сравнимые before/local/remote представления и eligibility repair; выполняет только локальное восстановление служебного снимка с trusted state, backup/journal/CAS; обычный upload остаётся прежним.
- Desktop VM/AXAML: readable details, видимые причины disabled, команда repair, invalidate preview и ручная следующая проверка; не пишет JSON напрямую.
- FileWorkspaceStore: существующая атомарная запись и резервная копия; не ослабляются lease/index/namespace/photo/path защиты.
- Tests: observed end-to-end исправление data+google → repair → check → upload → readback/no-op и failure cases.

### 6.2 Детальный дизайн

Normal entry показывает только изменённые части своей категории и направление. Для имени leaf rows отображают «Фамилия»/«Полное имя» и точный JSON path. Столбцы: «Было», «В файле», «В Google». Unchanged значения не создают лишних строк; полный JSON остаётся доступным отдельно. Отсутствующее свойство, null, пустая строка и [] различаются.

Blocked contact из-за google изменений получает trusted baseline ContactDocument, local ContactDocument и remote ContactDocument в одной форме. Details разделены на «Ваши правки» (data/labels/starred/photo) и «Служебная копия Google» (google). Выводятся обе группы и явная причина блокировки; исходный blocked entry не превращается в selectable upload.

Уточнённый scope общего diff/отправки: **все** категории `CapabilityRegistry.WritableFields`, а не hard-coded names. Включены имя/псевдонимы, телефоны, email, адреса, организации, заметки, даты, ссылки, пользовательские и прочие writable поля реестра; отдельно labels/starred/photo. Много полей одного контакта остаются отдельными selectable entries, но обрабатываются одним Apply выбранного плана. Конфликты и blocked не выбирают сторону автоматически; неизменённые категории, PROFILE/output-only данные и системные memberships не очищаются. Точные field masks, отдельные membership/photo API и existing checkpoints сохраняются.

Photo details формируются независимо от раннего metadata block: сравниваются local bytes/hash и cached remote CONTACT portrait bytes/hash из Prepared с **раздельными** baseline PhotoLocalHash/PhotoRemoteHash. Path/URL не подменяют содержимое. UI показывает «добавлено / заменено / удалено / разные правки», текущие миниатюры «В файле»/«В Google», путь/hash. Замена bytes по прежнему пути при неизменном JSON обязана быть видна. «Было» содержит достоверный baseline hash; старая миниатюра допустима только при доступных bytes с этим hash, иначе прямо «Предыдущее изображение не сохранено». Текущее Google изображение нельзя выдавать за baseline при remote изменении. Image decode bounded/off-thread; при invalid/missing/out-of-workspace файле применяется существующая блокировка с причиной, а не очистка Google фото.

Current thumbnails используют захваченные LocalEntity.PhotoBytes и Prepared.Photos, а не повторный URL fetch или произвольное чтение пути из display-модели. Выбор строки не запускает Google requests; decode нужен только выбранному контакту. Hash/photo guard перед записью остаётся обязательным, отображённая миниатюра не является новым источником доверия.

Новый допустимый `photo` файл означает добавление/замену CONTACT portrait; `photo:null` — намеренное удаление портрета, отдельно показываемое и применяемое только после выбора. Другая фотография на стороне Google даёт three-way conflict; mutation сохраняет fresh remote guard. Google может перекодировать изображение: подтверждённые local hash и remote readback hash сохраняются раздельно, поэтому next-noop проверяется и при различных local/remote bytes. PROFILE/coverPhotos остаются только для чтения.

Snapshot repair не меняет ни editable baseline, ни PhotoLocalHash/PhotoRemoteHash и не перезаписывает фото: новая фотография должна остаться кандидатом на Upload после ручной проверки. Если поздняя photo mutation завершилась ошибкой после успешных текстовых writes, UI показывает частичный результат; checkpoint сохраняет подтверждённые поля, следующий Prepare предлагает только остаток/неизвестные операции. Автоматический retry remote mutation не добавляется.

Массивы: сначала семантически исключаются неизменившиеся элементы с сохранением кратности; перестановка равных элементов не считается правкой. Для names singleton сравниваются вложенные свойства. У многозначных массивов без надёжного соответствия элементы показываются как добавленные/удалённые с полными значениями и фактическими индексами стороны; нельзя угадывать, что два произвольных телефона — одна запись. Числовые/boolean значения сохраняют тип. Неизвестные пути показываются дословно. Разница считается для выбранного entry, вне UI thread; длинные значения прокручиваются/копируются без обрезания данных.

Команда `RepairGoogleSnapshotAsync(preview, entryKey, progress, token)` добавляется в ISyncCoordinator с default NotSupported для тестовых/неподдерживающих providers. Она доступна только при известном state/baseline и читаемом валидном файле, совпадении UUID/kind/account binding, известной причине readOnlyModified и отсутствии незавершённой remote mutation этой записи. Для описанного ниже завершения pending local repair вместо readOnlyModified проверяется соответствие captured trusted replacement и ожидаемому результату intent: уже исправленный снимок не обязан снова быть повреждён. Global degraded-state recovery (view.IsRecovery из повреждённой/восстанавливаемой базы, не preview.IsRecovery из одного local intent), неоднозначная identity, duplicate IDs, invalid JSON/schema/unknown binding не превращаются в repairable. Для отсутствующего/удалённого remote контакта repair disabled с объяснением; соответствующие delete/recovery потоки остаются прежними. Новый UI показывает кнопку только при eligibility, иначе объясняет недоступность.

При нажатии используется зарегистрированный immutable Prepared, а не значения preview из UI: проверяются PlanId/root/account/key/state identity. Общий coordinator mutex + UI ApplicationActivity не допускают concurrent sync/auth/update. План потребляется; перед любой записью заново читаются файл/фото/state и сверяются hash/привязка с просмотренным планом. Drift → остановка без перезаписи.

Target — clone текущего локального документа, у которого заменяется только `google` на trusted `state.Baseline.google`. Источник не берётся из отредактированного google и не угадывается по имени. Не меняются data, labels, starred, photo, extensions, UUID или filename. Фото не перезаписывается. Данные baseline не обновляются: иначе имя могло бы ошибочно считаться синхронизированным.

Перед заменой `BeginRunAsync` сохраняет точные raw bytes текущего файла/фото, state и cached remote из Prepared. Затем durable local-only journal intent `$repairGoogleSnapshot`, CAS WriteAsync, committed и CompleteRun. Intent хранит trusted replacement snapshot, входной document/photo hashes и ожидаемый результат; optional journal metadata должна читаться старым кодом без обязательной миграции. Результат явно «Служебная копия исправлена. Ваши правки сохранены. Нажмите “Проверить изменения”, чтобы отправить их в Google».

Ни один gateway method/token request не вызывается repair-командой: включая get/read/photo download/mutate. Внешнее состояние не меняется; repair не запускает Prepare автоматически. UI очищает старый план/Apply selection, обновляет историю, сохраняет возможность открыть backup. Новая ручная Prepare использует обычный Google read; upload перед удалённой записью сохраняет fresh remote/drift проверки.

Journal recovery этого нового intent обрабатывается отдельно от generic `$journal:` с EnsureRemoteAsync. После перезапуска точка входа — **явная ручная «Проверить изменения»**, то есть существующая PrepareAsync сначала читает Google, затем строит новое immutable Prepared. Offline recovery без подготовленного плана не входит в текущий scope: при недоступной сети/token ручная проверка сообщает ошибку и не меняет файл/журнал. Никакой автоматической recovery-записи на startup нет.

В подготовленном плане local repair recovery проверяет файл против captured trusted replacement: если снимок уже восстановлен, показывается отдельное явное «Завершить локальное исправление», которое закрывает только этот intent; если файл всё ещё исходный, пользователь может явно повторить repair; при новой правке/смене binding/недоверенном состоянии — остановка, новый review. Нельзя записывать весь старый intent document поверх новых data edits или подтверждать посторонние pending remote operations. Само применение repair/recovery из готового плана не вызывает gateway/token request, не делает EnsureRemote и не создаёт Google-контакт. Полный restart→manual Prepare включает обычное чтение Google;0 requests относится к отдельной repair/recovery команде, а не ко всему restart flow. Original/committed/drift состояния проверяются отдельно.

Visual planning artifact (вымышленные значения, embedded wireframe):

```text
Иван Петров                       Требует исправления
Имя изменено. Служебная копия тоже отредактирована.

Ваши правки
Поле / путь                 Было       В файле       В Google
Фамилия · data.names[0]      Петров     Петренко      Петров
Полное имя                  Иван ...   Иван ...     Иван ...
Телефон                     +7 ...01   +7 ...02      +7 ...01
Заметка                     старый     новый         старый
Фото                        [hash]    [миниатюра]   [миниатюра]
Фото заменено · путь прежний · сравнивается содержимое

Служебная копия Google   [4 изменения · раскрыть]
Этот раздел восстановится; ваши правки останутся в data.
[Исправить служебную копию]  [Открыть файл] [Полный JSON]

после repair: Правки сохранены. Проверьте изменения.
после ручной check: Имя, Телефон, Заметка, Ярлыки, Фото · В Google
[Применить выбранные изменения]
```

Details имеет собственную вертикальную прокрутку, длинные пути/значения wrap, toolbar/footer не перекрывают текст. Проверяются открытые настройки, обычное/уменьшенное окно и DPI текущего компьютера. Существующие automation IDs сохраняются, новые для DiffRows/RepairGoogleSnapshotButton/RepairExplanation. Before/after видео одинакового воспроизводимого synthetic name-edit сценария, только app window, без настоящих контактов/аудио.

Уточнённый visual acceptance: before/after видео того же **multi-field+photo** сценария, включающего исходный name-edit defect. Инспектируются diff нескольких категорий и две current photo thumbnails, в том числе в blocked metadata details; новый стабильный selector PhotoComparison. Подставлять старые видео согласованного окна вместо этого сценария нельзя.

### 6.3 User-Observable Scenarios

| Scenario | Trigger | Visible result | Evidence | AC |
| --- | --- | --- | --- | --- |
| Исправить имя в data | Правка familyName/unstructuredName → check | Точные old/local/Google values, В Google и selectable | Core/coordinator + Headless/FlaUI | 1,2 |
| Изменить весь контакт | Правки нескольких data категорий/labels/starred/photo → check/Apply | Все правки видны и выбранные применяются одним Apply, untouched поля сохранены | Category contracts + full mixed UI cycle | 7,8 |
| Заменить фото по прежнему пути | Новые image bytes при том же photo path → check | Content replacement видно, фото доступно к отправке; next-noop после readback | Real disk/photo gateway+native | 8 |
| Фото изменено с двух сторон | Local replacement и remote photo update | Явный конфликт, нет авто выбора источника | Three-way photo tests | 8 |
| Ошибка после text upload | Photo API failure | Частичный результат; текст не отправляется заново, pending photo видимо | Fault checkpoint test | 8 |
| Исходная ошибка | Те же правки в data и google snapshot | Ваши правки видны, причина block, repair button | Реальный fake disk fixture + inspected native PNG | 1,3 |
| Исправить копию | Нажать repair | Backup, data сохранены,0 Google requests, старый план очищен | Disk/HTTP-count tests + native flow | 3,4 |
| Отправить исправленное имя | Новая check → Apply | Upload names, readback соответствует, next no-op | Coordinator→real disk→demo/HTTP gateway UI cycle | 2,5 |
| Файл успел измениться | Редактирование после preview | Объяснимый drift, новые bytes не перезаписаны | Negative disk/UI test | 4 |
| Менялось только google | Snapshot edit без data edit | Видны служебные правки; repair не создаёт upload имени | Coordinator test | 3,5 |

### 6.4 State / Interaction Matrix

| State | Trigger | Result | Failure/disabled |
| --- | --- | --- | --- |
| Ordinary preview | Выбрать entry | Diff выбранной категории | Нет entry — существующий empty state |
| Blocked metadata | Выбрать контакт | Diff обеих групп + repair | Нет trusted binding/валидного документа — причина, repair disabled |
| Repairable | Нажать repair | Busy, backup и локальный CAS | Cancel/error/drift — безопасный результат, старый план invalid |
| Repaired | Ручная check | Обычный upload либо conflict при удалённой правке | Никакого auto check/upload |
| Names upload | Apply | Существующие remote guard/update/readback | Remote drift → повторный preview, никаких silent retries |

### 6.5 Decision Ledger

| Decision | Owner | Chosen | Confidence | Risk | Needs user before EXEC |
| --- | --- | --- | ---: | --- | --- |
| Новая repair action | agent | Явная local-only кнопка и ручная следующая check | .95 | Ещё одно действие, зато data сохраняются и metadata не принимаются молча | Нет |
| Источник снимка | agent | Trusted SQLite baseline, неизменный data baseline | .99 | Нет baseline — только объяснение, не угадывание | Нет |
| Actual user book write | user | Не входит в разработку; только read-only исследование | 1 | Имя существующего контакта не отправляется агентом | Нет |
| Установка/публикация | user | Нет в этом scope | 1 | Отдельные полномочия | Нет |
| SPEC→EXEC | user | Exact «Спеку подтверждаю» | 1 | Central gate | Да, только phase approval |

### 6.6 Runtime / Config / Data Contract Matrix

| Area | Source of truth | Change | Compatibility | Check |
| --- | --- | --- | --- | --- |
| Editable contact | data/labels/starred/photo | Только presentation, repair не меняет | Формат1 сохранён | Field/value equality before/after repair |
| Identity/snapshot | account-bound SQLite state | Explicit restore local.google | Нет server rebinding/etag bypass | Identity/readonly/CAS regressions |
| Plans | Coordinator registered Prepared | Один новый local repair method | Default unsupported для fake providers | Forged/stale keys/root/account |
| Journal | Durable operations | local-only repair intent/recovery | Optional metadata; обычные intents прежние | Crash before/after Write, no gateway calls |

## 7. Бизнес-правила / Алгоритмы

Readonly block сохраняется до explicit repair. Не переносить изменения из snapshot в data автоматически; пользователь редактирует data. Имя синхронизируется через names writable projection/обычный mask. Восстановление snapshot не сбрасывает edit baseline и не подтверждает Google mutation. Conflict не превращается в Upload после ремонта: three-way применяется заново. Preserve массивную кратность/семантику; diff не меняет actual payload.

## 8. Точки интеграции и триггеры

BuildContact/BuildGroup/readOnlyModified формируют различимые details и eligibility. SelectionChanged строит display diff только выбранного entry. RepairCommand вызывает coordinator API внутри RunAsync/ApplicationActivity; никакой записи из VM. После repair InvalidatePreview. Manual Check/Apply прежние owner paths. Новый journal field имеет отдельную local-only recovery branch до generic journal handling.

## 9. Изменения модели данных / состояния

Calculated preview diagnostics/eligibility и diff rows; новая coordinator API. Optional local repair journal metadata, без изменения contact schemaVersion/SQLite user_version. Интенты не содержат credentials. Доверие не делегируется boolean из UI: coordinator повторно вычисляет все условия.

## 10. Совместимость, миграция и откат

Старые файлы читаются без миграции; прежние корректные Upload/Download/Conflict/Create/Delete/Restore не меняют direction/selection. Backup хранит исходные bytes с ошибочной snapshot копией и хорошими data edits. Rollback файла — existing backup recovery с preview, без автоматического Google overwrite. Rollback кода — отдельный commit revert после EXEC, не reset текущей работы; сначала все новые repair intents должны быть завершены/проверены новой версией. Downgrade с pending `$repairGoogleSnapshot` **не поддерживается**: старый код принимает неизвестный pending.Field как generic `$journal:` и выполняет EnsureRemote, поэтому fail-closed блокировка старой версией не гарантируется. Документация явно требует завершить local recovery новой версией перед downgrade. Содержательная compatibility проверка: новая версия читает прежние journal записи, а после завершения repair журнал не содержит новых pending intents и прежний reader принимает completed state; это не обещание безопасного старого recovery для pending нового поля.

## 11. Тестирование и Acceptance Criteria

Обязательный набор: RED воспроизводимого names+snapshot block с отсутствующим usable diff/repair; targeted Core/coordinator/disk regressions; полная solution Release build и все Core/Google/Headless/FlaUI suites. Full обязателен: shared preview/API/local journal/storage behavior. Tests/build с общими bin/obj последовательно. Исторические212 PASS относятся к старому commit и не являются новым evidence.

| AC | Automated check | Visual/log check | Artifact |
| --- | --- | --- | --- |
| 1 | Leaf name diff; blocked case содержит before/local/remote и все6 изменённых paths synthetic; array reorder, удаление одного дубликата, add/remove без guessed pairing, missing/null/empty distinctions | Native exact old/new values, readable with settings/window size | TRX + inspected PNG |
| 2 | Ordinary data.names upload field mask и proper source; unaffected categories retained | Visible selectable «В Google» | Coordinator + Headless/FlaUI |
| 3 | Repair of data+google edits: data/labels/starred/photo/extensions preserved; Google restored trusted baseline; exact raw backup;0 gateway/token calls | Repair explanation/button/success and invalidated selection | Disk/HTTP-count + native |
| 4 | Hash/photo drift, forged plan/root/account/key, missing baseline, duplicates, pending remote operation; restart original/committed/drift через explicit manual Prepare (read count>0), затем repair/recovery0 extra gateway calls; throwing gateway/token при Prepare → ошибка и0 writes/изменений журнала; legacy journal/completed repair compatibility, pending-new-intent downgrade unsupported | Disabled reason and no unsafe restart during repair | Safety/UI regressions |
| 5 | repair→manual Prepare→names Upload→remote readback→next-noop; metadata-only repair→no names Upload | Automated full user flow on ordinary demo app | Before/after video + disk/HTTP proof |
| 6 | All mandatory full suites/build green, old export no-repeat-Google/read-only/lease/recovery/updater tests retained | Source re-review and private artifacts excluded | New TestResults + docs/VALIDATION |
| 7 | Parameterized preview/upload contracts **каждой** WritableFields категории + labels/starred, exact masks/proper CONTACT source; mixed name/phone/email/address/org/bio/date/custom/labels/starred/photo Apply; unaffected fields/PROFILE/system memberships сохранены | Все changed entries видимы/доступны В Google; mixed full-contact flow | Core/coordinator/HTTP + Headless/FlaUI |
| 8 | Photo add/replace/null-delete, same-path bytes change, local+remote conflict, invalid/missing path, post-preview file/photo drift; repair сохраняет photo baselines; readback differing remote encoding→no-op; text-success/photo-failure→checkpoint/only remaining preview | Current local/Google thumbnails и content status в normal/blocked details; same mixed flow video | Real file+production gateway fixtures/native/fault TRX |

Команды после approval:

```powershell
dotnet build ContactMirror.sln -c Release -m:1
dotnet test tests/ContactMirror.Tests -c Release --no-build
dotnet test tests/ContactMirror.GoogleTests -c Release --no-build
dotnet run --project tests/ContactMirror.UiTests.Headless -c Release --no-build -- --maximum-parallel-tests 1
dotnet run --project tests/ContactMirror.UiTests.FlaUI -c Release --no-build -- --maximum-parallel-tests 1
git diff --check
```

TUnit targeted runs только --treenode-filter. TRX/logs в TestResults/ContactEdit, PNG/video в chat-artifacts/contact-edit (local-only/не коммитить). До — current ordinary demo/failing repro с синтетическим JSON; после — тот же flow и API counts на final build. Видеозапись доступна через existing window recorder, поэтому fallback без причины недопустим. Не записывать Google user book. Полный новый live Google тест не обязателен и не разрешён этим scope; actual Google names supported mask уже реализован, новые проверки используют production gateway с fake HTTP и real disk, это явно ограниченный evidence.

К уточнению2026-10-08: coverage обязано перечислить все WritableFields из текущего реестра и отдельно memberships/photo; несколько удобных names/phones примеров не закрывают AC7. Whole-contact/photo evidence — production gateway с fake HTTP и real disk, не новый live Google claim. UI до/после использует один mixed-contact+replaced-photo fixture. Existing full suites остаются обязательными; image decode/preview регрессии добавляются в targeted набор.

Performance: не перебирать1000 файлов ради diff выбора; выбранный документ обрабатывается off UI thread и отменяется при смене выбора. Target обычного singleton names diff <100мс в deterministic fixture, не universal guarantee. Многомегабайтный JSON/long arrays — проверить отзывчивость и отсутствие silent truncation. После green не расширять проверки без нового риска. Timeout не повторять идентично; сначала progress/lock/root cause.

## 12. Риски и edge cases

Служебная правка только displayName не означает желание изменить data.names; не конвертировать автоматически. PROFILE/source IDs не writable. Параллельная правка фотографии блокирует repair CAS, photo bytes не теряются. Невалидный JSON/duplicate ID/нет baseline — нужен существующий error/recovery, не auto normalize. Cancel/failure after atomic write может оставить журнал, но не remote side effect; после restart для нового плана нужна обычная ручная Google-проверка, offline recovery не обещается. Диагностика должна избегать tokens/PII в публичных artifacts.

| Expected objection | Why likely | Mitigation | Disposition |
| --- | --- | --- | --- |
| «Не видно, что изменилось» | Whole JSON и пустое Before сейчас | Leaf rows + old/local/Google; normal/blocked screenshots | mitigated |
| «Исправлено только имя» | Первоначальный repro назван именем | Все WritableFields+labels/starred/photo в AC7/8, mixed full-contact cycle | mitigated |
| «Путь фото прежний — ничего не заметило» | JSON reference не меняется | Content hashes/current thumbnails/same-path regression | mitigated |
| «Фото снова предлагает отправить» | Google перекодирует изображение | Раздельные local/remote baseline hashes и differing-bytes no-op test | mitigated |
| «Вернётся старое имя» | Snapshot repair звучит как rollback контакта | Заменяется только google; data baseline/правки сохраняются, full cycle обязательный | mitigated |
| «Опять нельзя отправить» | Blocked guard прежний | Explicit repair + manual check → tested selectable upload/readback | mitigated |
| «Зачем Google снова читается при ремонте?» | Предыдущее поручение про cached save | Repair0 requests, manual Prepare отдельно; remote write guards сохраняются | mitigated |
| «Нужно самому искать backup» | Текущий message перекладывает работу | Кнопка восстановит trusted local snapshot; backup создаётся до записи | mitigated |

Rework checklist: outcome/scenarios/AC связаны; выбранные agent decisions явны; data/photo/identity risks рассмотрены; visual artifact выше; review до approval; completion требует полный цикл, не одно исправленное свойство.

## 13. План выполнения

После approval: RED fixture/native до → readable diff и baseline presentation → coordinator local repair/intent/recovery → UI command/flow → targeted и полный набор → обычный demo cycle/native PNG/video → source/docs review. Storage owner и UI owner root, overlapping writers нет. Публикация/установка остаются отдельным поручением.

## 14. Открытые вопросы

Существенных design вопросов до EXEC нет. Нужна exact phase approval; реальная запись в существующий контакт/публикация не требуется для этого локального outcome.

## 15. Соответствие профилю

Desktop: off-thread работа, shared activity, стабильные automation IDs, build/test обязательны. UI automation: existing Headless/FlaUI, embedded wireframe и same synthetic cycle before/after. Testing: RED→targeted→full и actual disk guards. Review: expanded full passes + adversarial read-only reviewer (sandbox writable явно фиксируется), no-code-before-approval.

## 16. Таблица изменений файлов

| Files | Change | Purpose |
| --- | --- | --- |
| Core/Models.cs, новый preview diff helper/CapabilityRegistry.cs | Diagnostic baseline/eligibility + diff labels/semantics | Объяснить поля/причину |
| Application/Contracts.cs, WorkspaceContracts.cs, SyncCoordinator.cs | Local-only repair contract/intent/recovery | Сохранить полезные edits и trusted binding |
| Desktop/MainWindowViewModel.cs, MainWindow.axaml | Diff rows, scroll, repair command/state | Исходный пользовательский UI flow |
| Core/coordinator/Google safety tests, existing UI suites/testhost/pages | Red/cycle/negative/proof, all categories/photo cases | AC1–8 |
| docs/FORMAT.md, VALIDATION.md, README если нужна короткая ссылка | Правила редактирования/фактическая валидация | Инструкции соответствуют UI |

## 17. Было → стало

| Area | Before | After |
| --- | --- | --- |
| Имя | JSON blob | Точные изменённые поля и значения |
| Весь контакт/фото | Diff/выход из blocked не покрыт целиком | Все writable категории, memberships и content photo сравнение/обновление |
| data+google edit | Whole-contact block без выхода | Block explained, local repair, manual check/upload |
| Repair data safety | Только manual JSON/backup поиск | Explicit transactional restore только google, data preserved |
| Google writes | Запрещены при metadata block | Прежняя защита до repair; обычный upload после новой проверки |

## 18. Альтернативы и компромиссы

Снять readonly guard — угроза identity/source/etag, отклонено. Молча нормализовать google при любом Apply — скрытая перезапись пользовательского файла, отклонено. Попросить пользователя вручную вернуть JSON — не устраняет UX проблему, отклонено. Явная local repair добавляет одно действие, зато не угадывает имя/не делает незаметных записей и сохраняет baseline для последующего upload. SchemaVersion2 с отделением snapshot в другой файл — существенно расширяет миграцию/совместимость, не входит в текущий bugfix.

## 19. Quality gate и review

### SPEC Linter Result

| № | Result / evidence |
| --- | --- |
| 1 | PASS outcome §1 |
| 2 | PASS AS-IS source + read-only6 paths §2 |
| 3 | PASS root problem §3 |
| 4 | PASS design goals §4 |
| 5 | PASS Non-Goals §5 |
| 6 | PASS owners §6.1 |
| 7 | PASS triggers §8 |
| 8 | PASS diff/repair invariants §6.2/7 |
| 9 | PASS drift/crash/recovery §6.2/12 |
| 10 | PASS selected-only/off-thread/performance §11 |
| 11 | PASS state/source contract §6.6/9 |
| 12 | PASS format1 compatibility §10 |
| 13 | PASS exact backup/local rollback §10 |
| 14 | PASS AC1–8 §11, все writable категории и фото |
| 15 | PASS automated/visual/evidence matrix §11 |
| 16 | PASS commands/stop §11 |
| 17 | PASS dependencies §13 |
| 18 | PASS decisions/phase boundary §6.5/14 |
| 19 | PASS expanded due storage/public API/trust boundary §0 |
| 20 | PASS profiles §15 |

### SPEC Rubric Result

| Criterion | Score | Evidence |
| --- | ---: | --- |
| Outcome/boundaries | 5 | §1/5, original named-edit scenario |
| AS-IS | 5 | source block/VM + actual baseline paths |
| Design | 5 | typed diff/local-only repair/state/journal |
| Safety/migration/rollback | 5 | CAS/backup/identity/pending/format1 |
| Verifiability | 5 | full cycle + negative cases/matrix/video |
| Autonomous decisions | 5 | no unresolved design/external dependency |

30/30 — readiness score, не phase approval и не runtime PASS.

### Role-Based Review Result

| Role | Applicable | Question | Final design verdict |
| --- | --- | --- | --- |
| Business/domain | Да | Имя сохраняется и отправляется после ремонта? | PASS design: explicit repair→check→upload cycle |
| UX/designer | Да | Понятны values/block/recovery и куда нажать? | PASS design: leaf rows/две группы/wireframe/scroll |
| Tester | Да | Отличает старый defect от useful cycle и drift? | PASS plan: red + real disk/native cycle + negatives |
| Architect | Да | UI не задаёт доверие/нет baseline reset? | PASS design: immutable plan/state/owner/CAS |
| Delivery/security | Да | Repair не пишет в Google/не rebinding? | PASS design:0 calls и отдельные внешние полномочия |

### Post-SPEC Review

Статус **PASS на уровне SPEC**, включая уточнение2026-10-08. Outcome/AC/wireframe дополнены всем writable контактом и фото; targeted adversarial reviewer подтвердил согласованность с photo planner/state/commit и PASS пяти ролей. Код/тесты ещё не менялись, exact approval не получен.

- Scope/Evidence: эта SPEC, canonical template/central owners/profiles, current commit/status, Core codecs/registry/Models/ThreeWayPlanner/JsonSemantics, coordinator Prepare/Blocked/Apply/Validate/journal branches, VM/AXAML, workspace/session contracts, FileWorkspaceStore state SQL; actual single contact + readonly SQLite baseline paths. web tool вернул connection failure; затем readonly официальный [Google People discovery](https://people.googleapis.com/$discovery/rest?version=v1) успешно прочитан: familyName/unstructuredName writable, displayName/displayNameLastFirst output-only. Секреты/контакты в этот запрос не передавались; AS-IS также подтверждён checked-in schema/projection.
- Contract: original name-edit/diff/sync outcome, Non-Goals, table scenarios→AC; прежняя cached export/no-auto-reread защита и remote mutation guards сохраняются.
- Adversarial: snapshot identity tamper, без baseline, duplicate UUID, open/mapped writer, photo drift, payload spoofing, journal crash, only-google edit и remote conflict рассмотрены. Local repair не переносит snapshot имена в data и не меняет data baseline.
- Role-Based: пять ролей выше; independent reviewer обязательный для trust/storage boundary, фактически доступный writable child используется как adversarial read-only fallback, не technically isolated.
- Fix and re-review: до draft из UI-кода установлено отсутствие Before и разные формы blocked Local/Google; дизайн нормализует их и отдельно обрабатывает local-only pending journal вместо EnsureRemote. Reviewer выявил ложное обещание downgrade fail-closed и отсутствующую offline recovery точку входа: §6.2/10 и AC4 уточнены, AC1 расширен multiset/empty cases. Targeted reviewer re-review подтвердил PASS всех пяти ролей, новых находок нет. Root затем уточнил название entryKey и eligibility завершения уже применённого local intent, не меняя проверенный recovery scope; исходный readOnlyModified guard нельзя ошибочно требовать у уже repaired файла.
- Depth checklist: diff scope только current SPEC; measurable AC и original flow; unsupported runtime/publication claims отсутствуют; ordinary app/video/native/disk evidence требуется в EXEC; docs/compatibility/hidden new API/journal названы; manual challenge — repair должен сохранить data и привести к Upload после ручной check, это обязательный end-to-end criterion.
- Stop decision: **PASS SPEC**; можно запросить «Спеку подтверждаю», EXEC пока запрещён. Scope/scenarios/AC/objections сверены. Technical writable reviewer boundary и ограничения downgrade/offline явно сохранены.

Targeted review2026-10-08: scope — уточнённые §1/6.2/6.3/11/12/visual wireframe и текущие coordinator photo planning/Apply/CommitContact state ветки. Contract/risk pass: every WritableFields category + memberships/photo coverage, same-path content change и blocked details, отдельные local/remote hashes при Google encoding, photo conflict/delete/failure checkpoints, mixed native cycle вместо name-only. Domain/UX/QA/architect/delivery-security PASS design. Findings: **нет новых находок**, основание — каждый новый user-observable scenario связан с AC7–8, отсутствует безусловный whole-Person overwrite/сброс photo baselines/подмена исторической миниатюры. Старые trust/recovery contracts не расширены, исправления в коде не выполнялись. Sandbox writable, reviewer read-only только по действиям. Root уточнил отсутствие повторного URL fetch при выборе photo details в рамках того же cache-based design. Stop остаётся PASS SPEC, ждёт exact approval.

| Severity | Area | Finding | Required action | Status |
| --- | --- | --- | --- | --- |
| MEDIUM | Preview / evidence | Blocked entry без baseline, local/remote в разных формах | Consistent before/local/remote и leaf diff | design fixed; reviewer PASS |
| HIGH | Repair trust | Нельзя принять изменённую identity или сбросить data baseline | Trusted state только google, CAS/backup и отдельный local journal | design fixed; reviewer PASS |
| MEDIUM | Downgrade compatibility | Старый generic journal принимает неизвестный field; автоматическая блокировка pending repair не гарантирована | Запретить поддержку downgrade с pending new intent, требовать complete новым кодом; проверить legacy/completed-state compatibility | corrected; targeted reviewer PASS |
| MEDIUM | Restart recovery entrypoint | Prepared требует Google Prepare, поэтому offline recovery было обещано без точки входа | Ограничить restart recovery явной ручной Prepare; repair/recovery command0 extra calls, offline error0 writes; AC original/committed/drift | corrected; targeted reviewer PASS |
| LOW | Diff validation | Multiset/empty-value contract не был явен в AC1 | Добавить array reorder/duplicates/add-remove/missing/null/empty cases | corrected; targeted reviewer PASS |

No-findings justification: после исправлений reviewer не нашёл новых design defects, исходный name-edit→repair→manualcheck→upload/noop и негативные сценарии имеют acceptance/evidence mapping. Внешние записи не входят в scope. Остаточный риск: техническая readonly изоляция reviewer недоступна; новое journal behavior требует executable failure tests. Tests и behavior ещё не выполнены.

### Post-EXEC Review

Реализация выполнена после exact approval2026-10-08. Root self-review и независимый adversarial reviewer рассмотрели весь tracked/untracked source/test diff. Reviewer работал только чтением в writable sandbox; технической readonly изоляции нет.

| Severity | Finding | Исправление / evidence | Статус |
| --- | --- | --- | --- |
| MEDIUM | Повторные repair failures создавали несколько логически одинаковых intents | Exact-intent dedup, CAS fingerprints, повторные failures и restart regressions | закрыт, source re-review PASS |
| MEDIUM | Progress создавался на worker thread | UI context capture до Task.Run | закрыт, source re-review PASS |
| MEDIUM | Diff терял missing/null/type либо угадывал singleton phone identity | Presence/type rows, singleton leaf pairing только допустимых категорий, multiset indices и regressions | закрыт, Core107 PASS |
| MEDIUM | Фото из журнала подменяло физическое local comparison | Actual images отдельно от planned target; journal-photo regression | закрыт, source/tests PASS |
| MEDIUM | Repair intent разрешался до завершения истории | CommitLocalRepairAsync: одна SQLite transaction для committed/intents/new+scoped-old runs; fault после resolution → rollback/restart regression | закрыт, Google147 PASS |
| MEDIUM | Settings вытесняли details в compact окне | Account/settings в ограниченной верхней scroll области; actual-height assertions, Headless1000×680 и NativeDPI216 | закрыт, inspected PNG/source PASS |
| MEDIUM | Debug evidence не закрывает Release AC6 | Release build и полные suites запущены отдельно | закрыт, Release276/276 PASS |

Debug и Release full:107 Core +147 Google +15 Headless +7 Native =276/276 PASS каждый. Release build0 errors/warnings. TestResults/ContactEdit/release-{core,google,headless,native}. Финальный gate закрыт: reviewer подтвердил Release TRX, чистое video evidence и PASS пяти ролей.

RED на исходном cfc57bf с корректным mixed-contact/photo fixture: `TestResults/ContactEdit/red-valid-baseline/`, ожидаемый failure `AutomationId:DiffSummary` отсутствует. Старый coordinator показывает blocked whole-document JSON без readable diff/repair; снимок в isolated baseline подтверждает тот же исходный сценарий. Исторический invalid-PNG video исключён из evidence.

AC1–8 реализованы и связаны с Core/HTTP/Headless/native regressions. Whole-contact HTTP использует production gateway и реальные файлы с fake server; не является новым live Google или установленным build. Native before/after: один synthetic fixture,30s,15fps, без звука; перезапись client area исключает DWM/desktop edges. Final evidence и stop decision добавляются ниже после Release.

## Approval

Exact phrase **«Спеку подтверждаю»** получена2026-10-08 после post-SPEC review и уточнения all-contact/photo scope. Разрешена реализация и локальная проверка этой SPEC; новая запись в реальный Google, установка, commit/push и публикация этим подтверждением не разрешены.

## 20. Журнал действий агента

| Фаза / событие | Решение / evidence | Остаток | Следующий шаг | Решение пользователя | Артефакты |
| --- | --- | --- | --- | --- | --- |
| SPEC / диагноз | Actual readonly diff:2data+4google name paths; coordinator whole-contact block и VM JSON-only | Prepare reviewable repair/diff design | Текущая expanded SPEC | Показывать diff и дать sync, затем «Продолжай» | Только эта SPEC |
| SPEC / draft | Explicit local-only repair, trusted baseline/CAS/backup/journal, manual check→upload, full acceptance plan | Reviewer и final design fixes | Adversarial read-only review | Нового exact approval нет | Эта SPEC |
| SPEC / fix-and-re-review | Два MEDIUM/LOW исправлены, reviewer targeted PASS всех ролей; downgrade pending unsupported, restart Prepare network read явно отделён от local command0 calls | Exact approval, затем новый RED/EXEC | Запросить phase approval | «Продолжай» сохраняет цель, новой exact phrase нет | Только эта SPEC |
| SPEC / scope2026-10-08 | Общий diff/обновление всех WritableFields+labels/starred/photo; same-path bytes, photo baselines/encoding/conflict/partial и mixed UI cycle обязательны | Targeted re-review уточнённого outcome/AC/visual plan, затем exact approval | Проверить дополнение | «Надо не только имя но и обновление всего контакта включая фото» | Только эта SPEC |
| SPEC / re-review2026-10-08 | Reviewer:нет новых находок, PASS пяти ролей, all-category/photo contracts и AC7–8 согласованы с источниками | Exact approval, затем RED/EXEC/full цикл | Передать обновлённую SPEC | Exact phrase пока не получена | Только эта SPEC |

| EXEC / approval2026-10-08 | Whole-contact/photo scope разрешён | Реализация и новые checks | RED → code → regressions → post-EXEC | «Спеку подтверждаю» | Эта SPEC |
| EXEC / implementation | Readable three-way diff; explicit local repair0 calls; photo previews; atomic repair history; compact scroll/virtualization | Final Release и video audit | Полные Release suites | В рамках approval | source/tests, TestResults/ContactEdit |
| EXEC / review-and-fix | Все production MEDIUM исправлены, source re-review PASS; Debug276/276 и inspected normal/compact PNG | Release gate и final audit | Завершить evidence | Новый live Google/install/commit не разрешены | docs/VALIDATION.md, private PNG/video |

### Final evidence2026-10-08

- Release solution build0 warnings/errors. Полные TRX: `release-core/core.trx`107, `release-google/google.trx`147, `release-headless/`15, `release-native/`7 —276/276.
- Readable surname native: `chat-artifacts/ui/20261008-104627-112/contact-edit-blocked-name-values-native.png`, Ветрова / Петренко / Ветрова. Photo comparison в той же папке, compact settings/details в `20261008-104609-164/`.
- `chat-artifacts/contact-edit/{before,after}.mp4`: PrintWindow client area,450 кадров,15fps,30s,2656×1846. Исходныйcfc57bf и final Release, synthetic fixture. Before frames5/25 и after1/10/25 просмотрены; foreground других приложений не снимается. Это state-flow demonstration, не wall-clock performance evidence.
- Повторные recording runs отдельно: `before-printwindow/`1 PASS и `after-printwindow/`1 PASS. Ожидаемый RED valid baseline в `red-valid-baseline/`; не включается в полный финальный green набор.
- `git diff --check` PASS. chat-artifacts/TestResults ignored; auth/реальный workspace/installed binaries не менялись. Новые source/tests/SPEC остаются незакоммиченными.
- Финальный независимый пяти-ролевой review: **PASS post-EXEC** (business/domain, UX/designer, QA, architect, delivery/security); открытых BLOCKER/HIGH/MEDIUM/LOW нет. Самостоятельно прочитаны Release TRX/storage без skips, recording runs1/1+1/1, native surname PNG, before/after кадры/ffprobe и документы. Release build0/0 подтверждён root; reviewer сборку и тесты не повторял. Adversarial read-only fallback в технически writable danger-full-access sandbox, техническая read-only изоляция не заявляется. Root self-review: PASS implementation/validation scope; внешняя установка/публикация/live Google вне scope.

| Фаза / событие | Решение / evidence | Остаток | Следующий шаг | Решение пользователя | Артефакты |
| --- | --- | --- | --- | --- | --- |
| EXEC / final review2026-10-08 | Release276/276, clean native before/after, пять ролей PASS; незакрытых замечаний нет | Нет обязательных работ в утверждённом локальном scope | Передать локальное исправление; STOP до отдельного поручения на установку/commit/публикацию/live Google | В рамках «Спеку подтверждаю» | source/tests, SPEC, docs/VALIDATION.md, private evidence |
| Delivery / installer2026-10-08 | Существующий workflow Build-Release создал Production0.2.6; SHA256 Setup и assemblies full nupkg проверены | Установка и публикация не выполнялись | Выдать локальный Setup | «Где инсталлятор?» | ignored artifacts/releases/Production/versions/0.2.6 |
| Delivery / commit2026-10-08 | Source/tests/docs подготовлены для локального Conventional Commit; generated artifacts и OAuth JSON исключены | Проверка коммита и чистого рабочего дерева | Локальный commit, без push | «Закоммить» | Git history |

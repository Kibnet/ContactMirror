# Понятные поля контакта: проверка реализации 12.10.2026

Подтверждённая спецификация: `specs/2026-10-12-friendly-contact-fields.md`. База — `1fa82b8`, ветка — `feat/contact-editor`. Данные всех запусков синтетические; установленная версия и настоящий Google не изменялись.

## Изменения

Карточка разделяет просмотр и редактирование. Типы телефона, почты, адреса и других полей показываются по-русски; пользовательские токены сохраняются точно. Имена, адреса, организации, заметки и даты имеют отдельные формы. Четыре режима даты не подставляют неизвестный год; проценты переводятся в исходные единицы только после явного ввода. Все 23 категории и 111 записываемых листьев доступны через именованные контролы, подробности и поиск.

Find показывает заполненность конкретного поля, значение записи и её исходный номер. Add не дублирует singleton. Фото, ярлыки и действия записи имеют отдельные точки входа. Ошибочный ввод переживает раскрытие полей и удаление соседних строк; Save блокируется до исправления ошибок. Подтверждение dropdown применяет JSON и буферы под одним generation gate. Сохраняются исходные порядок, типы, скрытые данные, Google snapshot и формат файлов/настроек.

## Проверки

| Проверка | Результат | Evidence |
|---|---|---|
| Release solution | PASS, 0 ошибок/предупреждений | `chat-artifacts/friendly-fields/after/build.log` |
| Core xUnit | 163/163, 0 skipped | `after/core/core.trx`, `after/core.log` |
| Google/storage xUnit | 185/185, 0 skipped | `after/google/google.trx`, `after/google.log` |
| Полный Headless UI | 90/90, 0 skipped, 1m56s756ms | `after/headless-full/ContactMirror.UiTests.Headless_net10.0_x64.trx`, `after/headless-full.log` |
| Полный Native / обычный EXE | 13/13, 0 skipped, 2m29s588ms | `after/native-full/native-full.trx`, `after/native-full.log`; `chat-artifacts/ui/20261012-114230-919/ordinary-demo-native.png` |
| Schema / doctor | PASS: 23 категории, 25 definitions; 24 fixtures; doctor 0 errors/warnings | `python schemas/generate_schemas.py --check`, `python schemas/check_schemas.py`, `after/doctor.log` |

Пути `before/...` и `after/...` относятся к `chat-artifacts/friendly-fields/`. Generated PNG/video/log/TRX остаются вне Git.

## Независимые проверки данных и взаимодействия

- Все 111 leaf paths получены из независимого schema oracle. Значения изменены через реальные TextBox/CheckBox/ComboBox/custom popup, записаны реальным `ContactEditorStore` и перечитаны из файла. Expected JSON, Google и extensions проверяются независимо от renderer.
- Option inventory учитывает schema enum и действительно predefined string values; примеры свободного текста не превращаются в закрытые варианты. Проверены custom/case/empty/null и literal `$custom`.
- Математический oracle проверяет проценты, высокую точность и raw-unit fallback `1e-28`; raw predicates, sorting и старые column IDs/настройки сохраняются. UI-фильтры дают `12,5% → 12500`, `workMobile`, `0000-02-29`.
- Реальные UI-проверки покрывают четыре date modes, stale mode → subsequent single-field edit с сохранением untouched `year:0`, ShowAll без подмены precision, clear/remove/primary, сохранение invalid buffers и корректное переиндексирование. Nested extension array с 60 элементами раскрывается по ошибке индекса 50 и фокусирует TextBox с сохранённым вводом; dotted JSON key не совпадает с nested path. Corrected Add снимает только свою ошибку; удаление даты фокусирует соседнюю дату либо Add.
- Owned VM regression проверяет coherently observed JSON+buffer+dirty, отсутствие вложенной mutation/restart authorization внутри commit, stale/invalid result и readonly VIEW menu. Ярлыки проверяются через popup: staged selection, Cancel и unchanged Done не меняют draft/bytes.
- Light/Dark ×1180×820,1000×680,820×640; stored fractions .25/.8 временно ограничиваются при layout без изменения preferences. Long identity при820 сохраняет viewport≥80 и полное имя в scroll/tooltip. Footer и поиск доступны.

## Визуальная проверка и записи

Перед новой реализацией сохранена чистая source-копия `1fa82b8`; production-файлы baseline не менялись. Скопированы одинаковый тестовый сценарий, его runtime adapter, readiness getter и hook точного HWND recorder. Baseline проверяет type/date/photo → local save/reopen → manual preview/apply → no-op. Before/after имеют одинаковый basename `contact-editor.mp4` в разных каталогах с фазой и timestamp.

Ранний source review нашёл и закрыл индексные buffers, структурное очищение ошибок, повторную запись после clear, collision custom token, defaults при primary, фокус, atomic dropdown payload, stale touched state, partial-date precision и readonly affordances. Финальный targeted review дополнительно закрыл Add-error recovery, виртуальный extension focus, date/delete focus и противоречащие счётчики ошибок. Средства доступности показывают Title выбранного пункта вместо технического record dump.

Первоначальный recorder захватывал экранный прямоугольник и зависел от foreground. При проверке кадров запись не прошла приёмку и удалена. Новый `NativeWindowVideo` снимает только HWND тестового процесса через `PrintWindow`, проверяет PID/геометрию каждого кадра и кодирует actual timestamps в MP4 5 fps без аудио. PNG/JPEG кадров, capture.json, ffconcat и raw MP4 сохраняются для проверки. Одинаковые geometry/DPI и сценарий обязательны; дополнительная нижняя чёрная строка обеспечивает чётную высоту yuv420.

Проверены Light/Dark VIEW ×3 размера, EDIT ×3 размера, длинный identity, несколько ошибок и исправление одной, восемь unknown labels при0groups и popup с названием группы в300 символов. Длинный ярлык переносится, Done/Cancel доступны, unknown labels имеют «Ещё 2» и явную причину readonly. Эти состояния не требуют реального аккаунта.

Настоящий Native-прогон дополнительно проверил системный picker (Cancel/replace/exact bytes/view/remove), custom type в UIA, draft keyboard guards, local save/reopen/manual preview/apply/no-op и обычный Desktop EXE. Цветное фото видно в `chat-artifacts/ui/20261012-114112-799/editor-native-picker-selected-native.png`; demo black avatar — исходное1×1PNG, а не ошибка рендера. В `20261012-114045-014/friendly-narrow-one-error-kept-native.png` footer и global status согласованно сообщают одну ошибку. Assert проверяет2→1→0 и затем successful save. Native узкое окно запросило820×640; фактические outer bounds1873×1519 приDPI216 учитывают Windows chrome/minimum, точная client layout820×640 отдельно проверена Headless.

Парная запись сценария `Friendly_contact_type_partial_date_photo_save_reopen_and_manual_sync` прошла на обоих binaries:

| Версия | Видео | ffprobe | Проверка |
|---|---|---|---|
| BEFORE, production archive1fa82b8 | `before/editor-before-20261012-114440-327/contact-editor.mp4` | 16.6s,2250×1576,5fps,без аудио | 1/1,27s110ms; `before/test-results/before-archive-native.trx` |
| AFTER, текущая реализация | `after/editor-after-20261012-114155-226/contact-editor.mp4` | 16.8s,2250×1576,5fps,без аудио | входит в full Native13/13; scenario19s551ms |

Root просмотрел декодированные кадры обоих видео: VIEW1.8s, EDIT8s(before)/7.8s(after), manual preview14s. В старой карточке первое окно заполнено большим фото и generic controls; новая показывает имя/организацию, телефон, почту и footer. В обоих роликах только окно ContactMirror, одинаковый synthetic seed/geometry/DPI, type/date/photo/local-save/reopen/manual-preview/apply/no-op. GUID и wall-clock timestamps закономерно различаются. `before/ffprobe.json` и `after/ffprobe.json` подтверждают codec dimensions/duration; source capture.json и исходные кадры сохранены. Парные frames лежат в `before/frame-{view,edit,sync}.png` и `after/frame-{view,edit,sync}.png`.

Reviewer не менял файлы и не запускал UI/build/tests; его sandbox был unrestricted, поэтому это процедурный advisory/adversarial fallback без технической read-only изоляции. После проверки фактических TRX, metadata/ffprobe, обоих видео по кадрам, native PNG и текущей SPEC/diff он завершил full post-EXEC PASS по пяти применимым ролям.

## Full post-EXEC review

Scope/Evidence: утверждённая SPEC, текущий diff Application/Core/Desktop/Authoring/Headless/FlaUI, README, этот audit, full Release0/0, Core163/163, Google185/185, Headless90/90, Native13/13, schema/doctor0/0; independent schema111/option/math/query oracles и просмотренные rendered PNG/video frames. Всего451 тест текущей реализации, отдельно baseline1/1. Owner stack: central AGENTS/routing, QUEST mode/governance, review-loops, UI/AppAutomation profile, appautomation/record-app-screen/run-tunit-tests skills.

Contract: AC1–8 сверены с формами, raw-preservation, Find/Add, guards/validation, readonly/photo/labels и responsive controls. AC9 закрыт full suites и обычным EXE. AC10 закрыт одинаковым actual native сценарием, exact-HWND video, ffprobe и просмотром кадров. Non-goals соблюдены: JSON/prefs/storage/sync contract не мигрируется, нет новых сетевых действий или массовой нормализации.

Adversarial/Fix and re-review: проверены raw custom/case/empty/null и sentinel, signed/high-precision units, year0/absent/partial dates, preferred duplicates, stale/foreign generation, atomic popup payload, invalid-only dirty, extension dotted key/index50/reindex, corrected Add и unrelated errors, date/delete focus, narrow Escape, long header/labels и current error counts2→1→0. Каждая наблюдаемая in-scope находка исправлена и сверена targeted source/actual UI; failed early runs не служат final evidence. Initial screen-recorder footage rejected/removed; exact owned-HWND replacement прошёл baseline/updated scenarios.

| Применимая роль | Проверенный результат |
|---|---|
| Domain | Google type/date/percent semantics и23/111 coverage; exact writable/raw preservation |
| UX/designer | compact VIEW/domain EDIT, понятные точки входа, error recovery,12 layout PNG, overflow, inspected native photo/error/video |
| Tester | full451 tests0skips, separate baseline1/1, meaningful independent controls→file/query/math checks, ordinary EXE |
| Architect | presentation overlay отделён от raw comparisons, stable144 column IDs; single-generation owned popup commit, lazy/virtualized rows |
| Delivery/security | local synthetic scope, HWND/PID-only recording, no real Google/installed rollout; actual writable reviewer sandbox disclosed |

Depth/NoFindingJustification: scope drift/unrelated diff, unsupported claims, hidden API/data/UX contracts, retained features, comments/docs, rollback and validation boundaries сверены. Новый generic JSON editor не заменяет все111 полей. Редактирование raw токена, потери invalid input после reindex или расхождение UI с исходными units обнаруживаются independent tests. Manual-review challenge закрыт actual persisted bytes/Google preservation, focus/visible error states и before/after footage. Генерируемые артефакты остаются ignored вне Git.

Stop decision: **PASS, локальный EXEC завершён**. AC1–10 закрыты в заявленном локальном scope; открытых BLOCKER/HIGH/MEDIUM/LOW findings и обязательного оставшегося work нет. Отдельный reviewer подтвердил Scope/Evidence → Contract → Adversarial → Fix/re-review → Roles → rendered/runtime evidence и отсутствие противоречий между source, persisted values и observable behavior. Доставка и более широкие внешние проверки не входят в эту приёмку.

## Границы

Это локальная реализация и проверка. Вход, propagation и all-field roundtrip в настоящем Google, обновление установленной версии, публикация, push и commit в эту задачу не входят. Откат к `1fa82b8` читает те же форматы JSON и table preferences. Native typing/keyboard focus проверены; отдельная матрица физических Windows IME не выполнена. TextBox при вводе не перестраивается, поэтому source contract caret/preedit сохраняется без утверждения о проверке всех внешних input methods.

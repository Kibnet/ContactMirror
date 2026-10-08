# ContactMirror: понятный просмотр и применение изменений

## 0. Метаданные
- Форма: Expanded, medium/large: меняются несколько пользовательских потоков, представление плана и UI-state. Short неприменима.
- Профили: dotnet-desktop-client, ui-automation-testing; context testing-dotnet; skills creator-vibe, appautomation.
- Owner: основной агент; текущая рабочая копия ContactMirror, detached HEAD. Git delivery отдельно не запрошен.
- Поверхность: Codex desktop, Windows, PowerShell, .NET SDK 10.0.401; влияние модели/tiers на продукт — Не применимо.
- Instruction stack: central AGENTS/routing-matrix; creator-vibe-lens, model-behavior-baseline, tool-execution-baseline, collaboration-baseline, quest-governance, quest-mode, testing-baseline; spec-linter, spec-rubric, review-loops; выбранные profiles/context. Локальный AGENTS.override.md не найден.
- Ограничения: SPEC до exact approval; нет реального Google, пользовательских контактов, установки, push, PR, release или публикации.
- Связанные документы: [предыдущая SPEC](2026-10-07-contact-edit-diff.md), [UI harness](../tests/ContactMirror.UiTests.Authoring/README.md), [валидация](../docs/VALIDATION.md).
- Baseline: 08.10.2026 Headless 15/15; открыты 9 PNG актуального MainWindow. Артефакты local-only, gitignored, chat-artifacts/ui/20261008-1802*/. Model eval — Не применимо: меняется продуктовый UI.

## 1. Overview / Цель
Исходное поручение: «Примени все твои рекомендации» после UX/UI-ревью в этой беседе. Все семь пунктов ревью, схема рабочего экрана и заключительные рекомендации о первом запуске/оформлении входят в scope.

Outcome contract:
- Пользователь видит, какой контакт и какие поля изменились, куда будут записаны данные, что выбрано и что ещё требует решения.
- Сравнение видно до решения о версии; один контакт не выглядит несколькими разными контактами.
- Работа возможна при 1180×820, 1000×680 и минимальном окне 820×640; настройки не сжимают сравнение.
- Итог: локальная реализация, тесты, просмотренные before/after PNG и безопасное video evidence, обновлённая документация поведения. Реальный OAuth и installed-runtime этим не подтверждаются.
- Stop: завершение только после обязательных AC/checks и post-EXEC review. Blocker фиксируется с точным остатком; не объявлять непроверенное выполненным.

## 2. Текущее состояние (AS-IS)
- MainWindow.axaml: крупная шапка, account card, настройки/capabilities внутри верхней прокручиваемой области, toolbar и отдельный фильтр; изменение — отдельная строка списка.
- В 1000×680 область списка/деталей около 160px высотой. Сравниваемые значения скрыты ниже кнопок выбора версии и служебных действий.
- MainWindowViewModel хранит Entries/VisibleEntries, SelectedEntry, DiffRows, результаты и историю. Выбор поля формирует PlanChoice по Entry.Key; нельзя заменить это выбором только контакта.
- PreviewDiff отдаёт before/local/google и технические пути; элементы массивов сопоставляются только по точному совпадению, без догадки о замене телефона.
- После Apply preview инвалидируется, но прежние entries/галочки видны; частичный итог находится в нижней строке.
- IsOnboarding зависит только от подключения аккаунта: карточка выбора папки исчезает после подключения даже при незавершённом шаге.
- Native integrated harness уже умеет записывать безопасное видео через CONTACTMIRROR_CONTACT_EDIT_VIDEO, recorder в record-app-screen. Ранее README о полном отсутствии видео устарел относительно кода.
- Инварианты: ручной prepare/apply, backup/journal/CAS, отдельное подтверждение удалений, snapshot repair без запросов Google, cancellation/off-thread diff/image decode, виртуализация больших планов.

## 3. Проблема
UI заставляет пользователя разбираться в операциях и служебном состоянии прежде, чем он может увидеть и уверенно выбрать результат синхронизации.

## 4. Цели дизайна
Контакт как единица навигации; операция/категория как единица разрешения и записи. Читаемое сравнение и точные последствия. Видимый следующий шаг. Сохранение совместимости файлов, безопасности и производительности. Технические подробности доступны по запросу.

## 5. Non-Goals
Новый редактор контактов, автоматическая синхронизация/повторная запись, новый алгоритм conflict/array matching, миграция JSON/SQLite, изменения API Google/OAuth/updater, работа с реальными данными, установка/публикация. UI не обещает откат Google через обычную отмену; восстановление по существующему плану истории сохраняется.

## 6. Предлагаемое решение (TO-BE)
### 6.1 Распределение ответственности
- Desktop MainWindow/новые локальные views: компоновка, панели настроек/истории/справки, диалоги, доступность.
- MainWindowViewModel и новые presentation VM: группы контактов/ярлыков, фильтры/counts, состояние flow и выбора, presentation diff.
- Core PreviewDiff: существующая точная семантика сохраняется; допустимы узкие дополнения metadata для lossless presentation, если desktop mapper не может достоверно извлечь их. Алгоритм синхронизации и matching не меняются.
- Application/Infrastructure: existing contracts остаются источником правды; не переносить запись файлов/Google в UI.
- Authoring/Headless/FlaUI: сценарии и реальные pixel/video checkpoints; изолированные synthetic провайдеры.

### 6.2 Детальный дизайн и visual planning artifact
Следующие wireframes — целевая структура и acceptance reference внутри текущей SPEC. Отдельные файлы до approval не создаются.

```text
1180×820 / 1000×680
ContactMirror   account · folder                 История  Настройки  Справка
Демо: вымышленные данные                         [Проверить снова]
[Все 12] [В Google 5] [В папку 4] [Требуют решения 2] [Удаления 1]
[Поиск контакта, телефона, почты или поля…]
┌ Контакты (~32%) ───────┬─ разделитель ┬ Детали (~68%) ──────────────────┐
│ Александра · 5 правок  │              │ Александра Петренко             │
│ → В Google            │              │ [✓ Имя] [✓ Телефон] [✓ Заметка] │
│ Борис · 1 правка       │              │ Поле       Сейчас   После       │
│ Вера · конфликт       │              │ Фамилия    …        Петренко    │
│                       │              │ Телефон    …        +1…         │
│ Ярлыки                │              │                                 │
│ Работа · 1 правка      │              │ Будет записано в Google         │
│                       │              │ ▸ Технические подробности       │
└───────────────────────┴──────────────┴─────────────────────────────────┘
3 контакта · 7 изменений · 1 конфликт остаётся     [Применить 7 изменений]
Перед записью будет создана резервная копия
```

Группировка по (EntityKind, EntityId), не по отображаемому имени; ярлыки — отдельный тип/секция. В фильтре отображаются соответствующие операции контакта; скрытые поля не исчезают из общего выбора. Итог явно сообщает весь выбор, включая скрытый фильтром. Список виртуализирован. Выбор отдельной операции и категории сохранён; групповой checkbox имеет три состояния и не выбирает неразрешённые конфликты/blocked/удаления автоматически. Общая команда безопасного выбора использует исходные правила; удаления остаются отдельным opt-in. Смена фильтра/разрешения поддерживает валидную видимую selection, не оставляет невидимое выбранное поле без пояснения.

```text
Конфликт поля
Вера Примерова · Телефон · разные правки
             В папке                 В Google
             +7…111                  +7…222
             [Оставить из папки]      [Оставить из Google]
▸ Предыдущая синхронизированная версия
Итог: выбранная версия из папки → будет записана в Google
[Отложить]                         [Следующий конфликт (1)]
```

При первом открытии обычного двустороннего конфликта значения и обе допустимые команды видны без прокрутки на 1000×680 (короткие стандартные данные). Recovery-варианты требуют отдельных presentation правил: `$relink:<resource>` разрешает только UseGoogle с командой «Связать с найденной записью», `$retry` только UseLocal с командой «Создать ещё одну запись» и явным предупреждением о возможном дубликате. Альтернативные relink/retry одного EntityId взаимоисключающие: выбор одного снимает остальные; group checkbox никогда не выбирает их автоматически. Одна remote candidate не может быть выбрана для двух local identities: presentation предотвращает identityCollision, coordinator остаётся окончательной проверкой. Неподдерживаемая сторона недоступна и по keyboard/команде; нельзя обещать обычную замену поля вместо relink/create. Для `$journal:*`, restore/reconcile и других служебных entries показывать семантику реальной операции/допустимые resolutions из существующего coordinator, не угадывать направление по общему ChangeKind. Header/details tools компактны; технические пути/JSON не стоят перед значениями. В нормальном entry две колонки «Сейчас в Google/папке» и «После применения» по фактическому направлению. В обычном конфликте «В папке / В Google», baseline раскрывается дополнительно. При невыбранной операции текст: «Изменение не выбрано; запись не запланирована». Фото сохраняют честные local/google previews и baseline hash/отсутствие прежнего снимка. Blocked показывает причину и existing repair action без автоматического разрешения.

Читаемое представление отображает поле/тип: «Домашний телефон», «Заметка», «Фамилия»; декодированные телефоны/почта/даты/адреса и существующие значения, без придуманной нормализации. Неизвестные свойства не пропадают: readable fallback + raw details. Missing, null, пустая строка, пустой массив остаются различимыми. Массивы сохраняют added/removed semantics; независимые элементы нельзя выдавать за достоверную замену. Технический view показывает точные исходные значения и paths; copy остаётся возможным. Не скрывать большие значения/строки: виртуализация и прокрутка вместо усечения данных.

Компактная шапка: account, folder с ellipsis/full tooltip, состояние проверки, компактный demo badge; ошибки содержательны и доступны рядом с местом действия. Фильтры кликабельны, имеют count и активное состояние, суммарные показатели объясняют операции, а не уникальных людей. Search покрывает имя/поле и видимые телефон/почту без Google requests; индекс из текущего плана строится off-thread либо при prepare, не на каждое нажатие с полным сериализованием.

Настройки, история и поддерживаемые данные — отдельные полноразмерные внутриоконные страницы/panels, открывается только одна. Возврат сохраняет поиск, фильтр и selection. Настройки сохраняют существующие OAuth/updater/theme/connect/disconnect guards; история — restore/cleanup confirmation. Нельзя одновременно показывать активную destructive modal и переключать account/folder/panel. На 820×640 допустим режим «список → детали → назад», без наложения колонок; граница переключения вычисляется по фактической usable width, начальная целевая ~900px.

Одно главное синее действие для текущего этапа: без плана «Проверить изменения»; с готовым планом «Применить N изменений»; без выбранных операций disabled с причиной; при нерешённых конфликтах видимый переход, безопасные остальные операции доступны. «Новый файл контакта», file tools и справка — secondary/overflow; сохранён обычный путь создания и открытия JSON.

```text
Результат
✓ Выполнено 7 изменений в 3 контактах
[Проверить изменения снова] [Посмотреть действия] [История]

Выполнено частично: 2 подтверждено, 1 ошибка, 1 неизвестный результат
Контакт · операция · причина · подтверждённый/неизвестный статус
[Проверить изменения снова] — новый план перед повторной записью
```

OperationResult содержит Key/Status/Message, но не EntityId: перед apply сохранить transient lookup Entry.Key → EntityKind/EntityId/Name/Field/resolved direction из захваченного preview и exact selected choices. Число затронутых успешной записью контактов считать по distinct Contact EntityId только для результатов confirmed; назвать «подтверждены изменения в N контактах», не «N контактов полностью синхронизированы», если часть полей failed/unknown. Ярлыки считать отдельно. Неизвестный key сохраняет статус/message, но не даёт придуманной identity.

Фактический SyncRunResult.Failed включает failed Operations плюс skipped; после break/cancel некоторые selected keys отсутствуют в Operations. Presentation явно разделяет confirmed, operations со status failed, unknown и selected keys без результата («не выполнялось / остановлено до выполнения»). Aggregate API не меняется; UI поясняет «не завершено: N, из них ошибки завершения X, не выполнялось Y», вместо выдачи всех skipped за ошибки записи. Failed не доказывает отсутствия записи в Google: подтверждённая remote write может предшествовать failed local commit. Текст «не удалось завершить; изменения могли уже попасть в Google — выполните новую проверку» и исходное message вместо обещания отката/нулевого side effect. Если coordinator вернул result со special key/агрегатами, которые нельзя достоверно разложить, показать aggregate с честным пояснением и original messages; не синтезировать confirmed/failed.

После apply старый план не интерактивен/не выглядит новым; результат отдельное состояние, прежний просмотр допускается только с маркировкой «Архивный план». Empty: «Изменений нет: папка и Google согласованы». Search/filter-empty отличается от no-change; pending/recovery/blocked не обозначать как успех. Отмена/ошибка prepare возвращает доступный check без восстановления актуальности старого плана.

Первый запуск остаётся wizard до наличия аккаунта И папки: завершённый шаг подключения виден, выбор папки остаётся; после обоих шагов «Посмотреть изменения». Реальная пригодность папки проверяется existing coordinator/path policy, непустая строка пути сама по себе не означает её валидность. После account/folder switch preview invalidation обязательно.

Удаление: modal с затенённым фоном и safe cancel; название контакта/ярлыка, отдельные counts «из Google / из папки», список при нескольких; информация planned backup. Явно сообщить, какая текущая копия сохраняется либо что другой стороны уже нет, исходя из captured entry и разрешения. Обычное распространение удаления не обещает surviving copy; `$restoreDeleteLocal` сообщает сохранение Google, `$restoreUnbind` — сохранение локального файла/удаление связи согласно реальному плану. Для удаления ярлыка в Google сообщить, что контакты сохраняются, ярлык снимается с их связей. Backup не выдавать за существующую до её успешного создания и не обещать автоматического восстановления. Поле УДАЛИТЬ только по existing threshold; ошибка непосредственно под ним, безопасный возврат по Escape, Enter не обходит проверку. Закрыть доступ к background mutations. Никаких изменений threshold/backup policy.

Визуальная система: основной текст 14–16px, readable supporting text 12–14px, компактные строки вместо 110px карточек, единый шаг отступов 4/8px; выбранная строка, blocked/conflict/deletion/success имеют текст+значок и theme-aware цвета. Для обычного текста contrast ≥4.5:1; важные UI-индикаторы ≥3:1; disabled отдельно не использовать как доказательство отсутствия действия. Проверить keyboard focus, Tab, Space, Escape, accessible names, selected/expanded/disabled states; автоматическая активация destructive action при переключении selection запрещена.

Evidence: baseline PNG уже существуют; video до/после безопасного synthetic UI run по recorder hooks. До EXEC video capture не запускается. Если native input desktop/recorder фактически недоступен — точный отказ, fallback из отрисованных Headless PNG и behavioral tests; отсутствие возможности не называть native pass. Screenshot/video local-only и не коммитятся.

### 6.3 User-Observable Scenarios
| Scenario | Trigger | Expected result | Evidence | AC |
|---|---|---|---|---|
| Первый запуск | Подключить Google, затем выбрать папку | Обе стадии доступны, явный старт preview | Headless + PNG | AC1 |
| Пять полей одного человека | Открыть подготовленный план | Один контакт, пять selectable операций, точные counts | UI tests + PNG | AC2 |
| Конфликт | Открыть телефон с разными правками | Сначала оба значения, затем выбор и явный итог; следующее нерешённое поле | Headless/FlaUI + PNG/video | AC3 |
| Обычная отправка/загрузка | Выбрать поле | Читаемое current→planned, точное направление, raw details | mapper/unit + UI | AC4 |
| Работа на небольшом окне | Resize, открыть настройки/историю, вернуться | Читаемый workspace, не сжат настройками, сохранён выбор | 3 размера PNG + UI | AC5 |
| Поиск/фильтр | Поиск имени/телефона/email, смена разрешения | Верный список/counts, явный общий выбор | UI tests | AC6 |
| Полный/частичный результат | Apply | Отдельный итог, stale-plan недоступен, новый check перед повтором | real demo + partial fixture | AC7 |
| Удаление | Выбрать destructive entry | Точный объект/сторона, inline validation, safe cancel | UI negative tests + PNG | AC8 |
| Доступность | Тёмная тема, Tab/Space/Escape | Различимые состояния, видимый focus, readable names | UI + contrast calculations/PNG | AC9 |
| Большой план/repair | 10k entries, 2k diff rows, snapshot repair | Виртуализация/cancel, точные поля/фото, без реального Google | existing regression suite | AC10 |

### 6.4 State / Interaction Matrix
| State | Trigger | Transition | Error/disabled/concurrent |
|---|---|---|---|
| Setup | Connect / folder | Setup complete → ready to check | Failed connect сохраняет выбор папки; invalid folder contextual error |
| Ready | Check | Checking → preview / no-change | Cancel/failed prepare → ready, stale apply запрещён |
| Preview | Filter / field / resolution | Counts/list/details обновляются | Blocked/нерешённый конфликт не входит в apply |
| Preview | Settings/history/help | Отдельная panel → возврат | Busy/applying/destructive modal guards сохраняются |
| Preview | Apply | Confirm deletion либо applying → result | Backup/unknown/failure имеют честный статус |
| Deletion modal | Phrase/confirm/cancel | applying либо preview | Неверная фраза остаётся в modal; фон недоступен |
| Result | Check again | Новый checking → новый preview | Автоповтор записи запрещён |
| Blocked | Repair snapshot | Локальный результат исправления → ручная новая проверка | Не сообщать об отправке в Google; old plan/selection сброшены, failed/unknown объяснены |
| Any idle | Switch account/folder | Preview invalid → setup/ready | Архивные details не обещают будущую запись |

### 6.5 Decision Ledger
| Decision | Owner | Chosen option | Confidence | Risk | Needs user before EXEC |
|---|---|---|---:|---|---|
| Scope всех рекомендаций | user | Все пункты ревью и финальные onboarding/visual предложения | 1.0 | Пропуск категории | Нет |
| Контакт и отдельный field choice | agent | Presentation group по identity, PlanChoice остаётся по key | 0.98 | Избыточная отправка полей | Нет |
| Settings/history/help | agent | Внутриоконные самостоятельные panels с возвратом | 0.9 | Потеря context | Нет |
| Минимальный размер | agent | Master/detail при достаточной ширине, narrow drill-in | 0.9 | 3-column comparison слишком узкий | Нет |
| Storage/theme persistence | agent | Только existing persisted contract, splitter transient | 0.96 | Незапрошенная migration | Нет |
| Side effects | user boundary | Только локальная реализация и synthetic validation | 1.0 | Live sync/release | Нет |
| QUEST EXEC | user | Exact «Спеку подтверждаю» | 1.0 | Нарушение gate | Да, approval после готовой SPEC |

### 6.6 Runtime / Config / Data Contract Matrix
| Contract | Source of truth | Change | Compatibility | Verification |
|---|---|---|---|---|
| Plan/choice | SyncPreview / SyncEntry.Key / PlanChoice | Presentation only | Categories/field masks не объединяются | coordinator/demo regression |
| Workspace data | FORMAT / FileWorkspaceStore | Нет | Нет migration | GoogleTests safety suite |
| OAuth/updater | account connector/activity/UpdatesVM | Только presentation/navigation | Existing guards/token policy | UI tests |
| Selection/filter/result | Desktop VM | Calculated/transient state | No settings migration | UX UI tests |
| Preview semantics | PreviewDiff / PhotoComparison | Readable presentation | Exact raw/baseline retained | PreviewDiff/photo tests |

## 7. Бизнес-правила / Алгоритмы
Counts операций и уникальных контактов не смешивать. Ярлыки не считать людьми. Name не identity. Selected contact не равен всем selected fields. Filter не снимает hidden selection; footer объясняет его. Resolution не применяет запись. CanApply/CanConfigure activity guards остаются владельцами доступности. Выбор/групповое действие не снимает обязательность destructive confirmation. Unknown result требует нового prepare, не автоматического replay.

## 8. Точки интеграции и триггеры
LoadPreview строит groups/search index и sets preview state; selection/resolution обновляют counts и projections без повторных запросов Google. InvalidatePreview и account/folder changes снимают apply validity. Apply result переходит в result state. Panel open/close сохраняют workspace context. Selected operation отменяет старые async details/photo work и освобождает bitmap. Close сохраняет существующую cancel/activity semantics.

## 9. Изменения модели данных / состояния
Transient presentation groups, selected group/operation, panel/page enum, narrow details navigation, result/empty state, deletion phrase validation, semantic rows/technical toggle. Диск/SQLite/формат1/preferences schema не меняются. Existing EntryViewModel остаётся источником per-field choices либо получает эквивалентную adapter projection.

## 10. Миграция / Rollout / Rollback
Existing workspace читается без преобразования. Нет установки/live rollout. Rollback — откат локального UI/presentation/test diff; пользовательские данные не затрагиваются. Существующие desktop команды запуска сохраняются. Splitter transient; обязательного persistence для размера панелей нет.

## 11. Тестирование и критерии приёмки
| AC | Acceptance criterion | Automated check | Visual/manual evidence |
|---|---|---|---|
| AC1 | Connect-first/folder-first onboarding сохраняет обе стадии; явный start check | Authoring/Headless scenario, invalid path case | Setup/completed-step PNG |
| AC2 | Пять entries одного EntityId — один контакт; distinct same names не объединены; mixed/group selection безопасен; relink/retry не выбираются группой | grouping tests + UI exact field selection/readback | Grouped preview PNG |
| AC3 | Стандартный конфликт в 1000×680 показывает два значения и кнопки без scroll; resolution/result/next правильны; 2 relink candidates + retry взаимоисключаются; обе недопустимые стороны и duplicate remote candidate для двух local IDs предотвращены | UI bounds/state + conflict/recovery safety regression | Open inspected PNG/video |
| AC4 | Human field values, current/planned, lossless raw, missing/null/empty/array/photo semantics сохранены | mapper/PreviewDiff regression, whole-contact repair/upload | Diff/photo/technical PNG |
| AC5 | Workspace usable во всех 3 размерах; comparison ≥320px height при 1000×680; panels не сжимают его; resize/navigation возврат корректен | Headless layout + native compact | Light/dark/3sizes/panels PNG |
| AC6 | Count+filter+search работают; hidden selection объяснён; 10k plan остаётся virtualized и cancel доступен | search/count/10k regression | Filter/no-match PNG, timing diagnostics |
| AC7 | Full/partial/unknown/no-change разделены; skipped показаны как не выполнявшиеся; failed после remote confirmed write не обещает отсутствие записи; stale apply отключён; новая проверка перед повтором | Authoring partial + real pipeline convergence + early-break/unknown/cancel/remainder/remote-success-local-fail fixtures | Result/empty PNG/video |
| AC8 | Confirm deletion называет объект и destination/count, surviving-copy либо absence без ложного обещания; label removal явно сохраняет контакты; phrase только при threshold; inline error; Escape/cancel и background guards | destructive/negative keyboard UI tests + ordinary delete/restoreDeleteLocal/restoreUnbind/label fixtures | Modal/invalid phrase PNG |
| AC9 | Light/dark readable, text/status/icon distinguish; focus/accessible names; theme-aware contrast targets | color calculations + keyboard UI | Light/dark focused screenshots |
| AC10 | Existing snapshot repair, photo, restore/cleanup, updater/activity, large diff и API/data safety regressions green | обе xUnit и обе UI suites | Integration/readback logs |

Mandatory validation: standard Release solution build; обе xUnit suites (общий PreviewDiff может затронуть semantic presentation); full Headless и FlaUI (flow/layout изменены широко); visual review текущего main window, keyboard/resize. Native failure не уменьшает обязательный набор: указать incomplete, Headless fallback ограничен соответствующей поверхностью. Mock/demo не объявлять live acceptance.

```powershell
dotnet build ContactMirror.sln -c Release
dotnet test tests/ContactMirror.Tests -c Release
dotnet test tests/ContactMirror.GoogleTests -c Release
dotnet run --project tests/ContactMirror.UiTests.Headless -c Release -- --maximum-parallel-tests 1
dotnet run --project tests/ContactMirror.UiTests.FlaUI -c Release -- --maximum-parallel-tests 1
# Targeted UI: --treenode-filter '/*/*/<Class>/<Scenario>'
# Before/after synthetic native video: existing CONTACTMIRROR_CONTACT_EDIT_VIDEO hooks;
# расширить checkpoints/flow в EXEC для grouped conflict/result/setup.
```

Build/test последовательно при общих output dirs. Existing suite baseline — 15 Headless tests/15s runtime плюс сборка/restore; новые durations измерить. Перед длинными runs commentary и сохраняемые test report/logs. Сначала characterization/failing checks по onboarding/grouping/stale result, затем targeted, build, остальные обязательные suites; не повторять green без новых изменений. Physical DPI modes 100/150/200% — не обещаются; current monitor DPI и headless logical sizes фиксируются отдельно. Для ordinary desktop smoke использовать --demo с отдельным временным workspace, не live данные.

## 12. Риски и edge cases
Одноимённые контакты; label/contact name collision; conflicting категории одного человека; разные направления в одной группе; все filtered-out selections; search-empty; stale selection after repair/invalidation; remote/local deletions via resolution; неизвестный result; snapshot repair; 10k groups/2k diff rows; большие фото; отмена diff; theme/keyboard focus; screen resize во время panel/modal. Mitigation — исходные identity/choice contracts, batched projections, виртуализация, async cancellation и перечисленные AC.

### Expected User Review Objections
| Objection | Why likely | Mitigation | Status |
|---|---|---|---|
| «Я всё ещё не вижу значения» | Ранний screenshot conflict | AC3 без scroll + AC5 высота workspace | mitigated |
| «Ты отправил весь контакт вместо одного поля» | Grouped navigation | Existing per-entry PlanChoice/readback | mitigated |
| «В маленьком окне опять неудобно» | Baseline 160px details | 3size acceptance, narrow drill-in, separate panels | mitigated |
| «Где JSON, repair, фото и история?» | Simplified presentation | Technical expander + explicit preserved actions + regressions | mitigated |
| «Применить 5 — это сколько людей?» | Baseline repeated contact | Separate contacts/operations/labels counts | mitigated |
| «Половина сделана, дальше непонятно» | Partial footer baseline | Dedicated result + new manual prepare | mitigated |

Rework checklist: observable outcome, каждый сценарий→AC/evidence, выбранные решения/границы, вероятные возражения, role review и путь EXEC-validation заданы. Independent review по центральному gate; техническую read-only isolation нельзя предполагать по имени роли.

## 13. План выполнения
После approval: baseline repro/video и meaningful characterization checks; presentation grouping/state; новый layout/panels/setup; readable diff/conflict/result/deletion; regression adaptation; staged full validation/inspected evidence; docs; post-EXEC review. Не переносить live sync/installation в validation.

## 14. Открытые вопросы
Продуктовых блокирующих вопросов нет. Exact approval требуется по фазовому owner. Runtime доступность native recorder/input desktop проверяется в EXEC; отказ фиксируется evidence и влияет на полноту validation.

## 15. Соответствие профилю
UI work остаётся off-thread для затратных расчётов; existing selectors сохраняются при семантическом соответствии, новые стабильные IDs задаются для новых групп/панелей/состояний. Per-operation selector keys сохраняются в field choices. Changed flows получают Authoring/Headless/FlaUI coverage, стандартный build и обе xUnit suites. Visual planning — §6.2, automation-linked videos или объективный fallback.

## 16. Таблица изменений файлов
| Files | Changes | Reason |
|---|---|---|
| src/ContactMirror.Desktop/MainWindow.axaml, .axaml.cs, App.axaml | Layout/panels/navigation/styles/keyboard | UX recommendation coverage |
| src/ContactMirror.Desktop/MainWindowViewModel.cs, новые *ViewModel/*Presentation files | Groups/state/counts/diff projection | Identity/choice preservation |
| src/ContactMirror.Core/PreviewDiff.cs | Только при необходимости exact presentation metadata | Lossless human view; no matching rewrite |
| tests/ContactMirror.UiTests.Authoring/**, Headless/**, FlaUI/** | Changed flows, snapshots/video/assertions | Observable behavior acceptance |
| tests/ContactMirror.Tests/* / GoogleTests/* при затронутом contract | grouping/diff safety characterization | Exact values/selection regressions |
| docs/VALIDATION.md, README.md, UI Authoring README | Final behavior и точные evidence boundaries | User instructions |
| specs/2026-10-08-ux-workspace.md | Журнал/AC evidence/reviews | QUEST trace |

## 17. Таблица соответствий
| Было | Стало |
|---|---|
| Одна строка на поле | Контакт → независимые поля/операции |
| Кнопки выбора до значений | Значения → решение → явный planned outcome |
| Settings/history сжимают viewport | Самостоятельные panels с сохранением context |
| JSON paths/generic value на первом плане | Readable fields + lossless technical details |
| Dropdown и раздельные counts | Clickable counted filters, объяснённый общий выбор |
| Два primary actions | Primary по текущему этапу |
| Stale plan после apply | Dedicated full/partial/unknown result |
| Onboarding исчезает после connect | Два завершённых шага → preview |
| Generic delete/верхняя ошибка | Объект/сторона/inline validation/modal focus |

## 18. Альтернативы и компромиссы
Минимальная косметика не покрывает исходные проблемы навигации/потери места. Группировка с выбором только контактов теряет granular control и отвергнута. Сохранён master/detail с узким drill-in; panels выбраны вместо дополнительных windows для единого focus/context. Human presentation не удаляет raw truth. Новый storage contract для splitter/theme не нужен для результата и исключён.

## 19. Quality gate и review
### SPEC Linter Result
| № | Блок | Статус | Evidence |
|---|---|---|---|
| 1 | A / цель | PASS | §1 исходное поручение и outcome |
| 2 | A / AS-IS | PASS | §2 AXAML/VM/PreviewDiff + 9 PNG |
| 3 | A / корневая проблема | PASS | §3 cognitive/interaction problem, §6.3 10 scenarios |
| 4 | A / цели дизайна | PASS | §4 goals, §6.2 wireframes/interaction design |
| 5 | A / границы | PASS | §5 non-goals/side effects |
| 6 | B / ownership | PASS | §6.1 presentation vs engine |
| 7 | B / integration | PASS | §8 triggers |
| 8 | B / invariants | PASS | §7 per-key choice/deletion/unknown |
| 9 | B / recovery | PASS | §6.4 result/cancel/modal |
| 10 | B / performance | PASS | §6.2 index/off-thread/virtualization; AC6/10 |
| 11 | C / state | PASS | §9 transient projections |
| 12 | C / compatibility | PASS | §6.6/10 no data migration |
| 13 | C / rollback | PASS | §10 local code rollback |
| 14 | D / AC | PASS | §11 AC1–10, visible/height assertions |
| 15 | D / mapping | PASS | §11 all criteria have behavioral/visual evidence |
| 16 | D / commands | PASS | §11 exact runner/staged/stop rules |
| 17 | E / plan | PASS | §13 dependencies |
| 18 | E / decisions | PASS | §6.5/14 no product blockers |
| 19 | E / form | PASS | §0 Expanded UI-state scope |
| 20 | F / profile | PASS | §15 build/tests/IDs/video constraints |

Linter итог: ГОТОВО по self-pass, subject to post-SPEC review disposition.

### SPEC Rubric Result
| Критерий | Балл | Обоснование |
|---|---:|---|
| Ясность цели/границ | 5 | Все рекомендации mapped, side effects исключены |
| AS-IS | 5 | Реальный код и просмотренный render baseline |
| TO-BE | 5 | Wireframes, semantics, states/narrow layout |
| Безопасность | 5 | Per-entry contract/guards/no migration/rollback |
| Проверяемость | 5 | AC1–10 и negative/full suite/visual evidence |
| Автономное EXEC | 5 | Нет продуктового выбора, exact approval остаётся |
Итог: 30/30 — self-evaluation плана, не доказательство реализации/приёмки.

### Role-Based Review Result
| Role | Applicability | Review question/result | Verdict | Required changes |
|---|---|---|---|---|
| Domain workflow | applicable | Per-field/manual sync и confirmations сохранены | PASS | None in self-pass |
| UX/designer | applicable | Values first/major workspace/grouping/all states задан | PASS | None in self-pass |
| Tester | applicable | Each AC mapped, mock/native/live boundaries exact | PASS | None in self-pass |
| Developer/architect | applicable | UI projections не меняют engine/data, large plans учтены | PASS | None in self-pass |
| Delivery/security | applicable | Нет external delivery; deletion/OAuth/activity safety сохраняется | PASS | None in self-pass |

### Post-SPEC Review
- Scope reviewed: эта SPEC, central owners, Desktop AXAML/App/VM/code-behind, PreviewDiff, UI Authoring README, native recorder hooks, previous SPEC и baseline 9 PNG/15 Headless tests.
- Scope/Evidence pass: сопоставлены все пункты ревью, onboarding/visual/accessibility и исходные screenshot symptoms.
- Contract pass: per-entry granular selection, raw values, photos/repair/restore/updater сохранены; все AC имеют mandatory evidence.
- Adversarial pass: проверены identity != name, labels != contacts, filtered selection, partial/unknown != success, unsafe group selection, narrow comparison, missing/null/array matching, stale-plan guards; последствия отражены в §6–12.
- Role-Based pass: таблица выше + отдельный reviewer ux_spec_review просмотрел actual VM/AXAML/Models/PreviewDiff/coordinator и Authoring/Headless. Его effective sandbox — danger-full-access, approval never: read-only isolation недоступна. Запрет мутаций соблюдён процедурно; выполнен отдельный advisory/adversarial fallback, технически изолированным independent acceptance не считается. Residual risk — отсутствие runtime isolation reviewer, не проверка будущей реализации.
- Findings/fixes: уточнён видеопуть по реальным native hooks; counts контактов после apply только по captured key→identity; field filters/hidden selection уточнены. Separate reviewer выявил mixed Failed/skipped, recovery alternatives и surviving-copy deletion coverage: §6.2/AC2/3/7/8 дополнены фактическими contracts coordinator.
- Depth checklist: outcome/output/stop boundaries; scenarios/states/decisions/contracts; AC+negative cases; profiles/visual/video; alternatives/rollback/side effects просмотрены.
- Findings disposition:

| Severity | Area | Finding | Required action | Status |
|---|---|---|---|---|
| HIGH | Result contract | Failed включает skipped; failed может следовать confirmed remote write | Exact selected lookup, separate unattempted, honest completion errors; AC7 | fixed, targeted re-review PASS |
| HIGH | Recovery choices | relink/retry имеют разрешённую сторону и взаимоисключение/identityCollision | Specific actions/guards, duplicate warning; AC2/3 | fixed, targeted re-review PASS |
| MEDIUM | Deletion coverage | Нужно показать surviving-copy/absence и label effects | Captured-plan truthful copy/association statement; AC8 | fixed, targeted re-review PASS |

- Fix and re-review: reviewer повторно просмотрел актуальные §6.2 и AC2/3/7/8, сопоставил Models:60–63 и coordinator:342,385–401,432,515–528,845/852/559–562; все три находки закрыты на уровне SPEC. Код ещё не менялся, новые tests не запускались.
- No-findings justification после исправлений: все пункты исходного ревью mapped; per-key choices, recovery alternatives, skipped/failed/unknown, deletion copies и safety guards определены; каждый значимый AC имеет конкретный planned check; открытых BLOCKER/HIGH/MEDIUM нет. Это PASS плана, не runtime acceptance.
- Manual-review challenge: опасные counterexamples — одинаковые имена, relink alternatives, filtered-out selection, false success after partial/cancel, misleading surviving copy. Они отражены в negative scenarios/AC и должны быть проверены в EXEC.
- Stop decision: PASS post-SPEC по self full-loop и separate advisory/adversarial fallback; готово к exact approval. Post-EXEC не выполнен.

### Post-EXEC Review
- Реализация: сгруппированный workspace и полевой выбор, отдельные panels/setup/result, readable/technical comparison, counted filters/search, narrow navigation, deletion modal/keyboard и theme resources. Engine/API/storage/preferences schema сохранены; PreviewDiff дополнен только transient source-type flags для точного отображения строк.
- Characterization RED: connect-first преждевременно закрывал setup; три поля давали три навигационных элемента вместо двух distinct EntityId. После реализации обе проверки green.
- Scope/Evidence и Contract passes: все AC сопоставлены с actual tests/PNG ниже; captured Key/Resolution остаётся источником apply, technical versions доступны, failed/unknown/unattempted различены, recovery/deletion guards проверены.
- Separate advisory/adversarial reviewer нашёл 3 HIGH и 2 MEDIUM. Все исправлены: raw three-side columns, modal/activity/confirm guards, narrow viewport, folder readiness, scalar-vs-JSON typing. Targeted code re-review закрыл находки. Reviewer самостоятельно просмотрел technical PNG после BringIntoView, актуальные native name/photo и dark PNG, финальный native log; итоговый advisory/adversarial verdict PASS, открытых находок нет. Его sandbox остаётся unrestricted/danger-full-access: технически изолированной independent acceptance нет.
- Native визуальный проход дополнительно выявил слишком маленький viewport при repair/photo в compact. Подробное explanation перенесено в scroll, pinned repair copy сокращена; preview фото 88px полностью виден после прокрутки. Blocked comparison подписан «В папке / В Google», без обещания замены обычных полей после локального repair. Whole-contact readback подтвердил сохранение правок/фото.

| Finding | Disposition / regression |
|---|---|
| HIGH raw values | Copyable paths/Before/Local/Google + source JSON; Technical_details... + inspected technical PNG |
| HIGH background/confirm guards | Common CanConfigure/RunAsync + confirmation-only guards; direct configure/restore/confirm negatives; mutable plan disabled |
| HIGH narrow values | Filters/search остаются на list page; 820 values bounds + inspected PNG |
| MEDIUM invalid folder | Exists/policy readiness + invalid/valid connect-first test |
| MEDIUM literal JSON-looking scalar | Source-type flags; `[1,2]`/JSON-object-looking string сохраняются буквально |
| Native compact repair/photo | Details viewport вырос с 54 до 155 UIA units; inspected complete photo/field pair + integrated native readback |

### EXEC acceptance evidence

Все пути относительно репозитория, PNG/video находятся в ignored `chat-artifacts`, в продукт не включаются.

| AC | Фактическая проверка | Evidence |
|---|---|---|
| AC1 | Connect-first invalid/valid path; folder-first Authoring | `ui/20261008-200606-776/ux-setup-ready.png`, onboarding PNG |
| AC2 | same-name/different-ID, granular exact applied choices, recovery exclusivity/collision | `ui/20261008-200606-972/ux-grouped-same-name.png`, `ui/20261008-200444-326/contact-edit-upload-native.png`: 1 контакт / 5 полей |
| AC3 | Both values before choices, next conflict, invalid relink/retry sides | `ui/20261008-200608-136/ux-conflict-1000x680.png`, native conflict PNG |
| AC4 | Literal/missing/null/empty/unknown-property tests, technical three sides, photo/whole-contact readback | `ui/20261008-200609-647/ux-technical-exact-values.png`; `ui/20261008-200441-032/contact-edit-blocked-photo-values-native.png` и `...-name-values-native.png` |
| AC5 | 1180×820, 1000×680 ≥320 comparison, 820×640 list/detail/back; panels preserve context | `ui/20261008-200608-799/ux-narrow-contact-details.png`, `ui/20261008-200608-207/ux-settings-1000x680.png`, native compact |
| AC6 | counted filters/search/hidden selection/no matches; 10k items only7 realized; cancellation27ms; 2000 diff rows only4 realized | `ui/20261008-200605-489/preview-10000-virtualized.png`, `ui/20261008-200607-713/ux-search-no-matches.png` |
| AC7 | confirmed/failed/unknown/unattempted, stale apply invalidation, no-change convergence, cancellation | `ui/20261008-200607-435/ux-partial-unknown-unattempted.png`, `ui/20261008-200611-440/ux-field-only-result.png`, integrated noop PNG |
| AC8 | ordinary/restore/label destinations, threshold phrase, inline validation, Escape/Tab, direct-command guards | `ui/20261008-200609-034/ux-delete-inline-validation.png`, native keyboard/modal PNG |
| AC9 | light/dark + Tab/focus/Escape; resource contrast: supporting text6.89/8.23, indicator3.49/3.69, primary text5.63 | `ui/20261008-200608-314/ux-conflict-dark-1000x680.png`, native focus PNG; Theme_resources... |
| AC10 | Release solution0 warnings/errors; xUnit107/107 +147/147; Headless26/26; native full9/9 на итоговом исходном коде (1m19s738ms, 0 skipped) | UI HTML reports, command outputs; `chat-artifacts/ux-review/native-final.log` |

Video: `chat-artifacts/ux-review/video/before.mp4` (29.934s,2684×1924) и `after.mp4` (30s,1250×876). Связаны с автоматизированным whole-contact repair/upload scenario; просмотрены извлечённые кадры before/after и native checkpoints фото/фамилии/пяти полей/noop. До/после записаны при разных текущих monitor DPI:216 и120; это evidence поведения и layout, не равная физическая DPI-матрица. Первая after-запись не вместилась в1920×1080 — recording helper position исправлен, повторная запись успешна.

Ordinary Desktop `--demo` smoke: production project EXE, temporary workspace, подготовленный synthetic preview, без apply/Google. Demo preferences byte snapshot восстановлен в finally. Это запуск из source build, не установка/обновление/publication.

- Role review: UX/value-first/compact и доступность — self+separate fallback; domain/storage — per-field/readback/restore/recovery guards; testing — required suites и negative cases; delivery — локальные изменения без Git/public/live side effects.
- Residual boundaries: synthetic fixtures/demo и native current display; настоящий Google, установка, публикация и отдельная физическая DPI-матрица не входят в эту EXEC-приёмку. Нет изменений personal memory.
- Stop decision: PASS локальной EXEC-приёмки. Все AC выполнены, обязательные suites завершены успешно, rendered PNG и automated before/after video просмотрены; отдельный advisory/adversarial reviewer подтвердил PASS без открытых находок. Ограничения среды reviewer и live/native DPI coverage раскрыты выше.

## Approval
Получена отдельная фраза пользователя: «Спеку подтверждаю». Фаза EXEC разрешена для описанного локального scope.

## 20. Журнал действий агента
| Фаза/событие | Решение/основание | Evidence/остаток | Следующее | Решение человека | Артефакты |
|---|---|---|---|---|---|
| SPEC, 08.10.2026 | Expanded для всех рекомендаций | Код/9 PNG baseline/15 tests; wireframes и AC готовы | Separate review/disposition | «Примени все твои рекомендации»; exact approval ещё нет | Только эта SPEC |
| SPEC review завершён, 08.10.2026 | Исправлены 2 HIGH и 1 MEDIUM; targeted re-review PASS | SPEC ready; code/tests не изменены, техническая reviewer isolation недоступна | Запрос exact approval → EXEC | Ожидается «Спеку подтверждаю» | Только эта SPEC |
| EXEC начат, 08.10.2026 | Exact approval получен | Реализация/проверки всех AC впереди | Characterization, UI/presentation, validation | «Спеку подтверждаю» | Approved local scope |
| EXEC implementation + RED/GREEN | Grouping/setup/diff/results/panels/modal/theme реализованы | 2 meaningful characterization RED → GREEN; separate review 5 findings исправлены | Full validation / inspected images | В approved scope | Code/tests/README |
| EXEC native refinements | Два native failures выявили obsolete result/search transition и compact repair viewport | Scenario adapted to new result state; actual layout fixed; native full9/9 и affected3/3 PASS, video recorded | Final evidence disposition | В approved scope | PNG/MP4, ignored chat-artifacts |
| EXEC завершён, 08.10.2026 | Все AC PASS; separate advisory/adversarial fallback PASS, открытых находок нет | Release 0 warnings/errors; xUnit107+147; Headless26; final native9; ordinary demo smoke и video | Передать локальный результат пользователю | Исходное exact approval; новых side effects нет | Code/tests/README/VALIDATION/SPEC; evidence ignored |

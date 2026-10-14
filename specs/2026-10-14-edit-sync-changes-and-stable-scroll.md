# Ручная правка изменений синхронизации, сброс к Google и стабильная прокрутка

## 0. Метаданные
- Форма: Expanded, масштаб large: UI, план синхронизации и локальная запись связаны; ошибки выбора массива или устаревшего плана могут затронуть чужие сведения.
- Профили: `dotnet-desktop-client`, `ui-automation-testing`; context `testing-dotnet`.
- Владелец реализации и единственный writer: основной агент. Reviewer читает и возвращает замечания.
- Central stack: `C:\Users\Kibnet\.codex\agents\AGENTS.md`, routing-matrix, creator-vibe-lens, model-behavior-baseline, tool-execution-baseline, QUEST governance/mode, collaboration/testing baseline, spec-linter/rubric, review-loops и указанные профили. Применён установленный skill creator-vibe для interaction design.
- Шаблон: центральный `templates/specs/_template.md`. Локальных AGENTS.override.md в исходниках и проверенных родительских каталогах не найдено.
- Источники: `C:\Projects\My\ContactMirror`, main, `43624c7`, исходно clean. Текущий cwd `C:\Users\Kibnet\.codex\worktrees\749b\ContactMirrorData` содержит данные, а не приложение; рабочие данные не менялись.
- Поверхность: Codex desktop. Целевой behavior baseline: GPT-6 Astra из central stack; effective model/tier и reasoning не подтверждались, настройки не менялись. Model eval: Не применимо, меняется Avalonia-приложение.
- Ветка EXEC: отдельная `feat/sync-change-editing` от проверенной актуальной базы; checkout выбрать после approval, не использовать чужую `feat/mcp-runtime`.
- Релиз: не задан. Commit/push/merge/release/install и действия с рабочим Google не входят в текущую подготовку.

## 1. Overview / Цель
Исходное поручение: «если я выбираю изменения для синхронизации то я могу только галочку поставить или снять… возможность любое изменение в папке … подредактировать вручную прямо в интерфейсе или сбросить значение до гугловского». Второй симптом: «при раскрытии разделом или создании/удалении сведений прокрутка бросает вверх».

Success means: пользователь открывает изменение в «Синхронизации», правит локальные значения или восстанавливает выбранное поле/раздел из Google, сохраняет в папку, видит актуальное сравнение и отдельно запускает синхронизацию. При работе с длинной карточкой сохраняются место, раскрытые разделы и разумный фокус.

Output: локально работающий обычный Desktop, regression tests, просмотренные UI evidence и эта SPEC с фактическим журналом. Stop: сейчас завершить проверенную SPEC и ждать exact approval; в EXEC завершить только после обязательной проверки исходных сценариев. Runtime-проверки этой реализации пока не выполнены.

## 2. Текущее состояние (AS-IS)
- `MainWindow.axaml`: Sync details содержат `DiffRows`, readonly SelectableTextBlock и общий выбор записи. `UseGoogle` доступен для конфликтов и является выбором синхронизации, а не независимой правкой файла.
- `MainWindowViewModel`: `CheckAsync` регистрирует `_preview`; `LoadDetailsAsync` строит `PreviewDiff`; `ApplyAsync` выполняет выбранный план. Локальный contact editor через onSaved инвалидирует preview.
- `PreviewDiff.Build`: умеет отличать отсутствие/null/пустые значения, singleton leaves, произвольные массивы и разные индексы сторон. Для телефонов и других неоднозначных массивов сопоставляет только точные значения, не угадывает замену записи.
- `WorkspacePresentation.Display`: переводит сравнение в человекочитаемые строки; Left/Right зависят от направления. Это представление не годится как источник JSON для записи.
- `ContactsView` подписан на StructureChanged → RebuildCard и FieldVisibilityChanged → RebuildFields. `FriendlyContactsView.RebuildCard` создаёт новый ScrollViewer без сохранения Offset; RebuildFields заменяет его Content. Раскрытие разделов задаётся заново через ShowAllFields/RevealedPath.
- `ContactsViewModel.Mutate(..., rebuild:true)`, popup mutations, смена фото и режима вызывают перестройку. Это подтверждённый механизм потери состояния при структурных действиях. Для отдельного раскрытия Expander точная цепочка focus/layout ещё требует UI repro; причина не объявляется полностью доказанной.
- `ContactEditorStore.SaveAsync` защищает id/google/photo, folder binding, workspace/account, labels и CAS; делает backup и отличает Committed/Unchanged/Uncertain. Обойти этот контракт прямым File.WriteAllText нельзя.
- `PersonCodec.Data` и CapabilityRegistry.WritableProjection определяют writable CONTACT-значения. Plan содержит не только fields: контакты, пользовательские группы, фото, labels/starred, entity create/delete, readonly snapshot и recovery.

## 3. Проблема
Пользователь не может разрешить разницу непосредственно в сравнении, а структурная правка карточки теряет контекст просмотра. Галочка лишь откладывает операцию: локальные значения продолжают отличаться при новой проверке.

## 4. Цели дизайна
Дать обычную форму редактирования в контексте сравнения; явно показать область сброса; сохранить невыбранные сведения и рабочую позицию. Переиспользовать schema/forms, защиту черновика, local backup/CAS и существующий planner. Не расширять поддерживаемые Google-поля и не менять формат документов.

## 5. Non-Goals
Автоматическая отправка после правки; массовый сброс папки; новые Google API capabilities; миграция schema/state/preferences; редактирование id/google.person/служебных источников; произвольное угадывание соответствия телефонов; переработка всей навигации; изменение рабочих контактов агентом; доставка/установка релиза.

## 6. Предлагаемое решение (TO-BE)
### 6.1 Распределение ответственности
- Core/Application: типизированный edit target, writable projection и local patch; источник Google берётся из зарегистрированного Prepared plan, а не из DisplayRow текста или произвольного клиентского JSON.
- Infrastructure: узкий local-edit путь через существующие IWorkspaceSession primitives (lease/binding/account/CAS/backup/path/journal), общий для контактов, групп и отсутствующего локального файла. Это EXEC-уточнение заменяет отдельный вызов ContactEditorStore.SaveAsync при тех же гарантиях: protected envelope и scope проверяет registered target, запись и backup — существующая session. Remote mutation отсутствует. Существующие sync baselines не продвигаются; initial registration новой remote-only сущности отдельно описана ниже.
- Desktop: действия у SyncEntry, форма, feedback, draft protection, инвалидирование и обновление preview; contact-card presentation state для scroll/expand/focus.
- Tests: independent schema coverage, реальные временные folder read-back, recording gateway без remote writes, rendered Headless и native scenarios.

### 6.2 Детальный дизайн и visual planning artifact
Wireframes встроены в SPEC; это planning artifact, не скриншот работающей реализации.

```text
СИНХРОНИЗАЦИЯ / Анна Примерова
☑ Имя                 В Google              ⋮
  В папке: Анна        В Google: Анна-Мария
  ⋮ Изменить в папке…
    Сбросить раздел «Имя» до Google…

ПРАВКА В ПАПКЕ / Имя
Google: Анна-Мария                 По проверке 14:32
Имя [Анна                ]        [Взять из Google]
Фамилия [Примерова       ]         [Взять из Google]
Область сброса: только раздел «Имя» выбранного контакта
[Сбросить раздел до Google] [Сохранить в папку] [Отменить]
После сохранения: Сохранено в папку · обновляем сравнение…

КАРТОЧКА / РЕДАКТИРОВАНИЕ
  ... предыдущие разделы выше области видимости ...
  ▾ Организация                            ← остаётся здесь
    Должность [Руководитель]                  ⋮
    [+ Добавить запись]
  ▸ Адреса
  ... последующие разделы ...
  [Сохранить в папку] [Отменить] Не сохранено
```

Действия доступны у каждого обычного пользовательского изменения независимо от галочки и направления (Upload/Download/Conflict). Один компактный `⋮` у категории/операции вместо кнопок у каждого технического leaf; menu доступен клавиатурой и имеет accessible name. Действие ведёт в форму именно выбранного поля/раздела, не требует сначала искать контакт в таблице. Existing automation IDs сохраняются; новые задаются по entity/id/field, не по переведённому заголовку.

«Изменить в папке…» открывает изолированный черновик в контексте сравнения. Переиспользовать friendly typed forms всех 23 writable категорий, labels/starred и фото; поле/категория видно в заголовке. Остальной контакт не переписывается. Отмена и Escape не меняют файл. Dirty close/navigation использует имеющийся Save/Discard/Stay.

«Сбросить … до Google…» открывает тот же черновик с точной областью сброса и подготовленным Google-значением. Запись происходит только по «Сохранить в папку». Google означает снимок из последней успешной проверки, время показано. Старый google.person из файла не выдаётся за свежий источник. Повторная проверка до сохранения инвалидирует token; форма предлагает заново открыть/проверить, сохраняет введённый буфер для копирования.

Типизированный target фиксирует PlanId, entity kind/id, field, original snapshot hash/photo hash/binding/labels, typed raw side values с presence и версией/хешем remote, а также поколение UI. Application разрешает получение такого target только из зарегистрированного immutable plan. Мутация не авторизуется одним SyncEntry/DisplayRow или строкой JSONPath. Изменённый файл, аккаунт, папка, plan, generation либо pending/recovery state отменяют разрешение записи; visible disabled reason и recovery action обязательны.

Границы сброса:

| Вид изменения | Ручная правка | Сброс к Google |
|---|---|---|
| Все 23 `data` категории | Existing typed form, включая вложенные значения | Весь выбранный раздел; отдельный leaf через «Взять из Google» только при однозначной адресации |
| Несколько телефонов/email/адресов и дубликаты | Выбрать конкретную локальную запись в форме; можно добавить удалённую | Сброс всей категории явно подписан; для копирования одной remote записи пользователь выбирает её явно, индекс сторон не приравнивать |
| Labels/starred | Existing picker/toggle | Remote membership → local GUID; GUID в Prepared недостаточен: нужен валидный локальный файл группы. Missing dependency → явное предложение сохранить перечисленные группы в папку, либо остаться с черновиком |
| Фото | Existing picker/remove/preview | Cached GooglePhoto bytes/hash из плана; отсутствие фото означает удаление; отсутствующие bytes при наличии фото блокируют сохранение, не подставляют URL |
| User group `name/clientData` | Typed name/list form в Sync details | Только выбранное group поле; extensions/google/id сохраняются |
| Новая локальная сущность, которой нет в Google | Полная форма контакта/группы | Google-версии нет: объяснение; «Отменить локальное создание…» отдельно, через local trash confirmation |
| Новая remote-only сущность (`CreateLocal`, state/local отсутствуют) | Полная форма из remote projection; local materialization с initial trusted state | Сохранить показанную Google-версию в папку, создать initial binding/state из remote; не создать дубликат Google |
| Локально удалённая сущность с remote counterpart | Восстановить draft из remote projection и подправить | Восстановить локальный файл с прежним GUID/связью, не создавать remote entity |
| Remote entity отсутствует, local есть | Редактировать существующий local | «В Google записи нет»; удаление local только отдельным явным local-delete/trash confirmation, не маскировать под сброс значения |
| `$snapshot`, readonly Google/source, journal/recovery/blocked | Существующее исправление/восстановление и объяснение | Нет общего writable reset для служебной операции; не терять существующие recovery controls |

Удалённая отдельная категория восстанавливается через typed draft; отсутствие поля в Google означает убрать выбранное локальное поле. Отличать missing, null, пустую строку и [] в модели; хранить writable/legal эквивалент по DocumentCodec/validator, не парсить подписи «Нет поля». Leaf reset не строит частичный объект, нарушающий schema: форма показывает ошибки, до валидности save запрещён. Локальные extensions и необработанные значения вне выбранной области сохраняются без нормализации.

Remote-only materialization — отдельный явный контракт, не обычное `ContactEditorStore.SaveAsync` с отсутствующим state. GUID/resource/source/account/root фиксируются зарегистрированным plan; local файл должен оставаться отсутствующим до commit. Initial EntityState/Baseline формируется из исходной trusted remote projection, mapped memberships и remote photo hash, никогда из отредактированного draft. Local candidate может уже содержать ручную правку. На первом сохранении в существующем формате state/journal зарегистрировать связь, записать local file/photo, подтвердить оба read-back; journal/backup покрывает pre-state absence и intended file/state. При частичном file/state результате вернуть classified uncertain/recovery и заблокировать дальнейшую запись до сверки, не оставлять необъяснённый readonly всего workspace. Существующие EntityState/Baseline не перезаписывать ради local edit; восстановление удалённого local файла использует существующий trusted state. Если его нет, это materialization/recovery, а не обычный save.

После successful materialization следующий Prepare сохраняет тот же GUID и единственную связь Google: исходная remote версия становится baseline, ручная local delta показывается как обычный Upload (или штатный Conflict при новой remote delta); reset без ручной delta не порождает writable difference. `google`/metadata в новом файле генерируются из trusted remote source, не редактируются пользователем. Первичное создание state — точное исключение к запрету продвижения существующего baseline, не миграция и не запись в Google. Проверка всей папки после повторного открытия должна остаться writable вне реальных issues/recovery.

Missing local groups: список remote GUID в Prepared не доказывает `Catalog.Labels`; существующий editor блокирует такие контакты. Показать имена/число конкретных недостающих групп и предложить «Сохранить эти ярлыки в папку и продолжить». Никакого silently dropping memberships или скрытого autocreate. Подтверждение запускает только перечисленные local materialization/restore group операции из того же trusted plan, затем reread/recheck и заново валидирует target контакта; до этого контакт не сохраняется. Cancel оставляет draft и файлы неизменными. Неподтверждённая/unknown/group-source/system-group dependency не копируется как user label. При частичном успехе честно показать какие группы сохранены и что контакт ещё не записан; backup/journal/read-back и recovery действуют для каждого шага, обещания общей atomic transaction нет.

Выбран последовательный dependency flow: group commits → invalidate old plan/token → fresh Prepare → новый trusted contact target → повторное явное «Сохранить в папку» в форме контакта. Нельзя использовать старый LabelsHash либо обходить CheckBinding. Сохранить пользовательский typed buffer/invalid input/caret; перед переносом его правок в новый target проверить неизменность исходного contact raw hash/photo hash/id/account/root (для отсутствующего local — его продолжающееся отсутствие). Изменившийся contact требует обычного drift recovery, а изменившийся remote source показывается в форме с новым временем/значениями для повторного решения; автоматической записи контакта нет. Injected group success + contact failure/cancel оставляет честный partial feedback, новые группы сохранены, контакт не объявлен сохранённым, старые Apply/token недействительны.

После локальной записи старый план немедленно становится неприменимым. При активном аккаунте автоматически выполнить один новый Prepare (read-only Google); UI не блокируется. До результата Apply выключен. Вернуть entity/field, поиск, фильтр и прокрутку сравнения; исчезнувшее изменение отмечается «Значение в папке совпадает с Google», выбран ближайший сосед.

Сохранить choices остальных записей только при совпадении ключа и signature kind/before/local/google/photo/recovery. Для изменённой записи сохранить исходное selected/unselected при обычном безопасном изменении; новый конфликт/удаление требует существующего явного выбора/подтверждения. Новые посторонние операции не выбирать автоматически из-за локальной правки. Ошибка fresh Prepare не откатывает состоявшуюся локальную запись: показать «Сохранено в папку. Не удалось обновить сравнение» и кнопку повторной проверки; старый план остаётся выключенным. Ни save, ни reset не вызывает Apply или gateway create/update/delete/photo/membership mutation.

Стабильная прокрутка: presentation state живёт на время выбранного контакта/root и отдельно для view/edit, не в JSON/preferences. Сохранить текущий визуальный anchor, его смещение от верхней границы scroll viewport, Offset fallback, раскрытые разделы, фокус и caret/selection при существующем input. При изменении того же контакта восстановить после layout и реализации virtualized rows, проверяя generation/root/id; устаревший Dispatcher callback ничего не делает.

Anchor определяется действующей записью/контролом, не одним индексом массива: на UI-время дать row token; после удаления выбрать следующий, затем предыдущий в том же разделе, затем заголовок. После вставки выше viewport сохранить прежний видимый anchor. При добавлении через кнопку показать новую запись с минимальным scroll, фокус в её первом поле; не переходить в начало карточки. При expand/collapse сохранять заголовок/область действия, прокрутка допускается только для доступности цели и clamp на уменьшенной высоте. Не вызывать BringIntoView общего header после каждой перестройки.

Сохранить ScrollViewer и обновлять затронутую область, где это возможно; если полная перестройка необходима, явно capture/restore state. Обычный ввод не пересоздаёт input на каждый символ. Search/finder/error navigation продолжает целевой reveal/focus и имеет приоритет над старым anchor. Смена контакта/root начинает собственный state и не наследует позицию/раскрытие чужой карточки. Layout shrink/modal/type/date/photo actions не отбрасывают dirty/invalid buffers.

Performance: capture/restore ограничен текущей карточкой и реализованными контролами; не обходить все контакты и все нераскрытые virtualized rows. Remote Prepare/file IO выполняются вне UI thread. Проверить длинную категорию с 2000 записями; не заявлять выигрыш скорости без замеров.

Video plan: failing/repro scroll BEFORE и passing AFTER из автоматизированного native run с synthetic fixture, existing `NativeWindowVideo`; AFTER для нового edit/reset flow. Для edit/reset BEFORE — отсутствие действий в существующем comparison flow. Если recorder/native desktop недоступен, указать конкретный отказ, команду и next-best rendered PNG/Headless assertions; такой fallback не превращает непроверенную native работу в native PASS.

### 6.3 User-Observable Scenarios
| Scenario | Trigger | Видимый результат | Evidence | AC |
|---|---|---|---|---|
| Исправить значение перед отправкой | Выбрать difference → ⋮ → edit → save | Новый local файл, новое сравнение, Google не изменён | UI + store read-back + recording gateway | AC1/3/5 |
| Отказаться от локальной правки | Reset selected category → save | Local равен показанному Google, соседние данные сохранены | UI + canonical projection compare | AC2/3 |
| Телефоны/даты/ярлыки/фото/группы | Редактировать и reset разные типы | Typed controls и точная область действия, фото по содержимому | Coverage fixtures + rendered flow | AC1/2/4 |
| Удалённая запись | Restore local draft / absent counterpart | Явный recreate local или отсутствие reset, без remote create/delete | Gateway count + folder/trash checks | AC4/5 |
| Expand/add/delete внизу карточки | Длинная карточка, actions below first viewport | Остаётся раздел, открытые блоки и разумный фокус | RED→GREEN UI + inspected paired video | AC6/7 |
| Drift/offline/cancel | Смена файла/plan, ошибка Prepare, Escape | Draft не теряется, save/apply защищены, честный feedback | Negative integration/UI | AC3/5/8 |

### 6.4 State / Interaction Matrix
| State | Trigger | Transition | Error/concurrent case |
|---|---|---|---|
| Fresh preview | Edit/reset | Owned target + draft | Busy/recovery/readonly → disabled reason |
| Draft | Cancel/Escape | Original preview, no write | Dirty → existing Save/Discard/Stay |
| Valid draft | Save | CAS + backup → committed → fresh Prepare | File/plan drift → no overwrite; buffer remains |
| Invalid draft | Save | Stay, inline errors/reveal | No write/no remote calls |
| Committed | Prepare fails | Local saved; preview stale, Apply disabled | Retry Prepare, не повторять запись автоматически |
| Structural action | Layout complete | Restore anchor/expand/focus | Deleted anchor → сосед/header; stale callbacks ignored |
| Different contact/root | Selection | New presentation context | Не применять состояние старой карточки |

### 6.5 Decision Ledger
| Решение | Owner | Выбрано | Confidence | Риск | Needs user before EXEC |
|---|---|---|---|---|---|
| Источник reset | agent | Последняя successful Prepare projection + показанное время | 0.98 | Stale remote → token invalidation и recheck | Нет |
| Сохранение reset | agent | Draft → явное Save in folder | 0.96 | Лишний шаг, но можно проверить область и отменить | Нет |
| Массивы | agent | Category reset; explicit remote row copy, без guessed matching | 0.99 | Category reset затронет соседние записи → scope label | Нет |
| Расположение действий | agent | ⋮ у категории/операции и focused typed form | 0.95 | Discoverability → accessible menu + UI test | Нет |
| Scope «любое изменение» | agent | All writable contact/group/photo/metadata-free fields + ordinary entity lifecycle; recovery readonly сохраняется | 0.93 | Hidden special entries → inventory coverage | Нет |
| Реализация | user | Exact SPEC gate | 1.0 | Преждевременная мутация | Да: только «Спеку подтверждаю» |

Approval — ожидаемая фазовая граница, а не недостающий product-design выбор; открытых решений о результате нет.

### 6.6 Runtime / Config / Data Contract Matrix
| Область | Source of truth | Изменение | Совместимость | Проверка |
|---|---|---|---|---|
| Editable data | Schema/CapabilityRegistry/DocumentCodec | Only selected subtree or explicit entity lifecycle | Same schema/id/extensions | Coverage and read-back |
| Google source | Registered Prepared plan / PersonCodec | Typed edit target, snapshot time/hash | No raw source editing | Stale/forged target negative tests |
| Local storage | ContactEditorStore + workspace lease/state | Narrow local adapter for groups/restore/materialization | Existing baseline unchanged; initial remote-only state из trusted remote; no schema migration | CAS/account/path/uncertain + reopen/next-Prepare checks |
| Photo | Plan bytes/hash + existing photo store | Content reset/picker | Same files/path limits | Content hash/reopen |
| Presentation | ContactsView current selection | Ephemeral row/anchor/expanded/focus state | No pref/schema migration | UI generation/context tests |

## 7. Бизнес-правила / Алгоритмы
Галочка определяет участие в будущей синхронизации; reset/edit меняет local draft и сохраняется отдельно. Reset не выбирает UseGoogle и не отправляет автоматически. Нет Google counterpart ≠ пустое поле; entity absence требует отдельного local-delete действия. Service metadata immutable. Preserve local GUID, extensions, unknown out-of-scope fields и системные memberships. Existing sync baseline не продвигается в local-save/reset; новый remote-only binding получает initial baseline только из trusted remote projection. Entity lifecycle требует backup/trash/journal и существующих drift/recovery guards.

## 8. Точки интеграции и триггеры
`MainWindow.axaml`/VM/WorkspacePresentation связывают menu с typed target. New local-edit application operation получает trusted Prepared values, local snapshot и activity lease. ContactsViewModel save/onSaved invalidates preview; автоматический Prepare возвращает контекст по entity/field/signature. ContactsView StructureChanged/FieldVisibilityChanged и все Expander transitions используют единый scoped presentation state. Finder/error навигация передаёт явную цель восстановления.

## 9. Изменения модели данных / состояния
Новые edit-target/patch/result contracts и in-memory UI state; schemaVersion/contact/group/SQLite schema/preferences не меняются. Существующие baseline values сохраняются; new remote-only materialization добавляет initial EntityState в существующем формате с remote baseline, как описано §6.2. Время Google снимка/remote identity/hash — calculated/captured из существующего Prepared. Backups/trash/journal используют совместимые существующие форматы; не стирать историю. New group adapter не становится универсальным API редактирования Google.

## 10. Миграция / Rollout / Rollback
Первый запуск не пишет файлы автоматически. Рабочие datasets/OAuth не использовать для тестов; временные synthetic folders и fake/recording gateway. Source rollback возвращает текущий Desktop без миграции; пользовательские local saves восстанавливаются из backup/trash с CAS и отдельным явным действием. Перед EXEC перепроверить SHA/clean state и новый checkout. Installed rollback/release здесь не выполняется.

## 11. Тестирование и критерии приёмки
Полный набор обязателен: меняются shared local writes и registration/preview integration, область включает несколько modules. Expected RED scroll должен быть создан и воспроизведён до fix; отсутствие UI repro описывается честно.

### Acceptance-to-Test Matrix
| AC | Automated test | Manual/visual/log check | Evidence artifact | Фактический статус EXEC |
|---|---|---|---|---|
| AC1: edit открыт у каждого ordinary writable SyncEntry независимо от checkbox/direction; все 23 категории + labels/starred/photo/group name/clientData | Schema-derived inventory, Core/Headless typed-form routes | Rendered selected form, keyboard menu at 1000×680 и 820×640 | TRX/PNG/AFTER video | PASS: см. AC matrix в audit |
| AC2: reset exact shown Google projection в выбранной области, presence/duplicates/type/date/custom/photo byte-content корректны; out-of-scope unchanged | Core projection/patch, Google/storage read-back; missing/null/[]/empty, 0/1/N/duplicate/reordered arrays | Scope captions и removed-field restore | Test log + canonical before/after | PASS: см. AC matrix в audit |
| AC3: после save старая PlanId не применяется; fresh Prepare показывает новые значения, choices unaffected preserved, old error/offline не undo local save | VM/planner integration with recording gateway + local reopen | Save status/new preview/retry/no auto Apply | TRX + AFTER UI | PASS: см. AC matrix в audit |
| AC4: local create/delete, remote-only CreateLocal/materialization, group name/clientData, photo и missing counterpart имеют полный явный flow; trusted source/state/GUID сохраняются; remote-only label dependencies явно подготовлены или cancel | Inventory EntityKind/ChangeKind/field; new remote contact/group state=null, missing local label fixture; file/state partial failures; group commit→fresh target→contact failure/cancel/drift, restore/trash tests | Next Prepare: same GUID, manual delta Upload/no reset delta, no duplicate/неожиданный whole-folder readonly; changed LabelsHash не обходится, буфер и truthful partial result сохранены | Temp-folder/state log + PNG | PASS: см. AC matrix в audit |
| AC5: Save/reset/cancel вызывают 0 Google mutations; no CAS/identity/account/path/protected-field regression; existing baseline unchanged, initial new binding строго remote-derived; uncertain не теряет draft | Full Core/Google suites, throw-on-write gateway, forged/stale target, state-init crash/partial tests | Backup/journal/read-back после reopen; existing state hashes unchanged, only expected initial states added | TRX/hash log | PASS: см. AC matrix в audit |
| AC6: expand/collapse/add/delete/field visibility/photo/type/date в длинной карточке не бросают к началу; раскрытие других разделов сохраняется | Actual controls RED→GREEN Headless with Offset>0; anchor Y tolerance ≤8 DIP when old anchor survives/no clamp | Inspected BEFORE/AFTER native video same synthetic fixture | TRX + paired MP4/PNG | PASS: см. AC matrix в audit |
| AC7: удаление/insert-before меняют anchor предсказуемо, focus/caret/input buffers не теряются; finder/error reveal работает; смена id/root игнорирует old callbacks | Headless virtualized rows + keyboard/navigation + generation tests | Actual focused control, viewport visible, long 2000-row category | TRX + rendered checkpoints | PASS: см. AC matrix в audit |
| AC8: invalid/dirty/offline/readonly/recovery/plan refresh/concurrent save показывают recovery, не портят файл; UI обычного EXE пригоден в light/dark wide/narrow | Existing popup/update/editor safety + new negative workflows; build/full UI suites | Ordinary Desktop EXE smoke on temporary folder, light/dark and focus/overlap | Build log/TRX/inspected PNG/video | PASS: см. AC matrix в audit |

Команды EXEC из source checkout (выполнять последовательно для общего output):
```powershell
dotnet build ContactMirror.sln -c Release
dotnet test tests/ContactMirror.Tests -c Release
dotnet test tests/ContactMirror.GoogleTests -c Release
dotnet run --project tests/ContactMirror.UiTests.Headless -c Release -- --maximum-parallel-tests 1
dotnet run --project tests/ContactMirror.UiTests.FlaUI -c Release -- --maximum-parallel-tests 1
dotnet tool run appautomation doctor --repo-root . --strict
git diff --check
```
Core/Google используют xUnit, UI executables — TUnit: targeted selection через repository-proven `--treenode-filter`. Точные новые имена/фильтры записать при добавлении tests. Video files local-only: `chat-artifacts/ui/sync-change-editing/scroll-before.mp4`, `scroll-after.mp4`, `edit-reset-after.mp4`; не коммитить generated evidence по умолчанию. Проверить файлы/кадры, а не только факт создания.

Visual acceptance: readable scope and source time, кнопки без наложений, action menu keyboard reachable, сохранённый открытый раздел внизу карточки. Native/Headless/build/synthetic Google и реальный Google — разные границы. Реальный аккаунт и установленное приложение не проверяются текущим локальным EXEC. Stop после full green, закрытых AC и post-EXEC review; новые широкие прогоны только при новых изменениях/failure/risk.

## 12. Риски и edge cases
Смещение индексов/duplicates → UI row tokens, category scope, explicit remote record choice. Stale Prepared/remote snapshot → token guard + explicit time + invalidation. Unknown/raw values → preserve вне scope, schema validation выбранного subtree. Недостающие labels/photo → объяснение и recheck, без lossy copy. Group delete с local memberships → проверка dependencies, не каскадить молча; отдельное действие не может оставить невалидные ссылки. Modal/focus BringIntoView и виртуализация → actual UI callbacks + generation и anchor tests. Backup/commit failure → existing classified outcome, no false success.

### Expected User Review Objections
| Вероятное замечание | Причина | Решение | Статус |
|---|---|---|---|
| «Снял галочку, но правка возвращается» | Choice ≠ local reset | Reset сохраняет выбранное поле в папку, fresh Prepare | mitigated |
| «Сбросил один телефон, пропали остальные» | Неоднозначное соответствие массивов | Явная подпись category reset + explicit row copy, no guessed matching | mitigated |
| «Это только имя, а фото и ярлыки?» | Много специальных типов | AC1/2/4 inventory охватывает all writable + photo/groups | mitigated |
| «Открыл раздел, меня снова унесло вверх» | Пересоздание scroll и focus | AC6/7 реальные control flows/anchor/paired evidence | mitigated |
| «Правка сама ушла в Google» | Recheck легко спутать с Apply | Local save → read-only Prepare; 0 remote mutation assertion | mitigated |
| «Google уже изменился» | Reset source stale | Время снимка/token guard и явная проверка; актуальность до последующего Apply защищает существующий planner | mitigated |

Rework Prevention: исходные оба сценария сохранены; каждый имеет AC/evidence; принятые решения и scope reset указаны; objections закрыты планом; обязательные роли review применимы; AC — наблюдаемый результат; обычный runtime и original-scenario verification предусмотрены.

## 13. План выполнения
После approval подготовить изолированный source checkout и подтвердить baseline scroll repro. Затем получить typed targets/projections/local-write contracts; интегрировать формы/edit/reset и безопасное обновление плана. Presentation-state fix зависит от воспроизведения, независим от remote adapters. После targeted tests — full mandatory suites/build/ordinary runtime/render/video и full post-EXEC review. Каждый выполненный результат и отказ фиксировать; до approval code/tests не менять.

## 14. Открытые вопросы
Блокирующих product-design вопросов нет. Неизвестная точная причина отдельного Expander jump — задача executable characterization в EXEC; не основание считать исправление готовым. Exact approval ещё не получено.

## 15. Соответствие профилю
План предусматривает background IO, stable automation IDs, meaningful regression tests, обязательные build/test/UI suites, wireframes, actual inspected PNG и paired native recording. Невозможный native capture фиксировать с объективной причиной/fallback; green Headless не выдавать за native PASS. Separate reviewer должен проверить план local-write safety и actual user workflows.

## 16. Таблица изменений файлов
Пути предварительные; допустимы узкие new partials/adapters/tests в том же scope без unrelated cleanup.
| Файл/модуль | Изменение | Причина |
|---|---|---|
| MainWindow.axaml/VM/WorkspacePresentation | Menu, typed target route, draft modal, feedback/refresh context | Правка непосредственно из сравнения |
| Application SyncCoordinator/local edit contracts | Trusted Prepared projection/export and invalidation | Не доверять presentation payload |
| Infrastructure Contacts/local edit adapter | Contact reuse + group/absent-file CAS/backup operations | Full ordinary change coverage |
| ContactsView/FriendlyContactsView/FriendlyContactCard/ContactsViewModel | Scoped anchor/expand/focus integration, forms reuse | Stable scrolling и draft continuity |
| Core/Google/Headless/FlaUI tests | Projection/safety/lifecycle/actual UI regressions | AC1–8 |
| Эта SPEC | Approval/evidence/review/log | Auditable result |

## 17. Было → стало
| Область | Было | Стало |
|---|---|---|
| Difference | Readonly + checkbox | Direct focused edit/reset в папку |
| Отказ от правки | Откладывается галочкой | Явное восстановление выбранного поля из Google |
| После save | Preview invalid | Safe fresh preview + retained context/choices |
| Structural card edits | New scroll без state | Stable anchor/open sections/focus |

## 18. Альтернативы и компромиссы
Переход к общей карточке вместо focused edit дешевле, но теряет выбранную разницу и не даёт group flow; можно reuse её controls, не весь маршрут. Применить существующий UseGoogle через Apply — смешивает local correction с remote work и другими selected entries, поэтому не подходит. Сохранять только Offset просто, но insert/delete выше viewport и virtualized rows смещают смысловое место; выбран anchor с Offset fallback. Automatic guessed array matching экономит один выбор, но может сбросить другой телефон; выбран явный category scope/record choice.

## 19. Результат quality gate и review
### SPEC Linter Result
| № | Статус | Основание |
|---|---|---|
| 1 | PASS | §1 оба исходных сценария и outcome |
| 2 | PASS | §2 actual source/clean SHA; runtime inference отделён |
| 3 | PASS | §3 отсутствующий control и потеря контекста |
| 4 | PASS | §4 typed forms, scope, compatibility |
| 5 | PASS | §5 no automatic upload/schema migration/delivery |
| 6 | PASS | §6.1 responsibilities |
| 7 | PASS | §8 callbacks, target/plan/local-save boundaries |
| 8 | PASS | §6.2/7 presence/projection/CAS/identity |
| 9 | PASS | §6.4/12 draft/error/stale recovery |
| 10 | PASS | §6.2 current card/virtualization/background IO |
| 11 | PASS | §9 ephemeral state/new typed contracts |
| 12 | PASS | §9/10 existing data format/no migration |
| 13 | PASS | §10 source rollback/local backups |
| 14 | PASS | AC1–8 observable and measurable |
| 15 | PASS | §11 independent oracle/negative/UI/read-back |
| 16 | PASS | §11 explicit commands/evidence/stop |
| 17 | PASS | §13 baseline/contracts/UI/full gates dependencies |
| 18 | PASS | §6.5/14 chosen defaults, exact approval outstanding |
| 19 | PASS | §0 Expanded/large multi-module storage/plan risk |
| 20 | PASS | §15 desktop/UIA acceptance and boundaries |

### SPEC Rubric Result
| Критерий | Балл | Основание |
|---|---|---|
| Цель/границы | 5 | Both original pains, all writable coverage/service exclusions |
| AS-IS | 5 | Actual source paths/events/guards/projections inspected |
| Дизайн | 5 | Wireframes/typed targeting/reset scope/presentation state |
| Safety/rollback | 5 | No remote writes/baseline advance, CAS/backup/trash/token |
| Проверяемость | 5 | AC inventory/read-back/actual UI/paired evidence/negative paths |
| Автономность | 5 | Default decisions/staged plan/commands/no design blockers |
Self 30/30 относится к плану, не runtime acceptance.

### Role-Based Review Result
| Роль | Applicability | Review question | Verdict | Required spec changes |
|---|---|---|---|---|
| Domain workflow | applicable | Reset действительно меняет выбранные local values и не трогает соседей? | PASS плана | Arrays/groups/entity absence и dependencies проверены |
| UX/designer | applicable | Действия discoverable, scope/source видны, рабочее место сохраняется? | PASS плана | Wireframes/source time/scope/focus/draft/recovery проверены |
| Tester | applicable | Original scenarios/all writable types/negative states доказуемы? | PASS плана | AC1–8 + read-back/partial writes/actual controls имеют evidence plan |
| Architect | applicable | Prepared/patch/local-write boundary не доверяет presentation JSON? | PASS плана | Trusted target/local adapter/initial registration exception coherent |
| Delivery/security | applicable | CAS/account/baseline/no remote write/rollback защищены? | PASS плана | Identity/account/root/CAS/journal/uncertain/0 remote writes определены |

### Post-SPEC Review
Статус / stop decision: PASS плана после separate review и targeted re-review; готово к exact approval. Scope reviewed: эта SPEC; central owner stack/шаблон/linter/rubric/review; desktop/UIA профили; перечисленные §2 source/modules/tests; open questions §14 и planned changed files §16.
Self Scope/Evidence: confirmed readonly fields, preview invalidation, array semantics, recreate ScrollViewer и existing local guards. Contract: исходный edit/reset/scroll scope → AC1–8, no delivery/live data authority. Adversarial: duplicates/reorder, missing/null, stale target, protected google, plan refresh vs local write, photo bytes, group dependencies, deleted anchor и stale callback. Role-Based pass: separate review требуется до PASS по large/multi-module central owner.
Findings self: scope category reset явно подписан; local-only adapter вместо общего Apply; «Google отсутствует» отделено от empty; подпись времени добавлена. Fix/re-review: эти counterexamples сверены с §6/7/11/12.
Depth checklist: unrelated source/data changes отсутствуют; AC/evidence перечислены; runtime claims не делаются; docs/changelog/delivery вне scope; regression и hidden UX/storage contract отмечены; manual challenge — «почему нельзя смешать Left/Right/индексы и применить старый plan после save?» закрыт typed source/token/signature правилами.

Отдельный `spec_review` выполнил Scope/Evidence, Contract, Adversarial, Role-Based, Fix/re-review и Stop. Прочитаны actual revised SPEC, central AGENTS/quest-mode/review-loops/desktop/UIA owners, SyncCoordinator, CapabilityRegistry, PersonCodec, DocumentCodec, ContactEditorStore/contracts, Desktop VM/card/scroll и existing UI evidence harness. Проверены HEAD43624c7, source status только SPEC и clean data cwd. Reviewer работал в writable danger-full-access: это отдельный procedural advisory/adversarial review, без технической read-only изоляции; edits/tests/build/runtime/external writes не выполнял. Ограничение не скрывается под claim независимого read-only audit.

| Severity | Area | Finding | Required action | Status |
|---|---|---|---|---|
| HIGH | Initial state | Remote-only CreateLocal не покрывался восстановлением; bound file без state переводит workspace в readonly | §6.2 explicit materialization + remote-derived initial state + AC4/5 reopening/next-Prepare/no duplicate/partial outcome | Fixed, targeted re-review PASS |
| HIGH | Labels dependency | Mapped remote GUID не доказывает writable Catalog.Labels; missing local group блокирует контакт | §6.2 listed local group preparation, no implicit autocreate/drop, cancel/partial feedback + AC4 | Fixed, targeted re-review PASS |
| MEDIUM | Dependency sequencing | Group commit меняет LabelsHash; старый contact snapshot/target невалиден | §6.2 sequential invalidate/fresh Prepare/new target/explicit Save; preserve buffer, original contact/photo/account/root guards; AC4 partial/cancel/drift | Fixed, targeted re-review PASS |

Fix/re-review: reviewer перечитал обновлённые materialization/dependency paragraphs и связанное изменение §6.6/7/9/AC4/5. Открытых actionable findings нет. No-findings justification после fixes: initial remote-only binding не использует edited draft как baseline; labels не теряются; новое target после dependency не обходит CheckBinding; остальные source/presence/photo/array/scroll callbacks имеют explicit invariant и meaningful evidence. Scope/AC/scenarios/decisions/objections/validation план заполнены; unsupported runtime/performance claim отсутствует; schema migration/unrelated cleanup не добавлены. Дополнительный manual challenge «remote GUID есть, local group нет; group сохранена, contact отменён» закрыт truthful partial result и новым target.

Needs human: только exact approval перехода в EXEC. Residual evidence boundary: actual partial-write/reopening, scroll RED→GREEN и rendered/native flow ещё предстоят; PASS плана не доказывает рабочую реализацию. NativeWindowVideo и длинные Headless fixtures подтверждены существующими исходниками. Пять ролей подтвердили PASS плана; обязанности EXEC сохраняются.

### Post-EXEC Review
Статус: PASS локального EXEC. Выполнены implementation, full source review, fix/re-review, обязательные suites и визуальная проверка. Подробная evidence/disposition и AC→actual tests — [audit](../docs/SYNC-CHANGE-EDITING-VALIDATION.md).

Scope reviewed: approved SPEC, tracked diff и новые untracked partials/tests, immutable target authority, binding/session/CAS/journal/recovery, draft lifecycle, typed forms, popup row identity, runtime logs и rendered PNG. Root — единственный writer; отдельный reviewer процедурно read-only в writable sandbox, без технической изоляции. Self Scope/Contract/Adversarial/Role-Based review и отдельный source re-review не выявили unrelated code/schema/data changes.

Fix/re-review: исправлены transient remote GUID/resource remap, restoration google envelope, private entry authority, binding hash pin, active editor/busy-close/idle lifetime, choices/resolutions и label delta retention, invalid input reset, missing full-entity fields, surviving row identity через popup clone и обновление Google bitmap после dependency refresh. Дополнительный manual challenge «первая запись файла без групп, затем отказ initial state» закрыт HasRecoveryFlag и verified journal recovery. Отдельный targeted source re-review findings — PASS. Native harness получил адресованный UIA ввод и observable status wait после диагностированной потери символов; assertions не ослаблены, отдельный reviewer это подтвердил.

Validation: Release build 0 warnings/0 errors; Core174/174; Google/storage185/185; Headless100/100; Native15/15 включая ordinary Desktop EXE --demo; schemas generation/check PASS; strict doctor0/0; tracked и новые untracked files whitespace checks PASS (только informational LF→CRLF предупреждения Git). Headless и native failures промежуточных прогонов разобраны, исправлены и перепроверены; они описаны в audit, финальные обязательные suites зелёные.

Visual acceptance: inspected 820/1000 Light/Dark формы, native edit/reset PNG, BEFORE/AFTER ключевые кадры. Paired HWND-only MP4 на одинаковом 3868×2080 fixture: scroll-before.mp4 6.6s, scroll-after-190033.mp4 6.0s; edit-reset-after-190044.mp4 5.4s, все5fps. Baseline scroll26.03778%→0 воспроизводит исходный дефект; финальная версия26.03778%→37.72947% с фокусом новой строки. Пути — audit. Непрерывный просмотр каждого кадра не заявляется.

| Роль | Итог EXEC | Основание |
|---|---|---|
| Domain workflow | PASS | Локальные edit/reset/materialization/dependencies, existing baselines неизменны, 0 live remote mutations |
| UX/designer | PASS | Scope/time/source и compact ⋮, actual forms/scroll/focus, inspected wide/narrow Light/Dark |
| Tester | PASS | AC1–8 matrix, full174/185/100/15, RED→GREEN, negative/fault/actual-popup/photo-byte fixtures |
| Architect | PASS | Private registered authority, bounded session/CAS/journal recovery, identity-preserving guarded mutation |
| Delivery/security | PASS локально | Backup/binding/account/readonly guards, schema compatibility; installation/release/live Google за пределами EXEC |

Depth checklist: approved scope соблюдён; unrelated changes и schema migration отсутствуют; comments и README актуальны; no hidden API/operations contract changes; initial remote-only registration — явное согласованное исключение; неподтверждённых performance claims нет. Manual challenge проверил реальные popup mutation, first-state crash и смену source photo между Prepare. Открытых actionable findings нет. Stop: локальная реализация завершена; commit/push/merge/release/install и рабочие данные не затронуты, внешний OAuth/Google не тестировался.

Отдельный `spec_review` после чтения финальных logs174/185/100/15 и actual source/docs подтвердил полный post-EXEC PASS по всем пяти ролям, Scope/Evidence, Contract, Adversarial, Fix/re-review, Depth и Stop. Reviewer реально просмотрел Light/Dark phone forms, final native reset и ordinary EXE PNG; тесты/команды/внешние действия им не выполнялись, технической read-only изоляции не было. Interpretation MP4 ограничена key frames/assertions/logs, не непрерывным просмотром.

## Approval
Получено exact approval «Спеку подтверждаю» 14.10.2026. Разрешён локальный EXEC этого scope; отдельная доставка/публикация/установка и изменение рабочих данных агентом не разрешены.

## 20. Журнал действий агента
| Фаза/событие | Решение/основание | Evidence/остаток | Следующее | Решение человека | Артефакты |
|---|---|---|---|---|---|
| SPEC, 14.10.2026 | Проверить source отдельно от data cwd; подготовить full scope | Source main43624c7 clean; inspected VM/UI/codec/store/tests; scroll mechanism code-confirmed, Expander runtime unverified | Separate reviewer/fixes/review | Исходное поручение, exact approval ещё нет | Только эта SPEC |
| Post-SPEC, 14.10.2026 | 2HIGH/1MEDIUM исправлены; отдельный advisory reviewer targeted re-review PASS | Explicit CreateLocal state-init/dependencies/fresh target; all5roles/depth/stop PASS плана; source/data не менялись | Показать SPEC и запросить exact approval | Exact approval ещё не получено | Только эта SPEC |
| EXEC начат, 14.10.2026 | Изолированный checkout `C:\Projects\My\ContactMirror-sync-change-editing`, ветка feat/sync-change-editing, база43624c7 | SDK10.0.402; root единственный writer; Structural_card_change test воспроизвёл Offset=0 после add; BEFORE PNG сохранён | Targeted scroll fix, typed edit/registered-plan/local writes, mandatory acceptance | «Спеку подтверждаю» | SPEC/code/tests и ignored evidence |
| EXEC implementation/review | Все ordinary writable edit/reset, session-backed local CAS/backup/recovery, fresh preview/choices, stable rows/viewport | Separate advisory review нашёл boundary defects; fixes и regression tests проверены повторно | Full validation | В пределах approval | Source/tests/README/audit |
| EXEC validation/final, 14.10.2026 | Все обязательные проверки PASS; промежуточные automation failures разобраны | Build0/0, Core174, Google185, Headless100, Native15, doctor/schema PASS; inspected PNG/paired MP4; data cwd clean | Локальная работа завершена, delivery не выполняется | Дополнительного решения не требуется для локального результата | Audit и ignored chat-artifacts |
| Отдельное Git delivery поручение, 14.10.2026 | Commit/rebase/push/PR merge разрешены текущим сообщением | Fetch/ls-remote: актуальный main43624c7 совпадает с исходной базой; tested product tree без новых upstream изменений | Rebase, PR и проверка merge receipt; без release/install/data writes | «Перебазируй на актуальный мейн и влей» | Git history и GitHub PR |

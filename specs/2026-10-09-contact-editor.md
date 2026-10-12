# Полноценный редактор контактов: таблица и карточка

## 0. Метаданные
- Фаза: EXEC завершён, post-EXEC PASS (exact approval получено), форма Expanded, масштаб large: новые UI-переходы, локальная запись JSON/фото, сохранение настроек, несколько модулей.
- Профили: dotnet-desktop-client, ui-automation-testing; context testing-dotnet и visual-feedback. Central stack: creator-vibe-lens, model-behavior-baseline, tool-execution-baseline, quest-governance/mode, collaboration/testing-baseline, spec-linter/rubric, review-loops. Применён creator-vibe; визуальная ясность и ежедневная работа важнее технического JSON-интерфейса.
- Владелец: основной агент; reviewer только читает. Effective runtime: текущая Codex desktop session, точный model ID инструментами не подтверждён. Model eval baseline не применим: меняется приложение.
- База: `d5585c8`, выпущенная 0.3.0. EXEC выполнен в отдельной рабочей ветке `feat/contact-editor`; существующий релиз не изменён.
- Поручение: «Добавь полноценный редактор контактов: настраиваемая таблица с фильтрами и сортировкой + карточки контактов с возможностью посмотреть и редактировать все поля включая фотографию».
- Разрешение этого поручения: подготовка SPEC; код — после нового exact approval. Ранее полученное разрешение публикации0.3.0 не распространяется на будущий релиз редактора. Настоящие контакты/Google/установленная программа не используются для проверки.
- Связи: `docs/FORMAT.md`, `docs/VALIDATION.md`, SPEC UX0.3.0, `CapabilityRegistry`, `people-schema.json`, `FileWorkspaceStore`, `ApplicationActivity`.

## 1. Overview / Цель
В ContactMirror можно просматривать всю локальную адресную книгу, настроить таблицу под свою работу, находить контакты и редактировать их в полноценной карточке без внешнего JSON-редактора.

Outcome contract:
- Таблица содержит контакты выбранной папки, включая записи без предложенных изменений синхронизации. Google-снимок не является отдельной редактируемой адресной книгой.
- Пользователь находит контакт, открывает карточку, меняет любые поддерживаемые пользовательские поля и фотографию, сохраняет; после повторного открытия видит те же значения.
- Сохранение означает запись в локальную папку. Отправка в Google происходит только после обычных ручных «Проверить изменения» и «Применить». Новые записи, удаления и очистка полей не отправляются незаметно.
- Результат EXEC: работающий интерфейс, Application/Infrastructure contract, meaningful tests, обновлённая документация и просмотренные visual/video artifacts. Макет или JSON textarea вместо редактора не закрывают задачу.
- Stop: все AC ниже и post-EXEC PASS. Approval/installer/release/настоящий Google — отдельные границы; открытые обязательные AC нельзя переносить в follow-up.

## 2. Текущее состояние (AS-IS)
- MainWindow показывает сгруппированный план изменений, но не каталог всех контактов. `Новый файл` создаёт шаблон; «Все поля» открывает внешний файл.
- `ContactDocument` содержит `data`, `labels`, `starred`, `photo`, `extensions`, `google`, UUID и версию. Известны 23 writable categories; nested schema встроена в Core. Служебные поля PROFILE/metadata и неизвестные Google-поля доступны только для чтения.
- `DocumentCodec` запрещает неизвестные envelope keys, недопустимые типы и null внутри editable values. `CapabilityRegistry.ValidateEditable` проверяет schema/readOnly, singleton и частичные даты. Списки сравниваются как мультимножества: порядок не меняет смысл, повторения сохраняются.
- `IWorkspaceSession.WriteAsync` уже проверяет ожидаемые JSON/photo hashes, сохраняет исходный путь, проверяет UUID, изображения, выполняет CAS replace с сохранением displaced версии. Фото content-addressed; исходное фото не перезаписывается.
- `FileWorkspaceStore.OpenAsync` привязывает папку к AccountIdentity, блокирует другую identity, держит workspace.lock, читает SQLite/файлы; его нельзя использовать для неподтверждённой смены владельца папки.
- `ApplicationActivity` сериализует sync/config/restart. DesktopPreferences сейчас содержит Folder/OAuthPath. Текущий close guard знает о busy, но не о несохранённой карточке.

## 3. Проблема
Пользователь может синхронизировать все поля контакта, но для их изменения должен вручную редактировать JSON; отсутствуют каталог, настраиваемое представление и безопасный черновик карточки.

## 4. Цели дизайна
- Быстрая таблица и удобная карточка; действия с папкой и с Google ясно различаются.
- Typed controls для всех writable leaf properties, а не только имени/телефона/email. Сохранение неизвестных сведений без потери.
- Один черновик выбранного контакта, явное сохранение, защита при смене контакта/вкладки/папки/аккаунта и закрытии.
- Shared validators/atomic writes/photo safety вместо второго незащищённого пути записи.
- Отзывчивость при 10 000 контактах, понятные empty/error/busy states, light/dark и клавиатура.

## 5. Non-Goals
- Автоматическая отправка в Google, фоновые remote writes, объединение дубликатов, массовая правка/массовое удаление, редактирование ярлыков как самостоятельных сущностей, CSV/CRM/import/export.
- Редактирование UUID/schemaVersion/google bindings/PROFILE/etag или превращение неизвестного Google-поля в writable API field.
- Изменение формата папки, отключение drift/recovery/path checks, обновление установленной версии, commit/push/publication нового релиза в рамках этой SPEC.
- Обрезка/ретушь фото; добавление, просмотр, замена и удаление фотографии входят в scope.

## 6. Предлагаемое решение (TO-BE)
### 6.1 Ответственность
- Core: immutable описание editable schema/field labels для форм и колонок; существующая schema и валидатор остаются source of truth. API предоставляет read-only descriptors, не mutable JsonObject registry.
- Application/Contacts: catalog/editor contracts, identity/dirty lifecycle, validation, open/save/create/delete orchestration. Никакого Google gateway в editor service.
- Infrastructure/Contacts: bounded catalog reads, редакторские backups, короткие lease, CAS через общие storage primitives, локальная конфигурация таблицы. Existing store/locking/path/photo semantics сохраняются.
- Desktop/Contacts: ContactsTableView + ContactCardView, view models, schema-driven формы с русскими labels, фото picker abstraction, settings/filter/sort panels.
- MainWindow/App: навигация «Контакты / Синхронизация», composition и guards; существующее сравнение/история/обновлятор сохраняются.

### 6.2 Детальный дизайн
**Навигация и dataset.** В основной рабочей области две страницы «Контакты» и «Синхронизация». На подготовленной папке «Контакты» — обычный стартовый экран; onboarding сохраняется для отсутствующей папки. Empty table: «В папке ещё нет контактов», «Создать контакт» и переход к ручной загрузке через синхронизацию. Таблица читает локальные файлы без HTTP/token calls. В существующей привязанной папке локальное редактирование возможно без сети: editor-specific session использует валидный локальный manifest только как local workspace binding, не как разрешение Google; не меняет accountKey, sync state/baselines и не вызывает OAuth. При известной активной несовпадающей identity запись блокируется. Инициализация новой непривязанной папки требует текущей проверенной identity через прежний OpenAsync; если её нет, предложить подключение, не создать неполный `.contactmirror`. Не ослаблять runtime checks sync session.

**Таблица.** Виртуализированная сетка, строки по UUID, не по имени; invalid/duplicate files видны в отдельном счётчике ошибок каталога со списком путей, не исчезают бесследно. Default columns: фото, отображаемое имя, телефоны, email, организация/должность, ярлыки, избранное. Empty names → «Без имени» + UUID в details. Многозначные поля показывают несколько значений и `+N`, полное значение доступно по раскрытию/карточке.
- «Столбцы»: показывать/скрывать, менять порядок drag либо доступными вверх/вниз, resize, restore default. Каталог колонок включает человекочитаемые вычисленные колонки, все 23 категории и все известные writable leaf paths; списки не разрезаются на неверные пары, колонка показывает все значения соответствующего пути. Minimum widths и горизонтальная прокрутка; скрытие/обрезка не теряет данные.
- Сортировка по любому data column: header click asc/desc/off; Shift добавляет критерий, отдельный panel позволяет менять приоритет с клавиатуры. Null/отсутствующие значения идут в конец, строки через русскую culture-insensitive comparer с UUID tie-break; числа/булевы/даты по типу. Многозначное поле: sort key минимальное непустое значение, UI это объясняет; partial date сравнивается по month/day, затем year, отдельно от обычной строки. Сортировка не меняет порядок исходных данных.
- Быстрый поиск по всем editable значениям/ярлыкам независимо от видимых колонок, Unicode case-insensitive; телефон ищется также по цифрам. Header «Показано X из Y». Search text ephemeral.
- Фильтры: любой editable scalar/leaf path, labels/starred/photo; есть/нет значения, содержит/не содержит, равно/не равно; числовые/датовые сравнения только подходящим типам. Rules с переключателем «Все условия / Любое условие», включая выбор конкретного ярлыка. For arrays позитивный predicate = существует matching value, негативный = нет matching value; empty treatment явный. Сброс видим; новый фильтр не уничтожает открытый draft, даже если он перестал попадать в таблицу.
- Config schemaVersion1 в отдельном `contact-table.json` под ApplicationPaths.DataRoot, demo/validation изолированы. Columns/order/width/sort/filter/splitter сохраняются per workspaceId; настройки не входят в JSON контактов и не отправляются в Google. Unknown config fields/column ids безопасно игнорируются, corrupted preferences дают defaults+notice; другие DesktopPreferences и OAuthPath не стираются. Reload/restart acceptance обязателен.

**Карточка.** На широком окне справа resizable detail pane, таблица слева; можно развернуть карточку. На <900DIP контакты и карточка по очереди с «Назад». Верх: имя/фото/избранное/ярлыки, «Редактировать»; view mode показывает заполненные поля, «Все поля» включает пустые. Edit mode показывает удобные секции, «Добавить поле» содержит все категории. Поиск по названиям полей находит редко используемую категорию. Footer закреплён: «Сохранить в папку», «Отменить», состояние dirty/validation; просмотр или изменение фото не закрывает черновик.
- Категории: addresses, biographies, birthdays, calendarUrls, clientData, emailAddresses, events, externalIds, genders, imClients, interests, locales, locations, miscKeywords, names, nicknames, occupations, organizations, phoneNumbers, relations, sipAddresses, urls, userDefined; дополнительно labels, starred, photo и локальные extensions.
- Human sections: Имя и обращения; Связь; Организации и занятия; Адреса; Даты и события; Заметки и интересы; Дополнительные сведения. Их grouping не ограничивает доступные свойства.
- Каждая известная writable nested property имеет typed editor: текст/multiline, boolean, integer, enum с русскими titles и точным исходным value, object/date group, repeatable rows с добавлением/удалением и sourcePrimary. Custom `type` остаётся произвольной строкой там, где это допускает schema. Scalar literal не угадывается как JSON. Дубликаты сохраняются, primary только внутри допустимой категории; singleton names/birthdays/biographies/genders не допускают второй записи. Частичный год/месяц/день поддерживаются, не подставляется текущий год. Требования вроде «email обязан иметь @»/«телефон обязан быть E.164» не вводятся сверх существующего формата; при необходимости только подсказка.
- Effective editable descriptors учитывают schema **и** overrides существующего ValidateEditable/WritableProjection: у FieldMetadata только sourcePrimary редактируем, source/id/etag/type/primary/verified и другие metadata paths исключены даже без readOnly в исходной schema. Колонки просмотра могут показывать такие сведения отдельно, но формы не создают writable controls для них. AC5 использует независимую enumeration effective paths и негативные проверки Source metadata.
- Неизменённые категории сохраняются из исходного JsonObject целиком; editing не делает global projection, не удаляет extra/readonly metadata. Unknown nested data присутствует read-only с пояснением; если старый валидатор не разрешает безопасную запись такой записи, карточка не обещает сохранение и показывает точный blocking path. `google`/PROFILE отображаются в отдельной секции «Сведения Google, только просмотр», все неизвестные поля доступны в раскрываемом tree/raw view. `extensions` редактируются typed JSON tree (включая arrays/null) с optional raw object editor и strict duplicate-key/type validation; они явно локальные.
- Невалидный файл/UUID duplicate не разрешает typed save/create-overwrite. Errors catalog показывает все причины; доступны read-only raw file и прежний «Открыть файл» для внешнего исправления, после refresh запись заново валидируется.

**Фото.** В карточке нормальная миниатюра, просмотр целиком, «Выбрать / Заменить / Удалить». JPEG/PNG до 20 МБ, ≤8192×8192/40Мп, полный decode через существующий ValidateImage. Выбранные bytes читаются один раз в draft; отмена picker или draft ничего не пишет. Save копирует в content-addressed `photos`, JSON хранит относительный путь; внешний source file не становится живой ссылкой. Remove = photo:null, старые фото не удаляются из shared storage. PROFILE/обложка показываются отдельно, не подменяют editable CONTACT photo; сетевые картинки не подгружаются автоматически.

**Создание, сохранение, удаление.** Создать открывает пустой draft с новым UUID; файл возникает только при явном Save, cancel не оставляет шаблон. Delete — одиночное локальное перемещение JSON в editor trash с backup/metadata, подтверждение с именем/направлением: «Удалить из папки; Google пока не изменится». Старое фото сохраняется. Если storage pending/recovery неоднозначен, запись этого контакта запрещена до существующего процесса восстановления. Массовых действий нет.
- Draft capture: root/workspaceId/id/path/original document+bytes/hash/photoHash и generation; memory-only доSave. Неизменённый draft не делает write/backup и не инвалидирует preview.
- Save заново проверяет root/binding/UUID uniqueness/current raw bytes/photo/labels, валидирует весь resulting document. Labels ссылаются на существующие уникальные groups, duplicate UUID labels не допускаются. Открытый draft не держит workspace.lock; read/save/delete имеют короткую общую lease и ApplicationActivity guard.
- До CAS сохраняются точные исходные JSON bytes и старое фото в `.contactmirror/editor-backups/<operation-id>` с bounded atomic manifest. Ошибка backup запрещает commit. Новая запись требует expectedHash=null и проверки отсутствия UUID; сохранение имеет точный existing path. Использовать общий CAS/WriteAsync algorithm, не File.WriteAllText overwrite.
- Editor session не вызывает обычный OpenAsync/LoadAsync на каждый read/save: они могут создавать/мигрировать SQLite и загрузить фото всех контактов. Выделить общие lease/path/CAS/selected-document write primitives с characterization tests прежнего sync path; editor читает только выбранный JSON/photo и необходимые identity/labels/pending записи. Catalog scan не декодирует все фотографии.
- Для существующей папки состояние SQLite читается read-only без создания, миграции, recovery acknowledgement или записи baselines. Missing/corrupt/unsupported state.db, global recovery flag, Google-bound документы без доверенного entities state, marker/pending без определимого UUID либо неизвестного типа означают whole-workspace read-only. Manifest сам по себе не доказывает безопасную готовность. Только доказанно scoped pending/issues блокируют отдельные записи; остальные доступны для записи при отсутствии глобального риска. Чтение каталога и просмотр причин остаются доступны; использовать существующий процесс восстановления, не исправлять state из editor.
- External drift в JSON/фото/переименовании/удалении либо labels/pending/binding: не затирать, сохранить draft в памяти, показать «Файл изменился вне редактора» и действия «Загрузить актуальную версию» с отдельным отказом от draft либо «Оставить черновик». Без silent merge/force overwrite. Случай commit-then-exception проверять по authoritative file/hash до сообщения о результате; отмена после commit не называется «не сохранено». Content-addressed orphan фото при failure допустимо и не удаляется автоматически.
- Delete contract использует immutable exact path/raw JSON/hash/photoHash версии, уже показанной пользователю в карточке и диалоге (opened/confirmation snapshot). Нажатие Confirm не перечитывает текущий файл для замены expectedHash или baseline: внешняя правка JSON/фото пока modal открыт означает отказ с сохранением версии пользователя. Внутри общей lease заново проверяются обе версии, pin/validate JSON и старое фото удерживается от backup до move, фактически перемещённые bytes сверяются с expected hash; чужие версии сохраняются и результат не называется успехом при drift. Простое reuse существующего TrashAsync(expectedHash) недостаточно: оно не проверяет expectedPhotoHash. Editor move primitive добавляет photo guard, сохраняет прежний sync behavior; deterministic JSON/photo change while confirmation open и photo replacement до backup/между backup и move входят в AC8/9. Отмена подтверждения не создаёт backup/trash/photo.
- Save/delete/create success инвалидирует подготовленный sync preview; статус «Сохранено в папку. Для Google проверьте изменения». Обновляется одна catalog row, selection сохраняется; если row скрылась из-за фильтра, открытая карточка остаётся с объяснением. Если sort передвинул row, focus остаётся в карточке.
- Переход от dirty к другому контакту/режиму/папке/аккаунту, close и updater restart: dialog «Сохранить / Не сохранять / Остаться»; failed save остаётся в текущем draft. Update download/check может выполняться, ApplyAndRestart блокируется до разрешения draft. Check/apply/repair/restore и editor mutations не выполняются одновременно; direct command invocation тоже guarded. Активная синхронизация оставляет каталог/карточку для чтения, edit/save запрещены.

**Wireframe — доступный visual planning artifact внутри этой SPEC:**
```text
1180×820, Контакты
ContactMirror       [Контакты] [Синхронизация]       История Настройки Справка
Папка: …                         Сохранение в папку, отправка отдельно
[Поиск по контактам…………] [Фильтры:2] [Столбцы] [Сортировка] [+ Контакт]
Показано128 из420
┌────────────────── таблица ─────────────────┬──────── карточка ────────┐
│ Фото  Имя↓      Телефоны     Email   Ярлыки│ [фото] Анна Примерова  ☆ │
│ ○     Анна…     +7…         anna@… Друзья │       [Редактировать]    │
│ ○     Борис…    +7…         boris@… Работа│ Связь: телефон/email…    │
│ … строки виртуализированы; scroll X/Y …  │ Адреса / Работа / Даты  │
│                                         │ Заметки / Все поля       │
│                                         │ Google: только просмотр ▸│
└─────────────────────────────────────────┴──────────────────────────┘

Редактирование: строки значения+тип+основное; [+ Добавить телефон/поле]
Фото: [Выбрать/Заменить] [Удалить]  |  Footer [Отменить] [Сохранить в папку]
820×640: та же таблица → [Назад] карточка на ширину окна; footer виден.
Drift: inline error + [Загрузить актуальную] [Оставить черновик]; Save blocked.
```
Обязательны rendered PNG view/edit/all-fields/photo/filter/columns/sort/dirty/drift/errors в light/dark, 1180×820/1000×680/820×640. Новый flow таблица→карточка ранее отсутствовал, поэтому `до` для него — «Не применимо» по ui-automation-testing; старый внешний JSON-путь можно дополнительно показать characterization PNG/video, без выдуманного baseline PASS. Обязательное after video из автоматизированного native test run показывает поиск→карточка→несколько полей+фото→save→reopen→ручной preview. Logical window и DPI указываются; отсутствие after video требует только объективного fallback по профилю.

### 6.3 User-Observable Scenarios
| Сценарий | Действие | Видимый результат | Evidence | AC |
|---|---|---|---|---|
| Каталог без sync plan | Открыть папку/Контакты | Все local contacts, нет HTTP; errors count/empty понятны | UI+gateway counter | 1 |
| Своя таблица | Выбрать/reorder/resize columns, multi-sort, filters | Верный набор/порядок, restart сохраняет config | UI+restart | 2–4 |
| Полный редактор | Найти contact, изменить обычные и редкие nested поля, labels/starred/extensions | Typed controls, точный reopened document | Parameterized schema+native | 5–6 |
| Фотография | Добавить/заменить/удалить/отменить | Полное preview и persisted correct bytes/path | Native+storage | 7 |
| Внешняя правка | После opening изменить JSON/photo | Draft остаётся, чужой write не затёрт | Fault+UI | 8 |
| Создать/удалить | СоздатьSave / cancel; удалитьconfirm/cancel | Exact local file/trash, remote untouched | Integration+UI | 9 |
| Возврат/выход | Dirty→другой contact/close/restart | Save/discard/stay; failed save остаётся | Native keyboard+guards | 10 |
| Ручной sync | Save→Check→Apply→Check | Только явный upload, no-op после подтверждения | Demo full flow | 11 |

### 6.4 State / Interaction Matrix
| Состояние | Trigger | Переход | Ошибка/concurrent |
|---|---|---|---|
| Loading | new root/refresh | Ready/Empty/Issues | Generation ignores stale result, cancel retains old root truth |
| View | Edit/Create | Dirty draft | Busy/applying/recovery disallows mutation |
| Dirty | Save | Saving→Saved view | Invalid/drift/I/O keeps draft+inline error |
| Dirty | navigate/close/restart | Decision modal | Escape=Stay, Tab trapped, failedSave prevents transition |
| Clean draft | Cancel | Original view | No file/photo writes |
| External drift | Reload | View fresh only after discard confirmation | Cannot overwrite external/deleted version |
| Ready | filter hides selected row | Table count changes, card stays | Identity selection never swaps to same-name neighbor |
| Saving/Delete | sync/config/restart/direct call | Disabled/refused | Single ApplicationActivity + workspace lease |
| Scoped corrupt/duplicate/pending contact | Open | View+exact reason | Save/delete этой записи blocked; другие safe contacts usable только без global risk |
| Global/unscoped recovery, missing/corrupt/newer state.db | Open | Workspace read-only+reason | Все create/save/delete blocked; state не создаётся/не меняется |

### 6.5 Decision Ledger
| Решение | Owner | Выбрано | Confidence | Риск | Needs user before EXEC |
|---|---|---|---:|---|---|
| Local vs Google write | agent | Local save, прежний manual sync | 0.99 | Скрытые remote writes запрещены | Нет |
| «Все поля» | agent | Все 23 writable categories/nested, labels/starred/photo/extensions; Google readonly | 0.99 | Не выдать JSON-only форму за полный editor | Нет |
| Create/delete | agent | Одиночные локальные CRUD, explicitSave/confirm | 0.88 | Удаление отправляется только прежнимmanualApply | Нет |
| Editor UI | agent | Table+resizable card, narrow drill-in | 0.95 | Нужен полный viewport иkeyboard | Нет |
| Save policy | agent | ExplicitSave + memory draft + guarded transitions | 0.96 | Crash доSave теряет unsaved draft; autosave не заявлен | Нет |
| Release | user | Реализация локально; будущий релиз отдельно | 1.0 | Предыдущее разрешение не переносится | Нет, out of EXEC scope |

### 6.6 Runtime / Config / Data Contract Matrix
| Источник | Truth | Guard | Совместимость |
|---|---|---|---|
| contacts JSON/photo/groups | Actual local bytes/UUID/path | Schema, bounded read, hash/photoHash CAS, labels existence | SchemaVersion1 без миграции |
| workspace manifest | Existing UUID/accountKey | Не менять при offline open; new binding only existing verified account flow | No owner reassignment |
| sync state/baselines/pending | Existing SQLite/journal | Read-only inspection; missing/corrupt/newer/global/unscoped blocks all writes; proven scoped pending blocks entity | Existing repair/recovery preserved |
| preferences | Separate local config schema1 | Safe IDs/finite width/bounded rules, defaults on corruption | Old DesktopPreferences preserved |
| Draft | Captured snapshot+workingcopy+generation | No aliases to original JsonNodes; no network | Memory-only |
| Backup/trash | Dedicated editor paths | Before commit copy, path constraints, no overwrite | Не маскируются под sync runs |

## 7. Бизнес-правила / Алгоритмы
- Контакт идентифицируется UUID; error rows — безопасным canonical path. Same-name contacts не смешиваются.
- Только dirty changed paths меняют document; untouched data/google/extensions остаются semantically identical. Null правила для data и extensions различаются.
- Missing/empty arrays = category clear; удаление последней строки должно показывать очистку, не подставлять фиктивное значение. Preview объяснит последствия вручную.
- Filter AND/OR относится к правилам, array predicate — к значениям одной rule. Row result computed on immutable catalog snapshot; stale generation never applies.
- Не приписывать сохранение в Google, синхронизированность или «конфликт» только по offline catalog: status column показывает только известные local состояния/errors, без актуальности remote claims.

## 8. Точки интеграции и триггеры
- Composition injects IContactEditorService/config/photo picker; test doubles deterministic, no actual account/files.
- Folder/account/mode change и MainWindow Closing используют общий draft transition coordinator; VM setters нельзя использовать как bypass.
- Save/create/delete success→catalog update+preview invalidation; Sync completed→catalog refresh. External FileSystemWatcher только сообщает potential drift/debounces; CAS остаётся authoritative, manual Refresh доступен.
- Update ApplyAndRestart интегрируется в **ApplicationUpdateService и его interface**, не только в CanExecute UpdatesViewModel. Контракт: draft transition/возможный Save завершается до restart lease; выдаётся readiness token с editor generation. Service захватывает единственную `ApplicationActivity.TryEnter(restart:true)` lease, затем под общим draft gate проверяет authoritative clean/current generation и получает guard authorization, удерживаемую до вызова backend под **той же lease**. Новое изменение между preparation и TryEnter, failedSave, stale generation или отсутствующая authorization дают Stay без backend. Wrapper не захватывает вторую lease и не заменяет service-level guard.
- Все typed field edits/photo/create/cancel/navigation mutations проходят тот же draft gate и повторно проверяют applying/activity; guard check и backend commit нельзя обогнать direct edit setter. Регистрируется общий coordinator для MainWindow/editor/update service в composition; before registration restart не разрешён. AC10 детерминированно меняет draft между readiness и lease acquisition и проверяет direct service/command invocation. Existing close-cancel-await busy behavior сохраняется.

## 9. Изменения модели данных / состояния
Новые transient catalog row/editor draft/schema field VM/filter/sort/config models; отдельная persisted table config/editor backup metadata. Contact schema, sync journal operations и version не меняются. Каталог не содержит весь photo byte массив для 10k строк: thumbnails lazy bounded cache, full image только selected contact. Чтение/filter/sort на worker, публикация пачкой; data-grid row virtualization обязательна.

## 10. Миграция / Rollout / Rollback
First run config defaults. Existing files не переписываются при чтении/view/navigation. ПослеSave старое приложение видит совместимый JSON/photo. Откат к0.3.0 возможен по code/package при отсутствии незавершённых существующих sync/repair intents; editor backups сохраняются, reset таблицы не удаляет контакты. Commit/publish/install требуют отдельного поручения.

## 11. Тестирование и критерии приёмки
| AC | Automated verification | Visual/manual evidence |
|---|---|---|
| AC1: local full catalog/empty/invalid/duplicate/offline; new binding не создаётся молча | Integration real temp JSON/SQLite, zero HTTP counter, mismatch/no-account; lost/corrupt/newer DB/global flag/unscoped marker preserve all state bytes and forbid writes | Native table/empty/issues/read-only PNG |
| AC2: all field columns, resize/reorder/show/hide/reset | Headless column descriptors and keyboard actions, no field omitted | Native columns/table PNG |
| AC3: typed stable multi-sort + full-value search | Independent reference oracle, same-name/UUID/null/multi/Unicode/phone/date fixtures | Header priorities and focused card |
| AC4: AND/OR/all operators/arrays/missing; persistence isolated per workspace/demo | Truth-table tests, corrupted/old preference config, fresh process restore | Filter/chips + reopened table |
| AC5:23 categories + all writable leaf types/singletons/partial dates/custom type/primary/duplicates | Schema coverage enumeration + nontrivial percategory mutate→save→reopen against real files; positive/negative validator parity | Filled common/rare forms, no JSON-only category |
| AC6: labels/starred/extensions edit; Google/profile/unknown preserved read-only | Exact semantic/raw preservation of untouched fields, null/data rules, missing labels/recovery | Readonly section + extensions tree |
| AC7:photo add/replace/remove/cancel/view invalid formats/limits | Real bytes/hash/filepath; no writes oncancel, sourcefile external change cannot alter draft | Native picker including cancel, complete photo PNG |
| AC8: JSON/photo/path/UUID/binding/group drift and backup/CAS failure | Deterministic write fault injection, verify both external bytes+draft+beforebackup, concurrent process lease; scoped-vs-global recovery truth table | Drift error retains typed values |
| AC9: create/delete local CRUD correct/cancel; trash safe | Temp store, UUID absent guards, exact path/JSON/photo captured baseline and guarded move, exact trash copies, zero remote calls | Native new+delete confirmation |
| AC10: dirty lifecycle/windowclose/updateRestart/direct-command/busy/modal safety | Headless state/directcalls, native keyboard/close; direct ApplicationUpdateService+VM tests; mutation between readiness/TryEnter and failedSave spy never invokes backend; shared lease/gate authorization | Dirty modal/focus screenshot/video |
| AC11: save invalidatesoldplan; manual preview/apply/noop including all fields+photo | Real DemoGateway+SyncCoordinator+FileWorkspaceStore integrated test | Native scenario and video |
| AC12:10k catalog virtualized/filter/sort cancel; light/dark sizes; existingflows unaffected | Target fixture ≤50 realized rows, UI ping/cancel≤250ms; large draft≥2000rows lazy UI; Release+full suites | Inspected1180/1000/820 PNG, ordinary --demo smoke |

Characterization before implementation: existingfileCAS/photo checks, unchangedgoogle roundtrip, manual sync noauto network, account binding, update dirtycheck currentlyabsent; meaningful failing editor scenario. Не добавлять tests, которые зеркалят generated UI; reference oracles/schema complete coverage обязательны.

Commands EXEC sequential where shared bin/obj:
```powershell
dotnet build ContactMirror.sln -c Release -m:1
dotnet test tests/ContactMirror.Tests -c Release --no-build
dotnet test tests/ContactMirror.GoogleTests -c Release --no-build
dotnet run --project tests/ContactMirror.UiTests.Headless -c Release --no-build -- --maximum-parallel-tests 1
dotnet run --project tests/ContactMirror.UiTests.FlaUI -c Release --no-build -- --maximum-parallel-tests 1
python schemas/generate_schemas.py --check
python schemas/check_schemas.py
```
AppAutomation strict doctor по repo-proven DLL fallback, UI suite video recording hooks и record-app-screen skill. В execution log сохраняются exact command/filter/HTML/PNG/MP4. API integration fixtures не доказывают настоящий Google. Timeout с progress/log и новой проверяемой гипотезой; не повторять без изменения причины.

## 12. Риски и edge cases
Самые существенные: schema-driven coverage против удобства; partial dates/multi values; повреждённый/удалённый/переименованный file; binding и recovery; shared photo; after-commit error; dirty close/restart race; virtualization/big JSON. Контракты и соответствующие negative AC заданы выше, ни один не снимается красивым макетом.

### Expected User Review Objections
| Возражение | Mitigation | Status |
|---|---|---|
| «Опять только имя и телефон, остальные через JSON» | Все writable leaf controls и автоматическая schema coverage, реальный rarefields native flow | mitigated |
| «Столбцы/фильтры пропали после перезапуска» | Separate persisted perworkspace config + fresh process AC4 | mitigated |
| «Сохранить отправило всё в Google» | No gateway editor contract, zero HTTP, отдельныйmanual sync | mitigated |
| «Фото/внешняя правка/черновик потерялись» | CAS+copy-beforecommit+immutablephoto+dirtyguards+faulttest | mitigated |
| «Карточка тесная, редкое поле нельзя найти» | Resizable/fullscreen/narrow + fieldsearch/allfields + inspectedviewport | mitigated |

Rework checklist: пользовательская table/card названа; каждый сценарий mapped; defaults/permissions определены; objections mitigated; relevant roles ниже; AC описывают outcome; EXEC имеет полноценный storage/UI доказательный путь.

## 13. План выполнения
1. Characterization и schema descriptor coverage; typed data/editor contract на fixtures.
2. Shared safe storage/editor leases/backups/CAS/photo/delete; integration fault/restart tests. До UI обеспечить безопасное сохранение.
3. Catalog/query/config; reference filter/sort tests, virtualization и persistence.
4. Card typed controls/photo/dirty coordinator + navigation/update integration.
5. Whole native flows, full suite, inspected PNG/video, fixes, post-EXEC advisory fallback/independent review и завершение всех AC.

## 14. Открытые вопросы
Нет user-owned blockers. Выбор local/manual sync и protected technical fields сохраняет уже действующий продуктовый контракт. Separate post-SPEC review завершён: PASS плана, все четыре находки исправлены и повторно проверены.

## 15. Соответствие профилю
Avalonia MVVM, Application не зависит от Controls, UI virtualized+typed bindable readonly projections, stable automation IDs, native/headless из Authoring при reuse; negative keyboard/file/photo/context scenarios обязательны. Existing source/schema/persistence не мигрируют. Tests/build и packaging последовательны. UI acceptance — actual render, не только bounds.

## 16. Таблица изменений файлов
| Файлы/модули | Изменение | Причина |
|---|---|---|
| Core/ContactFieldSchema (new), CapabilityRegistry | Readonly descriptor access/Russianleaf titles | Single schema truth |
| Application/Contacts (new) | Catalog/draft/save/query contracts | Testable no-network editing |
| Infrastructure/Contacts (new), FileWorkspaceStore | Editor sessions/common atomic guards/backups/trash/config | CAS compatibility |
| Desktop/Contacts (new AXAML/VM) | Table/card/typed field editors | Full user workflow |
| MainWindow/MainWindowViewModel/App/UpdatesViewModel/Program; Application/Updates/ApplicationUpdateService и interface | Navigation/dirty coordinator/single-lease restart authorization | No bypass transitions/race |
| Authoring/TestHost/Headless/FlaUI/Core tests | Completecoverage/fixtures/negative/e2e | Meaningful verification |
| README/FORMAT/VALIDATION/CHANGELOG | После approvalactual editor/saving semantics | User instructions |

## 17. Было → стало
| Область | Было | Стало |
|---|---|---|
| Контакты | Только entries плана | Каталог всех локальных контактов |
| Правка | Внешний JSON-файл | Typed full contact card |
| Таблица | Нет | Columns/filter/sort persisted |
| Фото | External file change | Picker/preview/replace/remove/save |
| Guard | Busy only | Busy+dirty+CAS+safe transitions |

## 18. Альтернативы и компромиссы
- JSON-only editor: минимален, но не выполняет запрос полноценной карточки; только technical fallback.
- Ручные формы для 23 категорий: легко потерять nestedполя/новуюschema; chosen гибрид common polished sections + schema typed controls, completecoverage.
- Inline table editing: удобно для массовой работы, но умножает drafts/save semantics исложныеphoto/nestedforms; table отвечает за navigation, card заedit.
- Auto-save/auto-sync: меньше кликов, но скрытыеwrites/сложныйrollback; explicitlocalSave и прежнийmanualsync сохраняютконтроль.
- Offline editing existing bound workspace: localmanifest сохраняетсвязь, не выдаётremoteauthority. Empty unboundfolder требует identityдляinit; лишнийnew ownership contract не вводится.

## 19. Результат quality gate и review
### SPEC Linter Result
| Блок | Пункты | Status | Основание |
|---|---|---|---|
| A | 1 | PASS self | §1 original table/card/allfields/photo outcome |
| A | 2 | PASS self | §2 actual inspected schema/store/UI |
| A | 3 | PASS self | §3 no internal editor/catalog |
| A | 4 | PASS self | §4 usability/typed safety/performance |
| A | 5 | PASS self | §5 manual sync/protected fields/release boundaries |
| B | 6 | PASS self | §6.1 module ownership/no gateway |
| B | 7 | PASS self | §8 navigation/dirty/invalidation/restart hooks |
| B | 8 | PASS self | §7 identity/source types/filter invariants |
| B | 9 | PASS self | §6.2/6.4 draft/drift/global recovery/errors |
| B | 10 | PASS self | §9 lazy photos/worker query/virtualization, AC12 |
| C | 11 | PASS self | §6.6/9 persisted vs transient, source authority |
| C | 12 | PASS self | §10 schema1/config additive/no state migration |
| C | 13 | PASS self | §10 rollback guard/exact beforeimages |
| D | 14 | PASS self | AC1–12 visible outcome/persistence/negative cases |
| D | 15 | PASS self | §11 each AC automated+visual evidence |
| D | 16 | PASS self | §11 exact commands/sequential writes/timeout rules |
| E | 17 | PASS self | §13 storage before UI/dependencies |
| E | 18 | PASS self | §6.5/14 chosen defaults, no user-owned blockers |
| E | 19 | PASS self | §0 large multi-module/writes justify Expanded |
| F | 20 | PASS self | §15 MVVM/AppAutomation/native inspection |
Исторический итог фазы SPEC: ГОТОВО к exact approval после self-pass и отдельного advisory/adversarial review. На тот момент реализация ещё не начата; актуальный результат приведён в Post-EXEC ниже.

### SPEC Rubric Result
| Критерий | Балл | Основание |
|---|---:|---|
| Цель и границы | 5 | Исходный запрос table/card/allfields/photo и manual sync |
| AS-IS | 5 | Реальные Models/schema/codec/store/activity/UI |
| TO-BE | 5 | Typed forms, query/config contracts, states/wireframe |
| Безопасность | 5 | CAS/backup/binding/global recovery/no migration |
| Проверяемость | 5 | AC1–12, fault/UI/real-file/restart/full suites |
| Автономность | 5 | Все внутренние defaults определены, human gate явный |
Итог self: 30/30. Оценка плана не подтверждает реализацию и не заменяет separate review.

### Role-Based Review Result
| Role | Applicability | Question / self-pass | Verdict | Changes |
|---|---|---|---|---|
| Domain | applicable | Local/manualsync, full data/photo сохранены? | PASS self | None |
| UX/designer | applicable | Table/card/commonrare/narrow/empty/dirty удобны? | PASS self | None |
| Tester | applicable | Полноеcoverage иfailurecounterexamples доказуемы? | PASS self | None |
| Architect | applicable | SharedCAS, schemas, guards, performance? | PASS self | None |
| Delivery/security | applicable | Binding/path/config/privacy/runtime границы? | PASS self | None |

### Post-SPEC Review
- Scope inspected self: этот SPEC, canonical template/quest/review owners, Models/DocumentCodec/CapabilityRegistry/people-schema, WorkspaceContracts/FileWorkspaceStore WriteAsync/OpenAsync/ValidateImage, ApplicationPaths/DesktopPreferences, MainWindow/App/Activity, FORMAT и approved UX SPEC context.
- Passes self: original request→table/card/allfields/photo; contracts→CAS/noHTTP/dirty; adversarial→unknown metadata loss/partialdates/duplicates/stale draft/wrongbinding/update race; role tables иAC matrix заполнены.
- Depth checklist: scope/permissions/defaults; data/source types; allfieldscoverage; invalid/empty/readonly; persistence/cancel/rollback; fullsuite/render/video; releaseboundaries просмотрены.
- Preliminary findings: offline new binding, unknown metadata preservation, photo shared storage и dirty restart уточнены в тексте до review.
- Separate reviewer ux_spec_review завершил чтение актуальной SPEC и actual storage/schema/codec/MainWindow/VM/Activity/UpdatesViewModel/ApplicationUpdateService contracts. Выполнены Scope/Evidence, Contract, Adversarial, relevant Role-Based passes и targeted Fix/re-review; итог PASS плана. Effective sandbox подтверждён: danger-full-access, filesystem unrestricted, approval never; reviewer не менял файлы/внешнее состояние. Это процедурный advisory/adversarial fallback, технически изолированным независимым acceptance не считается.

| Severity | Area | Finding | Required action | Status |
|---|---|---|---|---|
| HIGH | Offline recovery | Manifest не доказывает отсутствие global/unscoped pending/lost DB | Read-only state inspection без init; whole-workspace readonly для неизвестного состояния; AC1/8 | fixed, re-review PASS |
| HIGH | Restart race | Wrapper readiness до service lease допускает dirty race | ApplicationUpdateService same-lease authorization/current generation/draft gate; direct/race AC10 | fixed, re-review PASS |
| MEDIUM | Field metadata | Schema readOnly не учитывает validator overrides | Effective schema+projection rules; Source metadata readonly, sourcePrimary writable; AC5 | fixed, re-review PASS |
| MEDIUM | Delete CAS | TrashAsync не проверяет фото, поздний rebaseline мог удалить чужую версию | Immutable показанный snapshot; JSON/photo pin от backup до move; confirmation drift; AC8/9 | fixed, re-review PASS |

- No-findings justification после fixes: исходный table/card/allfields/photo запрос полностью mapped; сохранение и unknown/source types не подменены UI projection; offline/global state и restart/delete races имеют конкретные guards и negative fixtures; все AC имеют behavioral/visual/persistence evidence plan. Открытых BLOCKER/HIGH/MEDIUM нет.
- Manual-review challenge: rare nested fields/metadata, external photo/JSON while confirm open, global lost state, hidden selected draft и restart between readiness/TryEnter остаются обязательными EXEC counterexamples. На SPEC они определены, а не считаются уже проверенными runtime.
- Role disposition: все пять применимых ролей self-pass и separate fallback PASS плана. Residual risk — отсутствие технической read-only isolation reviewer; runtime correctness подтверждается только будущими tests/render/fault flows.
- Stop decision: PASS post-SPEC в раскрытых границах, можно запросить exact approval. Код, инфраструктура, настройки и установленная версия не изменялись.

### Post-EXEC Review
Первый отдельный проход EXEC: NEEDS-FIX, восемь HIGH/MEDIUM findings. Исправлены value/structural notifications, coherent DB/WAL capture с deny-write pins до commit, запрет инициализации bound/unknown folders без manifest, authoritative write outcomes, offline root transition, каталог проблемных файлов, общий modal/picker guard, sort cycle/Shift/narrow persistence/typed label filters. Повторное source review закрыло все восемь замечаний.

Дополнительно исправлены refresh выбранной карточки и фото по тому же пути, очистка удалённой записи, честный статус ошибки удаления, decode фото с крайним соотношением сторон, стабильный header при поиске полей, deferred column order persistence и двухстрочная narrow navigation. Последний visual re-review закрыл MEDIUM по placeholder contrast (6.64:1 light, 11.22:1 dark) и подтвердил полную видимость значений в narrow comparison после удаления пустого Explanation spacer. Длинные account/folder ограничены доступной шириной с ellipsis, без перекрытия навигации; проверки bounds и шесть PNG пройдены. Исправлен поздний старт video recording: до действий требуется реально записанный первый кадр, stdout/stderr рекордера читаются одновременно. Открытых source/visual findings нет; финальная сверка acceptance evidence отдельным reviewer выполняется после записи этой матрицы.

### Итоговая AC-mapping
| AC | Результат и конкретное evidence |
|---|---|
| AC1 | PASS: реальные temp JSON/SQLite, valid/empty/invalid/duplicate/offline/mismatch/recovery состояния, zero HTTP и no-init guards. Native каталог/empty (`20261009-134941-442/editor-local-delete-empty-native.png`). Raw issues: инспектированный Headless `20261009-134534-443/editor-problem-file-readonly.png` и exact-byte readonly tests; явный fallback вместо отдельного native corrupt-file flow. |
| AC2 | PASS: 144 descriptors, hide/show/order/resize/reset; fresh VM восстанавливает phone index0/width190 и name index1/width311. Native columns `20261009-145028-616/editor-native-columns-native.png`. |
| AC3 | PASS: independent typed query oracle, full-value search, stable multi-sort, Shift priorities/off cycle; native поиск и focused card. |
| AC4 | PASS: AND/OR typed operators/arrays/missing, label names/Да/Нет chips, hidden draft, corrupt/old prefs, fresh VM и perworkspace/demo isolation. Native filters `20261009-145030-064/editor-native-filters-native.png`. |
| AC5 | PASS: независимый schema oracle — 23 категории, 111 writable leaves изменены через UI controls → real ContactEditorStore/CAS → reopen. Type/singleton/partialdate/customtype/primary/duplicates validator parity; filled rare fields в native CRUD. |
| AC6 | PASS: actual UI labels/starred и extensions types; Google/profile/unknown preservation exact, source metadata readonly/sourcePrimary writable; null/duplicate negative cases. |
| AC7 | PASS: native picker cancel/accept/view; teal → gold замена по одному внешнему пути, сохранение/перечитывание точных фото-байтов, remove. Invalid/oversize/extreme-aspect and stale picker/decode tests. Инспектирован `20261009-134938-832/editor-new-rare-fields-photo-native.png`. |
| AC8 | PASS: immutable JSON/photo/path/UUID/binding/group baseline, pinned coherent DB/WAL, CAS/backup failures, concurrency/recovery outcomes; чужие bytes и draft сохранены. Drift screenshot и реальные storage fault tests. |
| AC9 | PASS: native create/rare fields/local save/delete cancel/confirm/trash; exact trash/photo bytes, zero remote writes, confirmation JSON/photo drift rejects. |
| AC10 | PASS: dirty Save/Discard/Stay для navigation/folder/window close; Tab/Escape/Ctrl+S/modal guards. Same lease/current generation/direct update service races; busy/picker mutation denied. Native `20261009-145026-921/editor-native-close-dirty-focus-native.png` и видео кадр24. |
| AC11 | PASS: local save invalidates plan; настоящий DemoGateway+SyncCoordinator+FileWorkspaceStore pipeline, ручные preview/apply/readback и next no-op; allfields/same-path photo repair/upload сохранены. Native preview `20261009-145040-653/editor-manual-preview-native.png`, результат `20261009-145043-161/editor-manual-sync-result-native.png`; видео40/50. Google HTTP fixtures не являются настоящим Google. |
| AC12 | PASS: final Release build 0 warnings/errors; Core137/137, Google185/185, Headless61/61, Native11/11, все 0 skipped. 10k:18 realized/input6ms; 2k draft:2 realized/ping6ms; прежний diff:4 realized/18ms. Все шесть light/dark 1180/1000/820 PNG инспектированы; ordinary Desktop --demo PASS. Native DPI216; отдельной физической DPI-матрицы нет. |

Полные команды, TRX и inspected PNG перечислены в `docs/VALIDATION.md`. Итоговые suites выполнены последовательно после последней правки приложения (header); 394/394 PASS. Дополнительные Headless preview1/1 и native recording1/1 не прибавляются к394; после полного native11/11 менялся только опциональный recording hook теста, приложение неизменно. Schema generation/check и strict doctor PASS. Performance — локальные synthetic UI fixtures, не обещание времени реальной адресной книги.

Видео `chat-artifacts/editor/editor-after-client.mp4` — 64.800s,2216×1488; исходный `editor-after-20261009-144958-349.mp4` — 120.000s,2250×1576, DPI216/logical1000×700. Запись GDI прямоугольника окна, затем crop до клиентской области без чужих окон/desktop edges. Основной агент инспектировал кадры20/21 (поиск→карточка),22 (редактирование и действия фото),23/24 (новые имя/телефон и dirty modal),35 (save/reopen),40 (ручной preview трёх изменений),50 (no-op). Начальные18s — ожидание готовности записи. Native recording target1/1 PASS; first-frame readiness до UI действий и concurrent stdout/stderr drain устраняют пропуск начала. Прежние40s/120s записи исключены из итогового evidence. Добавление/замена цветного фото доказаны отдельным native CRUD; в видео показаны исходное фото, просмотр/удаление и сохранение.

Separate review scope: source contracts/failure counterexamples, все шесть rendered light/dark PNG, narrow values, native picker/card, final TRX/logs/docs/AC matrix и client video frames. Все пять ролей applicable: Domain, UX, Tester, Architect, Delivery/security. Effective sandbox danger-full-access/unrestricted/approval never; reviewer работает read-only по процедурному запрету. Это advisory/adversarial fallback без технической изоляции, не isolated acceptance.

Финальная отдельная acceptance-сверка `ux_spec_review`: PASS, findings отсутствуют, все прежние HIGH/MEDIUM закрыты. Reviewer лично сверил Core137/Google185/Headless61/Native11 TRX (394/394,0 skipped), дополнительный recording1/1, build0/0, шесть light/dark размеров, narrow comparison, native фото/columns/filters, readonly fallback, финальные видео-кадры20/22/23/24/35/40/50 и ffprobe64.8s/2216×1488.

| Роль Post-EXEC | Disposition / evidence |
|---|---|
| Domain | PASS: 23 категории/111 writable fields, unknown/Google preservation и ручная синхронизация |
| UX/designer | PASS: состояния, narrow/focus/footer, контраст и отсутствие перекрытий |
| Tester | PASS: полные suites, независимые oracles, negative scenarios, PNG/video |
| Architect | PASS: CAS/backup/coherent state pins, draft/restart guards, virtualization/photo bounds |
| Delivery/security | PASS: binding/path isolation, own-PID cleanup и раскрытые границы внешних действий |

Depth / NoFindingJustification: сверены контракты, полный исходный scope, failure/data-preservation counterexamples, dirty/modal/restart transitions, persistence, производительность и actual evidence. Открытых расхождений нет; synthetic/native/fallback разделены. Runtime reviewer danger-full-access/unrestricted/network enabled/approval never; выполнено без edits/build/tests/mutations, технической read-only изоляции нет.

Stop decision: PASS, локальный EXEC завершён; runtime/visual AC1–12 закрыты. На завершении EXEC commit/push/release/install не выполнялись, настоящие пользовательские Google/workspace данные не изменялись. Raw corrupt-file visual — явный Headless fallback; отдельная физическая DPI-матрица не заявляется.

## Approval
Получена новая точная фраза пользователя «Спеку подтверждаю». EXEC разрешён для полного редактора в этом документе; commit/push/release/install не разрешены этим approval.

12.10.2026 получено отдельное поручение «Закоммить»: разрешён локальный commit реализации/tests/docs/SPEC после выполненного rebase; push, release и установка этим поручением не разрешены.

## 20. Журнал действий агента
| Фаза/событие | Решение/основание | Evidence/остаток | Следующее | Решение человека | Артефакты |
|---|---|---|---|---|---|
| SPEC начат,09.10.2026 | Expanded для full editor+config/localwrites | Репозиторийclean d5585c8; schema/storage/UI проинспектированы | Separate review иfixes | Исходное поручение редактора, exact approval ещё нет | Только этотSPEC |
| Post-SPEC завершён,09.10.2026 | 2 HIGH и2 MEDIUM исправлены; targeted re-review PASS | All-fields/effective schema, offline global state, same-lease restart, immutable delete/photo guards; толькоSPEC изменена | Запрос exact approval → EXEC | Ожидается «Спеку подтверждаю» | Только эта SPEC |


| EXEC начат, 09.10.2026 | Exact approval получен; ветка feat/contact-editor | Все AC впереди; source base d5585c8 | Safe storage/schema, table/card, validation | «Спеку подтверждаю» | SPEC и утверждённый код/tests/docs |
| EXEC storage и review fixes, 09.10.2026 | Сохраняем full scope всех полей; независимое review выявило и направило восемь исправлений | Core 135/135 до последних additive tests; final Google185/185, guard22/22; schema generation/check PASS; Desktop первая Release build PASS | Итоговая solution build, Headless/native/PNG/video, повторный review, all AC | Новых разрешений не требуется; внешняя доставка не выполняется | Новые store/schema/table/card/tests; reports/visual evidence ещё впереди |
| EXEC итоговая валидация, 09.10.2026 | Полные suites на итоговом исходном коде и inspected UI/video | Build0/0; Core137, Google185, Headless61, Native11 — 394 PASS/0 skipped; schema/doctor PASS; все AC mapped выше | Финальная separate acceptance-сверка и stop decision | Exact SPEC approval; новых побочных эффектов нет | `docs/VALIDATION.md`, final TRX/logs, ignored PNG/video |
| EXEC завершён, 09.10.2026 | Separate пяти-ролевой advisory/adversarial review PASS, findings нет | Итоговые TRX/build/docs/PNG/video лично сверены reviewer; техническая read-only изоляция отсутствует | Доставить локальный результат пользователю | Внешняя доставка требует отдельного поручения | Финальная AC-mapping и video64.8s, ignored evidence |
| Rebase, 11.10.2026 | Ветка feat/contact-editor перенесена на origin/main3a93ea9; иконка и редактор сохранены | 53 файла сохранены, conflicts0; build0/0; targeted Headless2/2,0 skipped; full394 относятся к pre-rebase | Локальный commit по отдельному поручению | «отребейзь на актуальный мейн» | Ignored `chat-artifacts/editor-rebase-20261011/` |
| Локальная доставка, 12.10.2026 | Conventional Commit для единого результата editor+storage+tests+docs | Generated evidence исключено; функциональные исходники после rebase не менялись | Выполнить commit и проверить SHA/status | «Закоммить»; без push/release/install | Код/tests/docs/SPEC |

# MCP: полноценная работа агентов с ContactMirror

## 0. Метаданные
- Дата: 2026-10-13. Владелец: Павел. Фаза: EXEC после exact approval 2026-10-14.
- Масштаб: large, expanded: публичный API, доступ к данным, несколько процессов, Google, desktop lifetime и упаковка.
- Профили: central `dotnet-desktop-client`, `dotnet-backend-api`; UI validation — существующий AppAutomation workflow проекта.
- Stack: central AGENTS → routing/core QUEST → spec-linter/spec-rubric/review-loops → названные профили. Локальный AGENTS содержит pointer; локальный override не обнаружен.
- Поверхность разработки: Codex, PowerShell, Windows, unrestricted filesystem; runtime/model-specific migration не выполняется. Целевой behavior baseline GPT-6 Astra из central не является утверждением о фактически запущенной модели. Model eval baseline: не применимо, меняется программа.
- База: `docs/release-0-4-0-validation`, HEAD `43624c7`; implementation branch после approval — `feat/mcp-runtime`. Номер публичного релиза этой SPEC не назначается.
- Ограничения: до «Спеку подтверждаю» меняется только этот файл. Реализация не включает push, merge, публикацию, установку, включение MCP в пользовательском профиле или запись в настоящий Google.
- Источники: `docs/FORMAT.md`, `docs/VALIDATION.md`, `docs/RELEASE.md`; исходники в §2; [официальный SDK](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/getting-started.html), [совместимость версий](https://csharp.sdk.modelcontextprotocol.io/v2/versioning.html), [tools](https://modelcontextprotocol.io/specification/2026-07-28/server/tools), [stdio](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports).

## 1. Overview / Цель
Исходное поручение: «Напиши MCP сервер для программы, чтобы агенты могли полноценно взаимодействовать с ней».

Агент подключается стандартным MCP-клиентом, находит контакт по имени/телефону/почте или структурным фильтрам, читает все поля, исправляет нужное поле и фотографию, управляет метками, проверяет изменения Google и применяет выбранные изменения. Затем получает проверяемый результат и при необходимости готовит восстановление из истории. Всё работает при открытом окне и без видимого окна; общие бизнес-правила и сохранение применяются одинаково.

Success means: реальный MCP stdio-процесс проходит этот сценарий на вымышленной рабочей папке и Demo Google, а обычное окно показывает сохранённые изменения. Черновик пользователя, чужой аккаунт, внешние изменения и неопределённый исход записи не затираются.

Output: сервер и общий runtime в solution, настройки подключения в программе, инструкция и готовый пример JSON-конфига, тесты протокола/операций/совместной работы, просмотренные UI evidence. Stop: обязательные AC §11 выполнены и post-EXEC review завершён; невозможность native/installed проверки оставляет соответствующий AC невыполненным, не скрывается за unit pass.

## 2. Текущее состояние (AS-IS)
- MCP/IPC нет. `Program.cs` запускает Avalonia, проверяет Velopack applying marker и, только для установленной сборки, mutex пользователя/PackId. `App.CreateViewModel` создаёт Google/Demo, `SyncCoordinator`, store и VM.
- `MainWindowViewModel` содержит оркестрацию входа, подготовки/apply синхронизации, подтверждения удалений, восстановления и истории. `RunAsync` использует shared `ApplicationActivity`; это блокировка внутри процесса.
- `ContactEditorService` и `ContactEditorStore` уже сохраняют/удаляют с проверками версии документа/фото, manifest/account binding, списка меток и read-only причин. Исходы сохранения: Unchanged, Committed, Uncertain. Повторять неопределённую запись автоматически нельзя.
- `ContactPresentationCatalog`, `ContactTableQuery`, presenter описывают типы, query, пользовательские названия и поддерживаемые поля. Редактор поддерживает 23 категории/111 writable leaves; raw Google snapshot служебный и только для чтения.
- `FileWorkspaceStore.OpenAsync` берёт `workspace.lock` с `FileShare.None`, проверяет пути/аккаунт; может создать manifest и служебную структуру для новой папки. Это не чистая read-only операция. Workspace lock защищает одну папку, но не одновременные Google writes одного аккаунта из разных процессов/папок.
- `SyncCoordinator` хранит prepared plans в памяти, повторно проверяет local/remote inputs, сохраняет backup и journal до записи, различает confirmed/failed/unknown. Его mutex process-local.
- Сейчас Prepare очищает `_plans`, Apply извлекает plan до проверки selection. Поэтому facade с несколькими сохранёнными SyncPreview не обеспечивает несколько настоящих Prepared; это тоже изменяется в scope.
- История sync использует SQLite runs и `.contactmirror/backups`; локальный editor отдельно пишет `.contactmirror/editor-backups`/`editor-trash`. Эти копии сейчас не перечисляются через `GetHistoryAsync`; MCP не должен выдавать их за sync runs.
- `GoogleAccountService`: явный browser OAuth, DPAPI vault, refresh/access tokens. MCP не должен экспортировать vault, токены, OAuth secret или callback параметры.
- `Build-Release.ps1` публикует один WinExe. Production/Synthetic/Integration identities разделены; existing updater/draft guard должны сохраниться.

## 3. Проблема
Нет контрактного входа для агента. Второй независимый экземпляр sync/editor обойдёт общий исполнитель, черновики и update barrier. Нужен MCP фасад существующих сценариев с одним владельцем операций.

## 4. Цели дизайна
Typed discoverable tools, работа без UI automation; общие domain services для UI/MCP; запись с preconditions и receipts; отсутствие неожиданных Google изменений; понятные ограничения и ошибки; сохранение формата контактов и updater identity.

## 5. Non-Goals
Удалённый HTTP/SSE сервис, произвольный shell/SQL/файловый доступ, импорт секретов через MCP, управление установкой/обновлением/релизом через tools, автоматическая синхронизация при старте, автоматическое разрешение конфликтов, обход защиты черновика, массовый unreviewed apply. Версия 1 MCP не управляет черновиком как формой и не имитирует нажатия каждого UI-контрола; все поддерживаемые сохранённые поля доступны domain API.

## 6. Предлагаемое решение (TO-BE)
### 6.1 Ответственности
- `ContactMirror.Mcp` — отдельный console executable, официальный `ModelContextProtocol` 2.2.0, stdio, schemas, tool annotations, mapping результатов; stdout содержит только MCP, диагностика stderr без контактных значений/секретов.
- `ContactMirror.Application/Automation` — commands/query DTO, schema/query adapter, opaque workspace/version/plan handles, operation receipts, CAS и общая оркестрация. `GroupEditorService` дополняет существующий editor для локальных меток.
- `SyncCoordinator`/`ISyncCoordinator` — ограниченный registry настоящих Prepared, разделённый по origin (GUI/MCP handle owner), retain/expiry/consume/invalidation; wrapper не хранит одни DTO вместо действующих планов.
- `ContactMirror.Infrastructure/Automation` — named pipe `CurrentUserOnly`, bounded framing, per-profile configuration/receipt/scratch store, запуск/выбор общего host, проверка path/profile/build handshake.
- Desktop — единственный runtime owner, обычное окно либо `--agent-host` без окна; общие account/sync/editor/activity, UI adapter для draft guards и событий. VM делегирует операции общим services вместо второго бизнес-исполнителя.

### 6.2 Детальный дизайн
#### Запуск и общий host
MCP console подключается к local named pipe своего пользователя и compile-time profile. Если host отсутствует и MCP заранее включён пользователем, запускает соответствующий `ContactMirror.exe --agent-host` скрыто. Gate расширяется на packaged/unpackaged runtime; одновременные старты выбирают одного owner. Проигравший старт подключается к winner. Timeout подключения 15 s, `host_unavailable` вместо независимого fallback с записью.

Background desktop lifetime — explicit shutdown, без MainWindow; показывает обычное окно в том же процессе по `show_window` от обычного ярлыка/экземпляра. Owner сохраняет services при закрытии окна, пока есть MCP подключения или операции. Последний клиент ушёл, операций/окна нет: завершение через 30 s. Background host не включает автосинхронизацию, не показывает OAuth браузер сам.

Общий gate сериализует изменяющие операции UI и всех MCP bridge. Нет двойного lease в facade и service: вводится один общий operation executor, wrappers используют тот же context/lease. Conflict возвращается сразу `busy` с operationId, скрытой неограниченной очереди нет. Status/job polling доступен во время записи; domain reads, требующие workspace lease, могут вернуть `busy`. Длительная работа не выполняется на UI thread.

Pipe handshake: contract version, PackId/profile, build version, executable directory, current owner; несовместимый host даёт `upgrade_required`. Pipe server создаётся с ACL текущего Windows пользователя. Это доверенный локальный доступ в рамках одного SID; различение agent names не является аутентификацией и не защищает от других программ того же SID. Endpoint не публикуется в сеть. IPC admin не допускает изменения permissions, executable paths или произвольный вызов методов.

#### Подключение и разрешения
В настройках: «Доступ для агентов (MCP)», выключен по умолчанию; разрешённые папки, «Чтение», «Изменение локальных контактов и меток», отдельно «Запись в Google», кнопки «Скопировать конфигурацию», «Отключить доступ», connected clients/текущая операция. Конфиг указывает полный стабильный installed `current` путь console exe, аргументы профиля не могут менять compile-time identity. Для source run генерируется полный путь именно текущей сборки. Ни OAuth secret, ни токены в конфиг не входят.

Read permission обязательно, localWrite/GoogleWrite независимо выбираются пользователем; предлагаемый UI default — read + localWrite, GoogleWrite выключен. Startup не включает permission автоматически. Tools list стабилен/упорядочен и не зависит от прошлых вызовов; запрещённые tool calls проверяются также server-side. Изменение settings инвалидирует permission generation, завершается доступ bridge; новые calls старой generation отказаны. Клиенты после изменения reconnect. MCP annotations — подсказки, не enforcement.

Все roots зарегистрированы через UI. `workspace_open` принимает только уже разрешённый canonical root; DTO далее использует opaque handle. Путь нормализуется, reparse/symlink/path traversal и запрещённые app/install roots проверяются существующей policy на каждом фактическом IO. Read/open не создают manifest. Отдельный `workspace_initialize` явно создаёт новую пустую разрешённую папку/binding при подключённом аккаунте; takeover/перепривязка аккаунта не поддерживается.

В UI добавляется компактная панель (visual planning artifact):
```text
Настройки → Доступ для агентов
[ ] Разрешить MCP                 Подключено: 0
Папки: C:\Contacts    [Добавить] [Удалить]
[x] Чтение  [x] Локальные изменения  [ ] Запись в Google
[Скопировать конфигурацию] [Отключить доступ]
Операция: — / «Агент: обновление контакта…»
```
Добавление папки/permissions допустимо только без операции и связанных черновиков. Настройки не выводят личные данные в diagnostics.

#### Контракт tools v1
Имена английские стабильные, title/описания/сообщения русские. Вход JSON schema с запретом неизвестных параметров, typed enum, bounds и обязательными preconditions. Ответ `structuredContent` + краткое text summary для старых клиентов; фото/огромный raw документ не дублируются в text. Envelope: `apiVersion`, `status`, `data`, `warnings`, optional `operationId`, `error {code,message,retryable,fieldErrors}`. Domain errors — `isError=true`; malformed JSON-RPC/unknown tool — SDK protocol error. Неизвестные enums/операторы не заменяются молча default.

| Tools | Inputs / результат |
| --- | --- |
| `app_get_state`, `app_show_window`, `app_open_contact` | Версия/profile, permissions, connection, current workspace/contact, busy, dirty/invalid-buffer/modal flags, update barrier; открыть окно/сохранённый контакт по handle/id, без discard/save draft. `app_open_contact` — только при localWrite permission как управление UI. |
| `account_get_status`, `account_connect`, `account_disconnect` | Account subject/email/issuer, configured/reason. Connect — явно `interactive=true`, OAuth browser job; disconnect `revoke=false` default, explicit true отдельно видно в call. Без сырых credentials. |
| `workspace_list`, `workspace_open`, `workspace_get_status`, `workspace_initialize` | Allowlisted roots/opaque handle, workspaceId, binding/read-only/recovery/issues с pagination; initialize — mutation/requestKey/account precondition. Не меняют выбранную в UI папку. |
| `contacts_get_schema` | Версия схемы, категории, paths, типы, cardinality, enum/type labels, date precision, writable/read-only, колонки и допустимые query operators, ограничения фото/документа; из действующих каталогов, не независимый список. |
| `contacts_list`, `contacts_get` | Handle; list: search, AND/OR typed filters, sort [{column,direction}], projection, take=50/max200, opaque cursor. Get: id, все данные/labels/starred/extensions, read-only Google metadata и issues, версия, photo descriptor, friendly presentation. Фото bytes отдельно. |
| `contacts_create`, `contacts_update`, `contacts_delete` | requestKey UUID; create — UUID на сервере/typed document data, labels/starred/extensions/optional sealed photo; update — id, expectedVersion, patches/optional photo; delete — id, expectedVersion, explicit `confirmDelete=true`, локальный trash/backup. Google вызовов нет. |
| `photo_get`, `photo_upload_begin`, `photo_upload_chunk`, `photo_upload_complete`, `photo_upload_abort`, `values_read` | PhotoGet: id/expectedVersion либо plan-bound photo descriptor, offset/count ≤1 MiB, original bytes base64/hash/mime/total; optionally bounded image preview. ValuesRead: opaque valueId/version/offset/count≤1 MiB, exact UTF8 JSON bytes base64/hash/total. Upload: handle, length ≤20 MiB, SHA256, ordered chunks ≤1 MiB, sealed uploadId; complete проверяет hash/decode JPEG/PNG/store limits. Upload ничего не присваивает контакту; contacts_create/update photo={action:set,uploadId} или {action:remove}. |
| `groups_list`, `groups_get`, `groups_create`, `groups_update`, `groups_delete` | Local group UUID/name/clientData/extensions согласно существующей schema; update/delete expectedVersion/requestKey, immutable служебная Google identity. Delete требует confirmDelete и отсутствие ссылок labels; иначе `group_in_use` + count/страница contact IDs. Membership меняется через contacts_update labels. System Google groups только read-only. |
| `sync_prepare`, `sync_get_plan`, `sync_apply`, `sync_repair_snapshot` | Prepare: handle, account precondition → job → opaque planId. GetPlan pagination, понятные before/local/Google, варианты решения/деструктивность, counts/notices/recovery; review digest. Apply: planId, digest, explicit choices [{entryKey,resolution}], requestKey, deletion confirmation (§7). RepairSnapshot — exact plan/entry/version, только если existing CanRepairGoogleSnapshot, local write permission; не Google write. |
| `history_list`, `history_get`, `history_prepare_restore`, `history_cleanup_backup`, `history_apply_local_restore` | Handle/opaque backupId, source=sync или localEditor, entity IDs, availability/digest. Sync restore готовит sync plan → sync_apply. LocalEditor restore готовит local plan → history_apply_local_restore с planId/digest/explicit selection/requestKey. Cleanup explicit confirm + expected backup digest/requestKey, account/recovery/active-operation guards; localWrite, без произвольных путей. |
| `operations_get`, `operations_cancel` | Opaque operationId; state/progress/item results/runId/commit outcomes. Cancel не обещает откат уже совершённых действий. |

DTO output budget≤4 MiB сериализованных bytes (включая envelope/text), page take — верхняя граница, не обещанное число items. Large raw/friendly field или before/local/Google получает descriptor `{valueId,source,version,mimeType,totalBytes,sha256}` вместо inline значения, с явным marker `externalValue=true`. Inline threshold64 KiB, photo bytes никогда не копируются из `PhotoComparison` в tool DTO: отдельные local/remote/planned descriptors читаются photo_get. Friendly summary≤4 KiB с явным summary marker, исходные данные доступны целиком. ContactsGet не дублирует полный document в friendly/text; contacts/list/plan/history/error output также byte-bounded. Если page не помещается, возвращается fewer items+cursor; один большой entry остаётся descriptor-based, без потери полей. Values привязаны к immutable captured contact version или actual Prepared source/plan, TTL15min/shared retained budget §6; drift/expiry даёт value_stale, не байты другой версии. Нет arbitrary URI/path читателя. Тест собирает exact JSON/photo из chunks и сверяет hashes. Превышение bounded payload без корректного descriptor — resource_limit, не обрезанное successful JSON.

Scopes: reads требуют read; локальные мутации/repair/cleanup/initialize/uploads — localWrite; account connect/disconnect — GoogleWrite (это explicit credential change), sync_prepare/restore — read и подключённый аккаунт, sync_apply дополнительно localWrite и GoogleWrite, если выбран хотя бы один remote write. `app_show_window` допустим с read. Права проверяются и после ожидания gate, до каждого следующего remote write; после отзыва already-issued write фиксируется журналом, последующие не запускаются.

Annotations задаются по фактическим эффектам, не по scope: get/list/schema/status — readOnly; prepare/restore staging — readOnly=false из-за cache/metadata; create/update/delete/restore/apply/cleanup — readOnly=false, destructiveHint=true для возможного replace/delete; local tools openWorld=false, Google interactions openWorld=true. IdempotentHint=true только там, где required requestKey/receipt реально исключают повторный эффект. Resources/prompts/subscriptions не обязательны v1: полнота domain workflow обеспечивается перечисленными tools; фото original bytes доступны через photo_get, без произвольных resource URI.

#### Поля и query
Patch `{op:add|replace|remove,path,value?}` — JSON Pointer только по writable root `data`, `labels`, `starred`, `extensions`, включая массивы и вложенные typed fields. Изменение `id/schemaVersion/google/photo` path запрещено; photo задаётся отдельным параметром. Полное replace массива/категории разрешено и проверяется schema. Отсутствующее поле не заполняется default; unknown existing extension/data поля сохраняются при незатронутых paths. Для unknown leaf внутри поддерживаемого data API не позволяет новое unsupported поле: `unsupported_field` с nearest schema path; произвольные JSON данные допустимы внутри extensions в пределах размера. Read возвращает unsupported данные без потери.

Source change обязательна: сейчас DocumentCodec.Contact/CapabilityRegistry.ValidateEditable строги, поэтому adapter-only не реализует preservation. Вводится **baseline-aware editor read/save compatibility path** в Core validators и ContactEditorStore inventory/Read/Save/rechecks/ClassifySaveFailure. Неизвестная root category или известная category с неизвестным leaf/legacy invalid cardinality читается как opaque protected category; **вся эта category** immutable, semantic subtree equality с original baseline обязательна. Это исключает потерю unknown при reindex/replace массива. Остальные известные категории, labels/starred/extensions/photo можно редактировать с обычной schema и guards; создаваемый контакт strict, добавлять/изменять/удалять protected category нельзя. Contact DTO возвращает protectedCategories/reasons. Service history/local restore также не удаляет unknown текущую category; конфликт с backup маркируется blocked. Глобальная sync/Google payload validation остаётся strict: unsupported/legacy category блокирует соответствующую синхронизацию с явным issue; MCP не выкидывает её из upload и не обещает поддержку новых Google fields. CAS/binding/Google immutable/state guards не ослабляются, UI editor использует тот же compatibility path и показывает readonly категории. Unknown сохранённые значения защищены semantic equality; перестановка JSON properties/formatting допустима, значения/точность не меняются.

Дата с неизвестным годом остаётся частичной; custom type/label, повторные телефоны/email/организации/адреса, phonetic и numeric/raw precision сохраняются. Cardinality из действующей Google модели: names/birthdays/biographies/genders max1; новые multi-name записи не разрешаются. Legacy invalid multiplicity только readonly protected category по compatibility path выше, не поддержанный create/update/sync. Patch сначала применяется к копии, валидируется целиком, затем один Save; несколько patch не дают частичного контакта. Версия непрозрачна и включает document/photo hashes, workspaceId/binding/labels revision/account; SHA не является client разрешением. На conflict возвращаются currentVersion и причины, без автоматической замены текущим документом.

List использует `ContactTableQuery` и semantic values из действующего presentation catalog, UI aliases тоже доступны. Результат без bytes/photo/full raw по умолчанию, только projection+id+version. Устойчивая сортировка с id tie-breaker. Cursor привязан к query/revision, TTL 60 s; на drift/expiry `cursor_stale`, первая страница заново. Schema/list/plan pagination не перескакивают тихо на другую revision.

Limits v1: JSON transport frame ≤32 MiB, doc ≤16 MiB по существующему store, patch ≤64 операций, search ≤512 chars, filters≤32, sort≤8, projection≤32; JSON depth≤64. Фото ≤20 MiB, chunk≤1 MiB, upload TTL10 min/не более двух uploads на bridge; shared total scratch≤80 MiB. Plan TTL15 min (не sliding)/не более8 retained plans на profile; aggregate retained serialized documents/photo bytes≤512 MiB, превышение `resource_limit` без write/apply и без тайного усечения entries. Освобождаются expired/consumed plans; вытеснение ещё действующего handle явно `plan_expired`. Existing GUI plan не уничтожается из-за agent quota. Job/status pagination≤200. Превью/фото/notes считаются данными, не инструкциями агенту; descriptors предупреждают об untrusted contact text. Корневые пути/секреты не извлекаются из contact content.

#### Jobs, disconnect, idempotency
Long actions connect/prepare/apply/restore/cleanup возвращают operationId сразу и проверяются polling; собственный domain job API доступен legacy MCP, protocol Tasks extension не требуется. Статусы running/cancelRequested/completed/failed/unknown; terminal result содержит confirmed/failed/unknown по существующей модели, actual saved versions/runId.

Каждая **durable domain mutation** требует requestKey UUID: contacts/groups CRUD, workspace_initialize, account connect/disconnect, sync_apply/repair, local restore и cleanup. До side effect persist receipt с canonical request hash, permission generation, root/workspace/account identifiers; для create заранее выделяется entity UUID, записываются operation type, expected/intended hashes и backup/run references для доказательства результата после crash. Тот же key/те же inputs возвращает прежний operation/result, иной payload → `idempotency_conflict`. Lookup receipt выполняется до повторной валидации volatile upload/plan handles (но после permissions/root/account checks), чтобы завершённый call можно было прочитать после их expiry. Для completed **account transition** replay проверяется recorded before→after identity, фактическое состояние должно совпадать с recorded after; current precondition для новой execution не используется для replay. Иная actual identity → account_changed без нового effect/чужих данных; scopes/profile проверяются всегда. Completed receipt хранится 30 дней, после expiry повтор с тем же UUID отвергается (`request_key_expired`, compact tombstone без контактных значений); key никогда не становится новой записью автоматически. Receipt store profile-local SQLite, same-user ACL, не содержит фото/полных contact values/tokens. Размер≤1 GiB; при исчерпании `receipt_store_full`, новые mutations не выполняются, tombstones не забываются молча. Unknown не автоматически retry. Crash с running receipt → unknown/recovery_required; локальная проверка уже записанных hashes и существующего sync journal может уточнить исход, но не запускает remote write.

Transient/UI policy: show_window/open_contact — без durable requestKey, повтор idempotent по целевому window/contact, read/localWrite соответственно, guards каждый раз. UploadBegin принимает transient uploadKey UUID, same bridge+hash/length возвращает прежний uploadId до TTL; chunk/complete/abort idempotent по uploadId/index/hash, localWrite и bridge owner обязательны, после reconnect/TTL нужен новый upload (completed durable contact replay от него не зависит). Prepare/restore prepare без durable requestKey, повтор создаёт новый bounded plan/job, effects ограничены metadata/cache; read и bound account нужны. OperationsCancel требует scope исходной операции и opaque operationId; originating bridge cancel или reconnect с совпадающим requestKey (ровно один target) может отменить только свой job. Другой bridge не отменяет чужую работу по одному подсмотренному id; status read доступен same-user permissions. Terminal cancel — successful no-op с actual outcome, повтор cancel идемпотентен, чужая/недоступная operation — operation_not_owned без раскрытия payload. UI-origin операции MCP не отменяет. Cancel не отзывает уже issued writes и не требует durable receipt для самого cancel.

EOF/закрытие bridge отменяет принадлежащие ему невыданные операции; уже начавшаяся mutation доходит до безопасной точки journal/commit, затем cancelRequested. Host не убивается, пока исход не зафиксирован. Reconnect может прочитать receipt по requestKey через `operations_get` (принимает operationId или requestKey, ровно одно), сохраняя allowlist/account checks. Совместный SID является owner; clientInfo/session не security boundary. PlanId не переживает host restart, `plan_expired` требует новое prepare.

### 6.3 User-Observable Scenarios
| Scenario | Trigger | Видимый результат | Evidence | AC |
| --- | --- | --- | --- | --- |
| Полный исходный сценарий | MCP client → поиск → edit всех типов/фото/меток → prepare → choices/apply | Сохранённый контакт и Demo Google соответствуют, actual receipt | stdio integration trace с вымышленными данными + native PNG/video | A1–A5,A9 |
| Без окна | MCP старт после enabled configuration, затем обычный ярлык | Один host, окно показывает тот же контакт | process/pipe tests + ordinary executable trace | A6 |
| Пользователь редактирует | Несохранённая карточка/invalid buffer; agent write/sync/account change | draft_conflict, UI значения не изменены | headless/native paired evidence | A7 |
| Два агента/внешняя правка | Одновременно mutate или stale version | busy/conflict; нет lost update | multiclient/external edit tests | A8 |
| Неопределённая запись | Google timeout/crash/EOF + повтор requestKey | unknown, journal/history, повтор не создаёт дубль | fault injection/restart tests | A10 |
| Настройка доступа | Enable/copy config/revoke/update restart | Правильный путь/profile; доступ отозван; updater требует закрыть bridge | UI screenshot/packaging smoke | A11,A12 |

### 6.4 State / Interaction Matrix
| State | Trigger | Transition / error | Notes |
| --- | --- | --- | --- |
| Disabled/no permissions | bridge startup | mcp_disabled, без host mutation/OAuth | Setup в обычном UI |
| Enabled/no host | bridge startup | owner election → ready либо host_unavailable | No write fallback |
| Existing 0.4/incompatible host | bridge attach | upgrade_required | Старый UI не обходится |
| Clean/idle | mutation | lease → CAS → commit → UI invalidation → receipt | Background events Dispatcher |
| Dirty/invalid/modal in target workspace | mutate same contact; sync/account/initialize | draft_conflict/ui_modal_busy | Agent не dismiss-ит modal |
| Dirty other contact | update unrelated contact same workspace | разрешено, список обновляется без reload draft | Group/account/sync затрагивающие labels блокируются |
| Busy/applying update marker | domain write / host start | busy/update_in_progress | Status доступен, update recovery owner прежний |
| Revoke/EOF mid remote request | boundary | фиксируем issued write, прекращаем последующие | cancel != rollback |
| Group referenced | delete | group_in_use | Сначала явные изменения labels и новая проверка |
| Agent session connected | UI ApplyAndRestart | отказ «Отключите MCP-клиенты перед обновлением» | Windows не обновляет загруженные bridge binaries |

### 6.5 Decision Ledger
| Decision | Owner | Выбор | Confidence | Risk | Needs user before EXEC |
| --- | --- | --- | ---: | --- | --- |
| Полный режим с окном/без окна | agent, scope утверждается этой SPEC | Предложен в async вопросе; ответа пока нет, выбран по исходному «полноценно» | .85 | Только UI режим оказался бы уже нужды | Нет отдельного вопроса; exact SPEC approval фиксирует scope |
| Транспорт | agent | local stdio + shared current-user pipe, официальный SDK2.2.0 | .95 | Compat клиента доказать тестом | Нет |
| Owner | agent | Desktop process с optional window, общий executor | .9 | Lifetime/updater regression | Нет, AC6/12 обязательны |
| Permissions | agent | explicit GUI enable/allowlist, defaults local read/write, Google отдельно off | .9 | Setup один раз требуется | Нет |
| Delete group | agent | restrict if referenced, clear labels explicit | .9 | Несколько вызовов для удаления populated label | Нет |
| Update while clients connected | agent | clear blocker, disconnect clients then update | .9 | Постоянно подключённый агент требует перезапуска клиента | Нет |
| Live writes / publishing | user | Не входят в реализацию | 1 | Отдельные side effects | Нет, не выполняются |

### 6.6 Runtime / Config / Data Contract Matrix
| Area | Source of truth | Изменение | Compatibility | Verification |
| --- | --- | --- | --- | --- |
| Contact schema/types | core/schema + catalog | typed tools adapter | schemaVersion1 unchanged | all leaf contract coverage |
| Account/vault | GoogleAccountService + DPAPI | shared runtime status/explicit commands | Existing storage paths/profile | token redaction/signin cancellation |
| Workspace | manifest/store/path policy | registered handles/no-init reads | Existing binding/locks/backup | wrong root/account/reparse tests |
| Plans | SyncCoordinator retained prepared inputs | opaque TTL handles + review selection | Existing prepare/apply revalidation | stale local/remote tests |
| MCP | official SDK pinned2.2.0 | stdio contracts v1 | legacy2025-11-25 +2026-07-28 SDK negotiation | actual clients for both versions |
| Settings/receipts | new profile-local automation store | version1 config, receipt/tombstones | Missing config=disabled; malformed=blocked | restart/crash/permissions tests |
| Package/updater | ApplicationPaths/Velopack/Build-Release | include bridge, profile-matched host | Same PackId/feed, no installed activation now | Synthetic package/profile smoke |

## 7. Бизнес-правила / Алгоритмы
1. Перед mutation: permission → allowlisted handle+bound account → draft/modal/update barrier → shared lease → reread/CAS → backup/journal → write → actual result. Guard повторяется под lease; UI начало редактирования и broker запись не могут пройти независимо. Dirty state регистрируется в runtime с монотонной generation на UI dispatcher; commit mutation проверяет captured generation, UI запрещает начало edit для занятой entity/root до освобождения lease. Account/sync/group-label changes требуют root-wide guard; unsafe future unknown scope блокируется.
2. Чтение показывает **сохранённое** состояние; app state отдельно dirty. Background без UI работает теми же services, не fake VM command clicks. UI event после commit invalidate preview/history/list; reload карточки только если clean и commit target тот же, draft сохраняется.
3. Apply принимает только выбранные entries из server-owned plan, no empty/all defaults, digest связывает план, account/workspace, entries и choices. `sync_get_plan` принимает optional choices и выдаёт selectionDigest, который требуется sync_apply; base planDigest тоже возвращается. Unresolved conflict не Automatic. При stale Google/local/version reject, новый prepare. Деструктивные selections требуют `confirmDeletions=true`; ≥10 либо≥20% contacts дополнительно exact phrase `УДАЛИТЬ`, как существующий UI. Эти поля — явное намерение MCP caller, не доказательство отдельного human approval. Окончательное разрешение клиенту принадлежит host MCP клиента и enabled permissions.
4. Ни один CRUD tool сам не синхронизирует Google. Repair snapshot существующего допустимого entry — local repair, explicit selection. Restore всегда preview/choices/apply, не immediate overwrite.
5. Partial apply/cancel сохраняет поэлементные исходы; completed job может содержать failed/unknown и не считается полностью успешным. Tombstones/receipts не входят в Google payload. Неопределённый исход — отдельный тип, не generic retryable error.
6. Groups update/delete требует CAS группы и labels revision; backup before edit, file expected hashes, account binding и existing recovery gates. Не добавлять cascade write без отдельного атомарного плана.
7. Upload handles только временные bytes этого профиля; path не передаётся клиентом, hash/size/decode до sealing, symlink/reparse запрет; abort/expiry убирает только проверенный scratch path. Receipt относится к mutation контакта, upload retry chunk с тем же index/hash не дублирует bytes.
8. MCP history adapter объединяет существующие sync runs и editor backups без миграции их формата и без изменения UI истории в этой задаче. Old editor manifest проверяется по workspaceId/entityId/path/hashes/schema; отсутствие before.json означает отсутствие восстанавливаемого прежнего контакта, не фиктивную копию. Для local restore подготавливаются writable data/labels/starred/extensions/photo и версии текущего контакта; Google snapshot/baseline/state никогда не откатываются этим tool. Deleted contact можно восстановить с тем же UUID только если identity свободна, account/workspace/labels/recovery guards разрешают запись; иначе blocked entry. Group local backups используют explicit entityKind в новом manifest. ApplyLocalRestore создаёт новую backup/receipt и применяет только явную selection с CAS; partial result по каждой entity, без обещания атомарности нескольких контактов. Local backup cleanup запрещён при pending/unknown операции с её reference, не удаляет journal/state/trash автоматически. Sync restore/cleanup сохраняют existing coordinator правила.
9. Prepared registry: новый Prepare/Restore другого caller/root не очищает чужие планы; GUI Prepare заменяет только прежний GUI plan. MCP quota/TTL §6 применяются к actual Prepared. Невалидная selection/permission/request digest до side effect не consume-ит plan. После успешного начала apply plan consume-ится и requestKey связывается с receipt; повтор только receipt. Local/remote commit или repair инвалидирует планы затронутого root/account с `plan_stale`; account disconnect — account_changed, host restart/TTL/eviction — plan_expired. Признак причины сохраняется bounded tombstone на срок TTL для понятной ошибки; нет silent DTO reuse. Во всех случаях existing ValidateInputsAsync остаётся окончательной CAS/remote revalidation.

## 8. Точки интеграции и триггеры
Общие commands вызывают UI Connect/Disconnect/Check/Apply/Restore/Repair/Cleanup и MCP; editor save/delete/new используют тот же executor. `Program` instance/update startup, `App` lifetime/composition, `RequestCloseAsync` и restart guard учитывают bridge lease. Dirty/modal changes и UI account/folder changes публикуют guard generation. Account change/disconnect инвалидирует старые plan/workspace handles; account tools требуют expected current account key (или explicit null при первом входе), а не произвольно переключают чужую текущую сессию. Folder change UI не переносит MCP handle на другую папку. Settings disable блокирует новые calls, просит клиентам завершить bridge, не убивает Google request. File watcher/external hashes инвалидируют cursors/plans; watcher является оптимизацией, CAS — окончательная проверка.

## 9. Изменения модели данных / состояния
Contact/group schemaVersion, UUID и Google snapshots не мигрируют. New persisted automation config/receipts в per-profile appdata, scratch bounded/ephemeral, без переносимых auth tokens. In-memory workspace handles/plans/query cursors содержат revision/owner profile; не авторизация сами по себе. Failed config fail-closed с понятной причиной. Operation DTO version1, IPC version2, MCP API version1; неизвестные future major versions отказаны. В EXEC handshake уточнён до IPC2: immutable loaded MVID fingerprint общих assemblies дополняет app/profile/SID/build/full host path. Публичный API1 и прежний contact format сохраняются; существующего MCP клиента0.4 нет.

## 10. Миграция / Rollout / Rollback
Новая сборка запускается с MCP disabled и старой папкой/credentials без миграции. Пользователь позже включает в UI и копирует конфиг. Данная задача создаёт и проверяет binaries/package на synthetic профиле, не меняет installed Production settings, feed, Google или пользовательский MCP client config.

Rollback: выключить MCP, дождаться settled operations/закрыть bridge/host, вернуть предыдущую сборку. Contact edits восстанавливаются existing history/backup preview; Google restore не автоматический. Прежний формат совместим. До downgrade зафиксировать unknown journal: downgrade не означает rollback данных/remote writes. Новые config/receipts старая сборка игнорирует; cannot pretend old0.4 supports MCP.

## 11. Тестирование и критерии приёмки
### Acceptance-to-Test Matrix
Исходная матрица ниже определяет утверждённые обязательные проверки. Actual evidence и окончательный статус приведены в Post-EXEC Review и [MCP-VALIDATION](../docs/MCP-VALIDATION.md); planned формулировки не заменяют runtime evidence.
| AC | Automated evidence | Manual/visual/log evidence | Artifact / stop |
| --- | --- | --- | --- |
| A1 Реальный MCP discover/tools/read+mutate | Новые McpTests запускают console exe через stdio SDK, обе protocol revisions, malformed/error/EOF/stdout purity; oversized single contact/plan entry/page, descriptors+exact chunk reconstruction, output≤4MiB | Exact command/config с пробелами в пути | sanitized trace/TRX |
| A2 Поиск/filter/sort/pagination+schema | Query parity, AND/OR/aliases/repeated/raw types, cursor drift/expiry, stable ties | Найден contact по phone/mail без ID | query trace |
| A3 Все writable fields/unknown preservation | Matrix111 leaves, arrays/customlabels/partial dates/extensions; max1 categories rejected for new contact; existing unknown root/leaf+known OTHER category edit preserves raw value precision, protected category patch rejected; legacy multiplicity protected, sync explicitly blocked; forbidden google/id, no default injection, invalid patch atomic rejection | Roundtrip seeded multi-field contact и UI readonly opaque category | fixture diff/hash |
| A4 Фото | JPEG/PNG content-level update/samepath/delete/create; chunks/dedup/TTL/hash/20MiB/invalid decode/reparse cases | UI новая фотография совпадает с MCP saved hash | inspected PNG+hash |
| A5 Метки+Google lifecycle+history | group CRUD/referenced delete/system readonly; Demo create/update/photo/group memberships/conflict/delete/restore/cleanup/snapshot repair через MCP; sync/localEditor sources, restore deleted local contact/photo, missing/corrupt before, identity collision, no Google baseline rollback, protected cleanup | before/choices/after receipt totals | Demo gateway trace+history |
| A6 Один host с окном/без окна | two startups/multiclient/SID-profile/build isolation; no-head window promotion/close/idle/reconnect | ordinary ContactMirror.exe из synthetic publish, MCP start→ярлык→тот же owner | process trace/native PNG |
| A7 Черновик/modal/invalid buffer защищены | guard races UI vs MCP including clean→dirty before commit, unrelated contact allowed, related group/account/sync blocked | Native unsaved field retained, понятный отказ | paired video/PNG |
| A8 CAS/account/root/gate | wrong root/account/binding/labels/photo/remote drift, busy/doublelease/deadlock; A prepare→B prepare→A get/apply и GUI prepare, restore/repair invalidation, quota/expiry/invalid selection не consume-ит plan | External change survives stale call; plan error содержит настоящую причину | negative trace |
| A9 UI parity/refresh | existing Headless+Native regressions, MCP commit invalidates GUI preview/updates clean card/no dirty reload | screenshot MCP panel + reflected saved edits | inspected PNG/video |
| A10 Recovery/idempotency/cancel | fake gateway timeout before/after commit; crash+receipt restart; same key/different payload; completed key after plan/upload expiry; connect/disconnect lost-response replay после identity transition и отказ другой actual identity; expired tombstone/store full; EOF during write/no duplicate | report confirmed/failed/unknown distinctly | journal/receipt sanitized evidence |
| A11 Permissions/no-secret/stdout | disable/revoke during calls, actual operation enforcement despite annotations; read-only/UI/upload scope, cross-bridge/UI-origin cancel rejected, own/reconnect/terminal cancel; size/depth/fuzz-ish bounded invalids; token redaction | config matches path/profile/scopes; API failure says next step | generated JSON/negative trace |
| A12 Package/updater preserved | synthetic publish includes bridge/runtimeconfig/deps with compile profiles; update marker/activeclients/job/draft gates, no bridge file replacement while alive | synthetic local package install/start paths and ordinary update barrier; no Production install | package manifest/native trace |
| A13 Full regression/build | sequential Release build + all4 existing suites + new MCP suite, zero unexplained failures/skips | Evidence distinguishes unit/headless/native/package from live Google | validation report |

Commands after approval (actual project paths verified; new project paths created during EXEC):
```powershell
dotnet build ContactMirror.sln -c Release
dotnet test tests/ContactMirror.Tests -c Release --no-build
dotnet test tests/ContactMirror.GoogleTests -c Release --no-build
dotnet run --project tests/ContactMirror.McpTests -c Release --no-build -- --maximum-parallel-tests 1
dotnet run --project tests/ContactMirror.UiTests.Headless -c Release --no-build -- --maximum-parallel-tests 1
dotnet run --project tests/ContactMirror.UiTests.FlaUI -c Release --no-build -- --maximum-parallel-tests 1
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.5.0-mcp.validation -ValidationProfile Synthetic
```
McpTests runner выбрать/закрепить consistent TUnit UI workflow; no VSTest filter. Shared Avalonia build/tests последовательно. Обычные launched publish exe + stdio config, без TestHost-only обхода lifetime/identity. Synthetic package installation только isolated Validation identity/data; Production installation отдельным разрешением, не обязательный AC этой задачи.

Performance: bounded query response без фото/fullraw, chunking; existing 10k fixture, report timings/memory baseline и new; UI responsive во время job polling. При regression >20% одинакового fixture разобрать причину перед finish, не бесконечный benchmark.

Visual acceptance: settings panel соответствует wireframe, permission distinction видна, status не перекрывает contact editor; inspect actual rendered native PNG. Paired video existing no-MCP baseline → after MCP workflow. Если recording/native недоступны, сохранить fallback screenshot/headless trace с явным limitation; A6/A7/A9 native часть остаётся невыполненной, не PASS. Live OAuth/реальные Google изменения не тестируются; применяются Demo/HTTP fake существующего gateway, граница указана в final.

Test loops: после зелёного обязательного набора не повторять без изменения; failure фиксировать exact command/exit/error, исправлять in-scope и повторять затронутое. Функциональный contract/security failure блокирует finish; нельзя заменить AC низким severity/follow-up. Unrelated baseline failure требует доказанной baseline, остаётся validation gap.

## 12. Риски и edge cases
Самый большой риск — unified runtime lifetime с updater/draft guard; A6/A7/A12 обязательны. Other risks: permission revocation mid-issued Google write (journal, stop next), protocol legacy downgrade (actual client test), corrupted credentials/config (fail closed), lossy fields (schema-wide roundtrip), concurrent external program (CAS), large photo/message memory (bounded chunks), root binding read side effects (separate initialize). Plan prepare может обновлять существующие metadata/cache, tool description честно называет это, не помечает strict filesystem read-only. Restore prepare также non-destructive staging, но не абсолютное zero-write.

### Expected User Review Objections
| Objection | Почему вероятно | Mitigation | Status |
| --- | --- | --- | --- |
| «Это снова только JSON, агент не понимает типы» | Недавняя боль editor | schema/catalog/type labels/friendly typed get, all111 roundtrip | mitigated |
| «Почему нужно окно держать открытым?» | Агент работает автономно | background host, promotion ordinary shortcut, native test | mitigated |
| «Агент затёр то, что я печатал» | Общий workspace | server draft generation/lease+native guard race tests | mitigated |
| «Фотография/метки/восстановление недоступны» | Full interaction request | bounded uploads/group CRUD/restore plan included | mitigated |
| «Повтор вызова создал два контакта» | Connection loss/retries | durable receipts/tombstone/no unknown retries | mitigated |
| «Установленная программа сможет обновиться?» | Existing release workflow | profile-matched bridge packaging, clear disconnect-before-restart policy | mitigated; client disconnect требуется |

Rework checklist: outcome named §1; scenarios→evidence §6.3/11; assumptions §6.5; objections здесь; five roles §19; AC являются проверками; EXEC имеет обычный stdio+desktop flow. Approval разрешает scope, не выполнение живых агентских команд над личными контактами.

## 13. План выполнения
1. Shared application commands/executor, UI guards/event adapter; characterize existing sync/edit/updater parity.
2. Group editor/schema/query/version adapter, permission/receipt/jobs/plan contracts и tests.
3. Owner/pipe/console SDK, headless desktop lifetime/promotion; stdio/multiclient tests.
4. Typed tools/photo/workflow, settings/config generation, bundle publish, docs.
5. Complete synthetic original workflow, all suites, ordinary native/package/visual evidence, independent post-EXEC review; fixes/recheck только affected surface. Не откладывать mandatory tool families как «следующая версия».

## 14. Открытые вопросы
Блокирующих user-owned решений нет. Async уточнение режима задано; если до approval придёт ограничение, корректируется scope этой SPEC. Выбран полный режим §1/6.5, он показывается пользователю при approval. Никакая пауза ожидания не является approval.

## 15. Соответствие профилю
Desktop: no blocking UI thread, stable existing automation IDs, updated native/headless workflows, build/tests. Backend API: versioned explicit tools, schema/contracts, permissions/errors/idempotency/fault tests. AppAutomation будет применён при EXEC с объявлением/read skill; до approval tests/build отсутствуют по QUEST.

## 16. Таблица изменений файлов
| Файл / область | Изменение | Причина |
| --- | --- | --- |
| `src/ContactMirror.Mcp/*`, solution | Console SDK host/tools/config metadata | protocol facade |
| `src/ContactMirror.Application/Automation/*`, Contacts contracts, new GroupEditorService | Common commands/DTO/guards/versions/plans/receipts | UI/MCP parity |
| `src/ContactMirror.Application/SyncCoordinator.cs`, Contracts/plan lifecycle API | Actual multi-plan Prepared registry, origin/TTL/consume/invalidation reasons | Existing `_plans.Clear()` не совместим с concurrent callers |
| `src/ContactMirror.Core/DocumentCodec.cs`, `CapabilityRegistry.cs`, editor store read/inventory/save/recheck/classification | Baseline-aware protected unknown-category editor path, strict new/sync validation retained | Existing strict parser не позволяет обещанное preservation |
| `src/ContactMirror.Infrastructure/Automation/*`, configuration/store adapters | IPC/config/receipt/scratch/verified group writes | same-owner access |
| Desktop Program/App/VM/Contacts navigation/preferences/settings/update guards | host lifetime/config/UI events | one executor/draft safety |
| `scripts/Build-Release.ps1`, Directory.Build props as required | profile matched console payload/staging/docs/licenses | usable distribution |
| `tests/ContactMirror.McpTests/*`, existing4 suites/UI scenarios | protocol/integration/negative/regression | AC matrix |
| `README.md`, `docs/MCP.md`, `docs/FORMAT.md`, RELEASE/VALIDATION | setup, schema/read-only vs writes, results and limits | user-operable product |
| Эта SPEC | approval/evidence/review journal | QUEST trace |
План области файлов, не разрешение на unrelated refactor; existing schemas меняются только если документация контрактов требует уточнения, не migration.

## 17. Было → стало
| Область | Было | Стало |
| --- | --- | --- |
| Агент | Редактирование файлов/клики вручную | typed tools, explicit plans, saved receipts |
| Два процесса | Separate activities/Google race | Один runtime owner/profile |
| UI lifecycle | Visible window required | Background owner + обычное окно того же owner |
| Draft | UI-only guard | common mutation guard |
| Package | Desktop only | Desktop + console, existing update identity |

## 18. Альтернативы и компромиссы
- Independent stdio service прямо создаёт stores/Google: проще, но не знает UI draft и не сериализует Google из другого root; отклонено.
- Только pipe к открытому окну: меньше lifecycle работы, но не удовлетворяет выбранный unattended сценарий; отклонено.
- Отдельный обязательный Windows service + GUI client: больше установки/административных разрешений, другой lifetime/identity; для local user задачи излишне.
- HTTP remote service: расширяет auth/deployment/network scope без запроса; отклонено.
- Выбран owner Desktop+thin console: lifecycle требует tests, зато existing account/activity/updater остаются в одном процессе. Update restart требует отключить bridge — явный bounded tradeoff.

## 19. Результат quality gate и review
### SPEC Linter Result
| № | Статус | Evidence |
| --- | --- | --- |
| 1 | PASS | §1/6.3 исходный агентский сценарий |
| 2 | PASS | §2 source contracts/locks/lifetime |
| 3 | PASS | §3 отсутствие входа/единого owner |
| 4 | PASS | §4 typed/parity/safety |
| 5 | PASS | §5 no remote/update/admin tools |
| 6 | PASS | §6.1 module owners |
| 7 | PASS | §8 UI/broker/update triggers |
| 8 | PASS | §7 invariants |
| 9 | PASS | §6 jobs/§12 recovery |
| 10 | PASS | bounds §6, timings §11 |
| 11 | PASS | §9 state/persistence |
| 12 | PASS | §10/legacy protocol |
| 13 | PASS | §10 disable/data restore boundary |
| 14 | PASS | A1–A13 verifiable outcome |
| 15 | PASS | matrix incl crash/dirty/CAS |
| 16 | PASS | commands/stop §11 |
| 17 | PASS | dependencies §13 |
| 18 | PASS | ledger/questions §6.5/14 |
| 19 | PASS | expanded large §0 |
| 20 | PASS | desktop/API profile §15 |
Итог на авторском pass: ГОТОВО к review, не разрешение EXEC.

### SPEC Rubric Result
| Критерий | Балл | Evidence |
| --- | ---: | --- |
| Goal/bounds | 5 | §1/5 |
| AS-IS | 5 | inspected shared activity/workspace lock/Program/App |
| Design | 5 | host/tools/data/permissions contracts |
| Safety/rollback | 5 | CAS/receipts/guard/profile/§10 |
| Tests | 5 | A1–A13 actual process/native/fault matrix |
| Autonomous decisions | 5 | ledger/defaults/no blocked question |
30/30 авторская оценка; reviewer/adversarial fallback по central review и exact approval обязательны; оценки не заменяют их.

### Role-Based Review Result
| Role | Applicability / question | Verdict | Required changes |
| --- | --- | --- | --- |
| Business analyst | Full contact→sync→restore workflow | PASS author | all tool families + explicit choices |
| UX/designer | setup obvious, no draft loss | PASS author | wireframe/settings/copy state/dirty reason/native evidence |
| Tester | Real protocol + lifecycle + negative paths | PASS author | A1–A13 not unit-only |
| Architect | Shared owner, format/API compatibility | PASS author | shared executor, no second Google owner |
| Delivery/security | Secrets/permissions/profile/update limits | PASS author | local ACL/allowlist/config/receipt/update barrier |

### Post-SPEC Review
- Scope reviewed: эта SPEC, central QUEST/review/linter/rubric/canonical template, desktop/backend profiles; Contracts, ContactEditorContracts/ApplicationActivity, WorkspaceContracts/Core Models, SyncCoordinator.Apply, FileWorkspaceStore.Open, ContactEditorStore guards, GoogleAccountService, Program/App/VM connect-check-apply, Desktop csproj, query/catalog, Build-Release, FORMAT/VALIDATION; official SDK/versioning/tools/transport and stable package versions.
- Contract pass: full program domain access preserves source formats/guards; live writes/publish/installation activation excluded; protocol and native acceptance explicitly planned, not asserted.
- Adversarial pass: independent stores race even with one root lock; read initialization side effect; UI clean→dirty race; EOF after issued write; permission revoke; stale plans; updater bridge DLL locks. Решения вписаны §6/7/11.
- Findings table: author HIGH/runtime race → single owner, closed in spec; HIGH/draft race → lease+generation and UI edit barrier, closed; HIGH/retry ambiguity → durable receipt/tombstones, closed; MEDIUM/unbounded photo → upload chunks/limits, closed; MEDIUM/update lifetime → explicit client disconnect policy, closed. Implementation proof pending EXEC.
- Fix and re-review author: проверены affected lifetime/operation order/tool inputs/AC6–A12, отсутствует bypass to independent writer; unknown outcome не retryable.
- Reviewer/adversarial fallback: технически read-only reviewer недоступен в данном unrestricted runtime. Первый `/root/ux_spec_review` дал concrete Prepared registry finding, затем потерян при смене runtime (`unknown agent_type independent-reviewer`, inventory только root). Новый `/root/mcp_spec_review` procedural no-mutation review прочитал SPEC целиком и реальные Program/App/ApplicationPaths/DraftRestartGuard/ContactEditorStore/DocumentCodec/CapabilityRegistry/ContactTableQuery/SyncCoordinator/Repair/Build-Release. Он не изменял файлы/Git/внешнее состояние и не запускал builds/tests. Это отдельный advisory/adversarial fallback, не независимая sandbox read-only изоляция. Findings исправлены ниже; affected re-review §6.2/7, A1/A3/A4/A8/A10/A11, §16 завершён PASS 2026-10-14; пять affected roles PASS.

| Severity | Area | Finding | Required action | Status |
| --- | --- | --- | --- | --- |
| MEDIUM | Prepared lifecycle | Clear40/consume337 делают facade multi-plan ложным | Actual scoped bounded Prepared registry + AC8/files | Fixed SPEC, author rechecked source |
| HIGH | Output payload | Full documents/PhotoComparison могут не поместиться в frame даже при take1 | byte budget/descriptors/source-bound chunks + A1 | Fixed SPEC, reviewer re-review PASS |
| HIGH | Unknown preservation | Strict DocumentCodec/CapabilityRegistry/Store reject entire unknown document | baseline immutable category compatibility read/save/rechecks + strict new/sync + A3/files | Fixed SPEC, reviewer re-review PASS |
| MEDIUM | Cardinality | Multi-name противоречит max1 Google categories | existing cardinality + legacy readonly contract | Fixed SPEC, reviewer re-review PASS |
| MEDIUM | Transient tools | RequestKey/scopes/cancel origin неоднозначны | durable/transient policy + own/reconnect/terminal cases A11 | Fixed SPEC, reviewer re-review PASS |
| MEDIUM | Account replay | Recorded connect/disconnect делает execution precondition stale | recorded after identity replay, new execution preconditions separate + A10 | Fixed SPEC, reviewer re-review PASS |
- Depth checklist: complete scope ✓; guards/account/root/CAS ✓; protocol versions ✓; persisted receipts crash ✓; legacy/ordinary launch ✓; update/profile packaging ✓; scenarios/tests/objections/ledger ✓; unrelated code mutations absent ✓.
- Fix and re-review: author и reviewer повторно сверили affected contracts/source owners/AC. Сохранились guards и original full flow; large payload доступен полностью, неизвестные categories защищены, account replay не запускает повторный side effect, transient tools имеют owner/scopes, multi-plan не facade-only. Все actionable findings закрыты в SPEC. No-findings justification: нет незакрытых находок после перечисленных counterexamples и affected re-review; это не утверждение об отсутствии будущих implementation defects.
- Manual-review challenge: проверить обычный запуск после headless host, crash после issued Google create, редактирование local contact с UI invalid buffer и восстановление editor-backup, которого нет в sync runs. Все четыре сценария имеют отдельные contracts и обязательные tests/native evidence. Residual risks: крупная переработка lifetime требует фактической реализации/проверок, live Google OAuth не проверяется в разработке, same-SID ACL не изолирует вредоносную программу пользователя.
- Stop decision: PASS для SPEC, можно запросить exact approval. Root final scope check: `git status --short` показывает только новый текущий spec; проектные код/конфиг/docs не изменены. Implementation и AC runtime evidence пока не выполнены и не объявляются PASS.

### Post-EXEC Review
- Статус: PASS по реализации и A1–A13; full advisory/adversarial fallback STOP: PASS получен 2026-10-14. Runtime evidence не подменяется source inspection.
- Scope reviewed: approved полный scope §1–18, `git status --short`, tracked diff и все новые Automation/Mcp/Tests файлы; application lease/coordinator/Prepared registry, baseline-aware editor, receipts/jobs/values/uploads/group/restore/runtime, Windows SID pipe/build handshake, Desktop composition/lifetime/draft/settings/updater, Build-Release и docs/changelog. Central expanded review, desktop/backend profiles применены. Формат contact/workspace не мигрирован.
- Scope/Evidence pass: 38 actual stdio tools и111 writable leaves, обыкновенный host/window flow, fake/Demo Google, настоящие локальные files/SQLite/backup, Synthetic bundle/Setup/local feed. Production release/install, личные контакты, live OAuth и Google writes не объявляются выполненными. Generated artifacts остаются ignored. Отдельный исходный тест ordinary demo переведён на Synthetic payload+temporary data вместо общего desktop-demo.json; это in-scope изоляция обязательной проверки.
- Contract pass: API1/IPC2, immutable loaded MVID+path/profile/SID handshake; Common runtime имеет один lease и OAuth composition; actual Prepared registry/TTL/quota, root/account/manifest binding+CAS/state hash; patch не меняет id/google и opaque categories; server performs permissions, descriptor source authorization и remote-write boundary checks. Durable key сохраняется до записи, unknown не становится новым write, missing journal entries не означают success.
- Adversarial pass: исходы lost response/issued Google create/receipt persistence failure, пользовательский draft/modal/invalid input, revoked bridge, stale account/manifest, GUI/MCP concurrent plans, page UTF8/SDK escaping, unknown category precision, corrupt backup photo, cross-kind UUID collision, referenced/system group delete, old key/expiry/retention, EOF/cancel race и initialized-only bridge при обновлении проверены по source и соответствующим tests.
- Reviewer fallback: `/root/mcp_spec_review` выполняет central-mandated procedural no-mutation advisory/adversarial review, не technical read-only sandbox. Builds/tests не запускал, files/Git/external state не менял. Source findings и affected re-review перечислены ниже; окончательный STOP после чтения final native/TRX/package/AC evidence.

| Severity | Area | Finding / required action | Fix and re-review evidence | Status |
| --- | --- | --- | --- | --- |
| HIGH | Identity/editor | New contact UUID без separator, strict legacy parser и unsafe restore могли нарушить compatibility | GUID D, baseline-aware immutable categories; trusted metadata preserved, collision/corrupt photo guarded; matrix111/restore tests | SOURCE-CLOSED + tested |
| HIGH | Shared owner/draft | Separate composition, GUI draft race/async refresh failures | shared App connector/coordinator/activity, dispatcher lease/generation, clean-card refresh; Headless/native draft flows | SOURCE-CLOSED + tested |
| HIGH | Plans/handles | Facade plan registry и reopened manifest bypass | actual scoped Prepared, bound manifest and held pin, GUI separate quota, source-bound values and plan digest | SOURCE-CLOSED + tested |
| HIGH | Receipts/recovery | Retry ambiguous writes, missing selected journal entry falsely completed, expiry discarded unresolved references | persisted intent before write, expected entryKeys, readonly reconciliation, unknown retention/tombstones/cleanup guard | SOURCE-CLOSED + tested |
| HIGH | Job terminal state | Late cancel/EOF overwrote terminal with cancelRequested | atomic AgentJobLifecycle CAS; all cancel routes shared; deterministic completion/cancel barrier test | SOURCE-CLOSED + tested |
| MEDIUM | Account status | All receipt states lacked account check; unknown connect/disconnect lost finalization unavailable after transition | guards pre/post reconcile, volatile publication, bounded captured after identity only for pending account transition; key/reconnect/cancel tests | SOURCE-CLOSED + tested |
| HIGH | Update/build | Initialized bridge locks package before attachment, hot DLL hash could impersonate old owner | physical sibling-process fence + startup gate, immutable MVID; local installed update block | SOURCE-CLOSED + tested |
| MEDIUM | Large payloads | Full values/JSON Unicode escaping exceeded frame; serialization added quota allocation | lazy bounded pages, shared512MiB retention, canonical counting writer+encoder/LF, wire byte budget, large Cyrillic/chunk tests | SOURCE-CLOSED + tested; CPU residual reported |
| MEDIUM | Groups/CAS | O(n²) enumeration and missing live state/reference CAS | inspector hashes lazy paging, references/version held and rechecked; group CRUD/state tests | SOURCE-CLOSED + tested |
| MEDIUM | Native harness | Original ordinary launch collided with existing user-profile instance; panel without UIA peer; keyboard focus changed mid-entry; temp app data overlapped workspace | isolated Synthetic sibling root/data/payload, real controls/scroll, UIA ValuePattern; ordinary1/1, MCP installed1/1; no keyboard-input ergonomics claim | Fixed and rechecked |
| LOW | Performance target | Final single-run no-op5069ms exceeds tool5s target; first import/edit>20% | same-environment baseline and bounded quota cost analyzed in MCP-VALIDATION; allocations comparable, cancellation6.8ms, writes0; no speedup claim | Reported residual; SPEC requires analysis, not target5s PASS |

### Final Acceptance-to-Evidence Matrix
| AC | Evidence inspected | Status |
| --- | --- | --- |
| A1 | actual stdio clients both revisions, 38 tools, same owner; malformed/EOF/unknown tool; Cyrillic wire pages and exact chunks; mcp-complete.trx42/42 against published0.5.4 | PASS |
| A2 | real catalog/query aliases/filter/sort/cursor drift+manual clock expiry | PASS |
| A3 | every writable111 leaf real patch/store; opaque category/unknown precision, forbidden identity fields/atomic invalid patch; existing core/schema tests | PASS |
| A4 | PNG/JPEG content/hash/read/remove/external same PNG path, upload dedup/expiry/invalids; actual Headless PhotoBytes hash+decoded preview; inspected native151212/photo-refreshed.png+traceSHA | PASS |
| A5 | Demo full lifecycle+photo/membership/conflict/delete/sync restore/cleanup/repair; local deleted restore same UUID/photo/current Google metadata/collision/corruption | PASS |
| A6 | stdio two clients shared background owner; published ordinary startup promotes same PID, window-close/client-close/idleexit; fresh0.5.4 and installed0.5.0 native flows | PASS |
| A7 | activity/dirty and unrelated target cases; native full unsaved field retained after draft_conflict; actual UIA value event; inspected draft/video | PASS |
| A8 | manifest replacement/reopen, account/allowlist, live state/CAS, actual multi-plan GUI8/9 quota/TTL/choices/invalidation tests | PASS |
| A9 | Headless91 PASS incl clean card/photo/settings; native card/table refresh, settings/config paths; 14 unique native scenarios via full13/14+fixed ordinary1/1 | PASS; affected rerun explicit |
| A10 | frozen intent/local lost reply/remote create timeout/no repeat, selected journal entries, account finalization+reconnect exact-key replay, terminal CAS cancel/EOF/TTL/tombstones | PASS |
| A11 | explicit permissions/generation/revoke/own+foreign cancel; source/account/root auth including unknown receipts, bounded JSON/SDK stdout; native config parsed and paths verified, no credentials | PASS |
| A12 | fresh Synthetic0.5.4-mcp.validation2 shared-binary audit/bridge payload/build manifest published=false; installed0.5.0 setup and local0.5.4 update block, native151553trace true | PASS |
| A13 | Release0warnings/errors, Core163+Google185+Headless91+MCP42; Native14 unique via full13/14+affected ordinary1/1; 495 scenarios,0skip, no unexplained failures | PASS; not claiming one-shot native14/14 |

### Role-Based Review Result
| Role | Actual assessment | Verdict |
| --- | --- | --- |
| Business analyst | Full contact→fields/photo/groups→prepare/review/apply→history/restore scenario remains in implementation | Author PASS |
| UX/designer | MCP settings permissions/path generation, dirty refusal, saved card/photo refresh; inspected native/headless PNG and baseline/after frames+video metadata | Author PASS |
| Tester | Actual files/SQL/Demo, stdio process tests, manual clock/fault injection; final42/42 and all14 native scenarios verified, prior failures/affected runs explicit | Author PASS |
| Architect | One owner/lease, scoped Prepared/values/handles, format compatibility, immutable build handshake, real CAS | Source PASS |
| Delivery/operations/security | Disabled default, roots/scopes/revocation, SID pipe, payload audit, updater barrier, unknown proof; fresh package/native evidence, no public artifacts/secrets | Author PASS |

- Evidence inspected: exact commands/TRX/JSON/PNG/MP4/build manifests/performance files listed in [MCP-VALIDATION](../docs/MCP-VALIDATION.md); original HEAD archived separately for before video/benchmark, no branch reset. Source fixes re-reviewed by author and fallback reviewer. Actual native frames are opened, not merely checked for existence.
- Depth checklist: scope/related-only changes ✓; AC1–A13 matrix ✓; original user flow/decisions/objections ✓; real validation boundaries ✓; unsupported live-Google/update-success claims excluded ✓; regression/edge cases ✓; docs/changelog/format compatibility ✓; explicit API/IPC/lifetime/update tradeoff ✓; no unrelated comment/refactor cleanup ✓.
- Manual-review challenge: requestKey polling after connect/disconnect receipt-finalization failure; completion racing EOF; swapped manifest with old valueId; already-initialized bridge before pipe attachment/update; compare rendered photo content against saved bytes. Each produced concrete fixes/tests or named native evidence, not hypothetical reassurance.
- No-findings justification: source findings now have required actions, implementations, positive/negative fixtures and affected re-review; A1–A13 имеют actual evidence. This does not assert future defects absent. Performance CPU cost and tooltargetmiss/liveGoogle/isolated reviewer boundaries remain visible; none substitute a missing mandatory AC. Ошибочные harness assumptions corrected and rechecked; no new one-shot native14/14 claim.
- Stop decision: PASS, можно завершать авторизованную реализацию. `/root/mcp_spec_review` прочитал actual final TRX/manifest/traces/документы, осмотрел native/headless фото, draft/clean/settings/update PNG и before/after first frames; подтвердил все пять ролей PASS и закрытие A1–A13. Source findings re-reviewed, новых обязательных действий нет. Video metadata и first frames проверены; полный просмотр движения видео не заявляется. One-shot native14/14 не заявляется: полный13/14+affected ordinary1/1 закрывает14 unique cases. Performance target miss имеет transparent LOW residual disposition, поскольку SPEC требует измерение/анализ, а не5s PASS. No feature families deferred; publication/Production activation outside current authorization.

## Approval
Exact «Спеку подтверждаю» получено 2026-10-14 после завершения SPEC review. Утверждён полный scope с окном и без окна, без публикации/активации в личном профиле/Google writes во время разработки.

## 20. Журнал действий агента
| Фаза / событие | Решение и основание | Evidence / остаток | Следующее | Human decision | Артефакт |
| --- | --- | --- | --- | --- | --- |
| SPEC preflight 2026-10-13 | New behavior/public API → expanded QUEST; изучена база 0.4 | Shared activity process-local, workspace lock single root, OAuth/format/updater verified | Design/review | Новое поручение, approval ещё нет | Эта SPEC |
| Design | Full domain MCP + single Desktop owner/background + thin stdio; defaults explicit | Raw fields/type catalog, photo/groups/history, guards и operation receipts | Adversarial reviewer | Async mode question, ответа нет | Эта SPEC |
| Review fixes 2026-10-14 | Source-based reviewer выявил реальные DTO/strict-validator/plan/replay gaps | Исправлены source change scope, byte-bound output, cardinality, durable/transient policy, account replay; code untouched | Affected re-review → SPEC approval | Нового approval нет | Эта SPEC |
| SPEC ready 2026-10-14 | Affected advisory/adversarial re-review PASS; root final contract/scope pass | Нет незакрытых SPEC findings; writable reviewer isolation limitation указана; все runtime checks впереди | Exact «Спеку подтверждаю» | Ожидается | Эта SPEC |
| EXEC approved 2026-10-14 | Пользователь: «Спеку подтверждаю»; полный scope утверждён | Branch feat/mcp-runtime; implementation/tests/evidence впереди | Реализация общего runtime и MCP | Exact approval получен, live writes/publication не разрешены | Эта SPEC и planned files §16 |
| EXEC implementation | SDK bridge/38tools/shared owner, editor compatibility/groups/CAS/receipts/jobs/values/uploads/plans/UI/settings/bundle implemented | Source findings fixed with contracts/fault tests | Native/package/protocol acceptance | Existing exact approval; no new external side effects | src/tests/docs |
| Review fixes | Manifest binding, immutable MVID/update fence, complete journal entry keys, unknown retention/account proof, terminal cancel CAS, actual byte budget/counterLF | Advisory source re-review closed all HIGH/contract MEDIUM | Final suites and screenshots | No human decision needed | Post-EXEC findings table |
| Acceptance 2026-10-14 | Final MCP42/42, Core163/Google185/Headless91; native14 scenarios with explicit ordinary affected rerun; installed1/1 update barrier | 495 unique cases0skip; Synthetic0.5.4 package published=false; PNG/video inspected | Full advisory STOP | No live Google/publication | docs/MCP-VALIDATION.md |
| Performance | Counting writer removed extra JSON copies; final10k no-op5069ms missed tool5s target, delta>20% analyzed against same-environment HEAD | Bounded quota CPU cost/inference and full measurement limits documented | Residual report; no infinite rerun | No required feature deferred | performance-final-isolated/baseline-final-environment |
| Final full review STOP | Author and procedural advisory fallback reviewed scope/contracts/adversarial risks/five roles/depth/manual challenge; STOP PASS | A1–A13 closed,495 unique cases,0skip; source findings fixed; performance/liveGoogle/video limits explicit | Final report; no further authorized work remains | No new approval asked; no commit/push/release | Эта SPEC, MCP docs/validation |

### Перенос на main, 15 октября 2026

Пользователь запросил: «Отреьейзь на мейн и оформи ветку». Локальная ветка `feat/mcp-runtime` перенесена с `43624c7` на полученный через fetch `origin/main` (`e40eb5b`, main 0.5.0). Незакоммиченная реализация сохранена перед rebase; итоговый feature commit — `960132d`. Main checkout и другие рабочие деревья не изменялись.

Коммит `17575a8` совмещает MCP с редактором синхронизации из main: owner/TTL и scoped invalidation планов, общий modal/restart guard, deferred refresh revisions, повторная проверка activity после ожидания решения по черновику и сохранение row identity через origins. Добавлены regression tests; ошибки промежуточной сборки и Headless harness раскрыты в отчёте, assertions сценариев сохранены. Advisory source review выполнен тем же procedural no-mutation reviewer; source findings закрыты. Полные результаты текущей базы и окончательный review фиксируются в [MCP-REBASE-VALIDATION.md](../docs/MCP-REBASE-VALIDATION.md). Исторические 495 сценариев выше не заменяют текущие проверки. Запрос не использован для push/PR/merge/release или активации в Production.

Итог текущего rebase: Core174/174, Google185/185, MCP44/44, Headless103/103 PASS; Native15/16 с незакрытым failure проверки фокуса диалога черновика. Повторы и диагностические эксперименты сохранены в отчёте; Native acceptance и полный feature STOP PASS на новой базе не заявляются. Экспериментальные изменения Native helper сняты с ветки, product focus trap не менялся. Локальная доставка Git завершается с этим явным ограничением (521 PASS / 1 unresolved из 522 уникальных сценариев, 0 skip).

### Выпуск и локальное обновление, 20 октября 2026

Пользователь прямо поручил: «Влей в мейн и выпусти релиз. Обнови локальную версию». Это разрешает push/PR/merge, stable release 0.6.0 и обновление установленного приложения. Повторное подтверждение этих действий не запрашивается. Исходный MCP scope уже утверждён; новый продуктовый контракт не добавляется. Реальные Google-записи и изменение пользовательских контактов не входят в delivery workflow.

План: закрыть обязательный Native focus gate без ослабления assertions; проверить сборку и полные Core/Google/MCP/Headless/Native suites; дополнить version/changelog/curated notes; доставить через PR в main; собрать Production из exact merged SHA в новом output; проверить identity/channel/RID, OAuth continuity, bundled bridge, отсутствие пользовательских/test payloads и hashes. Затем draft release с пятью assets → remote digest/notes проверка → stable Latest → anonymous updater discovery/download/verification → локальная установка с проверкой сохранности данных/settings/vault и версии после запуска. Откат приложения — новая исправляющая версия или отдельная ручная переустановка; автоматический downgrade не поддерживается.

Instruction stack: delivery-task, .NET desktop/testing-dotnet, ui-automation-testing при работе с прежним Native gate, существующий expanded QUEST и review-loops, GitHub delivery/Conventional Commits/versioning. Повторный required procedural advisory reviewer — `/root/mcp_spec_review`, no mutation/tests/build/external writes при writable sandbox. Evidence текущего выпуска: `docs/RELEASE-0.6.0-VALIDATION.md` и ignored `chat-artifacts/release-0.6.0/`. Исторические результаты 14/15 октября не переименовываются в свежие результаты.

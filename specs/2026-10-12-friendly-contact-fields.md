# Понятные карточки и редакторы полей контакта

## 0. Метаданные
- Фаза: EXEC, получено exact approval «Спеку подтверждаю». Expanded, large: несколько модулей и новое публичное поведение редактирования, риск потери скрытых значений при преобразовании.
- Исходная ветка `feat/contact-editor`, база `1fa82b8`; локальный статус до SPEC чистый. Новый релиз, commit, push и установка не входят в эту задачу.
- Владелец: root. Профили `dotnet-desktop-client`, `ui-automation-testing`; context `testing-dotnet`, `visual-feedback`. Central owners: routing, creator-vibe-lens, model/tool/collaboration/testing baselines, quest-governance/mode, spec-linter/rubric, review-loops. Применён `creator-vibe`.
- Поверхность: Codex desktop, unrestricted/danger-full-access, approval never. Фактический model ID отдельно не проверен; модель/eval не меняются, eval baseline не применим.
- Canonical template: `C:/Users/Kibnet/.codex/agents/templates/specs/_template.md`.
- Источники: текущие schema/validator/store/query/card; [Google Person](https://developers.google.com/people/api/rest/v1/people), [updateContact](https://developers.google.com/people/api/rest/v1/people/updateContact), сверены 12.10.2026. Схема Google определяет допустимые данные; расположение контролов ниже — наше продуктовое решение.

## 1. Цель / outcome contract
Исходное поручение: «сделай редактор и отображение полей контакта более user friendly с учётом [модели Google], а то сейчас прям больно смотреть».

Результат: контакт читается как адресная книга. Телефон, email, адрес, организация и дата имеют подходящие формы и подписи; стандартные типы выбираются по-русски. Просмотр и редактирование визуально различаются. Пользователь меняет обычные сведения без путешествия по дереву свойств; все 23 категории/111 writable leaves остаются доступны.

Артефакт EXEC: работающая карточка и таблица, semantic presentation catalog, meaningful regression tests, инструкции и просмотренные before/after PNG/video. Stop: AC1–10 и post-EXEC PASS; одного schema coverage недостаточно для визуальной приёмки.

## 2. AS-IS, проверено
- `ContactCardControls` рекурсивно строит expander → запись → свойство. Даже view использует readonly TextBox; имена свойств общие («Значение», «Тип»). На каждое текстовое поле приходится отдельная кнопка очистки, на запись — большая кнопка удаления.
- `ContactFieldSchema` берёт варианты лишь из `enum`. Многие стандартные типы Google представлены `string` с описанием: телефоны, email, адреса, организации, отношения, IM protocol. Поэтому сейчас они остаются свободным TextBox.
- Дата распадается на три числовых поля; UI предлагает пользователю вводить нули. Фото180×180, все ярлыки как disabled checkboxes, поисковая строка и «Показать все поля» вытесняют основные сведения вниз.
- `ContactTableQuery.FormatValue` рекурсивно форматирует JSON; имена столбцов содержат «Телефоны · Значение». Этот же formatter участвует в поиске/сравнении, поэтому его нельзя вслепую заменить UI-форматтером.
- Просмотрены actual PNG: `chat-artifacts/ui/20261009-140216-871/editor-Dark-1180x820.png`, `20261009-145023-088/editor-filled-draft-native.png`. Это evidence исходной проблемы; картинки исторические, до новой работы. На текущей базе иконка уже добавлена.
- Существуют immutable CAS/backup, pinned workspace state, unknown-field preservation, dirty guards, virtualized rows, manual Google preview. Эти контракты сохраняются.

## 3. Корневая проблема
Схема сериализации одновременно используется как информационная архитектура карточки. Типизация storage не объясняет человеку смысл поля и не выбирает подходящий сценарий ввода.

## 4. Цели дизайна
Отделить schema/validation от presentation. Ввести контекстные названия и доменные формы. Прогрессивно раскрывать редкие свойства, сохранять полноту данных. Снизить число постоянных кнопок и вложенных scroll/expander. Проверять обратимость каждого UI-преобразования независимо от layout.

## 5. Non-Goals
Нет миграции JSON/SQLite, изменения remote write masks, автоматической отправки в Google, массового редактирования, нормализации телефонных номеров, распознавания имени, геокодирования, перевода произвольных пользовательских типов или нового rich-text движка. Полный редизайн sync comparison не входит; улучшается отображение в каталоге контактов и карточке. Existing sync/recovery/update guards сохраняются.

## 6. TO-BE
### 6.1 Ответственности
- Application `ContactPresentationCatalog`: семантика по category/path — контекстная подпись, варианты, custom-vs-closed-enum, вид поля, summary. Без зависимостей Avalonia.
- Application `ContactValuePresenter`: культурно заданное русское представление для карточки/таблицы. Не мутирует JSON и не заменяет raw typed sorting/filtering.
- Desktop domain editors: Name, Communication, Address, Organization, PartialDate, Note, Simple/KeyValue rows; card composer, compact view, field chooser и row menus.
- Existing VM/store: единственная точка draft mutation/CAS/save. Не создавать второй draft, копии скрытых веток или отдельный storage API. Типизированный fallback использует ту же schema и VM для редких листьев.

### 6.2 Информационная архитектура и wireframes
По умолчанию view показывает только заполненные значения и selected labels; пустые секции исчезают. Контакт без данных показывает одно объяснение и «Редактировать»; UUID не подменяет имя. В edit всегда доступны имя, телефон, email; остальные категории добавляются через «Добавить поле…» с поиском. Заполненная редкая категория никогда не прячется без её видимого summary. Поиск поля раскрывает путь к нему и даёт контекстную подпись.

Wireframe VIEW, карточка минимум420 logical px, ширина обычной панели сохраняет split/table preferences:
```text
┌───────────────────────────────────────────────────────────┐
│ [аватар64] Анна Примерова       [Развернуть] [⋮]           │
│            Дизайнер · Компания                            │
│ [Найти поле…]                 В избранном                  │
│ [Семья] [Друзья]                                          │
│                                                           │
│ Связь                                                     │
│ +7 999 123-45-67       Мобильный · Основной       [⋮]       │
│ anna@example.test     Личный                    [⋮]       │
│                                                           │
│ День рождения          29 февраля                         │
│ Адрес                                                     │
│ Москва, улица Примерная, 10                     Домашний   │
│                                                           │
│ Заметки                                                   │
│ Познакомились на конференции…                             │
│ Дополнительные сведения · 3                       [∨]      │
│                                                           │
│ [Редактировать]      Изменения сохраняются в папку          │
└───────────────────────────────────────────────────────────┘
```
Wireframe EDIT: компактные строки, постоянный footer; ⋮ доступен клавиатурой, не только hover:
```text
│ [аватар64] Анна Примерова               [☆ Избранное]       │
│ [Найти поле…] [Изменить фото…] [Развернуть]                 │
│ Ярлыки [+ Добавить ярлык]                                  │
│ Имя [Анна             ] Фамилия [Примерова           ]    │
│ [Добавить отчество или другие части имени]                │
│ Телефон                                                  │
│ [+7 999 123-45-67       ] [Мобильный ∨]              [⋮]    │
│ [+ Телефон]                                              │
│ Электронная почта                                         │
│ [anna@example.test     ] [Личный ∨]                 [⋮]    │
│ День рождения [29] [Февраль ∨] [Без года ∨]                │
│ [Добавить поле…]                                         │
│ [Сохранить в папку] [Отменить]          Не сохранено        │
```
Длинные значения переносятся в view; selectable text/«Копировать» вместо рамок read-only inputs. Общий ⋮ содержит удалить/перечитать/сведения Google. Row ⋮ в EDIT содержит копировать, сделать основным/снять предпочтение, дополнительные свойства, удалить запись; меню VIEW определено ниже. Малые clear affordances внутри заполненных edit controls; контекстное удаление «Удалить телефон/адрес/email», без огромной кнопки над каждой строкой.

Mode affordances: в VIEW звезда/ярлыки — readonly status, без hover/pressed оформления кнопки; «В избранном» показывается лишь если выбрано. Row ⋮ VIEW предлагает «Копировать» и «Редактировать этот телефон/email/адрес…», но не primary/remove. Edit-field entry запускает единственный existing draft, раскрывает соответствующую запись и фокусирует её value control; footer сразу меняется на Save/Cancel. Avatar в VIEW открывает только полный просмотр фото; изменение/удаление находятся в EDIT. Общий contact-delete остаётся отдельным явным действием с existing confirmation, не маскируется под удаление поля. Cancel после любого view→edit entry возвращает original bytes; ни VIEW actions, ни открытие dropdown не делают silent save. Readonly contact не предлагает недоступное «Редактировать», вместо этого причина и copy/view.

Find/Add discovery: компактная текстовая кнопка «Найти поле…» постоянно видна под identity header в обоих режимах; Ctrl+F при фокусе внутри карточки открывает её, вне карточки — existing table search. Результаты показывают friendly field name, category/record context, filled/not-filled и число подходящих записей. Existing leaf→раскрыть/прокрутить/фокусировать без мутации; readonly/service result остаётся VIEW. Для нескольких адресов/телефонов в результатах различаются строки по summary и исходному ordinal, path identity сохраняется. Absent leaf в EDIT раскрывает пустой control, запись появляется только по explicit add/input. Отдельная «Добавить поле…» в EDIT предлагает23 категории; existing singleton помечен «Уже есть» и действием «Перейти к полю», а не второй записью. Escape закрывает поиск и возвращает прежний focus/scroll, остальные dirty values сохранены.

Find фильтрует только свой список результатов, не скрывает остальные поля в body карточки. Last find query сохраняется при переходе к полю/исправлению ошибки и появляется при повторном открытии; Escape не удаляет query или typed draft. In-place expansion и scroll не включают edit автоматически. «Развернуть» всегда доступно в desktop split; expanded «Рядом с таблицей» и narrow «← Список» имеют видимые подписи, сохраняют selected record, формы/буферы/focus и не меняют preferences от одного resize.

Visual hierarchy: единая шкала spacing4/8/12/16/24 logical px, card padding16; ordinary value/input font14, secondary labels13, section heading16 semibold, contact name20 semibold. Это app layout решения, не утверждение о соответствии внешнему стандарту. Editable inputs обычно min-height36; строки VIEW минимум40. Секции разделяются воздухом/тонким separator, без вложенных контрастных прямоугольников на каждую category/property. Type/status chips нейтральные, accent blue — основной action/selection; readonly values остаются с обычным контрастом, а не выглядят disabled.

Header layout: identity row avatar64 + stretch name/company + bounded star/status/menu, actions находятся в отдельной toolbar, не справа от всех частей имени. Toolbar Find/Expand/Photo wraps на новую строку, если суммарные label/hit размеры не помещаются, сохраняя logical reading/Tab order; footer тоже wraps до clip и не перекрывает scroll content. Long contact name переносится, полномочие header112 измеряется на указанном common fixture, а не обрезает произвольное имя ради метрики. Labels expansion/многострочный header могут занять больше высоты, но body остаётся прокручиваемым и footer доступен.

Hit/focus contract: маленьким может быть glyph, не активная область. Clear hit target≥28×28, ⋮/star/photo-menu≥32×32 logical px; visible focus ring, accessibleName «Действия для телефона…», «Очистить фамилию», «Изменить фотографию». Кнопки видны без hover; Enter/Space открывают, arrows выбирают пункт, Escape закрывает и возвращает focus anchor. После удаления строки focus уходит в соседнюю запись либо Add button. Пустой disabled/read-only button не используется для представления значения. Длинный unbroken value переносится без потери символов; «Копировать» берёт полное raw value, tooltip/accessible name сохраняют полный custom type. В TextBox допустима собственная горизонтальная прокрутка/caret, но не page-level horizontal scroll.

Avatar64 в header; полный photo preview по нажатию. Изменить/удалить фото доступны в одном photo menu; cancel сохраняет старые bytes. Header не дублирует имя отдельным заголовком. Labels view: только выбранные chips; edit: searchable multiselect, а не весь список checkbox в body. Unknown label UUID остаётся видимым и сохраняется.

Labels entry в EDIT виден даже при0 selected: «Ярлыки» + «Добавить ярлык»; при выбранных chips — «Изменить». Selected chips в обычной карточке занимают не более двух строк, дальше «ЕщёN» раскрывает все; long names не выдавливают поля за footer. Overlay показывает «Выбрано:N», search, виртуализированный список, «Готово»/«Отменить»; очистка поиска не снимает выбор.0 available groups: «В папке пока нет ярлыков для выбора», без мёртвого предложения создать group (создание group не входит в scope). Existing unknown UUID отображается как «Неизвестный ярлык» с точным UUID в details/accessible description; исключение его из контакта — только явное действие в EDIT, без удаления group. Неизменённый selection on Done не пишет/reorders labels; stage apply использует только явный delta, сохраняет unknown и original ordering. Escape отменяет staged choices полностью.

Save/error feedback: footer всегда виден. «Сохранить в папку» enabled для нового контакта/изменённого draft, в том числе при invalid buffer; при ошибке запускает validation/reveal/focus и ничего не пишет. Для unchanged existing draft Save disabled, Cancel доступен. Ошибка подписана возле control и перечислена в compact summary; цвет не единственный сигнал, typed value остаётся. Во время save повторный action blocked и виден progress; failed/uncertain write сохраняет draft/status согласно existing storage outcome. «Сохранено в папку» появляется только после подтверждённого результата; в обычном VIEW нет ложного постоянного success toast. Рядом с успешным результатом entry «Синхронизация» только переключает страницу, не запускает check/apply автоматически.

Multiple-error recovery: footer «Не сохранено — исправьтеNполей» открывает список ошибок с category/record/friendly label; technical path лишь в дополнительных сведениях. Каждый пункт раскрывает требуемые details, прокручивает и фокусирует live input; исправленное поле исчезает из списка, остальные сохраняются. Save делает это для first invalid автоматически, без очистки finder query и без потери скрытых invalid buffers. Summary и footer доступны при820×640; summary имеет bounded scroll, не выталкивает действия за окно. External drift отдельно: «Файл изменился вне редактора», видимые действия «Загрузить актуальную версию» (existing discard confirmation) / «Оставить черновик». Не прятать единственный путь восстановления в ⋮ и не объединять drift с ошибкой ввода. Busy status «Сохраняем…» сменяется authoritative success/failed/uncertain outcome; error summary не объявляет несохранённым уже подтверждённый commit.

Empty-contact first use: «Контакт без имени», EDIT placeholders имени/телефона/email не создают JSON object/row и dirty state сами. First explicit phone/email input создаёт только соответствующую запись; type placeholder «Тип не задан», никакого mobile/home по умолчанию. «Мобильный» на wireframe — пример уже заполненного типа. Explicit Add date показывает пустые компоненты и выбор precision; today/2000 никогда не подставляются. Existing empty-contact Edit→Cancel или unchanged existing Save-noop не пишут файлы/backup; explicitly new draft Save разрешён existing create contract, а не считается no-op для нового UUID.

В panel<560 имя переходит в одну колонку, phone/email value получает всю ширину и type/menu — следующую строку. Общий adaptive breakpoint900 и table/card/back сохраняется. Страница имеет одну вертикальную прокрутку; virtualized overflow списки сохраняются для больших категорий. Секции не превращаются в семь постоянно закрытых expanders.

Saved TableFraction .25… .8 не отменяет minimum card420/table300. При split available width = panes width minus splitter10; effective fraction временно clamp в [300/available,1−420/available]. Если диапазон пустой либо окно<900 — single-pane table/card/back. Это layout-only: расчёт не пишет preferences/dirty state; stored fraction применяется снова, когда обе минимальные ширины помещаются. Только явный drag сохраняет новую допустимую actual fraction. Проверить старые extreme preferences .25/.8 на1180/1000/820; expanded card и обратный переход сохраняют выбранный контакт и фокус.

### 6.3 Доменные формы — полное покрытие
| Категории | View | Edit / доступ к редким полям |
|---|---|---|
| names | Полное имя в header, без повторения каждого пустого свойства | Имя/фамилия; отчество optional; обращение/суффикс/полное имя/все phonetic parts в «Другие части имени». Не парсить и не генерировать unstructuredName при typing |
| phoneNumbers, emailAddresses | Значение + русский type badge; основной отдельно | Value+type+⋮; email displayName дополнительный; contextual labels «Телефон», «Адрес электронной почты», не «Значение» |
| imClients, sipAddresses | Сервис/username либо SIP + type | Editable service choices + свой сервис, username; SIP value+type |
| urls, calendarUrls | Читаемая ссылка + type | URL+type, contextual «Сайт», «Ссылка на календарь»; без автоматического открытия внешних приложений |
| addresses | Многострочный postal summary + type | Улица/дом, дополнение, город, регион, индекс, страна; PO box/countryCode/полный адрес через details. Country suggestions позволяют custom country; code не выводится из country автоматически |
| organizations | Должность · организация, отдел/период, сейчас | Organization/title/type; department/location/current/dates; description/domain/costCenter/symbol/phonetic/занятость в details |
| birthdays, events | Локализованная полная/частичная дата; тип события | PartialDate control, event type, birthday legacy text в details |
| biographies | Selectable многострочная заметка без рамки | Большое text area; format enum в details. Existing HTML явно помечен, показан как текст исходника; без исполнения HTML и без автоконверсии в plain text |
| nicknames, genders | Псевдоним/type; пол/обращение если заданы | Closed nickname enum; gender choices + custom, pronouns text в details |
| relations | Имя человека + локализованное отношение | Person + type/custom; не выдавать free text за связанный ContactMirror UUID |
| locales | Язык + сохранённый language tag | Поиск языка + editable BCP47 tag; незнакомый tag сохраняется, без auto-normalize |
| locations | Место/current/type | Place+type/current; этаж/рабочее место/сектор/здание в details |
| occupations, interests | Простые читаемые строки | Contextual «Занятие», «Интерес»; add/remove rows |
| miscKeywords | Значение + локализованный enum | Contextual «Ключевое слово», полный closed enum |
| externalIds, clientData, userDefined | Идентификатор/type либо пары название/значение | External id+type/custom; key/value rows; контекстные подписи, без потери дублей, нулей и пустых строк |
| starred, labels, photo | Header star, selected chips, avatar | Existing exact mutation/storage semantics; компактные контролы |
| extensions, unknown Google/profile/source | Отдельные «Локальные данные»/«Служебные сведения» | Existing typed extension tree; unknown/source данные readonly. Не превращать local extensions в userDefined |

Каждый из 111 эффективных writable leaves получает явно названное место: основной editor, details соответствующей записи либо typed extra form. Coverage oracle проходит все листья; спрятать field в JSON textarea не считается покрытием. Mapping не расширяет whitelist записи.

### 6.4 Типы и правила преобразования
Из live People reference подтверждены: стандартные и пользовательские string types; output-only formatted/canonical/primary; частичные даты; sourcePrimary writable; singleton categories; percent scale организации. Список вариантов сверяется с embedded schema descriptions и источником, но зафиксирован в presentation catalog с тестами, без runtime scraping.

Phone choices: mobile→Мобильный; home→Домашний; work→Рабочий; workMobile→Рабочий мобильный; main→Главный; homeFax/workFax/otherFax, pager/workPager, googleVoice, other — все доступны в полном списке. Email: home→Личный, work→Рабочий, other→Другой. Address: home→Домашний, work→Рабочий, other→Другой. Organization: work→Работа, school→Учёба. Relationships/protocols/other categories покрывают все documented choices. Custom input сохраняет точное значение и регистр. Closed enum не разрешает новый произвольный token.

Type control: варианты «Не задано», стандартные, «Свой тип…». Existing unknown custom отображается сразу, не выбирается вместо него первое стандартное значение. Отрисовка/открытие меню не мутируют draft. Пусто, отсутствует и null различаются: no-op сохранение сохраняет raw; только явный пользовательский выбор удаляет/меняет поле.

Date control: режимы «Полная дата», «Без года», «Только год», «Год и месяц», очистка. Вид: `29 февраля`, `12 октября 1990`, `1990`, `октябрь 1990`. Yearless leap day не получает выдуманный год; anchor2000 только внутренний календарный расчёт. Неподдержанная/invalid existing combination показывается как «Неполная дата: …» с editable details и точным исходником; не clamp/repair при чтении. Existing missing/0 компоненты сохраняются до явной правки. Birthday text-only виден как сохранённый текст; ввод структурированной даты доступен явно, legacy text не удаляется автоматически.

Primary: пользовательская команда работает только с writable metadata.sourcePrimary, с existing exclusive behavior категории; пользовательский badge «Основной» берётся только из этой локальной записи. metadata.primary из Google показывается лишь внутри readonly служебных сведений; в common card не делать joins с Google snapshot ради badge. Не записывать primary/verified/source и не переносить readonly предпочтение в draft. Удаление основной строки не назначает новую автоматически. Boolean current/нет/не задано сохраняет три состояния; обычный checkbox не должен создавать false при чтении null.

Summary provenance: header organization выбирается из локального document по sourcePrimary=true, затем current=true, затем исходному array index; явно показывается одна организация и «ещёN» с раскрытием всех. Summary preferred phone/email выбирается по local sourcePrimary=true, затем исходному index, без вывода из типа main или внешнего snapshot. Полные списки в view/edit сохраняют исходный array order и path/index identity, основной обозначен badge даже если не первый. Header/name использует локальный existing DisplayName contract; никак не объединять PROFILE/CONTACT entries. Google arrays без гарантии порядка, duplicates и stale snapshot не участвуют в выборе local summary. Никакой presentation-only приоритет не сортирует raw arrays; implicit chosen summary не записывает sourcePrimary. При invalid duplicate-primary состояние определяется existing readonly/validation reason, никакого silent repair.

Filter operand units: для fullTimeEquivalentMillipercent поле ввода/чип подписываются «Занятость, %». Existing persisted operand12500/100000 показывается как12,5%/100%, без записи при открытии. Новый explicit input12,5% переводится в raw12500 invariant decimal; query/prefs хранят прежние raw единицы. Filter conversion точный decimal, в отличие от edit storage field не ограничивается3знаками: legacy fractional raw operands тоже отображаются/перечитываются без округления. Overflow/invalid ввод блокирует добавление правила с inline error. Presence operators не имеют operand. Никакой converter не применяется к другим numeric fields.

Exact-scaling edge: before raw÷1000 проверяются coefficient/scale и возможность точного представления результата (без вычисления с тихим округлением). Existing raw1e−28 дал бы1e−31%, недоступное CLR decimal. Для такого правила explicit advanced raw-unit fallback: «Занятость, тысячные доли %», точный исходный operand в textbox/chip и пояснение, что правило сохранено в исходных единицах для точности. Не показывать0%, не переписывать operand/prefs и не переключать units молча при редактировании; signed/high-precision raw entries остаются editable существующим ParseOperand. New percent input→raw допускается только при exact representable multiply, иначе inline error. AC7 включает1e−28, отрицательное/high-precision значение, zero, decimal overflow, unchanged reopen и математически независимый oracle; no silent rounding.

Date filter использует ту же partial-date UI, новый confirmed operand сериализуется в existing canonical year-month-day, unknown component0; старые ISO/--MM-DD/JSON operand формы сохраняются byte-for-byte до явного редактирования правила. Чип/подсказка показывают человекочитаемую дату. Existing date comparison (месяц→день→год) не заменяется chronological sort: соответствующая подпись/подсказка в date filter/sort объясняет этот порядок. Type Equal/NotEqual выбирает локализованный вариант/custom и хранит raw token; Contains/NotContains сохраняет existing substring-of-raw semantics с явной подписью «Часть сохранённого типа». Localized full-text aliases не включаются в typed predicates.

Организация: fullTimeEquivalentMillipercent показывается как проценты с exact decimal scale1000;100000→100%,12500→12,5%. Explicit edit переводится обратно только если результат целочисленный Int32; до3 знаков после запятой, ошибки inline. Не ограничивать существующие допустимые значения произвольным диапазоном0..100 и не округлять молча. CountryCode/locale tags и phone canonicalForm не угадывать и не переписывать.

### 6.5 User-visible scenarios
| Сценарий | Действие | Видимый результат | AC/evidence |
|---|---|---|---|
| Исходная боль | Открыть обычный контакт | Имя, связь и заполненные сведения читаются без disabled forms/дерева свойств | AC1, PNG view |
| Тип телефона | Выбрать «Рабочий мобильный», свой тип; сохранить/перечитать | Понятные подписи; raw workMobile/custom в файле | AC2, real-store test+native |
| Дата | Ввести29 февраля без года | `29 февраля`, year0/absent корректно сохранён | AC3, UI/codec/native |
| Полнота | Добавить редкую категорию; найти phonetic part/PO box | Контекстные поля, editable через details, без JSON-редактора | AC4,111-leaf oracle |
| Label/photo | При0selected открыть «Добавить ярлык», выбрать; заменить фото в EDIT | Chips/avatar и persisted exact bytes; picker/menu cancel сохраняет old values | AC5, native CRUD+empty/longlabelsPNG |
| Редкое поле/первый вход | VIEW: «Найти поле…» → existing PO box; «Редактировать этот телефон» | Existing value раскрыт и focused без изменения; edit entry имеет очевидный Save/Cancel | AC1/4/6, mouse/keyboard/native |
| Draft/error | Ошибка даты, смена типа, внешний drift, выход | Inline error + понятный guard, draft не теряется | AC6, negative/native |
| Таблица | Смотреть birthday/type/phone столбцы с ранее сохранёнными prefs | Понятные значения/заголовки; старые IDs/фильтры работают | AC7, independent query oracle |
| Плотность | Открыть при1000/820, длинные значения | Основные действия и несколько строк связи доступны без горизонтального scroll | AC8,6PNG/native |

### 6.6 State / interaction matrix
Popup ownership contract: inline «Другие части имени»/details — раскрытие в карточке, не modal. Type ComboBox dropdown, custom-type dialog, row menu, Add-field chooser и Labels overlay — transient editor-owned popups; они участвуют в guard competing navigation/folder/page/close/restart. Existing `HasModal` делает `CanEdit=false`, поэтому нельзя применять их own SelectionChanged через обычный `Mutate` пока popup открыт.

Решение: capture document reference + root/contact/path + Guard.Generation перед открытием; choices живут в popup buffer. Подтверждение передаёт semantic result, cancel/Escape ничего не меняет. Один UI result handler закрывает только свой popup и применяет payload атомарно через expected-generation проверку под тем же `DraftRestartGuard` gate, с проверкой current document/root/id/path и activity/restart state внутри критической секции; без await между release owned overlay и commit. Guard получает минимальный checked-mutation overload, обычный `Mutate` остаётся закрыт для посторонних callers. Stale/busy/retired result discarded с честным пояснением и сохранением текущего draft. Labels «Готово» применяет один staged result; Escape отменяет весь staged выбор. Native type dropdown подтверждает selection только по user accept, не по initial binding; Escape восстанавливает исходный выбор. Inline type selection без открытого popup использует обычный mutation gate. Direct navigation/restart и второе competing popup блокируются даже если первый был вызван напрямую, а не кнопкой.

| Состояние | Trigger | Результат / edge |
|---|---|---|
| View | Edit | Draft с точными исходными значениями; default choices ничего не записывают |
| Edit | Открыть type/add-field/labels menu | Owned popup с captured version; own confirmed result разрешён через checked gate, competing navigation/restart blocked; inline details немодальны |
| Edit, invalid | Save | Первое проблемное поле раскрыто/прокручено/фокусировано, inline error, нет записи |
| Edit | Type input / menu choice | Стабильная identity контролов; caret, IME/composition и scroll не сбрасываются |
| Edit | Add/remove row | New row focused; remove только выбранная запись; hidden values остальных сохранены |
| Edit dirty | Contact/folder/page/window/restart | Existing Save/Discard/Stay, same lease/generation guard |
| Readonly/problem workspace | View / попытка Edit | Readable card+reason, без новых writes/init/network |
| Save success | Reopen / manual sync | Local file matches controls; preview invalidation→manual apply→no-op |

### 6.7 Decision ledger
| Решение | Owner | Выбрано | Confidence | Риск | Needs user before EXEC |
|---|---|---|---:|---|---|
| Информационная архитектура | agent | Domain forms, compact view, progressive details, add-field chooser | .95 | Требует visual acceptance, не cosmetic-only | Нет |
| Полнота/совместимость | existing contract | 23/111 доступны, unknown/null/order preserved | 1 | Тихая потеря данных | Нет |
| Types | agent | Catalog + explicit custom token; closed enum отдельно | .99 | Ошибочный перевод raw | Нет |
| View vs sync | scope | Card/table presentation; raw query/remote masks прежние | .98 | Разрушение saved filters | Нет |
| HTML | agent | Явный безопасный source text, без execution/conversion | .95 | Меньше rich formatting | Нет |
| Внешняя доставка | user | Local EXEC после exact approval; delivery отдельно | 1 | Старое разрешение на commit не переносится | Нет |

### 6.8 Data / config matrix
| Contract | Source of truth | Изменение | Проверка |
|---|---|---|---|
| Writable fields/validator | Embedded People schema + CapabilityRegistry | Presentation-only overlay | Independent111-leaf coverage, tokens/validation parity |
| Contact JSON/unknown/photo | ContactDocument + editor snapshot/store | Existing mutation paths; формат прежний | Read→view→unchanged save exact/no writes; explicit edit→CAS→reopen |
| Table preferences | Stable column IDs + saved filters/sorts | Человекочитаемые titles/cells, IDs не меняются | Old preference fixtures; filter/sort oracle |
| Display search | Existing raw full-field query | Сохраняет raw match; добавляет локализованные type/date aliases отдельным searchable projection | Queries mobile иМобильный находят тот же token; не менять типизированные raw predicates |
| Runtime/Google | Existing services | Никаких автозапросов из formatter/forms | Zero HTTP during view/edit/save |

## 7. Бизнес-правила / инварианты
UI expansion, label popup, read formatting, suggestions, sorting presentation не изменяют document/dirty state. Редактируется только явно затронутый path; details не пересобирают объект из видимых контролов. Type labels/category-sensitive labels не сериализуются в JSON. Array order/doubles/unknown/source metadata остаются точными. Ошибки format/date/fraction сохраняют введённый draft и не подменяют invalid value пустым.

## 8. Интеграция
Presentation вызывается row.Cell/card view/type choices/add-field search. Raw predicates используют прежний GetSortValues/Compare; localized aliases — отдельная ветка full-text search, не замена raw oracle. Existing Mutate/StructureChanged обновляют view только при structural action; typing работает без полной пересборки. Stable UIA ID по field path сохранён либо semantic replacement mapping documented; тесты проверяют actual controls, а не лишь VM.

## 9. Модель состояния
Только UI transient state: открытое меню/секция, chosen date mode, invalid draft string, focus/scroll. Persistent contact/prefs schema прежняя. Нужный raw value берётся из документа, не из presentation text. Invalid UI buffers входят в существующий unsaved/close/restart guard: их нельзя потерять из-за того, что JSON ещё не изменён.

## 10. Rollout / rollback
Обычный local build. Без инициализации/перезаписи пользовательской папки. Откат к1fa82b8 читает новые файлы: формат данных тот же. Процент/date/type converters не выполняют mass repair. Fixtures только synthetic/temp; native demo preferences возвращаются в exact bytes. Commit/push/release/install требуют нового поручения.

## 11. Приёмка и evidence
| AC | Критерий | Automated / visual evidence |
|---|---|---|
| AC1 | Compact VIEW без рамок TextBox, пустых labels/categories, повторного имени и ложных interactive/status affordances. Identity header≤112 logical px (без labels/find toolbar); при1180×820 минимум2 phone/email строки и footer видны до scroll на common fixture; view→edit-field меняет footer и фокусирует control | Bounds assertions + inspected PNG view/edit light/dark, mouse view→edit→Cancel exact bytes / Save→reopen |
| AC2 | Все стандартные/custom string types, closed enums, context titles; unknown/custom/case/empty/null unchanged until explicit action; new phone не получает type автоматически | Independent option inventory из schema/source; empty existing→Edit/Cancel/noop exact no writes,firstvalue no inferredtype,explicitMobile only on accept; controls→real store→reopen; native workMobile/custom |
| AC3 | 4 date modes, yearless29Feb, legacy/invalid safe fallback, new date blank без today/anchor write; precise percent conversion, country/locale/current semantics | Table-driven independent roundtrip/negative tests, actual UI controls, new-date-empty/no guessed year |
| AC4 | Все23 категории/111 leaves сохраняются и доступны через named editors/details и obvious Find/Add entries; existing singleton нельзя продублировать; сервисные поля readonly; local summary priority не использует ambiguous Google joins | Independent schema oracle через actual UI→save/reopen; mouse/CtrlF existing phonetic/PObox and absent rare, dirty/no-op/read-only/multiple-record search; duplicates/primary-notfirst/multi-org/stale Google/PROFILE fixture; exact bytes/order preserved, correct header/only-local badges |
| AC5 | Photo VIEW preview/EDIT picker/replace/remove/cancel, labels entry even0selected, starred explicit EDIT, local extensions именуют и сохраняют данные | Native sys picker+exact bytes+reopen;0selected→choose→Done→save,Escape/no-op preserve raw;0groups/unknownUUID/longnames/manylabels/chips+popup inspected PNG |
| AC6 | No-op UI zero mutation/write; focus/IME/scroll; multiple-error summary+inline/first-invalid reveal with buffers/query preserved; explicit drift actions; dirty/restart races; owned popup commit/cancel/stale/competing transitions | Native invaliddate+hiddenpercent at820→fixone→otherremains→success;footer/summary/typedbuffer readable;driftStay exactdraft,Reload only afterdiscard; typing/Tab/Escape/Save, popup success/stale/direct competing/race and existing guards |
| AC7 | Friendly table title/value/filter operands, stable144 column IDs/prefs, raw typed predicates/order; localized alias search | Existing raw12500/100000/fractional/1e−28/high-precision/ISO/JSON/date rules retain exact persisted operands/results, explicit exact-scaling fallback; new user12,5%→raw12500 and PartialDate→canonical date roundtrip; type equality/raw contains fixtures; independent mathematical/query oracle + fresh VM preference reopen |
| AC8 | Light/dark1180×820,1000×680,820×640; ≥4.5:1 ordinary text, specified visual hierarchy/hit28/32/focus/menu route, no clipping; min-card420/table300 либо single-pane при saved extreme split prefs без config writes; large category lazy/virtualized | 6 inspected PNG view/edit/custom/date/details/labels/error, measured contrast/targetbounds;420card/820window longunbroken values/type/UUID/address;Tab/ShiftTab/menuEscape/remove-neighbor; old fraction.25/.8 ×3widths, temporary clamp/no-write/restore split/back/footer;10k virtualization and2k row fixtures |
| AC9 | Full existing suites/solution build pass; ordinary Desktop --demo works; local save→manual preview/apply→no-op | Core,Google,Headless,Native sequential Release,0 skips; ordinary EXE synthetic native smoke |
| AC10 | Одинаковый before/after обычный сценарий показывает реальное уменьшение вертикальной формы и понятные types/dates; separate post-EXEC PASS | Automated native videos+inspected frames+ffprobe; add/update phone type/date/photo→save/reopen→manual preview |

Baseline снимается из1fa82b8 до замены controls. Before/after: одна synthetic fixture, одна geometry/DPI, номер/email/дата и фото, те же gestures; recording readiness first-frame до действий. Все generated artifacts в ignored `chat-artifacts/friendly-fields/`; не записывать настоящие контакты/экраны других программ. На SPEC код и тесты не запускаются/не меняются; actual acceptance только в EXEC.

Commands EXEC, serialized: `dotnet build ContactMirror.sln -c Release --nologo -m:1`; repo-proven Core/Google xUnit and TUnit Headless/FlaUI invocations из `docs/VALIDATION.md`, `--maximum-parallel-tests 1`, exact final TRX; schema check и strict doctor по изменённым contracts; `git diff --check`. Full394 исторические и не доказывают новую UI-готовность. Не добавлять assertions, которые лишь повторяют production catalog; independent options/roundtrip и реальные controls обязательны. Native unavailable блокирует обязательный ordinary/video AC до объективно согласованного fallback.

## 12. Риски / expected objections
| Вероятное замечание | Причина | Решение / статус |
|---|---|---|
| «Снова всё дерево, только с другими словами» | Предыдущая generic form | Отдельный view, domain forms, compact inline rows и measurable first-fold AC1; mitigated |
| «Для красоты исчезли мои редкие поля» | Progressive disclosure | Все заполненные rare summaries видны,111-leaf actual-control coverage и precise patch mutation; mitigated |
| «Свой тип заменили на Другой» | Default ComboBox | Raw-preserving unknown/custom item, no mutation during construction, independent roundtrip; mitigated |
| «Дата без года стала1990/2000» | DatePicker needs full date | Explicit four-mode partial date, anchor never persisted; mitigated |
| «Смена типа прыгает и сбивает ввод» | Structure rebuild | Stable control/row IDs, focus/caret/IME/scroll characterization; mitigated |
| «После украшений фильтры считают иначе» | UI formatter reused in query | Separate presenter/raw predicate boundaries and old-config fixtures; mitigated |

## 13. План EXEC
Сначала сохранить comparable baseline evidence. Затем presentation catalog/converters с независимыми contract tests; compact view/domain controls; table/chips/menu integration и stable mutation paths; full existing+new validation, actual ordinary/native flow, visual rework и separate post-EXEC. Stop после всех AC; не переносить обязательные UI замечания в future polish.

## 14. Открытые вопросы
Нет блокирующих решений человека. Авторские выборы layout/types/defaults определены; качественный обычный сценарий подтверждается visual acceptance.

## 15. Соответствие профилю
Avalonia native controls, UIA и keyboard; real store roundtrips, before/after automated native video, inspected actual states, ordinary Desktop smoke, synthetic fixtures, sequential tests. Технический JSON показывается лишь в явно техническом/legacy fallback, не вместо supported editor.

## 16. Планируемые файлы
| Путь | Изменение |
|---|---|
| Application/Contacts/ContactPresentationCatalog.cs, ContactValuePresenter.cs | Semantic options/labels/formats, immutable data |
| Desktop/Contacts/ContactCardControls.cs + domain editor classes | Compact view/editor, partial date/type controls, menus/chips |
| Desktop/Contacts/ContactsView.cs, ContactsViewModel.cs | Header, chooser/search, stable focus/validation/mutation |
| Application/Contacts/ContactTableQuery.cs | Display aliases только в full-text ветке, сохраняет raw query predicates |
| Application/Contacts/DraftRestartGuard.cs | Minimal expected-generation checked mutation для owned popup result; существующий restart lock и ordinary callers сохранены |
| Tests Core/Google/Headless/Authoring/FlaUI | Independent metadata/roundtrip, before/after, negative/runtime acceptance |
| README, CHANGELOG, docs/VALIDATION | Новый user flow и exact evidence |

Core schema и storage не рефакторятся ради UI; update guard меняется только для checked popup-result/invalid-input integration с обязательными races/negative regressions, без расширения risk/scope.

## 17. Было → стало
| Было | Стало |
|---|---|
| Просмотр — readonly форма | Читаемая карточка/строки |
| Type — TextBox с API token | Русский список + точный свой тип |
| 3 числовых компонента даты | Единый partial-date control |
| Большие кнопки на каждое свойство | Row ⋮ и inline clear |
|180px photo + все checkbox labels |64px avatar + выбранные chips |
| Пустые блоки всех полей | Add-field chooser + named details |

## 18. Альтернативы
Просто перевести FieldTitle: мало риска, но сохраняет боль просмотра/плотность. Переписать23 независимые формы: хорошая специализация, высокий риск выпадения111 leaves и дублирования. Выбрано несколько domain editor families + typed named fallback; schema остаётся независимым oracle полноты.

## 19. Quality gate / review
### SPEC linter self
| Критерий | Статус | Основание |
|---|---|---|
| A1 outcome | PASS | §1 original pain, compact card/table, full23/111 outcome |
| A2 AS-IS | PASS | §2 source/schema/validator и две лично просмотренные PNG |
| A3 проблема | PASS | §3 schema serialization смешана с информационной архитектурой |
| A4 цели | PASS | §4 domain editors, progressive disclosure, lossless conversions |
| A5 границы | PASS | §5 no migration/normalization/network/rich HTML/new sync redesign |
| B6 responsibility | PASS | §6.1 immutable catalog/presenter/Desktop forms/existing VM-store |
| B7 integration | PASS | §8 row.Cell/search/mutation, stable controls/IDs, checked popup result |
| B8 invariants | PASS | §6.4/7 exact custom/raw/date/units/order и readonly boundaries |
| B9 errors/recovery | PASS | §6.6 state matrix, inline invalid buffer, stale popup/dirty/drift guards |
| B10 performance | PASS | §6.2 lazy/virtualized overflow, AC8 10k/2kfixtures, no full rebuild on typing |
| C11 state | PASS | §9 UI-only buffers; invalid-buffer dirty/restart protected |
| C12 compatibility | PASS | §6.8 stable144 IDs/prefs/schema, no default writes/migration |
| C13 rollback | PASS | §10 read-compatible1fa82b8, temp synthetic datasets, no mass repair |
| D14 AC | PASS | §11 AC1–10, first-fold/minwidth/contrast/roundtrip measurable |
| D15 test mapping | PASS | §11 matrix, independent111/options oracle, native sys picker, negative races |
| D16 commands/stop | PASS | §11 serialized Release/Core/Google/TUnit/doctor/schema, post-EXEC/all AC |
| E17 plan | PASS | §13 baseline→converters→forms→integration→actual acceptance; meaningful dependencies |
| E18 decisions | PASS | §6.7 ledger, §12 objections, §14 no blocking human choices |
| E19 form | PASS | §0 Expanded/large: multi-module behavior and hidden-value conversion risk |
| F20 profile | PASS | §15 UIA/native/ordinary smoke/PNG/videos, no unsupported acceptance claim |

| Rubric self | Балл | Обоснование |
|---|---:|---|
| Цель/границы | 5 | Исходная боль, card/table scope, full coverage и non-goals явно заданы |
| AS-IS | 5 | Actual code/embedded schema/две просмотренные PNG, существующие storage guards |
| Дизайн | 5 | View/edit wireframes,23-category mapping, precise type/date/percent rules |
| Safety/rollback | 5 | No-op/raw preservation, unchanged storage/prefs schema, compatible rollback |
| Проверяемость | 5 | AC1–10→independent/actual controls/native/visual evidence, negative states |
| Autonomous readiness | 5 | Default decisions, no human blockers, fixtures/stages/commands/stop |

Self30/30; self score не подтверждает реализацию или separate review.

| Role | Applicability | Self verdict / challenge |
|---|---|---|
| Domain | applicable | PASS: phone/date/type смысл без расширения writable contract |
| UX/designer | applicable | PASS: compact view/edit, density/rare/menu, concrete wireframes+visual AC |
| Tester | applicable | PASS: actual controls, independent inventory/no-op/roundtrip, exact full/new suites |
| Architect | applicable | PASS: overlay not schema, presenter not raw predicate, stable mutations |
| Delivery/security | applicable | PASS: no live data/network/HTML execution, existing CAS/restart preserved |

### Post-SPEC review
Статус / stop decision: PASS плана, готово к exact approval. Self Scope/Evidence прочитал current card/schema/query/VM boundaries, embedded schema/validator, inspected two original PNG, live People docs, canonical template/quest/review/profile/linter. Contract pass: table/card scope и full111 preservation; adversarial: custom/empty/partial dates, HTML/raw values, units, invalid buffer vs dirty, localized search vs raw comparisons.

Отдельный reviewer `ux_spec_review` выполнил Scope/Evidence, Contract, Adversarial, Role-Based, Fix/re-review и Stop passes. Проверены actual текущая SPEC, ContactCardControls/ContactFieldSchema/ContactTableQuery/ContactsView/VM/DraftRestartGuard, embedded schema/validator, depth/quality owners. Это procedural advisory/adversarial fallback: runtime danger-full-access/unrestricted, технической read-only isolation нет; reviewer не делал edits/tests/UI/build/external mutations.

| Severity | Area | Finding | Action / actual disposition |
|---|---|---|---|
| HIGH | Popup gate | HasModal выключает CanEdit; own dropdown choices нельзя применять через обычный Mutate | §6.6 owned buffered result + captured identity/generation + same-gate checked mutation; direct/stale/race AC6; fixed, re-review PASS |
| MEDIUM | Responsive prefs | Stored fraction.8 оставляет меньше420px карточке при1000 | §6.2 temporary clamp/no prefswrite/single-pane/restore; extreme fraction AC8; fixed, re-review PASS |
| MEDIUM | Summary provenance | Google array/index/value join не доказывает primary identity | §6.4 local-only badge/summary, Googleprimary service-only/no joins; raworder/duplicates/stale AC4; fixed, re-review PASS |
| MEDIUM | Quality record | Сгруппированные linter оценки не показывают все20 criteria/rubric rationale | §19 individual20 linter+6 rationale и findings/depth; fixed, re-review PASS |
| MEDIUM | Filter units | Friendly percent cell расходится с old raw operand; decimal scaling1e−28 silently rounds | §6.4 percent→raw operand boundary и checked exact scaling/raw-unit fallback, old raw/date operands unchanged, AC7 math oracle; fixed, targeted re-review PASS |

Relevant roles separate fallback: Domain PASS (type/date semantics/full coverage); UX PASS (wireframes/density/adaptive/modes); Tester PASS (independent actual-control/negative/before-after plan); Architect PASS (immutable overlay/raw-predicate/popup gate boundaries); Delivery/security PASS (no live data/HTML execution/storage migration, technical-isolation limit disclosed).

Depth / NoFindingJustification: исходное поручение и observable scenarios→AC сверены, full111 coverage не заменено общим JSON editor, no-op/custom/legacy/units/primary/defaults имеют конкретные counterexamples/tests. Проверены Scope drift, hidden contract changes, old prefs, busy/dirty/popup races, visual artifact и planned ordinary/native/video validation. Все пять находок исправлены и reviewer прочитал targeted fixes; открытых BLOCKER/HIGH/MEDIUM нет. Manual-review challenge: потеря hidden data/focus либо равенство неправильным unit/raw token должны обнаруживаться указанными независимыми UI/store/math checks. Actual runtime/render/physical acceptance новой карточки остаётся обязательным EXEC, PASS плана не подтверждает её работу.

### Дополнительное UI/UX review по поручению пользователя,12.10.2026
Исходное уточнение: «Сделай ревью как UI и UX дизайнер и исправь замечания». Выполнено в текущей фазе SPEC; root единственный writer. Отдельный `ux_spec_review` перечитал revised wireframes, interaction paragraphs и соответствующие AC; широкий math/CAS/source pass без новых оснований не повторялся.

| Severity | UI/UX finding | Исправление / evidence плана | Disposition |
|---|---|---|---|
| MEDIUM | VIEW affordance не объясняет вход в draft | VIEW readonly statuses/copy/Edit-field, exact focus + Save/Cancel, фото preview-only; AC1/5/6 | Fixed, targeted re-review PASS |
| MEDIUM | Find-existing/Add-missing не обнаруживаются из макетов | Visible Find/CtrlF/results context, singleton GoTo, Expand/Back, no-op reveal и сохранённый query; AC4 | Fixed, targeted re-review PASS |
| MEDIUM | При0selected ярлыках нет entry выбора | Always reachable EDIT entry, count/Done/Cancel,0groups/unknown/longlist/chips overflow; AC5 | Fixed, targeted re-review PASS |
| MEDIUM | Small actions могут оказаться недоступными | Hit28/32, accessibleName/focus, keyboard/menu return/delete neighbor, full-value copy, typography/toolbar wrap; AC8 | Fixed, targeted re-review PASS |
| MEDIUM | Несколько ошибок/hidden field/CAS drift не имеют полного recovery пути | Clickable N-error summary, live input reveal, query/buffers kept, visible Reload/Stay и честный save outcome; AC6 | Fixed, targeted re-review PASS |
| MEDIUM | Empty-first-use выглядит как автоподстановка типа/даты | No-write placeholders, «Тип не задан», blank date; existing no-op vs explicit new create; AC2/3/6 | Fixed, targeted re-review PASS |

NoFindingJustification: wireframes содержат actual points of entry, mode change/focus/keyboard contracts, progressive field discovery, editable labels при0, first-use и error recovery. Все шесть замечаний исправлены в тексте/macets и reviewer сверил affected AC1/2/3/4/5/6/8; пять ролей сохраняют post-SPEC PASS с обновлённым UX disposition. Root static diff check новой SPEC PASS; Git содержит только эту spec. Stop: PASS плана, можно запросить exact approval. Runtime/new rendered acceptance не заявляется. Reviewer sandbox writable/unrestricted; процедурный advisory/adversarial fallback без технической read-only isolation, без edits/build/tests/UI/runtime mutations.

Post-EXEC: **PASS, локальный EXEC завершён**. Full Core163/163, Google/storage185/185, Headless90/90 и Native13/13 прошли без пропусков (451 tests); ordinary Desktop EXE и Native photo/type/error/local-save/manual-sync сценарии прошли. BEFORE archive1fa82b8 rerun1/1 и AFTER exact-HWND videos16.6/16.8s,2250×1576,5fps,noaudio сверены ffprobe и просмотрены по кадрам. Schema/strictdoctor/diffcheck PASS. Separate full reviewer подтвердил Scope/Evidence, Contract, Adversarial, Fix/re-review,5roles, Depth и Stop; AC1–10 закрыты, открытых findings и обязательного оставшегося work нет. Sandbox reviewer unrestricted/writable: процедурный advisory/adversarial fallback без технической read-only isolation; edits/build/tests/UI не запускал. Полный audit/dispositions/evidence/границы: `docs/FRIENDLY-CONTACT-FIELDS-VALIDATION.md`.

## Approval
Получено exact approval «Спеку подтверждаю» для этой SPEC после дополнительного UI/UX review. Separate post-SPEC завершён PASS. EXEC разрешён; commit/push/release/install не входят в это approval. Предыдущие approval/commit относятся к предыдущей реализации.

### Разрешённая доставка,12.10.2026

После локальной приёмки пользователь отдельно поручил: «Влей в местер и выпусти релиз, чтобы программа могла обновиться». Разрешены commit/push/PR merge в фактическую основную ветку `main` и публичный стабильный GitHub Release0.4.0. Это доставка уже принятой реализации существующими workflow; новый product behavior не добавляется. Версия MINOR включает полный редактор и friendly forms, формат данных/настроек сохраняется. Package ID `ContactMirror.Desktop`, канал `win`, RID `win-x64`, Production profile и GitHub источник остаются прежними. Сборка использует существующий Desktop OAuth client издателя; исходный client JSON отдельно в Git/release не попадает.

Release gate: принятую451-test реализацию коммитить отдельно от version/changelog/release notes; проверить main/merged SHA, Production self-contained package profile/OAuth/update source, feed/full ZIP/nuspec/hash/Setup/portable, exact tag и пять публичных assets. После публикации проверить unauthenticated discovery/download в изолированном cache для существующей Production identity; установку/перезапуск рабочей программы не выполнять. Откат source через revert, исходные0.3.0assets сохраняются; обновления скачиваются и устанавливаются только по действию пользователя. Core stack central AGENTS/routing/QUEST/collaboration/tool-execution, dotnet desktop profile, commit/GitHub delivery policies, существующие `docs/RELEASE.md` и Build/Publish-Release scripts.

## 20. Журнал
| Фаза/событие | Решение | Evidence/остаток | Следующее | Решение человека | Артефакты |
|---|---|---|---|---|---|
| SPEC,12.10.2026 | Новый domain-aware view/editor, full schema coverage сохранён | Clean1fa82b8; source/PNG/API изучены; draft self-pass | Separate adversarial read-only по действиям, fixes/re-review | Исходное поручение; exact approval ещё нет | Только эта SPEC |
| Post-SPEC,12.10.2026 |1HIGH/4MEDIUM plan findings исправлены и actual targeted re-review PASS | Full5role/depth;20linter+6rubric; procedural fallback, technical isolation absent; runtime ещё впереди | Передать конкретную SPEC/wireframes на exact approval | Новое approval ещё не получено | Только эта SPEC |
| UI/UX review,12.10.2026 |6MEDIUM usability findings исправлены, affected targeted re-review PASS | Revised wireframes/visible entries/modes/labels/hitareas/save recovery/emptyfirstuse; source unchanged | Exact approval перед EXEC | «Сделай ревью как UI и UX дизайнер и исправь замечания», не является exact approval | Только эта SPEC |
| EXEC начат,12.10.2026 | Exact approval получено; база1fa82b8, existingbranch feat/contact-editor | Baseline→presentation/guards→domainforms→mandatoryacceptance; rootDesktopUI/docs/build, editor_schema_queryApplicationcatalog/query/tests, editor_storageVM/guard/tests; commonoutputsbuilds сериализованы root | Сохранить oldbinary baseline и реализовать approvedscope | «Спеку подтверждаю» | SPEC, утверждённые code/tests/docs; generatedevidence ignored |
| Post-EXEC,12.10.2026 | PASS, локальная реализация завершена |451/451,0skips;baseline1/1;ordinaryEXE;12layoutPNG+nativephoto/errors+pairedHWNDvideo;schema/doctor/diffcheckPASS;full5role/adversarial/depth reviewer PASS;allAC1–10 | Передать working result и audit | Нового решения для localEXEC не требуется; delivery отдельно | Source/tests/README/audit/SPEC; ignored PNG/video/TRX |
| Delivery начат,12.10.2026 | Выпустить0.4.0 через main/GitHub/Velopack | Originmain3a93ea9 актуален, existingbranch1fa82b8+acceptedworkingtree; latest public0.3.0; primarymain clean; release/auth/workflow checked | Commit accepted feature→version/changelog→Production package→PR/main→publicRelease→isolated update check | «Влей в местер и выпусти релиз, чтобы программа могла обновиться» | ExistingSPEC, release metadata/evidence; runtime-install не разрешён |

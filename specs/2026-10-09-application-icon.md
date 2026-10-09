# Подключение выбранной иконки D к ContactMirror

## 0. Метаданные

- Тип: delivery-task; профиль `dotnet-desktop-client`, контекст `testing-dotnet`.
- Владелец: основной агент текущего чата; риск небольшой, изменение локальное и обратимое.
- Форма: expanded по центральному `templates/specs/_template.md`, поскольку затрагиваются MSBuild и параметры упаковки Velopack.
- Stack: central AGENTS / routing-matrix; creator-vibe-lens / creator-vibe; model-behavior-baseline; tool-execution-baseline; collaboration-baseline; quest-governance / quest-mode; testing-baseline / testing-dotnet; dotnet-desktop-client; spec-linter / spec-rubric / review-loops.
- Целевой behavior baseline каталога: GPT-6 Astra; поверхность Codex desktop. Модельная миграция и eval модели не применимы: меняется ресурс приложения, настройки модели не затрагиваются.
- Checkout: `C:/Users/Kibnet/.codex/worktrees/68c1/ContactMirror`, detached HEAD `d64ed60`; до SPEC рабочие отслеживаемые файлы чистые. Другой worktree `5e9c` принадлежит release-ветке и не изменяется.
- SDK: фактически проверен `10.0.401`; Avalonia `12.1.3`; Velopack CLI закреплён на `1.2.0`.
- Исходник: `chat-artifacts/icon-selected-d/contactmirror-source.png`; ICO: `chat-artifacts/icon-selected-d/contactmirror.ico`.
- SHA256 выбранного ICO: `F80FFE117BBC64587E344BECFB0C004958CAD57C68524D2FF74F7C31C730F5B8`.
- Поручения: «Мне нравится D»; затем «Подключи к приложению».

## 1. Overview / Цель

Пользователь видит выбранный вариант D — человек перед стеклянным зеркалом на тёмном фоне — как иконку ContactMirror, а не только как отдельный файл из чата.

Outcome contract: один и тот же рисунок используется в ресурсе EXE, основном окне, окне восстановления обновления и локально собираемом Setup. Готовность подтверждается сборкой, проверкой ресурсов, существующими тестами и визуальным evidence. После обязательных проверок остановиться; установка, публикация и Git delivery не входят в результат.

## 2. Текущее состояние (AS-IS)

`ContactMirror.Desktop.csproj` задаёт WinExe / AssemblyName `ContactMirror`, но не ApplicationIcon и не AvaloniaResource иконки. `MainWindow.axaml` и `UpdateRecoveryWindow.cs` не задают Icon. `App.OnFrameworkInitializationCompleted` выбирает одно из этих окон. `scripts/Build-Release.ps1` вызывает `vpk pack` без `--icon`.

В чате уже подготовлен выбранный D с 10 кадрами ICO 16–256 px; они проверены декодированием и совпадают с PNG. Подготовленные картинки находятся в игнорируемом `chat-artifacts`, поэтому их нельзя оставить единственным источником для обычной сборки из Git.

## 3. Проблема

Выбранный знак не подключён к ресурсам и упаковке приложения.

## 4. Цели дизайна

Сохранить рисунок D без перерисовки; иметь один ICO для всех точек подключения; хранить исходный PNG в репозитории; встроить runtime-ресурс, чтобы работа приложения не зависела от внешнего файла в рабочей папке.

## 5. Non-Goals

Перерисовка, смена выбранного варианта, изменения синхронизации, OAuth, данных, идентичности пакетов, версии продукта или интерфейсных потоков. Установка или обновление существующего приложения и GitHub Release не выполняются. Изначальная граница исключала Git delivery; последующее прямое поручение «Влей в мейн» 2026-10-09 разрешает отдельную рабочую ветку, commit/push/PR и merge в main для этого же изменения.

## 6. Предлагаемое решение (TO-BE)

### 6.1 Распределение ответственности

`src/ContactMirror.Desktop/Assets/contactmirror.ico` — единый подключаемый ICO; соседний `contactmirror.png` — оригинальный мастер D. Проект встраивает только ICO как AvaloniaResource и использует его как ApplicationIcon. `App` предоставляет общий загрузчик WindowIcon, оба конструктора окон используют его. Release-скрипт передаёт этот же ICO в Velopack. Все изменения выполняет основной агент последовательно.

### 6.2 Детальный дизайн

- Скопировать ICO и исходный PNG побайтово из выбранного комплекта в `Assets/`.
- Добавить `<ApplicationIcon>Assets/contactmirror.ico</ApplicationIcon>` и явный `<AvaloniaResource Include="Assets/contactmirror.ico" />`.
- Общий загрузчик в `App.axaml.cs` читает `avares://ContactMirror/Assets/contactmirror.ico` через AssetLoader, создаёт WindowIcon и закрывает поток. Имя assembly берётся из проверенного csproj, а не из имени каталога.
- Установить Icon в `MainWindow` после InitializeComponent и в `UpdateRecoveryWindow` до отображения. MainWindowFactory тестового хоста продолжает работать.
- В вызов `vpk pack` добавить `--icon` с абсолютным путём от `$taskRoot` к тому же ICO. Локальный `dotnet vpk pack --help` уже подтвердил существование этого параметра: «Path to icon file for package».
- Отсутствующий ресурс должен выявляться сборкой или запуском smoke; исключения не подавлять и дефолтную иконку молча не подставлять.
- Performance: один маленький встроенный ресурс при создании окна; сетевых запросов и фоновых задач не появляется.
- Visual planning artifact: [выбранный D и маленькие размеры](../chat-artifacts/icon-selected-d/size-preview.png). Это local-only preview, не evidence работающего приложения. Сохранённые признаки: композиция, лицо, зеркало, тёмный фон и подсветка D.
- UI video: не применимо — статическая иконка, взаимодействия и анимации не меняются. Evidence после EXEC: инспекция PNG из фактического ресурса EXE/Setup, runtime-проверка WindowIcon; native кадр заголовка окна при доступной desktop-сессии. При недоступном native capture явно сообщить границу.

### 6.3 User-Observable Scenarios

| Scenario | Trigger | Expected result | Evidence | AC |
| --- | --- | --- | --- | --- |
| EXE | Обычная сборка Desktop | EXE содержит D вместо стандартного знака | Извлечение Windows icon resource и просмотр PNG | AC1 |
| Главное окно | Обычный запуск / demo | Window.Icon загружается из встроенного D | Существующий headless запуск, runtime smoke / native кадр | AC2 |
| Восстановление | Создание UpdateRecoveryWindow | Используется тот же D | Runtime smoke конструктора и общего загрузчика | AC2 |
| Setup | Build-Release с Synthetic | Локальный Setup содержит D | Проверка ресурса собранного Setup; CLI log | AC3 |
| Сборка из Git | Нет доступа к chat-artifacts | Все ресурсы берутся из отслеживаемого Assets | Пути csproj / release script; сборка | AC4 |

### 6.4 State / Interaction Matrix

Статическая иконка не вводит новых состояний. Обычный startup и recovery startup сохраняют прежние ветви; Icon присваивается при создании соответствующего окна. Пустые контакты, ошибки Google, busy-состояния и команды не меняются.

### 6.5 Decision Ledger

| Decision | Owner | Chosen option | Confidence | Risk | Needs user before EXEC |
| --- | --- | --- | ---: | --- | --- |
| Рисунок | user | D, уже выбран | 1.0 | Перерисовка разрушила бы принятый стиль; её нет | Нет |
| Точки подключения | agent | EXE, два окна, Setup | 0.99 | Только EXE оставил бы дефолтный значок окна | Нет |
| Формат | agent | Существующий проверенный ICO; мастер PNG | 0.99 | Нет зависимости от преобразований во время сборки | Нет |
| Проверочный пакет | agent | Synthetic, отдельный output, prerelease-версия | 0.99 | Не использовать Production OAuth / активный feed | Нет |
| Установка / публикация | user | За пределами поручения | 1.0 | Внешний side effect не разрешён | Нет |

Обязательная фаза EXEC начинается только после exact approval ниже; существенных нерешённых дизайнерских вопросов нет.

### 6.6 Runtime / Config / Data Contract Matrix

| Area | Source of truth | Change | Compatibility | Verification |
| --- | --- | --- | --- | --- |
| EXE resource | csproj / Assets ICO | MSBuild ApplicationIcon | Формат приложения прежний | Build и ресурс EXE |
| Window resource | AvaloniaResource / App loader | Встроенный ICO | URI использует AssemblyName ContactMirror | Runtime smoke обоих окон |
| Setup resource | Build-Release / vpk | --icon | ID, shortcuts, OAuth и update feed прежние | Synthetic pack и Setup resource |
| Пользовательские данные | Текущие storage / OAuth contracts | Нет | Миграция не нужна | Diff, существующие tests |

## 7. Бизнес-правила / Алгоритмы

Не применимо: статический бренд-ресурс. Инвариант — подключаемый ICO побайтово совпадает с выбранным D.

## 8. Точки интеграции и триггеры

MSBuild WinExe, конструкторы MainWindow / UpdateRecoveryWindow, `vpk pack` в Build-Release. Дополнительных жизненных циклов, действий пользователя или сервисов нет.

## 9. Изменения модели данных / состояния

Нет; SQLite, JSON, токены, настройки и update marker не затрагиваются.

## 10. Миграция / Rollout / Rollback

Локальная реализация и отдельная Synthetic-упаковка. Проверочный output — `artifacts/icon-validation`, версия `0.2.0-icon.1`; не менять Version в исходниках. Rollback: удалить добавленные asset-файлы и отменить только изменения подключения в перечисленных файлах. Кеш Windows может сохранять старую иконку уже установленного EXE; установленный сценарий здесь не заявляется и кеш пользователя не очищается.

## 11. Тестирование и критерии приёмки

- AC1: собранный Desktop EXE имеет Windows icon resource с рисунком D.
- AC2: оба окна получают декодируемый встроенный WindowIcon D; запуск главного окна не ломается.
- AC3: Release-скрипт успешно создаёт изолированный Synthetic-пакет; локальный Setup содержит D.
- AC4: сборка/пакетирование не читают иконку из chat-artifacts; ICO в Assets совпадает с выбранным по SHA256, PNG-мастер сохранён.
- AC5: обязательная сборка и существующие регрессионные проверки green, unrelated changes отсутствуют.

Набор проверок ограничен ресурсами и упаковкой: logic/storage/contracts не меняются, новый UI flow отсутствует. Новые постоянные тесты, зеркалирующие присваивание Icon, не добавляются. Runtime-диагностика допустима в игнорируемых artifacts; существующие тесты сохраняются.

Проверки последовательно, поскольку общий bin/obj:

```powershell
dotnet build ContactMirror.sln -c Release
dotnet test tests/ContactMirror.Tests -c Release
dotnet test tests/ContactMirror.GoogleTests -c Release
dotnet run --project tests/ContactMirror.UiTests.Headless -c Release -- --treenode-filter '/*/ContactMirror.UiTests.Headless.Tests/MainWindowHeadlessTests/*' --maximum-parallel-tests 1
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.0-icon.1 -ValidationProfile Synthetic -Output artifacts/icon-validation
```

Дополнительно: декодировать ICO, сверить SHA256; runtime smoke двух окон и общего ресурса; извлечь и осмотреть фактические EXE/Setup иконки. Извлечённые иконки и logs хранить в `chat-artifacts/icon-integration-validation/` или `artifacts/`. Headless screenshot не выдавать за проверку системной панели задач или native titlebar. Фильтр TUnit сверить с фактическим discovery/skill при запуске; ожидаемое число не равно нулю.

Stop rules: перед долгим запуском сообщить команду и место logs; отслеживать живой прогресс. При зависании/timeout сначала диагностировать процесс/компиляцию/lock, не повторять неизменённую команду. Не считать environment blocker дефектом иконки; не закрывать обязательный AC отсутствующей проверкой. После green обязательного набора не расширять testing без новой причины.

### Acceptance-to-Test Matrix

| AC | Automated / runtime check | Visual / log check | Evidence | If not tested |
| --- | --- | --- | --- | --- |
| AC1 | Desktop build; Windows resource extraction | Сопоставить извлечённый PNG с D | EXE icon PNG, build log | Не допускается |
| AC2 | Headless MainWindow suite; диагностический запуск обоих конструкторов | Проверить WindowIcon и исходный ресурс; native кадр при доступности | Runtime log, native PNG либо явная граница | Native capture может быть недоступен; это не заменяет runtime smoke |
| AC3 | Synthetic Build-Release | Извлечь и просмотреть Setup icon; pack log | Setup PNG, build manifest | Не допускается |
| AC4 | SHA256 / path inspection | Источник Assets в csproj / release | Hash / diff | Не допускается |
| AC5 | Build, два xUnit-проекта, существующая Headless class suite | Самопроверка diff и scope | Logs / git status | Не допускается |

## 12. Риски и edge cases

Ошибочный assembly URI, ресурс только на диске, пропущенное recovery-окно, отсутствие --icon в pack и кеш старой установки. Контрмеры заданы в AC и проверках. Картинка D имеет много деталей: в 16 px детали лица теряются; пользователь уже выбрал именно этот рисунок, поэтому скрытая стилистическая переделка не разрешается.

### Expected User Review Objections

| Objection | Why likely | Mitigation | Status |
| --- | --- | --- | --- |
| «Ты опять изменил красивый вариант» | Предыдущая векторная версия была отвергнута | Побайтовый перенос ICO и исходника D; без перерисовки | mitigated |
| «В приложении всё ещё дефолтный значок» | EXE icon не задаёт Icon Avalonia автоматически | Подключить EXE, оба окна и Setup отдельно | mitigated |
| «В установленном приложении не обновилось» | Установка и кеш отличаются от сборки | Отдельно сообщить границу: только локальные исходники/пакет | mitigated |

Rework prevention: наблюдаемые сценарии, решения, AC/evidence и возражения заполнены; applicable role review выполнен ниже; AC описывают готовый результат, EXEC имеет путь проверки.

## 13. План выполнения

После approval перенести Assets и подключить все точки; выполнить сборку и runtime/resource checks; запустить выбранные регрессионные проверки; собрать Synthetic-пакет и проверить Setup; провести post-EXEC review и выдать пути/evidence. В installed/runtime других checkout изменения не переносить.

## 14. Открытые вопросы

Нет. Выбор D и поручение подключения получены; ожидается только фазовое exact approval.

## 15. Соответствие профилю

dotnet-desktop-client: UI поток не получает длительных операций; automation IDs сохраняются; пользовательские взаимодействия прежние; build и test включены в обязательный набор. UI automation framework не модифицируется; создание новой интеграции AppAutomation не требуется.

## 16. Таблица изменений файлов

| Файл | Изменение | Причина |
| --- | --- | --- |
| src/ContactMirror.Desktop/Assets/contactmirror.ico | Новый выбранный ICO | EXE, окна, pack |
| src/ContactMirror.Desktop/Assets/contactmirror.png | Исходный PNG D | Сохранить мастер вне игнорируемых artifacts |
| src/ContactMirror.Desktop/ContactMirror.Desktop.csproj | ApplicationIcon / AvaloniaResource | Встроенные ресурсы |
| src/ContactMirror.Desktop/App.axaml.cs | Общая загрузка WindowIcon | Единый URI / закрытие stream |
| src/ContactMirror.Desktop/MainWindow.axaml.cs | Присвоить Icon | Главное окно |
| src/ContactMirror.Desktop/UpdateRecoveryWindow.cs | Присвоить Icon | Recovery окно |
| scripts/Build-Release.ps1 | --icon в vpk pack | Setup / package branding |
| specs/2026-10-09-application-icon.md | Фаза и evidence | Журнал QUEST |

## 17. Таблица соответствий (было → стало)

| Область | Было | Стало |
| --- | --- | --- |
| EXE / окна / Setup | Стандартный знак | D |
| Источник для сборки | Нет иконки в Git | Отслеживаемый Assets |
| Рисунок D | Выбранный PNG / ICO | Тот же рисунок, подключённый к сборке |

## 18. Альтернативы и компромиссы

Только ApplicationIcon недостаточен для Avalonia окон и Setup. Внешний runtime-файл создаёт зависимость от директории запуска. Глобальный Window style мог бы менять посторонние диалоги; выбрано явное подключение к двум существующим окнам через общий загрузчик. Новая генерация D рискует изменить принятый стиль и не нужна.

## 19. Результат quality gate и review

### SPEC Linter Result

| № | Статус | Evidence / план |
| --- | --- | --- |
| 1 | PASS | §1, EXE / два окна / Setup |
| 2 | PASS | §2, прочитаны csproj, App, MainWindow, recovery, release script |
| 3 | PASS | §3, отсутствующие точки подключения |
| 4 | PASS | §4, один ресурс / принятый D |
| 5 | PASS | §5, локальный scope |
| 6 | PASS | §6.1 / §16, ownership основного агента |
| 7 | PASS | §8, четыре триггера |
| 8 | PASS | §7, SHA256 инвариант |
| 9 | PASS | §6.2 / §12, нет silent fallback |
| 10 | PASS | §6.2, ресурс при создании окна |
| 11 | PASS | §9, storage/state без изменений |
| 12 | PASS | §10, миграция не нужна |
| 13 | PASS | §10, ограниченный rollback |
| 14 | PASS | §11, AC1–AC5 |
| 15 | PASS | AC matrix и ошибочный resource URI / packaging контрпримеры |
| 16 | PASS | §11, команды, logs, stop rules |
| 17 | PASS | §13, dependencies и последовательная сборка |
| 18 | PASS | §6.5 / §14, существенных вопросов нет |
| 19 | PASS | §0, expanded для MSBuild / pack |
| 20 | PASS | §15, profile validation и stable selectors |

Итог: ГОТОВО к фазовому approval, не к заявлению об уже реализованном изменении.

### SPEC Rubric Result

| Критерий | Балл | Основание |
| --- | ---: | --- |
| Цель / границы | 5 | Наблюдаемые точки и Non-Goals |
| AS-IS | 5 | Текущие исходники и CLI help проверены |
| TO-BE | 5 | Пути, URI, загрузка и pack параметр |
| Безопасность | 5 | Пользовательские данные вне scope; rollback / Synthetic |
| Проверяемость | 5 | AC matrix включает EXE, оба окна, Setup и ошибочный URI |
| Автономность | 5 | D принят, внутренних нерешённых решений нет |

30/30: готово к автономному EXEC после exact approval.

### Role-Based Review Result

| Role | Applicability | Проверенный вопрос | Verdict | Изменения спеки |
| --- | --- | --- | --- | --- |
| Business analyst | Не применимо | Контактные бизнес-правила не меняются | PASS | Нет |
| UX / designer | Применимо | D сохраняется, не подменяется прежним SVG? | PASS | SHA256 и visual reference |
| Tester / validation | Применимо | Проверяются runtime URI, EXE и Setup отдельно? | PASS | Runtime smoke и ресурсный evidence обязательны |
| Developer / architect | Применимо | Ресурс встроен, URI совпадает с assembly? | PASS | Общий загрузчик / явный AvaloniaResource |
| Delivery / operations / security | Применимо | Пакет не трогает OAuth, active feed, install? | PASS | Отдельный Synthetic output |

### Post-SPEC Review

- Статус / stop decision: PASS для перехода к запросу approval. Реализация и проверки после неё ещё не выполнены.
- Scope reviewed / Evidence inspected: эта spec; central AGENTS, quest-governance/mode, expanded template, linter/rubric/review-loops, testing-baseline/dotnet, desktop profile; csproj, App.axaml.cs, MainWindow root/constructor, UpdateRecoveryWindow, Build-Release, Directory.Build.props, global.json, dotnet-tools, xUnit/Headless csproj, headless MainWindow suite; выбранный ICO SHA256; dotnet --version; `vpk pack --help`; чистый Git status.
- Scope/Evidence pass: изменяемые пути перечислены, второе release-worktree исключено; reference локальный и доступен в текущем чате.
- Contract pass: исходный сценарий, Non-Goals, approval, пять AC и обязательный test set согласованы; нет обещания installed результата.
- Adversarial risk pass: проверены пропуск recovery, неверный assembly URI, зависимость от chat-artifacts, Setup без --icon, подмена D и кеш установленного приложения.
- Role-Based pass: таблица выше; review выполнен основным агентом. Независимый reviewer не привлекался: небольшой статический branding scope, нет high-risk runtime/config/security изменения.
- Finding MEDIUM / evidence: headless screenshot сам по себе не доказывает иконку native titlebar. Disposition fixed in SPEC: отдельный runtime smoke и извлечение EXE/Setup; native capture — отдельная граница.
- Finding MEDIUM / delivery: обычная проверка Desktop не проверяет Setup icon. Disposition fixed in SPEC: Synthetic pack и инспекция Setup добавлены в AC3.
- Fix and re-review: повторно сверены §6.3, §11 matrix, §12 и ограничения §5; обе найденные пробелы закрыты обязательными проверками, scope не расширен до установки.
- Depth checklist: scope/unrelated — clean checkout и точный список; AC — все пять покрыты; decisions/scenarios/objections — заполнены; evidence — planning отделён от будущего выполнения; unsupported claims — runtime/tests не объявлены green; regression — URI/stream/pack разобраны; docs/changelog — продуктовая версия не меняется, release notes вне scope; hidden contract change — данные/OAuth/update identity прежние; manual review challenge — проверка только EXE недостаточна, поэтому окна и Setup проверяются отдельно.
- Residual risk: native desktop может быть недоступен; этот факт не должен скрываться за headless кадром. Detail loss в 16 px — свойство принятого D, не повод менять рисунок.
- Needs human: только exact approval, все предметные решения готовы.

### Post-EXEC Review

- Статус / stop decision: **PASS**. Локальное подключение и обязательные проверки завершены; installed / Production rollout не заявляется.
- Scope reviewed / Evidence inspected: фактический diff пяти файлов подключения; два новых Assets; эта approved spec; `git status --short` / `git diff --check`; build/test/package logs; runtime-smoke.json; assets-check.json; EXE и Setup resource-check.json и извлечённые PNG; native-titlebar.png; Synthetic build manifest. Все evidence находятся в `chat-artifacts/icon-integration-validation/`, пакет — `artifacts/icon-validation/`.
- Scope/Evidence pass: changed set совпадает с §16. Подключены csproj, App loader, два окна и pack; рисунок D сохранён побайтово. Новые диагностики и скриншоты игнорируются Git, в продукт не включены. Отслеживаемых unrelated changes нет.
- Contract pass: AC1–AC5 подтверждены таблицей ниже; Non-Goals соблюдены. Версия в исходниках, пакетная identity, OAuth, storage и пользовательские потоки не менялись. Synthetic prerelease существует только как локальный проверочный пакет.
- Adversarial pass: из EXE и Setup действительно извлечены десять ICO кадров, а не только просмотрен исходник; все пиксели каждого кадра совпали с D. Ресурс avares имеет корректный assembly URI и совпадает по SHA256. MainWindow и UpdateRecoveryWindow создаются, получают иконки и открываются; закрытие исходного stream не мешает показу окна. Обычный native --demo запуск использовал EXE именно этого checkout, а не установленный процесс.
- Role-Based pass: UX — исходный D сохранён, native и извлечённые PNG осмотрены; Tester — 107/107 + 147/147 + 13/13 и runtime/resource checks green; Developer — единый встроенный ресурс и закрываемый поток; Delivery — Synthetic pack завершился, Setup resource совпал, manifest published=false / oauthConfigured=false. Business analyst не применим — контактные правила не менялись. Это self-review, независимый review не заявляется.
- Finding LOW / evidence: первый native кадр из screenshot helper содержит окружающее окно Codex из-за DPI/координат. Disposition fixed: из реально просмотренного кадра сохранён только участок с titlebar ContactMirror; icon и demo-контекст были видны в исходном кадре. Повторная попытка захвата после закрытия диагностического процесса невалидна и evidence не является. `native-titlebar.png` происходит из первого валидного кадра, проверен визуально.
- Fix and re-review: кодовых находок нет. После ограничения native evidence повторно осмотрен native-titlebar.png; сопоставление с D и данные resource checks подтверждены. Повторная сборка/тесты не нужны: код после green не менялся.
- No-findings justification для кода: отсутствуют изменения runtime contracts и selectors; все пути Assets/assembly/pack проверены фактической сборкой и ресурсами; исходники D сверены побайтово; общий загрузчик проверен в обоих окнах. Скрытый fallback отсутствует.
- Depth checklist: scope drift — отсутствует; AC — закрыты; validation — все обязательные команды green; unsupported claims — installed/FlaUI-full/Production не заявляются; regression/edge cases — URI, stream lifetime, recovery и Setup проверены; comments/docs/changelog — ложных комментариев нет, версия/релиз не менялись; hidden contract — storage/OAuth/identity прежние; manual review challenge — Setup проверен отдельно от EXE, runtime окна отдельно от PNG источника.
- Residual risks: детали D теряются при 16 px, как и в выбранном пользователем preview; визуальная переработка не проводилась. Проверочный Setup не подписан по существующему workflow; подписанная поставка и установка вне scope. Native screenshot подтверждает главное окно, recovery отдельно проверен runtime smoke. Диагностический native процесс уже закрыт; существующая установленная копия не изменялась.
- Needs human: нет в подтверждённом scope. Stop: завершить локальную задачу, не расширять до установки, публикации, Git delivery или дополнительных suites.

#### Фактическое AC evidence

| AC | Результат | Evidence |
| --- | --- | --- |
| AC1 | PASS: Desktop EXE содержит все 10 кадров D, пиксели совпадают | `exe-icon/resource-check.json`, `exe-icon/group-0.png` визуально осмотрен |
| AC2 | PASS: оба окна открылись в runtime smoke с Icon; встроенные 168466 bytes совпадают с выбранным ICO; D виден в native titlebar главного --demo окна | `runtime-smoke.json`, `runtime-smoke.log`, `native-titlebar.png` визуально осмотрен |
| AC3 | PASS: Synthetic 0.2.0-icon.1 успешно упакован; все 10 Setup кадров совпадают с D | `package.log`, `setup-icon/resource-check.json`, `setup-icon/group-0.png` визуально осмотрен; `artifacts/icon-validation/build-0.2.0-icon.1.json` |
| AC4 | PASS: csproj / pack используют Assets, ICO и мастер PNG совпадают побайтово с выбранным комплектом | `assets-check.json`, runtime SHA256, фактический diff |
| AC5 | PASS: build 0 errors / 0 warnings; core 107/107, Google 147/147, headless MainWindow class 13/13; diff check green | `build.log`, `core-tests.log`, `google-tests.log`, `headless-tests.log`, Git status / diff review |

Фактические test invocations использовали `--no-build` после успешной сборки решения: это исключило повторную запись общих bin/obj и не меняло фильтры/набор. Headless запущен с `--treenode-filter '/*/ContactMirror.UiTests.Headless.Tests/MainWindowHeadlessTests/*' --maximum-parallel-tests 1`; нулевого discovery нет. Runtime smoke — временный проект в игнорируемом evidence-каталоге с `BuildProjectReferences=false`. Synthetic Build-Release вызван ровно по §11; Setup не запускался.

## Approval

Получено «спеку подтверждаю» в текущем чате 2026-10-09. Фаза EXEC; подтверждён описанный локальный scope.

## 20. Журнал действий агента

| Фаза / событие | Решение и основание | Evidence / остаток | Следующее действие | Решение человека | Артефакты |
| --- | --- | --- | --- | --- | --- |
| SPEC, 2026-10-09 | Подключить принятый D к EXE, двум окнам и Setup | Все точки найдены, --icon проверен CLI; приложение ещё не менялось | Exact approval → EXEC | D выбран; подключение поручено; exact approval ожидается | Эта spec, выбранные ранее assets |
| EXEC, 2026-10-09 | Получено подтверждение спеки; внесено подключение ресурса | ApplicationIcon, AvaloniaResource, общий загрузчик двух окон и --icon в pack | Проверки обязательного набора | «спеку подтверждаю» | Desktop, Build-Release, Assets |
| EXEC completion, 2026-10-09 | Все AC закрыты; post-EXEC PASS | Build green, 107 + 147 + 13 tests; два runtime окна; EXE/Setup кадры совпали; native titlebar просмотрен | Завершить локальную задачу | Дополнительного решения не требуется | Assets, исходники подключения, Synthetic пакет, evidence |
| DELIVERY, 2026-10-09 | Получено «Влей в мейн»; main удалённого репозитория продвинулся до d5585c8 | Иконка проверена на d64ed60; для merge нужны перенос на актуальный main и повторная validation | Рабочая ветка → commit → rebase origin/main → проверки → push/PR/merge | Пользователь явно разрешил merge в main; установка/release не запрошены | Эта spec и тот же связный change set |

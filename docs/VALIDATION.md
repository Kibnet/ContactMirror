# Проверки локальной сборки

## Текущий EXEC: сравнение и отправка всего контакта, 2026-10-08

Реализация по `specs/2026-10-07-contact-edit-diff.md` подтверждена пользователем. Проверки используют вымышленные контакты; реальные Google-контакты и установленная версия приложения в этой задаче не изменялись.

- Core **107/107 Release**: точные различия, все 23 WritableFields, ярлыки/избранное, фото, сохранение исходных байтов и правок при исправлении `google`, drift guards, restart/repeated failures/metadata-only recovery. `TestResults/ContactEdit/release-core/core.trx`.
- Google **147/147 Release**: production HTTP gateway и coordinator с реальным файловым хранилищем, синтетическим HTTP сервером. Exact masks/source/etag, PROFILE/system memberships, photo add/replace/delete, перекодирование и partial failure. Сбой внутри SQLite repair transaction откатывает разрешение intents; перезапуск завершает локальное восстановление без HTTP/token calls. `TestResults/ContactEdit/release-google/google.trx`.
- Headless **15/15 Release**: исходные UI-сценарии, mixed-contact + same-path photo → repair → ручная проверка → upload → no-op, 2000 строк различий для JSON больше 1 МБ с виртуализацией и отменой старого выбора. Настройки открыты в 1000×680; детали сохраняют высоту больше 100 DIP. `TestResults/ContactEdit/release-headless/`.
- Native **7/7 Release**: полный набор, исходный mixed-contact/photo flow и compact settings-open. `TestResults/ContactEdit/release-native/`. DPI216; окно compact2250×1575 физических пикселей. UIA координаты логические: height138, assertion100. Поздние изменения recorder/capture дополнительно проверены этим Release run.

Сборка `dotnet build ContactMirror.sln -c Release -m:1`: 0 ошибок/предупреждений. Repair меняет только служебный снимок и локальный журнал, оставляет data/labels/starred/photo/extensions и baselines; следующий upload требует отдельной ручной проверки и применения. HTTP fixtures не являются новым тестом в настоящем Google аккаунте. Установка, commit и публикация не выполнялись.

Финальные PNG (просмотрены):

- `chat-artifacts/ui/20261008-104627-112/contact-edit-blocked-name-values-native.png`: полная фамилия Ветрова / Петренко / Ветрова, точный path и три стороны;
- в той же папке `contact-edit-blocked-photo-values-native.png`: текущие local/Google портреты и замена содержимого по прежнему пути;
- `chat-artifacts/ui/20261008-104609-164/contact-edit-blocked-settings-compact-{photo,field}-values-native.png`: настройки и детали в компактном окне, содержимое доступно прокруткой;
- headless compact/large rendered PNG сохранены тестами `release-headless`.

`chat-artifacts/contact-edit/{before,after}.mp4`: каждый30s,15fps,2656×1846; client area приложения через PrintWindow без звука; смена активного окна не попадает в кадр. Before взят из isolated исходногоcfc57bf с корректным synthetic photo, after — финальный Release. В before виден blocked JSON без diff/repair; after показывает фото/поля, локальное исправление,5 selectable uploads и следующий no-op. After проверен по ffprobe и кадрам1/10/25s: readable surname, затем no-op, внешних окон нет. Before проверен по ffprobe и кадрам5/25s: тот же blocked контакт, полный JSON без diff/repair, внешних окон нет. Запись450 кадров воспроизводится при15fps; видео демонстрирует последовательность состояний и не измеряет время синхронизации. Прежний GDI after с другим foreground окном исключён из evidence. Промежуточные window recordings с desktop/DWM edges и первоначальный invalid-photo video исключены из итогового evidence.

Новый UI regression на isolated baseline ожидаемо падает из-за отсутствующего `AutomationId:DiffSummary`: `TestResults/ContactEdit/red-valid-baseline/`. Исходные production файлы baseline не изменялись, auth secrets не копировались.

Итог: **Release276/276 PASS**, сборка0 warnings/errors. Финальный независимый пяти-ролевой audit: **PASS** (business/domain, UX/designer, QA, architect, delivery/security); открытых BLOCKER/HIGH/MEDIUM/LOW нет. Reviewer самостоятельно прочитал Release TRX (storage в bin/Release, нет пропусков), recording runs1/1+1/1, финальный surname PNG, before/after кадры и ffprobe, SPEC и эту документацию. Результат сборки0/0 подтверждён root, reviewer сборку не повторял. Review выполнялся read-only в технически writable danger-full-access sandbox; техническая read-only изоляция не заявляется. Локальный EXEC завершён; установка, новая live Google запись и публикация остаются вне этого scope.

Перед выдачей установщика выполнена упаковка текущих исправлений как Production0.2.6 через существующий `scripts/Build-Release.ps1`. Сборка Windows x64 self-contained включает Desktop OAuth client и GitHub feed Kibnet/ContactMirror; установка и публикация не выполнялись. Архивный Setup: `artifacts/releases/Production/versions/0.2.6/ContactMirror.Desktop-win-Setup.exe`,55.3MiB. SHA256 архивного Setup совпадает с build manifest; четыре production assemblies в full nupkg совпадают с собранным staging. Артефакты и OAuth JSON остаются ignored. Последующее поручение «Закоммить» разрешает локальный коммит этих source/tests/docs.

## Предыдущий EXEC: Google и Velopack, 2026-10-07

Локальные проверки завершены для кандидата 0.2.4. Публикация и проверка удалённого GitHub feed ожидают отдельного разрешения; полный post-EXEC PASS до этого не выставляется. Проверки ниже разделяют реальные установленные сценарии и HTTP fixtures.

- Настоящий Google OAuth вход выполнен в установленной IntegrationValidation. Обе изолированные копии Synthetic и Integration обновлены 0.2.0 → 0.2.2 через интерфейс. Новые PID/версии подтверждены; настройки и DPAPI vault сохранены, повторного согласия Google не потребовалось. Evidence: `chat-artifacts/installed-{google,synthetic}/preservation-{before,after}.json` и PNG после обновления.
- Настоящий экспорт завершён: 1003 контакта и 5 ярлыков сохранены в пользовательскую папку. Следующая ручная проверка: 0 отправок, 0 скачиваний, 0 конфликтов, 0 удалений; одна запись заблокирована из-за внесённой после экспорта правки служебного google.person. Самостоятельно эта пользовательская правка не перезаписывалась. Evidence: `chat-artifacts/installed-google/{status,manual-check}.png`; все 1003 JSON прочитаны и проверены.
- По отдельному разрешению выполнен Google↔JSON тест одного нового контакта и одного нового ярлыка: поля, фото, избранное, отправка телефона из JSON, скачивание изменённой заметки, следующее no-op. Созданные ресурсы удалены, отсутствие подтверждено чтением. Evidence: `chat-artifacts/google/live-fixture/fixture-journal.json`. Существующие контакты тест не изменял.
- Чистое Google→папка сохранение использует показанный снимок и фото без повторного чтения Google. Remote write, deletion/recovery и локальный drift проверяются отдельно. Read429 retries ограничены, учитывают Retry-After и обновляют access token после паузы; mutation retry запрещён.
- До дополнительного ускорения файлового хранилища: полная сборка 0 предупреждений; Core53/53, Google132/132, Headless13/13, Native5/5 PASS. Это результаты предыдущего состояния исходников; свежие проверки ускорения записываются отдельно.
- Первый download обнаружил несовпадающий SHA256 delta ZIP. Исправление скачивает исходный full один раз и снова строго проверяет размер, SHA256 и метаданные; после этого оба установленных обновления прошли.
- Пользователь выбрал `https://github.com/Kibnet/ContactMirror`. Production0.2.2 пакет собран с этим источником и publisher OAuth client; публичный репозиторий и релиз ещё не созданы. Скрипт `Publish-Release.ps1` в режиме подготовки проверяет все assets без публикации.

Ускорение файлового сохранения: session identity index заменяет повторный полный разбор папки. Namespace changes отслеживаются Windows directory notifications; RWH leases обнаруживают открытые и mapped writers, одни и те же native file handles используются для чтения. При неподдерживаемой FS, отмене наблюдений или overflow выполняется безопасный полный перечит. Один постоянный issuing thread сохраняет kernel notifications при завершении вызывающего потока. Проверки path/photo/CAS/recovery не отключены.

Свежие результаты ускорения: Index+Safety44/44 PASS, полный Google suite141/141 PASS (`TestResults/GoogleVelopack/{index-safety-final,google-final}.trx`). Disk harness выполняет настоящий `FileWorkspaceStore`/SQLite/backup/coordinator с fake Google HTTP:100 файлов16,852 с;1000 файлов76,651 с; во время Apply0 Google requests/0 mutations, следующий Prepare0 entries. `chat-artifacts/disk-performance/results.json`. Это замер одного запуска на текущем компьютере, без реальной сети/фото и без гарантии общего времени пользовательской книги. Прежний actual экспорт1008 записей занял около46 минут; workload отличается, строгий before/after benchmark не заявляется.

Свежая полная сборка: 0 предупреждений и ошибок. Core53/53, Google141/141, Headless13/13 и FlaUI5/5 PASS (212 тестов, дополнительные Index+Safety44 входят в Google suite). TRX в `TestResults/GoogleVelopack/`.

Финальные установленные сценарии:

| Проверка | Результат | Evidence |
| --- | --- | --- |
| Synthetic0.2.3 → 0.2.4, второй запуск после выхода старого процесса | PASS: второй экземпляр показал защитное окно; SDK запустил новую0.2.4, Applying marker потреблён | `chat-artifacts/installed-synthetic/update-0.2.4/{second-launch-guarded,after-upgrade-existing}.png` |
| Integration0.2.2 → 0.2.4 во время настоящей ручной Google-проверки | PASS: restart отключён до завершения sync, затем обновление завершилось | `chat-artifacts/installed-google/update-0.2.4/{restart-blocked-during-sync,after-upgrade-existing}.png` |
| Сохранность заполненных папок после обновления | PASS: preferences/vault SHA256 неизменны; все21 Synthetic и2096 Integration файлов workspace, включая JSON/фото/SQLite/backup, совпали до/после | `chat-artifacts/installed-{synthetic,google}/update-0.2.4/preservation-{before,after}.json` и `workspace-{before,after}.json` |
| Google-чтение установленной0.2.4 | PASS: прежний вход,1003 контакта и5 ярлыков прочитаны без нового consent.0 отправок/скачиваний/конфликтов/удалений, прежняя заблокированная правка google.person сохранена | `chat-artifacts/installed-google/read-after-0.2.4/manual-check.png` |
| Повреждённый настоящий full package | PASS: UI сообщает ошибку, restart недоступен, текущая0.2.2 и SHA256 её DLL неизменны; feed восстановлен | `chat-artifacts/installed-synthetic/corrupt-0.2.3/{result.json,corrupt-package-rejected.png}` |

Промежуточный Synthetic0.2.2 → 0.2.3 выявил различие строк одного локального feed (`C:\\…` и `C:/…`). Runtime безопасно сохранил Applying guard из-за несовпадения source identity. Восстановление выполнено через существующий UI с проверкой остановленных процессов и target hash (`chat-artifacts/installed-synthetic/recovery-0.2.3/recovered.png`). Build-Release теперь канонизирует файловый UpdateSource через GetFullPath; runtime nonce/source/hash проверка не ослаблена. Только локальная validation-конфигурация старой0.2.3 приведена к той же записи того же каталога перед успешным0.2.4 race.

Production0.2.4 Setup, portable, full/delta и feed собраны с publisher OAuth client и `https://github.com/Kibnet/ContactMirror`. Profile/payload/size/SHA256 gates и `Publish-Release.ps1` без `-Publish` PASS; `artifacts/releases/Production/build-0.2.4.json` и `release-notes-0.2.4.md`. Production-копия не устанавливалась, публичный репозиторий/релиз не создавались. Google Cloud OAuth остаётся в Testing (вход только добавленных тестовых пользователей), установщик не подписан.

Визуальная проверка: перечисленные native PNG просмотрены. Видео `chat-artifacts/ui-video/{before-client,after-client}.mp4`: Synthetic0.2.2/0.2.4, только client area2656×1846 с padding,12с, целевая частота15fps (фактическая средняя14,83/14,10), без аудио и настоящих контактов. До: обычное окно и ручная проверка до no-op; после: согласованное окно после ручной проверки. Начальные состояния и время нажатия различаются, это не видео benchmark. Кадры0/6/11 извлечены, репрезентативные PNG просмотрены; внешние окна исключены пространственной обрезкой baseline, исходный `before.mp4` сохранён локально. Google PNG и частные журналы не включаются в релиз.

Финальный adversarial re-review локальных runtime-гейтов0.2.4: PASS, новых HIGH/MEDIUM нет. Reviewer проверил installed PNG/логи, SHA-карты, manifest assets и видео metadata; writable sandbox, техническая read-only изоляция не заявляется. LOW ошибка имени portable launcher исправлена в README на `ContactMirror.exe`; immutable пакет0.2.4 не заменён. Исправленная документация упакована в0.2.5: compiled profile/payload/feed/hash gates PASS, root проверил реальный root launcher и точное совпадение embedded README с исходником, publication default plan9 assets PASS (`chat-artifacts/publication-plan-0.2.5.json`). Runtime-код0.2.5 тот же (кроме финального удаления пустых EOF строк), installed evidence относится к0.2.4;0.2.5 Production не устанавливалась. Публикация/remote AC10 остаются ASK-HUMAN.

Ниже сохранён отдельный отчёт предыдущей версии за2026-10-06.

Дата: 2026-10-06. Windows x64, .NET SDK 10.0.401, Release, Avalonia 12.1.3, AppAutomation 1.9.0. Проверки используют вымышленные контакты, временные папки и HTTP fixtures. Настоящие контакты и OAuth-токены пользователя не использовались.

## Выполнено

| Проверка | Результат | Evidence в исходном workspace |
| --- | --- | --- |
| Полная Release-сборка, все 10 проектов | PASS, 0 ошибок и 0 предупреждений | `dotnet build ContactMirror.sln -c Release -m:1 --no-restore` |
| Core/Application и реальные JSON/SQLite-файлы | 42/42 PASS | `TestResults/Final/core-final-release.trx` |
| Google OAuth/People API contracts, demo, vault и файловое хранилище | 106/106 PASS | `TestResults/Final/google-final-release.trx` |
| Headless UI | 12/12 PASS | `tests/ContactMirror.UiTests.Headless/bin/Release/net10.0/TestResults/` |
| Настоящее окно Windows, FlaUI | 5/5 PASS | `tests/ContactMirror.UiTests.FlaUI/bin/Release/net10.0-windows7.0/TestResults/` |
| AppAutomation Doctor strict | PASS, 0 ошибок и 0 предупреждений | Команда DLL fallback ниже |
| Schema generation drift check | PASS | `python schemas/generate_schemas.py --check` |
| Проверка JSON schemas | PASS: 2 примера, 18 отрицательных и 4 положительных случая | `python schemas/check_schemas.py` |

Проверка schemas использует собственный валидатор подмножества конструкций, которые выдаёт генератор; это не сертификация полного Draft 2020-12 сторонним валидатором. Примеры отдельно прочитаны runtime-кодеком в Core tests.

## Проверенные сценарии

Реальная цепочка `DemoGoogleGateway → SyncCoordinator → FileWorkspaceStore` пройдена через интерфейс в Headless и FlaUI: шесть контактов и два ярлыка скачаны; телефон изменён непосредственно в JSON; UI показал отправку в аккаунт; Apply изменил состояние демо-провайдера; следующая проверка не предложила изменений. Демо не выполняет сетевых запросов.

Автотесты проверяют трёхсторонние конфликты, изменения нескольких категорий, Unicode, даты и типизацию полей, PROFILE/CONTACT isolation, длинные массивы и неполные страницы, неизвестные вложенные поля, фотографии и повторное сравнение, ярлыки и starred, явные удаления, восстановление из копии, разные локальные/удалённые beforeimages и выбор источника восстановления.

Fault tests проверяют потерянный ответ создания, подтверждённую удалённую запись с ошибкой локального checkpoint, restart/relink без автоматического дубликата, изменение локального файла во время запроса, свежую фотографию после восстановления и сбоя журнала, SQLite/recovery, сохранение точных исходных байтов JSON, tampered backup, path traversal/reparse points и очистку только завершённых копий после подтверждения.

Core suite дополнена 512 детерминированными генерациями массивов телефонов: независимая модель мультимножества проверяет сохранение повторов, перестановки и направление three-way. Отдельная регрессия проверяет добавление ярлыка в удалённом провайдере → Download → локальный UUID → no-op; она закрывает несовместимость CLR-типа Guid с чтением JSON-строки в новом performance fast-path.

Последний source review выполнял отдельный reviewer: read-only по действиям, adversarial fallback. Среда reviewer имела `danger-full-access`, поэтому технически принудительной изоляции чтения не было. Найденные ошибки восстановлений, baseline/checkpoints, conflict-delete, relink, фото, отмены и UUID labels исправлены и покрыты регрессиями. Индексы identity сохраняют прежний first-match/приоритет связей; повторный source review подтвердил PASS. Review исходников не заменяет запуск тестов.

## Большая адресная книга и отзывчивость

Отдельный Release harness `tools/ContactMirror.Performance` использует production gateway с fake HTTP handler: 10 000 контактов, 20 страниц по 500 и страница ярлыков. Production coordinator сравнивает заранее подготовленный in-memory workspace; чтение файлов/SQLite, фотографии, настоящая сеть и UI в этот замер не входят. Измеряется время после полного `ReadAllAsync`; небольшой import прогревает JIT. Каждый сценарий запускается один раз, это приёмочный замер текущего компьютера, не статистическая гарантия скорости.

Исходный quadratic поиск связей дал 117,967 с для согласованной baseline и 109,951 с для одной правки. Индексы identity, пропуск пустых/неизменённых категорий и устранение лишней сериализации сократили эти времена. Финальная серия: import preview 1,415 с; baseline no-op 4,743 с; одна правка 1,992 с; отмена после чтения 5,61 мс. Цель ≤5 с — PASS; запас baseline небольшой, универсальной гарантии времени нет. Remote/contact/state writes — 0; история no-op записывается отдельно. Peak working set всего процесса — 475,9 MiB. JSON/Markdown сохранены в `chat-artifacts/performance/after-labels-fix/`; SHA-256 измеренного Application.dll совпал с DLL переносимой сборки: `5D7DA6DF03FAC6AE705965C488CA22D2BA871D40661B51660EF1C48D69951CAB`.

Headless test использует coordinator, который синхронно до первого await выполняет CPU-работу и шлёт 10 000 progress reports. Он работал на потоке 10, UI — на потоке 6; нажатие Cancel завершилось за 73 мс, до UI дошло одно status notification. Другой test предъявил настоящий ListBox с 10 000 entries: создано только три ListBoxItem, поиск последней записи оставил одну строку. Coordinator work вынесен через Task.Run, прогресс ограничен до 10 Hz перед отправкой в UI, token проверяется по сущностям и перед публикацией плана.

Снимок `chat-artifacts/ui/20261006-153547-063/preview-10000-virtualized.png` инспектирован. Это отдельное evidence отзывчивости и virtualization; оно не измеряет полную синхронизацию 10 000 файлов с Google.

## Визуальное evidence

В `chat-artifacts/ui/` сохранены инспектированные PNG onboarding, preview, conflict с обеими версиями, частичного результата, delete confirmation, restore preview, очистки копии и светлой/тёмной темы. Финальные native снимки выполнены через PrintWindow с DPI awareness, без посторонних уведомлений:

- `20261006-153733-047/integrated-initial-download-native.png`;
- `20261006-153734-769/integrated-local-phone-upload-native.png`;
- `20261006-153735-550/integrated-converged-noop-native.png`.

Self-contained production EXE отдельно запущен в ordinary и demo режимах без TestHost: подключение/папка доступны в onboarding, отсутствующая OAuth-конфигурация объясняется; demo показывает отдельный аккаунт и баннер, синхронизация без папки отключена. Собственные PID закрыты, LocalAppData fingerprint остался без изменений. Текущий native DPI — 225%; чистые PNG и smoke.log находятся в `chat-artifacts/production-smoke-*`.

Финальный smoke после всех исправлений: `chat-artifacts/production-smoke-20261006-154355/`. В demo проверена подсказка «Выберите папку и проверьте изменения». Автоматизация не выполняла OAuth, выбор папки или синхронизацию; полный cycle подтверждён отдельно через native TestHost с production coordinator/store/demo.

## Повторить проверки

```powershell
dotnet build ContactMirror.sln -c Release -m:1
dotnet test tests/ContactMirror.Tests -c Release --no-build
dotnet test tests/ContactMirror.GoogleTests -c Release --no-build
dotnet run --project tests/ContactMirror.UiTests.Headless -c Release --no-build -- --maximum-parallel-tests 1
dotnet run --project tests/ContactMirror.UiTests.FlaUI -c Release --no-build -- --maximum-parallel-tests 1
python schemas/generate_schemas.py --check
python schemas/check_schemas.py
dotnet run --project tools/ContactMirror.Performance -c Release -- chat-artifacts/performance
dotnet "$env:USERPROFILE/.nuget/packages/appautomation.tooling/1.9.0/tools/net8.0/any/AppAutomation.Tooling.dll" doctor --repo-root . --strict
```

UI-проекты используют TUnit и запускаются через `dotnet run`. Закреплённый DLL Doctor обходит устаревшую ссылку глобального tool resolver на отсутствующий worktree. Глобальный cache не изменялся.

## Границы результата

Не проверены вход/refresh/revoke через настоящий Google, all-field roundtrip в настоящем аккаунте, propagation Google, системные picker-диалоги, отдельная native DPI-матрица 100/150/200% и запуск на чистой машине без установленного .NET. Контрактные ответы и демо не закрывают эти пункты.

Сборка self-contained содержит .NET для Windows x64. Это локальный release candidate; публикация, подпись, лицензия продукта и Google OAuth verification не выполнены. Для простого входа других людей нужен OAuth-клиент издателя и соответствующий этап Google verification.

Google и файловая система не поддерживают общую транзакцию. У ряда Google endpoints нет условной записи; свежая проверка сокращает окно гонки, но не устраняет его полностью. Проверки путей защищают от наблюдаемых reparse points; хранилище не является kernel-handle sandbox против злонамеренной подмены каталогов прямо во время операции.

TRX, HTML и PNG остаются в workspace и не включаются в portable-продукт.

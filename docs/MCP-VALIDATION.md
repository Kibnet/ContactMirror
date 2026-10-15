# Проверка MCP ContactMirror

Дата: 14 октября 2026. Ветка: `feat/mcp-runtime`, база `43624c7` (0.4.0). Windows 11 x64, .NET SDK 10.0.402 / runtime 10.0.13. Разрешена реализация утверждённой SPEC; публикация и работа с личными контактами не выполнялись.

## Что проверяется

38 tools используют один Desktop owner, реальные локальные файлы, общий sync coordinator и ту же защиту редактора. Протокольные проверки запускают опубликованные Synthetic executable через официальный MCP SDK 2.2.0, а не подменяют stdio вызовами runtime. Доменные сценарии используют Demo/контрактный fake Google gateway. Финальные fixtures изолированы от Production settings, credentials и папок контактов; исправление старого ordinary harness описано ниже.

## Результаты

| Проверка | Результат | Evidence |
| --- | --- | --- |
| Release solution build | PASS, 0 warnings / errors | `dotnet build ContactMirror.sln -c Release` |
| Core / Application | 163/163, 0 skipped | `TestResults/Mcp/core-final/core-final.trx` |
| Google contracts | 185/185, 0 skipped | `TestResults/Mcp/google-final/google-final.trx` |
| MCP | 42/42, 0 failed/skipped, final source + published0.5.4 | `tests/ContactMirror.McpTests/bin/Release/net10.0/TestResults/mcp-complete.trx` |
| Headless UI | 91/91, 0 skipped, включая photo bytes/hash/decoded render | `tests/ContactMirror.UiTests.Headless/bin/Release/net10.0/TestResults/mcp-headless-photo-final.trx` |
| Native UI | 14 unique scenarios закрыты: full13/14 и исправленный ordinary1/1; 0 skipped | `tests/ContactMirror.UiTests.FlaUI/bin/Release/net10.0-windows7.0/TestResults/mcp-native-accepted.trx`, `mcp-ordinary-isolated.trx` |
| Installed native MCP | 1/1: draft, refresh, photo, copied paths, update barrier, idleexit | тот же results directory, `mcp-installed-photo-update.trx` |
| Synthetic package | PASS: initial0.5.0 и final0.5.4-mcp.validation2, shared-binary/profile audit | `artifacts/releases/mcp-acceptance/build-0.5.4-mcp.validation2.json` |
| Schema / Doctor | PASS, schemas current; Doctor0 errors/warnings | `generate_schemas.py --check`, `check_schemas.py`; AppAutomation1.9.0 `doctor --strict` |

Native результат — итог проверки14 сценариев с affected rerun, а не новый full14/14 TRX. Финальная приёмка и post-EXEC review фиксируются в `specs/2026-10-13-contactmirror-mcp.md`. Дополнительные targeted runs не добавляются к числу уникальных сценариев.

Всего495 уникальных сценариев:163+185+42+91+14. Все закрыты успешными результатами, пропусков0; Native harness failure закрыт отдельным affected run. Исходные failing TRX сохранены и объяснены ниже, не выданы за успешные. Performance benchmark отдельно имеет target miss и не входит в это число.

## Покрытие контрактов

- Реальные stdio clients двух protocol revisions (`2025-11-25`, `2026-07-28`), 38 tools, общий background owner; unknown tool и malformed JSON возвращают JSON-RPC errors, EOF завершает bridge. Кириллические большие строки проверены с учётом SDK JSON escaping и ограничения frame.
- Матрица 111 writable leaves проходит через MCP patch и реальный editor store. Защищены identity/Google metadata, неизменяемые неизвестные категории, legacy multiplicity; неизвестные значения и точность чисел сохраняются при изменении другой категории.
- Поиск, aliases, фильтры, сортировка, cursor revision/expiry; фото upload/chunks/dedup/hash/set/read/remove и ошибки размера/содержимого; local group CRUD, system read-only и referenced-delete guard.
- Полный Demo сценарий: create/update/photo/membership, review/choices/apply, conflict/delete, sync restore, локальная backup restore с тем же UUID/фото, cleanup и snapshot repair.
- CAS файла, labels, SQLite state и workspace binding; старый handle отвергается после замены manifest. Prepared registry удерживает восемь MCP-планов независимо от GUI; TTL, eviction, неверная selection и invalidation проверяются на actual plans.
- Durable requestKey, несовпадающий payload, lost local/remote reply, receipt finalization failure, selected journal entry missing, unknown retention, cleanup protection, истёкший completed key и replay после account transition. Recovery сверяет сохранённый эффект и не повторяет Google create.
- Permission generation/revocation, wrong root/account, собственная/чужая отмена, disconnect/EOF до выдачи remote write, UI lease/dirty guard; авторизация выполняется runtime, а не tool annotations.

## Установочная и визуальная проверка

Из локального feed установлен Synthetic `ContactMirror.Validation` 0.5.0-mcp.validation2 в `artifacts/installed/mcp-validation2/current`. При запуске bridge появляется background Desktop owner; обычный `ContactMirror.exe` открывает окно этого же PID. Изменение несохранённого поля приводит к `draft_conflict`, полное содержимое поля остаётся в карточке. После отмены черновика агентская правка появляется в чистой карточке и таблице. Закрытие окна сохраняет owner при активном клиенте, отключение клиента завершает idle owner.

Настройки показывают separate read/local-write/Google-write permissions и allowlisted roots. Скопированная конфигурация проверяется против installed executable paths. Обновление скачивается из локального Synthetic feed; попытка restart при живом MCP bridge блокируется сообщением о необходимости завершить MCP-клиенты. Пакет не заменяется под работающим bridge.

Первый успешный run: `chat-artifacts/mcp/native/20261014-141528-605/` — inspected `draft-retained.png`, `clean-card-refreshed.png`, `settings.png`, `installed-update-blocked.png`, `trace.json`. Видео `mcp-draft-and-refresh-after.mp4`: H.264, 2656×1846, 13.4 s, 1 106 037 bytes, подтверждено `ffprobe`.

Финальный published0.5.4 run: `chat-artifacts/mcp/native/20261014-151212-275/` — actual photo в таблице/карточке, SHA256 `D54BAB311F4D2E7544BDABA9ACB295054454FA88EF80AF7A4706A5D75D76A36E`, copied config matches payload paths. Actual image открыто и осмотрено; video5 s/H.264/2656×1846/826776 bytes. Installed0.5.0 → local feed0.5.4 barrier rechecked: `chat-artifacts/mcp/native/20261014-151553-096/`, `trace.json` все flags true, inspected `installed-update-blocked.png` с полной конфигурацией и отказом restart, video14.8 s/1377871 bytes. Эти artifacts различают свежий final payload и установленный initial fixture.

Парная запись исходного contact/photo/snapshot-repair/upload сценария: `chat-artifacts/mcp/baseline-native/before.mp4` (HEAD43624c7, git archive separate source; baseline test1/1) и `after.mp4` (current source, тот же inherited native scenario в suite). Оба H.264/2250×1576/30 s;850088 и969478 bytes. First frames открыты и осмотрены; before отражает repair/photo comparison, after — подтверждённый sync. Отдельный MCP video показывает добавленный agent flow, которого в старой версии нет. Это actual automated before/after evidence, а не утверждение о пиксельном равенстве разных состояний.

Native поле меняется через UIA ValuePattern одним событием. Это доказательство draft protection, а не непрерывного клавиатурного ввода: первый test harness набирал текст по клавишам и потерял focus при перестроении карточки до обращения MCP. Harness исправлен; product-claim о клавиатурной эргономике из этого не делается. Второй harness fix: поиск настоящего `McpEnabled` и `SettingsScroll` вместо StackPanel без UIA peer.

Old ordinary smoke сначала записывал общий `desktop-demo.json` и столкнулся с existing profile instance; его finally восстановил исходные bytes. Исправленный fixture использует отдельный Synthetic data root и опубликованный exe. Первый изолированный вариант по ошибке поместил app data внутрь workspace — защитный отказ был корректным. Data/workspace разведены в sibling directories; affected native ordinary1/1 PASS (7.6 s). Поэтому13/14 full run не скрывается и не объявляется сам по себе зелёным.

При расширении photo test исправлены неверные harness assumptions: `contacts_update` возвращает receipt/version, а document читается отдельно; upload применяет content-addressed filename, поэтому same-path detection проверяется внешней заменой байтов по прежнему пути, а не ожиданием прежнего пути после нового upload. Две sealed uploads удерживаются до abort/expiry — test освобождает handles после commit. PNG/JPEG и actual same-path read rechecked1/1 (`mcp-photo-formats-fixed.trx`). Product source при этих harness fixes не менялся. Source findings account guards/terminal cancel/persistence/byte accounting исправлялись отдельно и получили отрицательные regression tests.

## Производительность

База HEAD 43624c7 извлечена через git archive в отдельный `artifacts/mcp-validation/performance-baseline-source`. Fixture: 10 000 контактов, 500/page, реальный coordinator/gateway с fake HTTP и in-memory workspace. Single-run measurement исключает filesystem scan, SQLite, фотографии, live network и native UI.

| Сценарий | До, ms | После bounded counting writer, ms | Allocation до → после, MiB |
| --- | ---: | ---: | ---: |
| First import preview | 1100.3 | 1741.7 | 383.7 → 383.7 |
| Agreed baseline no-op | 3596.6 | 3059.7 | 1718.6 → 1718.0 |
| One local file edit | 2311.2 | 1621.3 | 1549.2 → 1549.2 |

Первоначальный quota measurement сериализовал полные планы и добавлял до 105 MiB allocations. Исправлено: Utf8JsonWriter считает канонические bytes через Stream.Null с тем же encoder/indent/LF. Optimized run выше: peak working set 480.6 MiB (база 480.7), cancellation 3.8 ms, writes 0. Он предшествует финальному уточнению canonical encoder/LF, поэтому отдельно приведён финальный результат.

| Сценарий | HEAD baseline в финальной среде, ms | Финальный isolated run, ms | Allocation до → после, MiB |
| --- | ---: | ---: | ---: |
| First import preview | 1104.2 | 1787.7 | 384.8 → 384.0 |
| Agreed baseline no-op | 4496.8 | 5069.3 | 1724.2 → 1717.1 |
| One local file edit | 1856.7 | 2460.8 | 1550.2 → 1548.7 |

Финальный benchmark **не прошёл собственный target 5 s**: no-op 5069 ms, exit1. Первый run одновременно с MCP suite дал 5349 ms; повтор isolated подтвердил 5069 ms. Baseline в той же финальной среде также медленнее раннего baseline. Не делается утверждение «все planning <5 s» или speedup. Разница примерно0.6 s во всех сценариях согласуется с добавленным обходом retained documents/remote snapshot/preview для точного shared 512 MiB budget; это объяснение по source/evidence, не измеренный отдельным profiler вклад. First-import +62% и single-edit +33% требуют этой явной оценки согласно SPEC. Устранена дополнительная копия JSON; оставлен ограниченный CPU cost безопасного учёта, без unrelated переписывания sync алгоритма и без бесконечного подбора benchmark. Финальный peak476.1 MiB, cancellation6.8 ms, writes0. Performance target остаётся указанным ограничением; функциональная приёмка и результаты suites не подменяются benchmark.

Evidence: `chat-artifacts/mcp/performance-before/`, `performance-after-counting-writer/`, `performance-final/`, `performance-final-isolated/`, `performance-baseline-final-environment/results.json`.

## Границы

Не проверены live Google OAuth/refresh/revoke, настоящий аккаунт и propagation Google. Установочный run проверяет готовность и отказ небезопасного restart, а не успешное обновление Production. Synthetic установка и локальные пакеты не означают GitHub release, подпись, push/merge, установку новой Production версии или настройку вашего MCP-клиента. Generated TRX/PNG/MP4/packages остаются в workspace и не входят в source commit.

Same-SID local ACL не является изоляцией от злонамеренного процесса этого же пользователя; CAS защищает наблюдаемые изменения, но не заменяет kernel filesystem sandbox. Уже выданный Google запрос может завершиться после отмены: отдельные confirmed/failed/unknown outcomes и durable journal обязательны.

# MCP: перенос на main 0.5.0

Дата: 15 октября 2026. Запрос: «Отребейзь на мейн и оформи ветку».

Ветка `feat/mcp-runtime` перенесена с базы `43624c7` на полученный через `git fetch origin --prune` актуальный `origin/main` — `e40eb5b35494a0c904903072564edf834c8433d4`. Main checkout и другие рабочие деревья не изменялись. Исходные незакоммиченные MCP-файлы сохранены коммитом до rebase; после переноса feature commit — `960132d`. Совместимость с редактором main и regression tests оформлены отдельным коммитом `17575a8` (`fix(mcp): preserve sync editor guards after main rebase`).

## Совмещение изменений

- В конфликте coordinator сохранены одновременно `BindingHash`/неизменяемый каталог редактируемых записей из 0.5.0 и MCP clock/`PreparedAt`.
- История выпуска0.5.0 сохранена, MCP остаётся в «Не выпущено». Default product version из main не менялась.
- Ручное сохранение/удаление из окна сравнения инвалидирует планы только соответствующей папки/аккаунта, освобождает их quota и уведомляет MCP. План другой папки остаётся доступен. Local edit handles проверяют owner/TTL; истечение освобождает registrations.
- Открытая форма синхронизации считается modal для MCP. После ожидания решения по пользовательскому черновику повторно проверяется shared activity, затем фиксируются preview/key.
- Общий restart guard учитывает форму синхронизации и сохраняет прежние Contacts/MCP guards и причины отказа, в том числе без подключённого MCP-клиента.
- Старый queued/in-flight refresh агента не очищает новое сравнение после ручного сохранения. Revision присваивается до UI dispatch и подтверждается после успешного prepare под lease; отмена формы продолжает обрабатывать pending refresh.
- `RememberOrigins` и новый `ApplyObjectPatch(..., origins)` сохраняют identity строк, фокус и прокрутку main; MCP проверка защищённых категорий выполняется до изменения live document.

## Проверка

| Проверка | Результат | Evidence |
| --- | --- | --- |
| Release solution build | PASS,0 warnings/errors | `dotnet build ContactMirror.sln -c Release --no-restore` |
| Core | 174/174,0 skip | `TestResults/McpRebase/core/core.trx` |
| Google/storage | 185/185,0 skip | `TestResults/McpRebase/google/google.trx` |
| MCP | 44/44,0 skip; actual stdio из нового payload | `tests/ContactMirror.McpTests/bin/Release/net10.0/TestResults/mcp-rebase-full.trx` |
| Новые race tests | 2/2,0 skip | `tests/ContactMirror.UiTests.Headless/bin/Release/net10.0/TestResults/mcp-rebase-targeted.trx` |
| Headless full | 103/103, 0 fail/skip, 5m42s | `tests/ContactMirror.UiTests.Headless/bin/Release/net10.0/TestResults/mcp-rebase-headless-final.trx` |
| Native full | 15/16, 1 failure, 0 skip; acceptance НЕ закрыта | `tests/ContactMirror.UiTests.FlaUI/bin/Release/net10.0-windows7.0/TestResults/mcp-rebase-native.trx`; diagnostic/affected runs ниже |
| Fresh Synthetic package | PASS, без публикации/установки Production | `artifacts/releases/mcp-rebase-20261015/build-0.6.0-mcp.rebase1.json` |

Для protocol/native используется объединённый payload из `ContactMirror.Validation-0.6.0-mcp.rebase1-4aeebb84146d4d5484e953360b0d81bc`, скопированный в `artifacts/mcp-validation/app`; это не прежняя сборка от0.4.0. Профиль, общие binaries и package audit проверены Build-Release. Synthetic version — только идентификатор тестового пакета, а не объявление production-релиза.

Targeted tests покрывают actual file save/подготовку нового preview при paused account refresh, queued refresh, cancel-close drain, отказ Prepare/TryAuthorize restart без bridge, а также pending draft decision → broker lease → discard без открытия новой формы. MCP tests проверяют независимый root, quota release и local-handle owner/expiry. Первая сборка выявила несовместимый двухаргументный вызов `ApplyObjectPatch`; он заменён новым контрактом с origins, последующая сборка PASS.

Первый Headless full остановлен после 88 PASS и одного failure: тест перечитывания карточки открывал меню сразу по статусу «Сохранено», до завершения asynchronous catalog refresh (`IsBusy` запрещает popup). Новый helper ждёт visible/enabled `ContactEditButton` и выполняет те же клики через существующий helper; assertions не ослаблены. Во вновь добавленной modal-проверке прямое ожидание `DesktopAgentUi.GetStateAsync` без Headless dispatcher pump привело к зависанию; вызов перенесён в `EditorUi`. Остановлен только собственный процесс тестов. Ошибка компиляции промежуточного helper (`private WaitForEditorAsync` базового класса) исправлена на доступный `EditorAdapterWait`; финальная сборка Headless — 0 warnings/errors. Незавершённый прогон не выдаётся за PASS и не добавляется к итоговым количествам. Failure PNG: `chat-artifacts/ui/5c8df7baca4b4c3c97713e129efdaf65/integrated-test-failure.png`.

Финальные Headless PNG просмотрены: `chat-artifacts/ui/20261015-102423-182/mcp-photo-refreshed.png` (новое содержимое фотографии и имя в карточке), `chat-artifacts/ui/20261015-102423-525/mcp-settings-rendered.png` (права, папки, конфигурация MCP). Это проверка отображённого состояния, а не отдельное UX-исследование.

Native full завершился 15 PASS / 1 failure / 0 skip за 7m57s. Единственный failure — `Contact_editor_saves_card_locally_reopens_and_sends_only_after_manual_check_apply`, проверка TAB focus trap. Native helper отправлял глобальный Alt+F4 без явного foreground и молча продолжал после отсутствия диалога. Первый affected rerun после foreground перед shortcut всё ещё failed; diagnostic run показал открытый диалог, но ни одного UIA focused descendant (`mcp-rebase-native-dirty-rerun.trx`, `mcp-rebase-native-focus-diagnostic.trx`). Это не наблюдение ухода фокуса в запрещённое поле. Повторная активация после modal wait дала совпавшие foreground/target HWND и пять успешных TAB assertions, но следующий UIA Invoke завершился COM error (`mcp-rebase-native-dirty-final.trx`). Захваты этого запуска имели размер 353×56 ещё до Alt+F4: тестовое окно было свёрнуто; такие кадры не считаются visual proof.

В диагностическом helper проверялись restore окна через существующий `NativeWindowCapture.Resize`, активация до shortcut и после ожидания реального диалога, foreground HWND и focused element/PNG на каждом TAB. Все пять focus assertions и проверки черновика сохранялись, принудительный `.Focus()` на ожидаемой кнопке не добавлялся. Первый запуск с exact-HWND recorder (`mcp-rebase-native-restored.trx`) прошёл функциональные стадии до результата manual sync, но завершился recording error: добавленный Resize менял геометрию уже запущенной записи. Advisory reviewer выявил это по source ещё до завершения запуска. Условие `_editorWindowVideo is null` устранило смену геометрии при активной записи. Последний corrected run (`mcp-rebase-native-restored-final.trx`) снова failed по фокусу: TAB0 сфокусирован на `ContactDecisionSave`, TAB1 — UIA focused descendant отсутствует; диалог открыт, HWND при активации совпал с target, размер 2250×1575 постоянен. Это не подтверждает ни отсутствие дефекта продукта, ни конкретную причину потери фокуса.

Повторы остановлены: Native gate остаётся НЕ пройденным. Неуспешные экспериментальные изменения Native helper сняты с рабочей ветки; их patch сохранён в ignored `chat-artifacts/mcp/native/20261015-103247-069/native-harness-diagnostics.patch`, вместе с исходными TRX/PNG/video. Продуктовый focus trap и native assertions не менялись. Для закрытия ограничения нужен отдельный воспроизводимый запуск с проверкой desktop foreground/focused element при каждом key и изоляцией внешнего ввода; успех не заявляется. Результат rebase: 521 PASS / 1 unresolved Native failure из 522 уникальных сценариев, 0 skip. Targeted/diagnostic reruns не увеличивают число уникальных сценариев.

Новый реальный MCP native flow прошёл на свежем rebased Synthetic payload: обычный запуск поднимает окно того же owner PID, dirty draft защищён, clean card/photo обновляются, configuration содержит правильные exe paths, закрытие окна оставляет owner до отключения клиента и idle exit. `chat-artifacts/mcp/native/20261015-103247-069/trace.json` подтверждает эти assertions; `installedUpdateBlocked=false` ожидаем, потому что установленное обновление не запускалось. Снимки `draft-retained.png`, `photo-refreshed.png` и first frame записи в той же папке просмотрены.

Новые test-run videos проверены через ffprobe: `chat-artifacts/mcp/native/20261015-103247-069/mcp-draft-and-refresh-after.mp4` — H.264 2656×1846, 6.6s; `chat-artifacts/ui/sync-change-editing/scroll-after-102646.mp4` — H.264 3868×2080, 47.4s; `edit-reset-after-102814.mp4` в той же папке — H.264 3868×2080, 9.6s. Полный просмотр движения не заявляется. Новая парная запись состояния до rebase не делалась: основной запрос — перенос существующей feature; прежние before/after материалы относятся к исторической проверке от 14 октября. Текущее next-best evidence — реальные native assertions, PNG/trace и новые записи affected flows. Generated `chat-artifacts` не включены в Git.

## Review и границы

Central-required procedural advisory reviewer `/root/mcp_spec_review` не менял файлы/Git и не запускал проверки. Выявленные HIGH restart-guard и MEDIUM refresh/open-barrier устранены и повторно просмотрены: source-closed, targeted evidence2/2+1/1 и полные Core/Google/MCP/Headless TRX прочитаны. Native validation остаётся открытой; полный feature acceptance STOP PASS после rebase не заявляется. Локальный перенос Git оформлен с явным residual, без merge/release.

Финальное advisory review пяти affected roles: локальная Git-доставка подтверждена (чистая ветка, 0 behind / 3 ahead), новых замечаний к разделению evidence нет. Полный feature STOP — **NEEDS-FIX** из-за незакрытой Native focus-проверки; для закрытия нужен воспроизводимый PASS исходных assertions без их ослабления.

Исходные495 сценариев и ограничения от14 октября остаются историческим evidence в [MCP-VALIDATION.md](MCP-VALIDATION.md), не выдаются за результаты этого rebase. Нагрузочный benchmark не повторялся: алгоритм подготовки не оптимизировался, прежнее ограничение около5.1s на10k контактов остаётся. Live Google и успешное Production обновление не проверялись; установленная Production версия и её настройки не менялись. Git push, PR, merge и релиз не выполнялись.

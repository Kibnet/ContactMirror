# Правка изменений и стабильная прокрутка

Локальная реализация по утверждённой [SPEC](../specs/2026-10-14-edit-sync-changes-and-stable-scroll.md), база `43624c7`, ветка `feat/sync-change-editing`. Рабочий каталог: `C:\Projects\My\ContactMirror-sync-change-editing`.

## Поведение

У обычного изменения есть меню «⋮»: ручная правка выбранного раздела либо сброс до показанной версии Google. Форма использует существующие типизированные поля, показывает область и время снимка. Сохранение меняет папку; отправка в Google запускается отдельно. Поддержаны 23 категории, ярлыки, избранное, содержимое фото и поля групп. Недостающие локальные ярлыки сохраняются отдельным явным действием; после него контакт требует повторного сохранения.

После локальной записи прежний план недействителен, выполняется новое сравнение, выбор остальных неизменившихся операций сохраняется. Существующие baselines не продвигаются. При первом получении remote-only записи начальное состояние создаётся из доверенной Google-проекции, а ручные отличия остаются будущими Upload.

Для записи использованы существующие guarded `IWorkspaceSession` lease/binding/CAS/backup/journal/path primitives. Отдельный вызов `ContactEditorStore.SaveAsync` заменён общим узким local-edit путём: это позволяет одинаково обработать контакт, группу и отсутствие локального файла, сохраняя те же ограничения и формат. Предварительные изменения кода и данных в рабочей папке контактов не выполнялись.

Карточка сохраняет раскрытие, прокрутку, позицию вложенных списков и фокус. При удалении/перестановке surviving rows сохраняют исходную identity; popup mutation сопоставляет candidate с исходными объектами до изменения, без угадывания по похожим значениям. Finder и переход к новому полю имеют явный приоритет.

## Проверки

| Проверка | Результат |
|---|---|
| Release build | PASS, 0 предупреждений / 0 ошибок |
| Core/Application/Infrastructure | PASS 174/174 |
| Google/storage contracts | PASS 185/185 |
| Полный Headless | PASS 100/100 |
| Полный Native/FlaUI и ordinary EXE | PASS 15/15, включая ordinary Desktop --demo smoke |
| Schema generation/check | PASS: 23 категории, 25 definitions, 24 fixtures |
| AppAutomation doctor 1.9.0 strict | PASS: 0 errors / 0 warnings |

Команды и полные логи находятся в ignored `chat-artifacts/ui/sync-change-editing/validation/`. Doctor выполнен через точный package DLL из NuGet cache: local tool shim не находил команду; глобальный cache не менялся.

Новыми проверками покрыты: private authority против mutable preview; все категории reset с неизменными baselines/Google bytes; чужое поле, stale plan, file/binding drift; remote-only materialization/dependencies без дублей; восстановление отсутствующего файла; первый partial file/state commit без групп; реальные меню edit/reset/starred/invalid-buffer; busy save/close; row menu primary/delete; focus/caret и исходная регрессия прокрутки. Отдельная фото-проверка обновляет Google A → B во время подготовки ярлыков, сверяет показанный pixel и сохранённые bytes.

| AC | Фактическое evidence |
|---|---|
| 1 | `Every_writable_category_has_scoped_form_from_comparison`, starred/menu tests, group materialization read-back; inspected 820/1000 Light/Dark forms |
| 2 | `Local_reset_covers_all_writable_categories...`, missing birthday reset, `Dependency_refresh_displays_latest_Google_photo...` со сверкой pixel/bytes |
| 3 | `Comparison_menu_edits_then_resets_phone...`, remote-only dependency refresh с несвязанным unchecked контактом; stale plan rejection |
| 4 | `Remote_only_contact_and_group_materialize...`, `Remote_only_dependencies_are_matched_by_resource...`, restore file и first-state fault fixtures; native CRUD/photo/delete |
| 5 | Scope/binding drift/presentation spoof tests; existing baseline hash неизменен; injected state/write failures и verified intent recovery; remote bytes unchanged |
| 6 | Structural scroll RED → GREEN, `Actual_row_menu_primary_and_remove...`, native `Native_scrolled_structural_edit...` |
| 7 | `Removing_preceding_row_keeps_focus_caret...`, existing discovery/virtualization/finder/popup lifecycle tests; root/id/version guards reviewed |
| 8 | `Busy_local_save_rejects_editor_close...`, invalid-buffer reset, full Headless100, native date/CRUD/ordinary EXE; полные suite results выше |

## Визуальное evidence

Baseline `43624c7` воспроизвёл исходный дефект: после добавления организации UIA scroll position изменился с 26.03778% до 0%. Сохранён raw HWND-only MP4 `C:\Projects\My\ContactMirror-scroll-baseline\chat-artifacts\ui\sync-change-editing\scroll-before-172816.mp4` (3868×2080, 5 fps, 6.6 s). Просмотрен PNG конца записи: карточка вернулась к имени/телефону. Старый сценарий также имел teardown dirty-close, который исправлен в финальном harness; сам RED подтверждён UIA и кадром.

Просмотрены реальные rendered формы: `chat-artifacts/ui/20261014-175533-723/sync-phone-1000-Light-headless.png`, `20261014-175534-164/sync-phone-1000-Dark-headless.png`, ранее `20261014-175307-919/sync-phone-820-Light-headless.png` и `20261014-175308-132/sync-phone-820-Dark-headless.png`. Область и время Google, оба поля, reset/save/cancel читаются, наложений нет. Последний full100 включает повтор этих размеров/тем на финальном source.

Просмотрены кадры финального product source: `chat-artifacts/ui/sync-change-editing/scroll-after-175806.mp4` (6.0 s) и `edit-reset-after-175820.mp4` (5.6 s), оба 3868×2080, 5 fps. После добавления остаётся раскрытая организация, фокус переходит к новой записи, верх карточки не появляется. Native menu → edit → save → reset → save успешно выполнен; файл перечитан, demo remote bytes неизменны. Финальный полный native run также записывает повтор этих сценариев. BEFORE raw capture скопирован в `chat-artifacts/ui/sync-change-editing/scroll-before.mp4`; исходник сохранён.

Финальный native15/15: [scroll AFTER](../chat-artifacts/ui/sync-change-editing/scroll-after-190033.mp4) (6.0 s), [edit/reset AFTER](../chat-artifacts/ui/sync-change-editing/edit-reset-after-190044.mp4) (5.4 s), [scroll BEFORE](../chat-artifacts/ui/sync-change-editing/scroll-before.mp4) (6.6 s). Все 3868×2080, 5 fps. Scroll position 26.03778% → 37.72947% после фокуса новой строки. Inspected final native PNG: `chat-artifacts/ui/20261014-190047-984/native-sync-reset-form-native.png`. Ordinary EXE smoke: `chat-artifacts/ui/20261014-190202-755/ordinary-demo-native.png`.

Все fixtures вымышленные, Google mutations проверяются через demo remote file/read-back. Native capture записывает только окно приложения без аудио; отдельный ContextMenu HWND не входит в main-window screenshot. Видео проверены ffprobe и извлечёнными PNG ключевых состояний, непрерывный просмотр всех кадров не заявляется.

Первый полный Native дал 13/15: existing date-repair и existing CRUD остановились на автоматическом вводе. Date-сценарий отдельно прошёл без изменения product source; CRUD отдельно показал потерю части пути при `Keyboard.Type` в shell picker. Harness теперь задаёт текст адресованным UIA Value control и проверяет read-back, а после исправления даты ждёт status с одной ошибкой перед переходом к finder. Проверки валидации, cancel, custom type, выбора/замены фото, файла и удаления сохранены. Targeted CRUD после этого прошёл 1/1. Это стабилизация automation input boundary, не изменение пользовательской валидации.

## Review и границы

Отдельный advisory reviewer нашёл и проверил исправления: inferred recovery первой записи, потерю row identity через popup clone и устаревшее Google photo preview. Targeted source/contract re-review — PASS. Reviewer работал процедурно read-only в writable sandbox, без технической read-only изоляции; тесты им не запускались.

Итоговый отдельный review после чтения финальных logs/source/docs и PNG — PASS локального EXEC по пяти ролям и full review/depth checklist. Reviewer подтвердил counts174/185/100/15, ordinary EXE smoke и отсутствие открытых actionable findings; непрерывный MP4 просмотр им не выполнялся.

Root full post-EXEC verdict: PASS локальной реализации. Scope/Contract/Adversarial/Fix-and-re-review покрыли AC1–8, все новые файлы и actual UI путь. Domain, UX, testing, architecture и delivery/security — PASS после исправлений и обязательных прогонов. Depth checklist: scope drift/unrelated changes не найдены; schema/API Google совместимость сохранена; README обновлён; версия/changelog не меняются без release; новых external side effects нет; неподтверждённых performance claims нет. Manual challenge: первый контакт без групп при отказе state, удаление одинаково выглядящих rows через настоящий popup и Google photo A→B между Prepare проверены отдельными regression fixtures. Остаточные границы — ниже, открытых actionable defects нет.

Никаких commit/push/merge/release/install, живых Google mutations или изменений `ContactMirrorData` не выполнялось. Новый пользовательский flow проверен на временных demo folders; production OAuth/API не является acceptance этого локального изменения. Native input assertions усилены адресованным UIA вводом; это не доказательство стабильности посимвольного ввода при внешнем перехвате фокуса. Видео проверены по ключевым кадрам; live Google, установка и выпуск не заявляются.

## Отдельное поручение на Git delivery

После завершения локального EXEC пользователь поручил: «Перебазируй на актуальный мейн и влей». Это разрешает commit, push и merge через PR для этого scope. Установка, выпуск и рабочие контакты остаются вне поручения. Проверка `git fetch origin --prune` и `git ls-remote origin refs/heads/main` подтвердила актуальную базу `43624c758fe65fc9dfff4c8af3f5386e9a0aac7c`: upstream изменений после исходной базы нет. Поэтому rebase не меняет проверенный product tree и не требует повторения всех уже зелёных suites. Коммиты разделены на исправление scroll/row identity, local edit/reset, native input harness и документацию. PR содержит результаты проверок и пути к paired UI evidence; фактический merge receipt фиксируется в GitHub и итоговом ответе.

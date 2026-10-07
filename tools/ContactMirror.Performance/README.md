# Проверка planning на 10 000 контактов

```powershell
dotnet run --project tools/ContactMirror.Performance/ContactMirror.Performance.csproj -c Release -- chat-artifacts/performance
```

Harness запускает production `GoogleContactsGateway` с fake HTTP handler (20 страниц по 500 контактов и пустая страница ярлыков) и production `SyncCoordinator`. Все идентификаторы и данные синтетические; email имеет домен `example.test`. Реальная сеть, OAuth, пользовательские файлы и credentials не используются.

Сценарии: первый import preview; заранее согласованная baseline и no-op Apply; одна локальная правка `names`; отмена сразу после полного чтения API. Store — in-memory, подготовленный до замера. Поэтому результаты planning **не включают чтение 10 000 файлов, SQLite, фотографии, задержки сети или UI** и не являются оценкой полного filesystem workflow.

Planning начинается сразу после `ReadAllAsync` и заканчивается после `PrepareAsync`. Вывод отдельно показывает чтение fake API, GC allocation, managed memory и process peak working set. Peak working set относится всему процессу. Короткий прогрев загружает JIT и capability schema. Каждый сценарий выполняется один раз; это приёмочная проверка порога, а не статистический benchmark.

План обязан содержать ровно 10 000 импортов, затем ноль изменений, затем один upload `names`. No-op Apply обязан иметь ноль remote mutations, contact writes и EntityState writes. Запись истории пустого запуска считается отдельно. Отмена обязана завершиться `OperationCanceledException` и не менять контакты.

Результаты сохраняются в `results.json` и `README.md` указанного output directory. Цель planning ≤5 секунд проверяется для всех трёх сценариев; её нарушение отражается в JSON/Markdown и exit code 2. Нарушение функциональных инвариантов приводит к исключению и ненулевому exit code.

# ContactMirror 0.5.0: проверка доставки

14.10.2026 пользователь отдельно поручил «Опубликуй релиз». [Стабильный релиз0.5.0](https://github.com/Kibnet/ContactMirror/releases/tag/v0.5.0) опубликован как Latest: не draft и не prerelease. Время публикации GitHub — `2026-10-14T19:15:48Z`.

## Source и проверки

- [PR #4](https://github.com/Kibnet/ContactMirror/pull/4) содержит правку/reset выбранного изменения и исправление прокрутки; merge `d9b650aa05effedc0ab4736d0098f23f8e766dad`.
- [PR #5](https://github.com/Kibnet/ContactMirror/pull/5) меняет только version stamp, changelog, curated notes и журнал SPEC. Merged main и тег `v0.5.0`: **`0ca08593c877e629054c8075c0568bb691a8dcf3`**.
- Production-сборка создана после merge из точного SHA тега, при чистом tracked tree. ProductVersion packaged assembly — `0.5.0+0ca08593c877e629054c8075c0568bb691a8dcf3`. Этот audit добавляется отдельным docs commit без изменения тега и бинарных файлов.
- Принятая реализация:474/474,0skips — Core174, Google/storage185, Headless100 и Native15, включая ordinary EXE и UI evidence. После version stamp повторно прошли Release build0errors/warnings, Core174/174 и Google185/185. UI runtime source не менялся, повтор UI suites после stamp не заявляется.
- GitHub Actions/status checks в репозитории отсутствуют; CI не заявляется. Metadata PR проверен CLEAN/MERGEABLE и слит с exact-head guard.

## Production package

App ID `ContactMirror.Desktop`, профиль Production, канал `win`, RID `win-x64`, GitHub источник `https://github.com/Kibnet/ContactMirror`. Desktop OAuth client совпадает с клиентом установленной0.4.0; его значения не выводились и не коммитились.

`Build-Release.ps1` проверил фактический compiled profile, staging, feed и hash/size пакета. `Publish-Release.ps1` запущен без `-Publish`, только для проверки и подготовки плана/контрольных сумм. Опубликованы curated notes из `docs/releases/0.5.0.md`, сверенные с changelog и diff; стандартные notes скрипта не использовались.

До stable publication в draft проверены exact target SHA, пять uploaded assets, их remote SHA256/size и body. После публикации Latest metadata, body и asset hashes перепроверены, tag fetch подтвердил exact commit.

| Asset | Bytes | SHA256 |
|---|---:|---|
| ContactMirror.Desktop-0.5.0-full.nupkg |55091697|`64f94a6178f4900d4ffb7455203095b07c732d80f8e0a0ae9f3f629c655118d0`|
| ContactMirror.Desktop-win-Setup.exe |59722225|`8cd93a226a05196761ec67421be2d47d75770e83f046ea437b18885c69428ceb`|
| ContactMirror.Desktop-win-Portable.zip |54922324|`0857617b2794ceaf020a403065168000db2d7843c67fa51a7396e7d0be04db0c`|
| releases.win.json |276|`b83387e7633421dbd6c702b7b595781bfba87b7557e7bd3415d2d35eba2be474`|
| SHA256SUMS.txt |400|`6bc4ce820173fba9f9e3e0851f7c047b521b7c09c98917b26d8515e53b265d94`|

`VelopackUpdateBackend.VerifyPackageFile` проверил full package SHA256/size, nuspec, App ID/version/channel/RID/OS/architecture/mainExe и ContactMirror.dll. Assembly SHA256 — `357ed769ab2f570924a010be2e75caf192a8bd35896039665a98c03a633382eb`, совпадает со staging. В ZIP252 entries; DataGrid dependency и bundled updater/OAuth configuration присутствуют; vault, пользовательские данные, test binaries и PDB отсутствуют. Build manifest и исходный OAuth JSON не загружались отдельными assets.

## Публичное обновление

Probe использовал фактический backend приложения и Velopack1.2.0 TestLocator с App ID, версией0.4.0, каналом и source из существующей установки. RootAppDir/PackagesDir направлены в отдельный ignored cache; GitHub-токен не передавался. Код updater/source options не менялся относительно0.4.0.

`CheckAsync` обнаружил0.5.0; `DownloadAsync` скачал публичный full package **55091697 bytes**, приложение выполнило штатную проверку. SHA256 скачанного файла совпал с local/feed/uploaded SHA256. Assembly и sq.version рабочей установки совпали с исходными хэшами после проверки.

**Discovery/download/verification PASS; `applied=false`.** ApplyAndRestart, установка, перезапуск рабочей программы и настоящий Google не вызывались; `ContactMirrorData` не менялся. Установка доступна пользователю через настройки. Автоматический downgrade не поддерживается: исправление доставляется следующей версией, ручной откат требует переустановки.

## Review и evidence

Отдельный procedural advisory reviewer подтвердил metadata/notes/plan PASS и package/draft pre-publication PASS: exact source, hashes, nuspec, 252 ZIP entries, OAuth/config boundary, пять remote asset digests/sizes и curated body проверены. Sandbox writable, технической read-only изоляции нет; reviewer не изменял файлы, не запускал tests/build/runtime и не менял внешнее состояние.

Итоговый отдельный delivery review — PASS по всем пяти ролям и full review/depth checklist. Reviewer дополнительно прочитал Latest/tag через GitHub GET API, сверил source и пять assets, захэшировал скачанный anonymous package, проверил probe source и public receipt. Публикация, целостность и `applied=false` подтверждены; открытых actionable findings нет.

Root post-EXEC release review: Scope/Evidence, Contract, Adversarial, Fix/re-review, пять ролей и Stop — PASS. Domain/UX: обе принятые функции и ограничения notes точны. Tester: полные feature suites и stamp revalidation отделены; package и public updater проверены. Architect: identity/source/channel/format не меняются. Delivery/security: exact merged tag/source, stable Latest, asset hashes, OAuth continuity и отсутствие install/live Google подтверждены. Scope drift/unrelated runtime changes/unsupported claims не найдены. Manual challenge закрыт реальным anonymous download и проверкой неизменности установленной программы. Открытых actionable findings нет; известные ограничения — unsigned installer и OAuth Testing.

Локальные ignored artifacts в `C:\Projects\My\ContactMirror-release-0.5.0\chat-artifacts\release-0.5.0`: `build.log`, `core.log`, `google.log`, `source-sha.txt`, `package-build.log`, `publication-plan.json`, `package-verification.json`, `oauth-continuity.json`, `draft-release.json`, `uploaded-verification.json`, `public-release.json`, `public-update.json`, `public-update.log` и source probe `public-update-probe/Program.cs`. Generated artifacts не коммитятся. Исходное UI evidence и AC matrix — [feature audit](SYNC-CHANGE-EDITING-VALIDATION.md).

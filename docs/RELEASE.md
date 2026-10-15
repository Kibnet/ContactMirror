# Установка и обновление Windows x64

`scripts/Build-Release.ps1` собирает self-contained Windows x64, проверяет фактический профиль исполняемого файла и запускает закреплённый Velopack CLI 1.2.0. Скрипт создаёт Setup, portable ZIP, full/delta nupkg, `releases.win.json`, контрольные суммы и архив установщика каждой версии в `versions/<version>`.

## Профили

| Профиль | Package ID | Настройки и токены в LocalAppData | Google |
| --- | --- | --- | --- |
| Production | ContactMirror.Desktop | ContactMirror | Издатель / управляемый импорт |
| Synthetic | ContactMirror.Validation | ContactMirror.Validation.Data | Отключён, вымышленные контакты |
| Integration | ContactMirror.IntegrationValidation | ContactMirror.IntegrationValidation.Data | Только клиент издателя |

Профиль задаётся при компиляции. JSON контактов, фото, журнал и резервные копии находятся в выбранной пользователем папке вне установочной папки. Обновление заменяет файлы программы; настройки и DPAPI vault остаются отдельно. Папку контактов внутри установки выбрать нельзя, включая пути через junction/symlink.

Пакет также содержит `ContactMirror.Mcp.exe`, его runtimeconfig/deps и `docs/MCP.md`. Desktop и bridge собираются с одинаковыми версией и профилем; несовпадающие общие файлы останавливают упаковку. Перед обновлением завершите MCP-клиенты, включая процессы, которые только прошли initialize/tools discovery. Общий startup/restart gate и update marker предотвращают запуск bridge во время замены файлов; активные записи и пользовательский черновик также блокируют перезапуск.

## Сборка

Для Production нужен Desktop OAuth JSON. Выбран источник GitHub Releases `Kibnet/ContactMirror`:

```powershell
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.4.0 -ValidationProfile Production -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind github -UpdateSource https://github.com/Kibnet/ContactMirror -Output artifacts/releases/Production-0.4.0
```

Локальный каталог feed разрешён только для изолированных проверочных профилей:

```powershell
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.0 -ValidationProfile Integration -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind file -UpdateSource C:\Projects\My\ContactMirror\artifacts\releases\Integration-final -Output artifacts/releases/Integration-final
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.4 -ValidationProfile Integration -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind file -UpdateSource C:\Projects\My\ContactMirror\artifacts\releases\Integration-final -Output artifacts/releases/Integration-final
```

Сборки, тесты и упаковку выполняйте последовательно: они используют общие `bin/obj`, а упаковка отключает PDB. Скрипт ничего не публикует. Подпись установщика пока не настроена.

## Установленный сценарий

Актуальный Setup в release устанавливает опубликованную версию. Уже установленные Production0.2.6 и0.3.0 используют тот же GitHub источник и могут получить полный пакет0.4.0 без промежуточных версий. Откройте настройки, проверьте обновления, нажмите «Скачать», затем «Установить и перезапустить». Во время синхронизации перезапуск недоступен. Настройки, vault и папка контактов находятся вне установки.

Проверяются App ID, версия, канал `win`, RID `win-x64`, содержимое nuspec, размер и SHA256 полного пакета. Если delta-реконструкция не совпадает с SHA256 исходного full ZIP, приложение один раз скачивает исходный full пакет и повторяет проверку. Проверка не ослабляется для delta.

Applying marker защищает промежуток между завершением старого процесса и запуском новой версии. Второй запуск не начинает синхронизацию в этот промежуток. Восстановление разрешается после проверки процессов и идентичности файлов; истечение времени само по себе не снимает защиту.

SDK/CLI: [Velopack Windows packaging](https://docs.velopack.io/packaging/operating-systems/windows), [Setup CLI](https://docs.velopack.io/reference/cli/content/setup-windows). Локальная установка и обновление не подтверждают доступность публичного feed или успешную проверку OAuth Google для других пользователей.

## GitHub Releases

Production-сборка использует публичный GitHub Releases источник `https://github.com/Kibnet/ContactMirror`. Приложение читает Releases без GitHub-токена. Стабильные версии публикуются с тегом `vMAJOR.MINOR.PATCH`, каналом `win` и App ID `ContactMirror.Desktop`.

Проверить пакет и получить план публикации без записи в GitHub:

```powershell
pwsh -NoProfile -File scripts/Publish-Release.ps1 -Repository Kibnet/ContactMirror -Version 0.4.0 -Output artifacts/releases/Production-0.4.0 -Target <commit-sha>
```

Скрипт сверяет профиль, OAuth-конфигурацию, GitHub URL и SHA256 всех файлов feed, Setup и portable. Он подготавливает стандартные русские release notes и `SHA256SUMS.txt`; перед публикацией описание сверяют с `CHANGELOG.md` и фактическим diff. Для публикации требуется отдельное разрешение пользователя, вход через GitHub CLI и проверенный commit в репозитории. Опция `-Publish` публикует именно стандартные notes скрипта; параметра для другого файла notes у него нет. Существующие версии не заменяются.

Для0.4.0 сначала выполните скрипт **без `-Publish`**, проверьте возвращённый список файлов и затем опубликуйте подготовленное описание отдельно. `--target` должен содержать точный SHA уже слитого main; эти пять assets должны совпадать с проверенным планом:

```powershell
gh release create v0.4.0 --repo Kibnet/ContactMirror --target <merged-commit-sha> --title "ContactMirror 0.4.0" --notes-file docs/releases/0.4.0.md artifacts/releases/Production-0.4.0/ContactMirror.Desktop-0.4.0-full.nupkg artifacts/releases/Production-0.4.0/ContactMirror.Desktop-win-Setup.exe artifacts/releases/Production-0.4.0/ContactMirror.Desktop-win-Portable.zip artifacts/releases/Production-0.4.0/releases.win.json artifacts/releases/Production-0.4.0/SHA256SUMS.txt
```

App updater запрещает downgrade. Revert исходников или сохранение старых assets не откатывает уже установленную0.4.0 автоматически: исправление доставляется новой версией0.4.1, а возврат на0.3.0 требует отдельной ручной переустановки. Папка контактов, настройки и vault остаются вне установки.

В release попадают только nupkg, Setup, portable, feed и контрольные суммы. Build manifest с локальными путями, исходный OAuth JSON, vault и контакты пользователя не загружаются отдельно. Desktop OAuth client входит в программу; режим Google Testing сохраняется и после публикации.

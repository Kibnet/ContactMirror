# Установка и обновление Windows x64

`scripts/Build-Release.ps1` собирает self-contained Windows x64, проверяет фактический профиль исполняемого файла и запускает закреплённый Velopack CLI 1.2.0. Скрипт создаёт Setup, portable ZIP, full/delta nupkg, `releases.win.json`, контрольные суммы и архив установщика каждой версии в `versions/<version>`.

## Профили

| Профиль | Package ID | Настройки и токены в LocalAppData | Google |
| --- | --- | --- | --- |
| Production | ContactMirror.Desktop | ContactMirror | Издатель / управляемый импорт |
| Synthetic | ContactMirror.Validation | ContactMirror.Validation.Data | Отключён, вымышленные контакты |
| Integration | ContactMirror.IntegrationValidation | ContactMirror.IntegrationValidation.Data | Только клиент издателя |

Профиль задаётся при компиляции. JSON контактов, фото, журнал и резервные копии находятся в выбранной пользователем папке вне установочной папки. Обновление заменяет файлы программы; настройки и DPAPI vault остаются отдельно. Папку контактов внутри установки выбрать нельзя, включая пути через junction/symlink.

## Сборка

Для Production нужен Desktop OAuth JSON. Выбран источник GitHub Releases `Kibnet/ContactMirror`:

```powershell
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.3.0 -ValidationProfile Production -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind github -UpdateSource https://github.com/Kibnet/ContactMirror -Output artifacts/releases/Production-0.3.0
```

Локальный каталог feed разрешён только для изолированных проверочных профилей:

```powershell
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.0 -ValidationProfile Integration -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind file -UpdateSource C:\Projects\My\ContactMirror\artifacts\releases\Integration-final -Output artifacts/releases/Integration-final
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.4 -ValidationProfile Integration -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind file -UpdateSource C:\Projects\My\ContactMirror\artifacts\releases\Integration-final -Output artifacts/releases/Integration-final
```

Сборки, тесты и упаковку выполняйте последовательно: они используют общие `bin/obj`, а упаковка отключает PDB. Скрипт ничего не публикует. Подпись установщика пока не настроена.

## Установленный сценарий

Актуальный Setup в release устанавливает опубликованную версию. Уже установленная Production0.2.6 использует тот же GitHub источник и может получить полный пакет0.3.0 без промежуточных версий. Откройте настройки, проверьте обновления, нажмите «Скачать», затем «Установить и перезапустить». Во время синхронизации перезапуск недоступен. Настройки, vault и папка контактов находятся вне установки.

Проверяются App ID, версия, канал `win`, RID `win-x64`, содержимое nuspec, размер и SHA256 полного пакета. Если delta-реконструкция не совпадает с SHA256 исходного full ZIP, приложение один раз скачивает исходный full пакет и повторяет проверку. Проверка не ослабляется для delta.

Applying marker защищает промежуток между завершением старого процесса и запуском новой версии. Второй запуск не начинает синхронизацию в этот промежуток. Восстановление разрешается после проверки процессов и идентичности файлов; истечение времени само по себе не снимает защиту.

SDK/CLI: [Velopack Windows packaging](https://docs.velopack.io/packaging/operating-systems/windows), [Setup CLI](https://docs.velopack.io/reference/cli/content/setup-windows). Локальная установка и обновление не подтверждают доступность публичного feed или успешную проверку OAuth Google для других пользователей.

## GitHub Releases

Production-сборка использует публичный GitHub Releases источник `https://github.com/Kibnet/ContactMirror`. Приложение читает Releases без GitHub-токена. Стабильные версии публикуются с тегом `vMAJOR.MINOR.PATCH`, каналом `win` и App ID `ContactMirror.Desktop`.

Проверить пакет и получить план публикации без записи в GitHub:

```powershell
pwsh -NoProfile -File scripts/Publish-Release.ps1 -Repository Kibnet/ContactMirror -Version 0.3.0 -Output artifacts/releases/Production-0.3.0 -Target <commit-sha>
```

Скрипт сверяет профиль, OAuth-конфигурацию, GitHub URL и SHA256 всех файлов feed, Setup и portable. Он подготавливает стандартные русские release notes и `SHA256SUMS.txt`; перед публикацией описание сверяют с `CHANGELOG.md` и фактическим diff. Для публикации требуется отдельное разрешение пользователя, вход через GitHub CLI и проверенный commit в репозитории. Опция `-Publish` создаёт публичный release через `gh release create`; при собственном описании передайте проверенный файл через `--notes-file`. Существующие версии не заменяются.

В release попадают только nupkg, Setup, portable, feed и контрольные суммы. Build manifest с локальными путями, исходный OAuth JSON, vault и контакты пользователя не загружаются отдельно. Desktop OAuth client входит в программу; режим Google Testing сохраняется и после публикации.

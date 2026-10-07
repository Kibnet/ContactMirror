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
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.4 -ValidationProfile Production -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind github -UpdateSource https://github.com/Kibnet/ContactMirror -Output artifacts/releases/Production
```

Локальный каталог feed разрешён только для изолированных проверочных профилей:

```powershell
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.0 -ValidationProfile Integration -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind file -UpdateSource C:\Projects\My\ContactMirror\artifacts\releases\Integration-final -Output artifacts/releases/Integration-final
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.4 -ValidationProfile Integration -OAuthClientPath appsettings/oauth-client.local.json -UpdateSourceKind file -UpdateSource C:\Projects\My\ContactMirror\artifacts\releases\Integration-final -Output artifacts/releases/Integration-final
```

Сборки, тесты и упаковку выполняйте последовательно: они используют общие `bin/obj`, а упаковка отключает PDB. Скрипт ничего не публикует. Подпись установщика пока не настроена.

## Установленный сценарий

Установите архивный Setup 0.2.0 из `versions/0.2.0`; актуальный Setup в корне feed устанавливает последнюю упакованную версию (Production-кандидат0.2.5; installed validation0.2.4). Откройте настройки, проверьте обновления, нажмите «Скачать», затем «Установить и перезапустить». Во время синхронизации перезапуск недоступен.

Проверяются App ID, версия, канал `win`, RID `win-x64`, содержимое nuspec, размер и SHA256 полного пакета. Если delta-реконструкция не совпадает с SHA256 исходного full ZIP, приложение один раз скачивает исходный full пакет и повторяет проверку. Проверка не ослабляется для delta.

Applying marker защищает промежуток между завершением старого процесса и запуском новой версии. Второй запуск не начинает синхронизацию в этот промежуток. Восстановление разрешается после проверки процессов и идентичности файлов; истечение времени само по себе не снимает защиту.

SDK/CLI: [Velopack Windows packaging](https://docs.velopack.io/packaging/operating-systems/windows), [Setup CLI](https://docs.velopack.io/reference/cli/content/setup-windows). Локальная установка и обновление не подтверждают доступность публичного feed или успешную проверку OAuth Google для других пользователей.

## GitHub Releases

Подготовленная Production-сборка использует `https://github.com/Kibnet/ContactMirror`. На момент подготовки репозиторий ещё не создан, release не опубликован. Приложение читает публичные Releases без GitHub-токена; нужен публичный репозиторий.

Проверить пакет и получить план публикации без записи в GitHub:

```powershell
pwsh -NoProfile -File scripts/Publish-Release.ps1 -Repository Kibnet/ContactMirror -Version 0.2.5 -Output artifacts/releases/Production
```

Скрипт сверяет профиль, OAuth-конфигурацию, GitHub URL и SHA256 всех файлов feed, Setup и portable. Он подготавливает русские release notes и `SHA256SUMS.txt`. После отдельного разрешения на публикацию добавьте `-Publish`; необходимы существующий репозиторий, его ветка `main` и вход через GitHub CLI. Опция создаёт публичный release `v0.2.5` через `gh release create`. Существующие версии не заменяются.

В release попадают только nupkg, Setup, portable, feed и контрольные суммы. Build manifest с локальными путями, исходный OAuth JSON, vault и контакты пользователя не загружаются отдельно. Desktop OAuth client входит в программу; режим Google Testing сохраняется и после публикации.

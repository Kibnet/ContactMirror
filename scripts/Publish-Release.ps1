[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$Output,
    [string]$Target = 'main',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
if ($Repository.Contains('..')) { throw 'Недопустимый адрес репозитория.' }
$taskOutput = [IO.Path]::GetFullPath($Output)
$taskManifest = Get-Content -LiteralPath (Join-Path $taskOutput "build-$Version.json") -Raw | ConvertFrom-Json
if ($taskManifest.profile -ne 'Production' -or $taskManifest.appId -ne 'ContactMirror.Desktop' -or $taskManifest.version -ne $Version -or -not $taskManifest.oauthConfigured) {
    throw 'Нужна проверенная Production-сборка указанной версии с Desktop OAuth client.'
}
$taskSource = Get-Content -LiteralPath (Join-Path $taskManifest.staging 'appsettings\updates.json') -Raw | ConvertFrom-Json
if ($taskSource.kind -ne 'github' -or $taskSource.url -ne "https://github.com/$Repository" -or $taskSource.channel -ne 'win') {
    throw 'Источник обновлений сборки не совпадает с выбранным GitHub-репозиторием.'
}
$taskAssets = [Collections.Generic.List[string]]::new()
function Add-VerifiedAsset {
    param([string]$Name, [long]$Size, [string]$Hash)
    if ([IO.Path]::GetFileName($Name) -ne $Name -or $Name.Contains('/') -or $Name.Contains('\') -or $Hash -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Некорректное имя или SHA256 артефакта.' }
    $taskPath = Join-Path $taskOutput $Name
    if ((Get-Item -LiteralPath $taskPath).Length -ne $Size -or (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash -ne $Hash) { throw "Контрольная сумма не совпадает: $Name" }
    if (-not $taskAssets.Contains($taskPath)) { $taskAssets.Add($taskPath) }
}
$taskFeedPath = Join-Path $taskOutput 'releases.win.json'
$taskFeed = Get-Content -LiteralPath $taskFeedPath -Raw | ConvertFrom-Json
if (-not @($taskFeed.Assets | Where-Object { $_.Version -eq $Version -and $_.Type -eq 'Full' }).Count) { throw 'Full-пакет выбранной версии отсутствует в feed.' }
foreach ($taskAsset in $taskFeed.Assets) {
    if ($taskAsset.PackageId -ne 'ContactMirror.Desktop' -or $taskAsset.Type -notin @('Full', 'Delta')) { throw 'В feed обнаружен другой профиль или неизвестный тип.' }
    Add-VerifiedAsset $taskAsset.FileName $taskAsset.Size $taskAsset.SHA256
}
foreach ($taskName in @('ContactMirror.Desktop-win-Setup.exe', 'ContactMirror.Desktop-win-Portable.zip')) {
    $taskMatches = @($taskManifest.artifacts | Where-Object file -eq $taskName)
    if ($taskMatches.Count -ne 1) { throw "Нет однозначного артефакта в manifest: $taskName" }
    Add-VerifiedAsset $taskName $taskMatches[0].size $taskMatches[0].sha256
}
$taskAssets.Add($taskFeedPath)
$taskChecksums = Join-Path $taskOutput 'SHA256SUMS.txt'
$taskAssets | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_) } | Set-Content -LiteralPath $taskChecksums -Encoding utf8NoBOM
$taskAssets.Add($taskChecksums)
$taskNotes = Join-Path $taskOutput "release-notes-$Version.md"
@"
ContactMirror $Version для Windows x64.

- Вход в Google через браузер и ручная двусторонняя синхронизация контактов, ярлыков и фотографий с папкой JSON.
- Сохранение в папку использует уже проверенный снимок; следующая проверка изменений запускается вручную.
- Ускорено сохранение большой адресной книги: файлы читаются через индекс сеанса, проверки внешних правок сохраняются.
- Установка и обновление через Velopack. Перед установкой обновления проверяются идентичность пакета и SHA256.

Google OAuth пока работает в режиме Testing: вход доступен добавленным тестовым пользователям. Публичная проверка Google ещё не завершена. Установщик пока не подписан.

Для установки используйте ContactMirror.Desktop-win-Setup.exe. SHA256 опубликованы в SHA256SUMS.txt.
"@ | Set-Content -LiteralPath $taskNotes -Encoding utf8NoBOM
$taskPlan = [ordered]@{ repository = $Repository; tag = "v$Version"; target = $Target; files = @($taskAssets | ForEach-Object { [IO.Path]::GetFileName($_) }); notes = $taskNotes; published = $false }
if ($Publish) {
    & gh release create "v$Version" --repo $Repository --target $Target --title "ContactMirror $Version" --notes-file $taskNotes @taskAssets
    if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI сообщил ошибку. Перед повторной публикацией проверьте репозиторий и release: удалённый результат может быть частичным.' }
    $taskPlan.published = $true
}
$taskPlan | ConvertTo-Json -Depth 4

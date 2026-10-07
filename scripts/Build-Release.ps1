[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OAuthClientPath,
    [ValidateSet('none', 'github', 'https', 'file')][string]$UpdateSourceKind = 'none',
    [string]$UpdateSource = '',
    [ValidateSet('Production', 'Synthetic', 'Integration')][string]$ValidationProfile = 'Production',
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskIdentity = switch ($ValidationProfile) { 'Synthetic' { 'ContactMirror.Validation' } 'Integration' { 'ContactMirror.IntegrationValidation' } default { 'ContactMirror.Desktop' } }
if (-not $Output) { $Output = Join-Path $taskRoot "artifacts\releases\$ValidationProfile" }
$taskOutput = [IO.Path]::GetFullPath($Output)
$taskPublisher = $null
if ($ValidationProfile -ne 'Synthetic') {
    if (-not $OAuthClientPath -or -not (Test-Path -LiteralPath $OAuthClientPath -PathType Leaf)) { throw 'Для этой сборки нужен настоящий Desktop OAuth client JSON. Google-enabled package без него не создаётся.' }
    if ((Get-Item -LiteralPath $OAuthClientPath).Length -gt 1MB) { throw 'OAuth JSON слишком велик.' }
    $taskClientJson = Get-Content -LiteralPath $OAuthClientPath -Raw | ConvertFrom-Json
    if ($taskClientJson.PSObject.Properties.Name -contains 'web') { throw 'Нужен OAuth client типа Desktop app.' }
    $taskClient = if ($taskClientJson.installed) { $taskClientJson.installed } else { $taskClientJson }
    $taskClientId = if ($taskClient.client_id) { [string]$taskClient.client_id } else { [string]$taskClient.clientId }
    if ($taskClientId -notmatch '^\S+\.apps\.googleusercontent\.com$') { throw 'Некорректный Desktop Client ID.' }
    $taskPublisher = @{ installed = @{ client_id = $taskClientId; client_secret = $taskClient.client_secret } }
}
if ($UpdateSourceKind -eq 'file') {
    if ($ValidationProfile -eq 'Production' -or -not [IO.Path]::IsPathFullyQualified($UpdateSource)) { throw 'Local feed разрешён только для compile-time validation profile и требует абсолютный путь.' }
    $UpdateSource = [IO.Path]::GetFullPath($UpdateSource)
} elseif ($UpdateSourceKind -ne 'none') {
    $taskUri = $null
    if (-not [Uri]::TryCreate($UpdateSource, [UriKind]::Absolute, [ref]$taskUri) -or $taskUri.Scheme -ne 'https' -or $taskUri.UserInfo -or $taskUri.Query -or $taskUri.Fragment) { throw 'Источник обновлений требует HTTPS без credentials/query/fragment.' }
    if ($UpdateSourceKind -eq 'github' -and ($taskUri.Host -ne 'github.com' -or $taskUri.AbsolutePath.Trim('/').Split('/').Length -ne 2)) { throw 'Укажите GitHub repository URL вида https://github.com/owner/repository.' }
} elseif ($UpdateSource) { throw 'Нужно указать UpdateSourceKind.' }
if (Test-Path -LiteralPath (Join-Path $taskOutput "$taskIdentity-$Version-full.nupkg")) { throw 'Эта версия уже упакована. Используйте новую версию или новый Output; существующий release не заменён.' }
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskStage = Join-Path $taskRoot ('artifacts\staging\' + $taskIdentity + '-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
function Invoke-ReleaseTool { param([string[]]$Arguments) & dotnet @Arguments; if ($LASTEXITCODE -ne 0) { throw "dotnet завершился с кодом $LASTEXITCODE" } }
Push-Location $taskRoot
try {
    Invoke-ReleaseTool @('tool', 'restore')
    $taskPublish = @('publish', 'src/ContactMirror.Desktop/ContactMirror.Desktop.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $taskStage, "-p:Version=$Version", '-p:DebugType=None', '-p:DebugSymbols=false')
    $taskPublish += "-p:ContactMirrorValidationProfile=$ValidationProfile"
    Invoke-ReleaseTool $taskPublish
    $taskCompiled = (& (Join-Path $taskStage 'ContactMirror.exe') --inspect-package-profile | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $taskCompiled.appId -ne $taskIdentity -or $taskCompiled.profile -ne $ValidationProfile) { throw 'Compiled profile/identity не совпадает с PackId. Pack остановлен.' }
    $taskSettings = Join-Path $taskStage 'appsettings'
    New-Item -ItemType Directory -Path $taskSettings -Force | Out-Null
    if ($taskPublisher) { $taskPublisher | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskSettings 'oauth-client.json') -Encoding utf8NoBOM }
    @{ kind = if ($UpdateSourceKind -eq 'none') { '' } else { $UpdateSourceKind }; url = $UpdateSource; channel = 'win' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskSettings 'updates.json') -Encoding utf8NoBOM
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskStage
    $taskDocs = Join-Path $taskStage 'docs'; New-Item -ItemType Directory -Path $taskDocs -Force | Out-Null
    foreach ($taskDoc in @('FORMAT.md', 'GOOGLE_SETUP.md', 'RELEASE.md')) {
        $taskDocPath = Join-Path $taskRoot "docs\$taskDoc"
        if (Test-Path -LiteralPath $taskDocPath) { Copy-Item -LiteralPath $taskDocPath -Destination $taskDocs }
    }
    'ContactMirror uses Velopack 1.2.0 (MIT), .NET, Avalonia, SQLite, Google.Apis.Auth and SkiaSharp. Package licenses remain with their respective owners.' | Set-Content -LiteralPath (Join-Path $taskStage 'THIRD-PARTY-NOTICES.txt') -Encoding utf8NoBOM
    foreach ($taskSymbol in Get-ChildItem -LiteralPath $taskStage -Recurse -File -Filter '*.pdb') {
        $taskSymbolPath = [IO.Path]::GetFullPath($taskSymbol.FullName)
        if (-not $taskSymbolPath.StartsWith($taskStage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Debug symbol выходит за staging directory.' }
        Remove-Item -LiteralPath $taskSymbolPath
    }
    $taskPayload = @(Get-ChildItem -LiteralPath $taskStage -Recurse -File)
    $taskForbidden = @($taskPayload | Where-Object { $_.Name -match '(?i)(\.pdb$|\.dpapi$|\.db($|-)|^desktop.*\.json$|\.local\.json$|TestHost|\.Tests\.|UiTests)' -or $_.FullName -match '\\(Credentials|\.contactmirror|TestResults|chat-artifacts)\\' })
    if ($taskForbidden.Count) { throw 'В staging найдены пользовательские данные, test binaries или debug symbols. Pack остановлен.' }
    Invoke-ReleaseTool @('vpk', 'pack', '--packId', $taskIdentity, '--packVersion', $Version, '--packDir', $taskStage, '--mainExe', 'ContactMirror.exe', '--packTitle', $(if ($ValidationProfile -eq 'Production') { 'ContactMirror' } else { $taskIdentity }), '--runtime', 'win-x64', '--channel', 'win', '--outputDir', $taskOutput, '--shortcuts', 'StartMenuRoot')
    $taskArchive = Join-Path $taskOutput "versions\$Version"
    New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
    Get-ChildItem -LiteralPath $taskOutput -File | Where-Object { $_.Name -match 'Setup\.exe$|Portable\.zip$' } | Copy-Item -Destination $taskArchive
    $taskFeedPath = Join-Path $taskOutput 'releases.win.json'
    if (-not (Test-Path -LiteralPath $taskFeedPath)) { throw 'Velopack release feed не создан.' }
    $taskFeed = Get-Content -LiteralPath $taskFeedPath -Raw | ConvertFrom-Json
    foreach ($taskAsset in $taskFeed.Assets) {
        if ([IO.Path]::GetFileName($taskAsset.FileName) -ne $taskAsset.FileName) { throw 'Недопустимый путь в feed.' }
        $taskPackagePath = Join-Path $taskOutput $taskAsset.FileName
        if (-not (Test-Path -LiteralPath $taskPackagePath) -or (Get-Item -LiteralPath $taskPackagePath).Length -ne $taskAsset.Size -or (Get-FileHash -LiteralPath $taskPackagePath -Algorithm SHA256).Hash -ne $taskAsset.SHA256) { throw 'Feed ссылается на отсутствующий или повреждённый пакет.' }
        if ($taskAsset.PackageId -ne $taskIdentity) { throw 'В feed смешаны разные app identities.' }
    }
    $taskHashes = @(Get-ChildItem -LiteralPath $taskOutput -File | Where-Object Extension -in '.exe', '.zip', '.nupkg' | ForEach-Object { @{ file = $_.Name; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    @{ appId = $taskIdentity; version = $Version; profile = $ValidationProfile; oauthConfigured = ($null -ne $taskPublisher); updateSourceConfigured = ($UpdateSourceKind -ne 'none'); published = $false; staging = $taskStage; artifacts = $taskHashes } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput "build-$Version.json") -Encoding utf8NoBOM
    Write-Host "Готов локальный пакет $taskIdentity $Version. Output: $taskOutput. Публикация не выполнялась."
} finally { Pop-Location }

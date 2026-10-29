$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "Repository audit failed: $message" }
}
$version = (Get-Content 'version.txt' -Raw).Trim()
Assert-True ($version -match '^\d+\.\d+\.\d+$') "version.txt must contain a stable semantic version."
$versionJson = Get-Content 'version.json' -Raw | ConvertFrom-Json
Assert-True ($versionJson.version -eq $version) "version.json is out of sync."
$checks = @{
    'src/GermanOSBackup/GermanOSBackup.csproj' = "<Version>$version</Version>"
    'installer/GermanOSBackup.iss' = ('#define AppVersion "' + $version + '"')
    'README.md' = "**Версия:** $version"
    'index.html' = "VERSION $version"
    'dashboard.html' = "v$version"
    'script.js' = $version
}
foreach ($entry in $checks.GetEnumerator()) {
    $content = Get-Content $entry.Key -Raw
    Assert-True ($content.Contains($entry.Value)) "$($entry.Key) is missing expected version marker: $($entry.Value)"
}
Assert-True (Test-Path 'LICENSE') 'MIT LICENSE file is missing.'
Assert-True ((Get-Content 'LICENSE' -Raw).StartsWith('MIT License')) 'The expected MIT license header is missing.'
foreach ($path in @('src/GermanOSBackup/BackupEngine.cs','src/GermanOSBackup/RestoreForm.cs','native-engine/src/lib.rs','installer/GermanOSBackup.iss','SECURITY.md','CONTRIBUTING.md')) {
    Assert-True (Test-Path $path) "Required project file is missing: $path"
}
$site = Get-Content 'index.html' -Raw
Assert-True ($site.Contains('releases/latest/download/GermanOSBackup-Setup-x64.exe')) 'Website installer URL is missing.'
Assert-True ($site.Contains('https://github.com/German-GPD/German-OS-Backup')) 'Website source URL is missing.'
$obsoletePatterns = @('две отдельные версии','две версии приложения','старые ветки ещё не удалены','отдельная Rust-версия','отдельная C#-версия')
foreach ($file in @('README.md','index.html','dashboard.html','script.js')) {
    $content = Get-Content $file -Raw
    foreach ($pattern in $obsoletePatterns) {
        Assert-True (-not $content.Contains($pattern)) "$file still contains obsolete architecture wording: $pattern"
    }
}
Write-Host "Repository audit passed for version $version."

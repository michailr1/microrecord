# MicroRecord installer — current user only, no administrator rights needed.
#
#   irm https://raw.githubusercontent.com/michailr1/microrecord/main/install.ps1 | iex
#
# Downloads the latest release to %LOCALAPPDATA%\Programs\MicroRecord, checks its SHA-256,
# adds a Start menu shortcut and starts it. Files downloaded by PowerShell carry no
# "downloaded from the Internet" mark, so SmartScreen does not stop the first launch.
# Set $env:MICRORECORD_SMALL = 1 first to get the ~2 MB build that needs the .NET 9 Desktop Runtime.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar on PowerShell 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$asset = if ($env:MICRORECORD_SMALL -eq '1') { 'MicroRecord-win-x64-net9.exe' } else { 'MicroRecord-win-x64.exe' }
$base = 'https://github.com/michailr1/microrecord/releases/latest/download'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\MicroRecord'
$exe = Join-Path $dir 'MicroRecord.exe'
$tmp = Join-Path $dir 'MicroRecord.download'
$sums = Join-Path $dir 'SHA256SUMS.txt'

New-Item -ItemType Directory -Force $dir | Out-Null

Write-Host "Скачиваю $asset ..."
Invoke-WebRequest "$base/$asset" -OutFile $tmp -UseBasicParsing
Invoke-WebRequest "$base/SHA256SUMS.txt" -OutFile $sums -UseBasicParsing

$expected = (Get-Content $sums | Where-Object { $_ -match "\s$([regex]::Escape($asset))$" } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
$actual = (Get-FileHash $tmp -Algorithm SHA256).Hash
if (-not $expected -or $actual -ne $expected.ToUpperInvariant()) {
    Remove-Item $tmp -Force
    throw "Контрольная сумма не совпала ($asset): ожидалась $expected, получена $actual. Установка отменена."
}

$running = Get-Process MicroRecord -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Host 'Закрываю запущенный MicroRecord для обновления...'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}
Move-Item $tmp $exe -Force

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MicroRecord.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dir
$link.Description = 'MicroRecord — запись встреч'
$link.Save()

Write-Host "Готово: $exe"
Write-Host 'Ярлык добавлен в меню «Пуск». Запись: Ctrl+Alt+R.'
Start-Process $exe

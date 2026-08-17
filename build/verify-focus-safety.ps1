$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path $PSScriptRoot '..\client'
$banned = 'SendInput|SetForegroundWindow|BringWindowToTop|AttachThreadInput'
$hits = Get-ChildItem -Path $sourceRoot -Recurse -File -Include *.cs,*.xaml | Select-String -Pattern $banned
if ($hits) {
    $hits | ForEach-Object { Write-Error ("Banned focus API reference: {0}:{1}" -f $_.Path, $_.LineNumber) }
    exit 1
}
$arrangement = Get-Content (Join-Path $sourceRoot 'Plugins\WindowArrangementService.cs') -Raw
if ($arrangement -notmatch 'SWP_NOACTIVATE') { throw 'Window arrangement must include SWP_NOACTIVATE.' }
Write-Output 'Focus-safety static gate passed.'

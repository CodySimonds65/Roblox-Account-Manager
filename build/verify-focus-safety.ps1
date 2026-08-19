$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\client')).Path
$sourceFiles = @(Get-ChildItem -Path $sourceRoot -Recurse -File -Include *.cs,*.xaml)
$violations = [System.Collections.Generic.List[string]]::new()

function Get-RelativeSourcePath([string]$path) {
    [IO.Path]::GetRelativePath($sourceRoot, $path).Replace('\', '/')
}

function Find-ForbiddenReference([string]$pattern, [string[]]$allowedPaths = @()) {
    foreach ($hit in @($sourceFiles | Select-String -Pattern $pattern)) {
        $relativePath = Get-RelativeSourcePath $hit.Path
        if ($allowedPaths -notcontains $relativePath) {
            $violations.Add("Forbidden focus API reference: ${relativePath}:$($hit.LineNumber): $($hit.Matches[0].Value)")
        }
    }
}

# These APIs always steal or reorder desktop focus and have no approved use.
Find-ForbiddenReference '\b(SetForegroundWindow|BringWindowToTop)\b'

# System-wide injection is isolated to a validator-guarded implementation, and
# cross-thread focus attachment is isolated to the embedded child focus bridge.
Find-ForbiddenReference '\bSendInput\b' @('Plugins/InputSendInjector.cs')
Find-ForbiddenReference '\bAttachThreadInput\b' @('Plugins/EmbeddedInputBridge.cs')

$injectorPath = Join-Path $sourceRoot 'Plugins\InputSendInjector.cs'
$injector = Get-Content $injectorPath -Raw
$requiredInjectorGuards = [ordered]@{
    'per-event target validation' = 'foreach\s*\([^)]*events\.OrderBy[\s\S]*?IsSafeTarget\(rootWindow, targetValidator\)'
    'target identity callback' = 'targetValidator\?\.Invoke\(\)'
    'live HWND validation' = '!IsWindow\(rootWindow\)'
    'visible-client validation' = '!IsWindowVisible\(rootWindow\)'
    'foreground-owner validation' = 'GetForegroundWindow\(\)\s*!=\s*GetAncestor\(rootWindow, GaRoot\)'
}
foreach ($guard in $requiredInjectorGuards.GetEnumerator()) {
    if ($injector -notmatch $guard.Value) {
        $violations.Add("InputSendInjector is missing its $($guard.Key) guard.")
    }
}

$bridge = Get-Content (Join-Path $sourceRoot 'Plugins\EmbeddedInputBridge.cs') -Raw
if ($bridge -notmatch 'try[\s\S]*finally[\s\S]*AttachThreadInput\([^;]+false\)') {
    $violations.Add('EmbeddedInputBridge must detach cross-thread input in a finally block.')
}

$arrangement = Get-Content (Join-Path $sourceRoot 'Plugins\WindowArrangementService.cs') -Raw
if ($arrangement -notmatch 'SWP_NOACTIVATE') {
    $violations.Add('Window arrangement must include SWP_NOACTIVATE.')
}

$clientsPanel = Get-Content (Join-Path $sourceRoot 'ClientsPanel.xaml') -Raw
if ($clientsPanel -match '<Style TargetType="ListBoxItem">[\s\S]*?<Setter Property="Focusable" Value="False"') {
    $violations.Add('Client tab ListBoxItems must remain focusable so mouse and keyboard selection work.')
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Error $violation -ErrorAction Continue
    }
    exit 1
}

Write-Output 'Focus-safety static gate passed.'

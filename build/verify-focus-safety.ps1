$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\client')).Path
$sourceFiles = @(Get-ChildItem -Path $sourceRoot -Recurse -File -Include *.cs,*.xaml)
$violations = [System.Collections.Generic.List[string]]::new()

function Get-RelativeSourcePath([string]$path) {
    $rootWithSeparator = $sourceRoot.TrimEnd('\') + '\'
    if ($path.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        return $path.Substring($rootWithSeparator.Length).Replace('\', '/')
    }
    return [IO.Path]::GetFileName($path).Replace('\', '/')
}

function Find-ForbiddenReference([string]$pattern, [string[]]$allowedPaths = @()) {
    foreach ($hit in @($sourceFiles | Select-String -Pattern $pattern)) {
        $relativePath = Get-RelativeSourcePath $hit.Path
        if ($allowedPaths -notcontains $relativePath) {
            $violations.Add("Forbidden input/focus API reference: ${relativePath}:$($hit.LineNumber): $($hit.Matches[0].Value)")
        }
    }
}

# Foreground automation is deliberately narrow: only the coordinator may
# activate a validated Roblox root, and only the injector may call SendInput.
Find-ForbiddenReference '\b(SetForegroundWindow)\b' @('Plugins/ForegroundAutomationCoordinator.cs')
Find-ForbiddenReference '\b(BringWindowToTop|SetActiveWindow|SetFocus|AttachThreadInput)\b'
Find-ForbiddenReference '\b(PostMessage|SendMessage|SendMessageTimeout)\b'
Find-ForbiddenReference '\b(mouse_event|keybd_event|SetCursorPos)\b'
Find-ForbiddenReference '\bSendInput\b' @('Plugins/InputSendInjector.cs')

$injector = Get-Content (Join-Path $sourceRoot 'Plugins\InputSendInjector.cs') -Raw
 $injectorGuards = [ordered]@{
    'per-event target validation' = 'foreach\s*\([^)]*events\.OrderBy[\s\S]*?IsSafeTarget\(rootWindow, targetValidator\)'
    'foreground-owner validation' = 'GetForegroundWindow\(\)\s*!=\s*rootWindow'
    'live HWND validation' = '!IsWindow\(rootWindow\)'
    'visible-client validation' = '!IsWindowVisible\(rootWindow\)'
    'held-input cleanup' = 'ReleaseHeldInputsAsync\(rootWindow, postedEvents'
}
foreach ($guard in $injectorGuards.GetEnumerator()) {
    if ($injector -notmatch $guard.Value) { $violations.Add("InputSendInjector is missing its $($guard.Key) guard.") }
}

$coordinator = Get-Content (Join-Path $sourceRoot 'Plugins\ForegroundAutomationCoordinator.cs') -Raw
 $coordinatorGuards = [ordered]@{
    'global automation lane' = 'new\(1,\s*1\)'
    'root foreground validation' = 'GetForegroundWindow\(\) != root'
    'PID/start identity validation' = 'ProcessStartTimeUtcTicks'
    'activation failure handling' = 'focus-denied'
    'session cleanup' = 'CloseAllForPluginAsync'
}
foreach ($guard in $coordinatorGuards.GetEnumerator()) {
    if ($coordinator -notmatch $guard.Value) { $violations.Add("ForegroundAutomationCoordinator is missing its $($guard.Key) guard.") }
}

if (Test-Path (Join-Path $sourceRoot 'Plugins\FocusSafeInputBroker.cs')) { $violations.Add('FocusSafeInputBroker must be removed; message-only delivery is disabled.') }
if (Test-Path (Join-Path $sourceRoot 'Plugins\EmbeddedInputBridge.cs')) { $violations.Add('EmbeddedInputBridge must be removed; Roblox owns its native human-input path.') }

$nativeHost = Get-Content (Join-Path $sourceRoot 'EmbeddedClientHost.cs') -Raw
if ($nativeHost -notmatch 'class\s+EmbeddedClientHost\s*:\s*HwndHost' -or
    $nativeHost -notmatch 'WsChild\s*\|\s*WsVisible\s*\|\s*WsClipChildren' -or
    $nativeHost -match '\b(PostMessage|SendInput|SetCursorPos|SetFocus|SetActiveWindow|AttachThreadInput|mouse_event|keybd_event)\b') {
    $violations.Add('EmbeddedClientHost must remain a passive native viewport anchor.')
}

$embedding = Get-Content (Join-Path $sourceRoot 'Plugins\ClientEmbeddingService.cs') -Raw
if ($embedding -match '\bSetParent\b' -or $embedding -match '\)\s*\|\s*WsChild\b') { $violations.Add('Client embedding must remain top-level; SetParent and WS_CHILD are forbidden.') }
if ($embedding -notmatch 'GetAncestor\(hostWindow,\s*GaRoot\)' -or
    $embedding -notmatch 'GetAncestor\(rootWindow,\s*GaRoot\)' -or
    $embedding -notmatch 'GwOwner' -or
    $embedding -notmatch 'OriginalOwner' -or
    $embedding -notmatch 'SwpNoActivate' -or
    $embedding -notmatch 'OriginalStyle' -or
    $embedding -notmatch 'OriginalBounds') { $violations.Add('Client docking must preserve top-level identity and original window state.') }

$arrangement = Get-Content (Join-Path $sourceRoot 'Plugins\WindowArrangementService.cs') -Raw
if ($arrangement -notmatch 'SWP_NOACTIVATE') { $violations.Add('Window arrangement must include SWP_NOACTIVATE.') }
$clientsPanel = Get-Content (Join-Path $sourceRoot 'ClientsPanel.xaml') -Raw
if ($clientsPanel -match '<Style TargetType="ListBoxItem">[\s\S]*?<Setter Property="Focusable" Value="False"') { $violations.Add('Client tabs must remain focusable.') }
if ($clientsPanel -notmatch '<local:EmbeddedClientHost\b') { $violations.Add('ClientsPanel must reserve its native game viewport.') }

if ($violations.Count -gt 0) { foreach ($violation in $violations) { Write-Error $violation -ErrorAction Continue }; exit 1 }
Write-Output 'Focus-safety static gate passed.'

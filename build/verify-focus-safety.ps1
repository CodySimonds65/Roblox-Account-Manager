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
            $violations.Add("Forbidden focus API reference: ${relativePath}:$($hit.LineNumber): $($hit.Matches[0].Value)")
        }
    }
}

# These APIs always steal or reorder desktop focus and have no approved use.
Find-ForbiddenReference '\b(SetForegroundWindow|BringWindowToTop)\b'
Find-ForbiddenReference '\b(mouse_event|keybd_event)\b'
Find-ForbiddenReference '\bSetParent\b'
Find-ForbiddenReference '\bSetCursorPos\b'

# Human input must remain native.  The embedding path must not synthesize
# activation/focus or cross-thread input state; only the explicitly approved
# macro brokers may use their isolated delivery APIs below.
Find-ForbiddenReference '\b(SetActiveWindow|SetFocus|AttachThreadInput)\b'
Find-ForbiddenReference '\b(SendMessage|SendMessageTimeout)\b'

# System-wide injection is isolated to a validator-guarded macro injector, and
# posted messages are isolated to the validator-guarded background macro broker.
# Direct cursor positioning is never approved.
Find-ForbiddenReference '\bSendInput\b' @('Plugins/InputSendInjector.cs')
Find-ForbiddenReference '\bPostMessage\b' @('Plugins/FocusSafeInputBroker.cs')

$injectorPath = Join-Path $sourceRoot 'Plugins\InputSendInjector.cs'
$injector = Get-Content $injectorPath -Raw
$requiredInjectorGuards = [ordered]@{
    'per-event target validation' = 'foreach\s*\([^)]*events\.OrderBy[\s\S]*?IsSafeTarget\(rootWindow, targetValidator\)'
    'target identity callback' = 'targetValidator\?\.Invoke\(\)'
    'live HWND validation' = '!IsWindow\(rootWindow\)'
    'visible-client validation' = '!IsWindowVisible\(rootWindow\)'
    'foreground-owner validation' = 'GetForegroundWindow\(\)\s*!=\s*(?:GetAncestor\(rootWindow, GaRoot\)|rootWindow)'
    'focused-client validation' = 'GetGUIThreadInfo\(gameThread, ref info\)[\s\S]*?IsFocusWithin\(rootWindow, info\.hwndFocus\)'
    'held-input cleanup' = 'ReleaseHeldInputsAsync\(rootWindow, postedEvents, targetValidator, releaseFallback\)'
    'non-foreground targeted release fallback' = 'releaseFallback\(releases\)'
    'virtual-desktop mouse mapping' = 'MouseeventfMove\s*\|\s*MouseeventfAbsolute\s*\|\s*MouseeventfVirtualDesk'
}
foreach ($guard in $requiredInjectorGuards.GetEnumerator()) {
    if ($injector -notmatch $guard.Value) {
        $violations.Add("InputSendInjector is missing its $($guard.Key) guard.")
    }
}

if (Test-Path (Join-Path $sourceRoot 'Plugins\EmbeddedInputBridge.cs')) {
    $violations.Add('EmbeddedInputBridge must be removed; docked Roblox windows own their native human-input path.')
}

$nativeHost = Get-Content (Join-Path $sourceRoot 'EmbeddedClientHost.cs') -Raw
if ($nativeHost -notmatch 'class\s+EmbeddedClientHost\s*:\s*HwndHost' -or
    $nativeHost -notmatch 'WsChild\s*\|\s*WsVisible\s*\|\s*WsClipChildren') {
    $violations.Add('The Clients viewport must use a native HwndHost child window for direct user input.')
}
if ($nativeHost -notmatch 'RegisterClassEx' -or
    $nativeHost -notmatch 'WmNcHitTest' -or
    $nativeHost -match '\b(PostMessage|SendInput|SetCursorPos|SetFocus|SetActiveWindow|AttachThreadInput|mouse_event|keybd_event)\b') {
    $violations.Add('EmbeddedClientHost must remain a passive registered viewport anchor and must not focus or synthesize input.')
}

$embedding = Get-Content (Join-Path $sourceRoot 'Plugins\ClientEmbeddingService.cs') -Raw
if ($embedding -match '\bSetParent\b' -or $embedding -match '\)\s*\|\s*WsChild\b') {
    $violations.Add('Client embedding must use a top-level overlay; SetParent and adding WS_CHILD are forbidden.')
}
if ($embedding -notmatch 'GetAncestor\(hostWindow,\s*GaRoot\)' -or
    $embedding -notmatch 'GetAncestor\(rootWindow,\s*GaRoot\)' -or
    $embedding -notmatch 'GwlpHwndParent' -or
    $embedding -notmatch 'GwOwner' -or
    $embedding -notmatch 'OriginalOwner' -or
    $embedding -notmatch 'SwpNoActivate' -or
    $embedding -notmatch 'SwHide' -or
    $embedding -notmatch 'OriginalStyle' -or
    $embedding -notmatch 'OriginalBounds' -or
    $embedding -notmatch 'if\s*\(!selected\)[\s\S]*?HideWindow\(window\.Root\)') {
    $violations.Add('Client docking must keep top-level/owner semantics, avoid activation, and restore original window state.')
}

$arrangement = Get-Content (Join-Path $sourceRoot 'Plugins\WindowArrangementService.cs') -Raw
if ($arrangement -notmatch 'SWP_NOACTIVATE') {
    $violations.Add('Window arrangement must include SWP_NOACTIVATE.')
}

$clientsPanel = Get-Content (Join-Path $sourceRoot 'ClientsPanel.xaml') -Raw
if ($clientsPanel -match '<Style TargetType="ListBoxItem">[\s\S]*?<Setter Property="Focusable" Value="False"') {
    $violations.Add('Client tab ListBoxItems must remain focusable so mouse and keyboard selection work.')
}
if ($clientsPanel -notmatch '<local:EmbeddedClientHost\b') {
    $violations.Add('ClientsPanel must reserve its game viewport with EmbeddedClientHost.')
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Error $violation -ErrorAction Continue
    }
    exit 1
}

Write-Output 'Focus-safety static gate passed.'

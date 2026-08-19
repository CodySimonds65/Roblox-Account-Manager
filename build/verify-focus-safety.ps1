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

# System-wide injection is isolated to a validator-guarded implementation, and
# cross-thread focus attachment is isolated to the embedded child focus bridge.
Find-ForbiddenReference '\bSendInput\b' @('Plugins/InputSendInjector.cs')
Find-ForbiddenReference '\bSetCursorPos\b' @('Plugins/InputSendInjector.cs')
Find-ForbiddenReference '\bPostMessage\b' @('Plugins/FocusSafeInputBroker.cs')
Find-ForbiddenReference '\bAttachThreadInput\b' @('Plugins/EmbeddedInputBridge.cs')
Find-ForbiddenReference '\bSetFocus\b' @('Plugins/EmbeddedInputBridge.cs')

$injectorPath = Join-Path $sourceRoot 'Plugins\InputSendInjector.cs'
$injector = Get-Content $injectorPath -Raw
$requiredInjectorGuards = [ordered]@{
    'per-event target validation' = 'foreach\s*\([^)]*events\.OrderBy[\s\S]*?IsSafeTarget\(rootWindow, targetValidator\)'
    'target identity callback' = 'targetValidator\?\.Invoke\(\)'
    'live HWND validation' = '!IsWindow\(rootWindow\)'
    'visible-client validation' = '!IsWindowVisible\(rootWindow\)'
    'foreground-owner validation' = 'GetForegroundWindow\(\)\s*!=\s*GetAncestor\(rootWindow, GaRoot\)'
    'focused-client validation' = 'GetGUIThreadInfo\(gameThread, ref info\)[\s\S]*?IsFocusWithin\(rootWindow, info\.hwndFocus\)'
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
if ($bridge -match '\b(PostMessage|SendInput|SetCursorPos|WmMouse(?:Move|Wheel|Button)|WmKey(?:Down|Up))\b') {
    $violations.Add('EmbeddedInputBridge must not synthesize human mouse or keyboard input.')
}

$nativeHost = Get-Content (Join-Path $sourceRoot 'EmbeddedClientHost.cs') -Raw
if ($nativeHost -notmatch 'class\s+EmbeddedClientHost\s*:\s*HwndHost' -or
    $nativeHost -notmatch 'WsChild\s*\|\s*WsVisible\s*\|\s*WsClipChildren') {
    $violations.Add('The Clients viewport must use a native HwndHost child window for direct user input.')
}
if ($nativeHost -notmatch 'RegisterClassEx' -or
    $nativeHost -notmatch 'WmNcHitTest' -or
    $nativeHost -notmatch 'WmMouseActivate' -or
    $nativeHost -match '\b(PostMessage|SendInput|SetCursorPos|mouse_event|keybd_event)\b') {
    $violations.Add('EmbeddedClientHost must use a registered native hit-test/activation path and must not synthesize input.')
}

$embedding = Get-Content (Join-Path $sourceRoot 'Plugins\ClientEmbeddingService.cs') -Raw
if ($embedding -notmatch 'GetAncestor\(hostWindow,\s*GaRoot\)' -or
    $embedding -notmatch 'OriginalStyle' -or
    $embedding -notmatch 'OriginalBounds') {
    $violations.Add('Client embedding must derive foreground ownership from its native root and restore original window state.')
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

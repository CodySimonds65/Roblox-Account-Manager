[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $HandleTool,

    [Parameter(Mandatory)]
    [string] $ResultPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$messages = [Collections.Generic.List[string]]::new()
$closedCount = 0

try {
    $processes = @(Get-Process -Name 'RobloxPlayerBeta' -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        throw 'No running Roblox client was found. Launch the first account into a game before preparing another account.'
    }

    foreach ($process in $processes) {
        $output = & $HandleTool -accepteula -nobanner -a -p $process.Id 2>&1
        foreach ($line in $output) {
            $text = [string] $line
            if ($text -match '^\s*([0-9A-Fa-f]+):\s+\S+\s+(.+)$') {
                $handleId = $Matches[1]
                $handleName = $Matches[2].Trim()
                if ($handleName -match '(?i)\\ROBLOX_singleton(Event|Mutex)$') {
                    $objectName = $Matches[0].TrimStart('\')
                    $closeOutput = & $HandleTool -accepteula -nobanner -c $handleId -p $process.Id -y 2>&1
                    if ($LASTEXITCODE -ne 0) {
                        throw "Could not release $objectName in PID $($process.Id): $closeOutput"
                    }
                    $closedCount++
                    $messages.Add("Released $objectName in PID $($process.Id).")
                }
            }
        }
    }

    if ($closedCount -eq 0) {
        $messages.Add('No singleton handles are currently present; Roblox is already unlocked.')
    }

    foreach ($process in @(Get-Process -Name 'RobloxPlayerBeta' -ErrorAction SilentlyContinue)) {
        $remaining = @(& $HandleTool -accepteula -nobanner -a -p $process.Id 2>&1 | Where-Object {
            ([string] $_) -match '(?i)\\ROBLOX_singleton(Event|Mutex)\s*$'
        })
        if ($remaining.Count -gt 0) {
            throw "Roblox still owns a singleton object in PID $($process.Id)."
        }
    }

    $result = [ordered]@{
        Success = $true
        ClosedCount = $closedCount
        Messages = @($messages)
    }
}
catch {
    $messages.Add($_.Exception.Message)
    $result = [ordered]@{
        Success = $false
        ClosedCount = $closedCount
        Messages = @($messages)
    }
}

$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
if (-not $result.Success) { exit 1 }

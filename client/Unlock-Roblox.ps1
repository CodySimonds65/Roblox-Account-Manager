[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $HandleTool,

    [string] $ResultPath,

    [string] $SessionDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-RobloxSingletonRelease {
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

        return [ordered]@{
            Success = $true
            ClosedCount = $closedCount
            Messages = @($messages)
        }
    }
    catch {
        $messages.Add($_.Exception.Message)
        return [ordered]@{
            Success = $false
            ClosedCount = $closedCount
            Messages = @($messages)
        }
    }
}

function Write-ReleaseResult {
    param(
        [Parameter(Mandatory)]
        [Collections.IDictionary] $Result,

        [Parameter(Mandatory)]
        [string] $Destination
    )

    $temporaryPath = "$Destination.$([Guid]::NewGuid().ToString('N')).tmp"
    $Result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryPath -Destination $Destination -Force
}

if (-not [string]::IsNullOrWhiteSpace($SessionDirectory)) {
    New-Item -ItemType Directory -Path $SessionDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $SessionDirectory 'ready') -Value '' -Encoding ASCII

    try {
        while (-not (Test-Path -LiteralPath (Join-Path $SessionDirectory 'stop'))) {
            $requests = @(Get-ChildItem -LiteralPath $SessionDirectory -Filter 'request-*' -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^request-([0-9a-f]{32})$' })
            foreach ($request in $requests) {
                $requestId = $request.Name.Substring('request-'.Length)
                $destination = Join-Path $SessionDirectory "result-$requestId.json"
                Write-ReleaseResult -Result (Invoke-RobloxSingletonRelease) -Destination $destination
                Remove-Item -LiteralPath $request.FullName -Force -ErrorAction SilentlyContinue
            }

            Start-Sleep -Milliseconds 100
        }
    }
    finally {
        Remove-Item -LiteralPath (Join-Path $SessionDirectory 'ready') -Force -ErrorAction SilentlyContinue
    }

    exit 0
}

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    throw 'ResultPath or SessionDirectory is required.'
}

$result = Invoke-RobloxSingletonRelease
Write-ReleaseResult -Result $result -Destination $ResultPath
if (-not $result.Success) { exit 1 }

[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^(https://(www\.)?roblox\.com/|roblox:)')]
    [string[]] $GameUrl,

    [Parameter()]
    [switch] $NoDownload,

    [Parameter(DontShow)]
    [string] $EncodedGameUrls
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PresetGames = [ordered]@{
    '1' = [pscustomobject]@{
        Name = 'Dungeon Quest Reborn'
        Url  = 'https://www.roblox.com/games/77649408247578/Dungeon-Quest-Reborn'
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-AsAdministrator {
    $arguments = @(
        '-NoProfile'
        '-NoExit'
        '-ExecutionPolicy', 'Bypass'
        '-File', ('"{0}"' -f $PSCommandPath)
    )

    if ($GameUrl -and @($GameUrl).Count -gt 0) {
        $encodedUrls = [Convert]::ToBase64String(
            [Text.Encoding]::UTF8.GetBytes(($GameUrl -join "`n"))
        )
        $arguments += '-EncodedGameUrls'
        $arguments += $encodedUrls
    }

    if ($NoDownload) {
        $arguments += '-NoDownload'
    }

    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments | Out-Null
}

function Get-HandleTool {
    $toolDirectory = Join-Path $PSScriptRoot 'tools'
    $handlePath = Join-Path $toolDirectory 'handle64.exe'

    if (Test-Path -LiteralPath $handlePath) {
        return $handlePath
    }

    if ($NoDownload) {
        throw "Handle.exe is missing. Put Microsoft's handle64.exe in '$toolDirectory', or run without -NoDownload."
    }

    Write-Host 'Downloading Microsoft Sysinternals Handle...' -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null

    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("roblox-alt-launcher-{0}" -f [guid]::NewGuid())
    $archivePath = Join-Path $temporaryDirectory 'Handle.zip'

    try {
        New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
        Invoke-WebRequest -UseBasicParsing -Uri 'https://download.sysinternals.com/files/Handle.zip' -OutFile $archivePath
        Expand-Archive -LiteralPath $archivePath -DestinationPath $temporaryDirectory -Force

        $downloadedTool = Join-Path $temporaryDirectory 'handle64.exe'
        if (-not (Test-Path -LiteralPath $downloadedTool)) {
            throw 'The Sysinternals archive did not contain handle64.exe.'
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $downloadedTool
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
            throw 'The downloaded Handle executable does not have a valid Microsoft signature.'
        }

        Copy-Item -LiteralPath $downloadedTool -Destination $handlePath -Force
        return $handlePath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
        }
    }
}

function Get-RobloxProcesses {
    return @(Get-Process -Name 'RobloxPlayerBeta' -ErrorAction SilentlyContinue)
}

function Close-RobloxSingletonHandles {
    param(
        [Parameter(Mandatory)]
        [string] $HandleTool
    )

    $closedCount = 0

    foreach ($process in (Get-RobloxProcesses)) {
        $output = & $HandleTool -accepteula -nobanner -a -p $process.Id 2>&1

        foreach ($line in $output) {
            $text = [string] $line
            if ($text -match '^\s*([0-9A-Fa-f]+):\s+\S+\s+(.+)$') {
                $handleId = $Matches[1]
                $handleName = $Matches[2].Trim()

                # Modern clients create both objects. Be deliberately strict so
                # unrelated Roblox handles are never closed.
                if ($handleName -match '(?i)\\ROBLOX_singleton(Event|Mutex)$') {
                    $objectName = $Matches[0].TrimStart('\')
                    Write-Host ("Releasing {0} in PID {1}..." -f $objectName, $process.Id) -ForegroundColor Yellow
                    $closeOutput = & $HandleTool -accepteula -nobanner -c $handleId -p $process.Id -y 2>&1
                    if ($LASTEXITCODE -ne 0) {
                        throw "Handle.exe could not close handle $handleId in PID $($process.Id): $closeOutput"
                    }
                    $closedCount++
                }
            }
        }
    }

    if ($closedCount -gt 0) {
        $remaining = @()
        foreach ($process in (Get-RobloxProcesses)) {
            $verificationOutput = & $HandleTool -accepteula -nobanner -a -p $process.Id 2>&1
            $remaining += @($verificationOutput | Where-Object {
                ([string] $_) -match '(?i)\\ROBLOX_singleton(Event|Mutex)\s*$'
            })
        }

        if ($remaining.Count -gt 0) {
            throw "Roblox still owns a singleton object after the close attempt: $($remaining -join '; ')"
        }

        Write-Host ("Verified: released {0} Roblox singleton handle(s)." -f $closedCount) -ForegroundColor Green
    }

    return $closedCount
}

function Wait-ForAdditionalRobloxProcess {
    param(
        [Parameter(Mandatory)]
        [int] $PreviousCount,

        [int] $TimeoutSeconds = 45
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $currentCount = @(Get-RobloxProcesses).Count
        if ($currentCount -gt $PreviousCount) {
            Write-Host ("Roblox instance detected ({0} running)." -f $currentCount) -ForegroundColor Green
            return $true
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    Write-Warning 'No additional Roblox process was detected. The browser may still be waiting for confirmation, or the same account may already be running.'
    return $false
}

function Open-NextAccount {
    param(
        [Parameter(Mandatory)]
        [string] $Url,

        [Parameter(Mandatory)]
        [string] $HandleTool
    )

    $beforeCount = @(Get-RobloxProcesses).Count
    $closedCount = Close-RobloxSingletonHandles -HandleTool $HandleTool

    if ($closedCount -eq 0) {
        throw 'No Roblox singleton handles were found. Make sure the first account is fully loaded into a game, then try again.'
    }

    Write-Host "Opening $Url" -ForegroundColor Cyan
    Start-Process $Url
    Wait-ForAdditionalRobloxProcess -PreviousCount $beforeCount | Out-Null
}

try {
    if (-not [string]::IsNullOrWhiteSpace($EncodedGameUrls)) {
        $decodedUrls = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($EncodedGameUrls))
        $GameUrl = @($decodedUrls -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    if (-not (Test-IsAdministrator)) {
        Write-Host 'Administrator access is required to release the Roblox single-instance handle.' -ForegroundColor Yellow
        Restart-AsAdministrator
        exit 0
    }

    Write-Host ''
    Write-Host 'Roblox Alt Launcher' -ForegroundColor Magenta
    Write-Host '===================' -ForegroundColor Magenta
    Write-Host 'This tool never asks for or stores Roblox credentials.'
    Write-Host ''

    $bloxstrapProcesses = @(Get-RobloxProcesses | Where-Object { $_.Path -match '(?i)\\Bloxstrap\\' })
    if ($bloxstrapProcesses.Count -gt 0) {
        Write-Warning 'This Roblox client was installed by Bloxstrap. Bloxstrap 2.10+ removed multi-instance support, so its launch confirmation may still prevent another client from starting.'
    }

    $handleTool = Get-HandleTool

    if (@(Get-RobloxProcesses).Count -eq 0) {
        Write-Host '1. Sign into the first Roblox account in your browser.'
        Write-Host '2. Launch a game and wait until it has fully loaded.'
        Read-Host 'Press Enter when the first account is in a game' | Out-Null
    }

    if (@(Get-RobloxProcesses).Count -eq 0) {
        throw 'Roblox is not running. Launch the first account into a game and run this launcher again.'
    }

    if ($GameUrl -and @($GameUrl).Count -gt 0) {
        foreach ($url in $GameUrl) {
            Write-Host ''
            Write-Host 'Switch your browser to the Roblox account that should be launched next.' -ForegroundColor White
            Read-Host 'Press Enter when that account is signed in' | Out-Null
            Open-NextAccount -Url $url -HandleTool $handleTool
        }
    }
    else {
        :accountLoop while ($true) {
            Write-Host ''
            Write-Host 'Switch your browser to the next Roblox account and open the game page.' -ForegroundColor White
            Write-Host '[1] Dungeon Quest Reborn'
            Write-Host '[2] Paste another Roblox game URL'
            Write-Host '[Q] Finish'
            $selection = (Read-Host 'Select an option').Trim()

            switch ($selection.ToUpperInvariant()) {
                '1' {
                    $url = $PresetGames['1'].Url
                    Write-Host ("Selected: {0}" -f $PresetGames['1'].Name) -ForegroundColor Cyan
                }
                '2' {
                    $url = (Read-Host 'Paste the Roblox game URL').Trim()
                    if ([string]::IsNullOrWhiteSpace($url)) {
                        Write-Warning 'No URL was entered.'
                        continue accountLoop
                    }
                }
                { $_ -in @('Q', 'QUIT', 'EXIT', '') } {
                    break accountLoop
                }
                default {
                    Write-Warning 'Choose 1, 2, or Q.'
                    continue accountLoop
                }
            }

            if ($url -notmatch '^(https://(www\.)?roblox\.com/|roblox:)') {
                Write-Warning 'That does not look like a Roblox URL.'
                continue
            }

            Open-NextAccount -Url $url -HandleTool $handleTool
        }
    }

    Write-Host ''
    Write-Host 'Finished. Leave this window open only if you want to review its output.' -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Error $_
    exit 1
}

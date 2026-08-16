param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\client\Assets\RobloxAccountManager.ico')
)

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $directory -Force | Out-Null

$width = 32
$height = 32
$pixels = New-Object byte[] ($width * $height * 4)

function Set-Pixel {
    param(
        [int]$X,
        [int]$Y,
        [byte]$Red,
        [byte]$Green,
        [byte]$Blue,
        [byte]$Alpha = 255
    )

    if ($X -lt 0 -or $X -ge $width -or $Y -lt 0 -or $Y -ge $height) {
        return
    }

    $index = (($height - 1 - $Y) * $width + $X) * 4
    $pixels[$index] = $Blue
    $pixels[$index + 1] = $Green
    $pixels[$index + 2] = $Red
    $pixels[$index + 3] = $Alpha
}

# Purple rounded-square mark with a white, tilted square cut-out.
for ($y = 0; $y -lt $height; $y++) {
    for ($x = 0; $x -lt $width; $x++) {
        $dx = [Math]::Abs($x - 15.5)
        $dy = [Math]::Abs($y - 15.5)
        $inside = ($dx -le 13.5 -and $dy -le 13.5) -and
                  (($dx -le 11.5 -or $dy -le 11.5) -or ($dx + $dy -le 25.5))
        if ($inside) {
            Set-Pixel $x $y 139 92 246 255
        }

        if (($dx + $dy) -le 5.5) {
            Set-Pixel $x $y 255 255 255 255
        }
    }
}

$stream = [IO.File]::Open($OutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $writer = [IO.BinaryWriter]::new($stream)
    # ICONDIR
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]1)
    # ICONDIRENTRY
    $writer.Write([byte]$width)
    $writer.Write([byte]$height)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $imageSize = 40 + $pixels.Length + ($width * $height / 8)
    $writer.Write([UInt32]$imageSize)
    $writer.Write([UInt32]22)
    # BITMAPINFOHEADER
    $writer.Write([UInt32]40)
    $writer.Write([Int32]$width)
    $writer.Write([Int32]($height * 2))
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]0)
    $writer.Write([UInt32]$pixels.Length)
    $writer.Write([Int32]0)
    $writer.Write([Int32]0)
    $writer.Write([UInt32]0)
    $writer.Write([UInt32]0)
    $writer.Write($pixels)
    $writer.Write((New-Object byte[] ($width * $height / 8)))
    $writer.Flush()
    $writer.Dispose()
}
finally {
    $stream.Dispose()
}

Write-Output "Generated $OutputPath"

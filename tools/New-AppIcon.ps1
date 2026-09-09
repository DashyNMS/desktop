param(
    [Parameter(Mandatory = $true)][string]$OutputPath
)

Add-Type -AssemblyName System.Drawing

function New-IconBitmap {
    param([int]$s)

    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [float]($s * 0.04)
    $d = [float]($s - ($inset * 2))

    $disc = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 23, 27, 35))
    $g.FillEllipse($disc, $inset, $inset, $d, $d)
    $disc.Dispose()

    $ringWidth = [float][Math]::Max(1.0, $s * 0.075)
    $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 59, 130, 246)), $ringWidth
    $half = $ringWidth / 2.0
    $g.DrawEllipse($ringPen, ($inset + $half), ($inset + $half), ($d - $ringWidth), ($d - $ringWidth))
    $ringPen.Dispose()

    $penWidth = [float][Math]::Max(1.0, $s * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 232, 238, 246)), $penWidth
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $coords = @(
        @(0.24, 0.55), @(0.38, 0.55), @(0.455, 0.33),
        @(0.565, 0.71), @(0.64, 0.50), @(0.77, 0.50)
    )

    $points = foreach ($c in $coords) {
        New-Object System.Drawing.PointF ([float]($c[0] * $s)), ([float]($c[1] * $s))
    }

    $g.DrawLines($pen, [System.Drawing.PointF[]]$points)
    $pen.Dispose()
    $g.Dispose()

    return $bmp
}

function ConvertTo-IconDib {
    # BITMAPINFOHEADER + bottom-up 32bpp BGRA XOR data + a (zeroed) AND mask.
    param([System.Drawing.Bitmap]$bmp)

    $w = $bmp.Width
    $h = $bmp.Height

    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $w2 = New-Object System.IO.BinaryWriter($ms)

    $w2.Write([UInt32]40)        # biSize
    $w2.Write([Int32]$w)         # biWidth
    $w2.Write([Int32]($h * 2))   # biHeight: XOR plus AND mask
    $w2.Write([UInt16]1)         # biPlanes
    $w2.Write([UInt16]32)        # biBitCount
    $w2.Write([UInt32]0)         # biCompression: BI_RGB
    $w2.Write([UInt32]($w * $h * 4))
    $w2.Write([Int32]0)          # biXPelsPerMeter
    $w2.Write([Int32]0)          # biYPelsPerMeter
    $w2.Write([UInt32]0)         # biClrUsed
    $w2.Write([UInt32]0)         # biClrImportant

    # XOR bitmap, bottom row first.
    for ($y = $h - 1; $y -ge 0; $y--) {
        $w2.Write($raw, ($y * $stride), ($w * 4))
    }

    # AND mask: 1bpp, rows padded to 4 bytes. Zeroed, because alpha does the work.
    $maskStride = [int][Math]::Floor((($w + 31) / 32)) * 4
    $maskRow = New-Object byte[] $maskStride
    for ($y = 0; $y -lt $h; $y++) {
        $w2.Write($maskRow, 0, $maskStride)
    }

    $w2.Flush()
    $bytes = $ms.ToArray()
    $w2.Dispose()
    $ms.Dispose()

    return , $bytes
}

function ConvertTo-IconPng {
    param([System.Drawing.Bitmap]$bmp)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()

    return , $bytes
}

# Small sizes as DIB so GDI+ (System.Drawing.Icon) can read them; large sizes as
# PNG, which is what Windows expects above 64 pixels and keeps the file small.
$plan = @(
    @{ Size = 16;  Png = $false },
    @{ Size = 24;  Png = $false },
    @{ Size = 32;  Png = $false },
    @{ Size = 48;  Png = $false },
    @{ Size = 64;  Png = $false },
    @{ Size = 128; Png = $true },
    @{ Size = 256; Png = $true }
)

$payloads = New-Object System.Collections.Generic.List[byte[]]

foreach ($entry in $plan) {
    $bmp = New-IconBitmap -s $entry.Size
    if ($entry.Png) {
        $payloads.Add((ConvertTo-IconPng -bmp $bmp))
    }
    else {
        $payloads.Add((ConvertTo-IconDib -bmp $bmp))
    }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([UInt16]0)
$w.Write([UInt16]1)
$w.Write([UInt16]$plan.Count)

$offset = 6 + (16 * $plan.Count)

for ($i = 0; $i -lt $plan.Count; $i++) {
    $s = $plan[$i].Size
    $bytes = $payloads[$i]
    if ($s -ge 256) { $dim = 0 } else { $dim = $s }

    $w.Write([Byte]$dim)
    $w.Write([Byte]$dim)
    $w.Write([Byte]0)
    $w.Write([Byte]0)
    $w.Write([UInt16]1)
    $w.Write([UInt16]32)
    $w.Write([UInt32]$bytes.Length)
    $w.Write([UInt32]$offset)

    $offset += $bytes.Length
}

foreach ($bytes in $payloads) {
    $w.Write($bytes)
}

$w.Flush()
[System.IO.File]::WriteAllBytes($OutputPath, $out.ToArray())
$w.Dispose()
$out.Dispose()

Write-Output "Wrote $OutputPath ($((Get-Item $OutputPath).Length) bytes, $($plan.Count) sizes)"

# Builds src/Omnisound.App/Assets/omnisound.ico: a dark grey rounded square with a light four-pointed
# star (straight edges, every corner rounded alike). Drawn at 8x and downscaled; DIB entries, PNG at 256.
# Run: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Omnisound.App\Assets\omnisound.ico'))
Add-Type -AssemblyName System.Drawing

$Sizes = 16, 20, 24, 28, 32, 40, 48, 64, 128, 256
$BackColor = [Drawing.Color]::FromArgb(255, 0x2C, 0x2C, 0x2C)
$StarColor = [Drawing.Color]::FromArgb(255, 0xE8, 0xE8, 0xE8)

function New-StarPath([double]$cx, [double]$cy, [double]$outer, [double]$inner, [double]$round) {
    # Four-pointed star with straight edges: points at radius $outer, inner corners at $inner (45° between).
    # Every corner, tip or inner, is replaced by a circular arc of the same radius $round.
    $v = for ($i = 0; $i -lt 8; $i++) {
        $a = [Math]::PI / 4 * $i - [Math]::PI / 2
        $r = if ($i % 2 -eq 0) { $outer } else { $inner }
        , @(($cx + $r * [Math]::Cos($a)), ($cy + $r * [Math]::Sin($a)))
    }
    $pts = New-Object System.Collections.Generic.List[Drawing.PointF]
    for ($i = 0; $i -lt 8; $i++) {
        $p = $v[$i]; $a = $v[($i + 7) % 8]; $b = $v[($i + 1) % 8]
        $u1x = $a[0] - $p[0]; $u1y = $a[1] - $p[1]; $l = [Math]::Sqrt($u1x * $u1x + $u1y * $u1y); $u1x /= $l; $u1y /= $l
        $u2x = $b[0] - $p[0]; $u2y = $b[1] - $p[1]; $l = [Math]::Sqrt($u2x * $u2x + $u2y * $u2y); $u2x /= $l; $u2y /= $l
        $theta = [Math]::Acos($u1x * $u2x + $u1y * $u2y)     # angle between the two edges at this corner
        $bx = $u1x + $u2x; $by = $u1y + $u2y; $l = [Math]::Sqrt($bx * $bx + $by * $by); $bx /= $l; $by /= $l
        $d = $round / [Math]::Sin($theta / 2)                 # corner → arc centre, along the bisector
        $ccx = $p[0] + $bx * $d; $ccy = $p[1] + $by * $d
        $t = $round / [Math]::Tan($theta / 2)                 # corner → tangent points, along each edge
        $a1 = [Math]::Atan2($p[1] + $u1y * $t - $ccy, $p[0] + $u1x * $t - $ccx)
        $a2 = [Math]::Atan2($p[1] + $u2y * $t - $ccy, $p[0] + $u2x * $t - $ccx)
        $sweep = $a2 - $a1
        while ($sweep -gt [Math]::PI) { $sweep -= 2 * [Math]::PI }
        while ($sweep -lt -[Math]::PI) { $sweep += 2 * [Math]::PI }
        for ($j = 0; $j -le 24; $j++) {
            $ang = $a1 + $sweep * $j / 24
            $pts.Add((New-Object Drawing.PointF ([float]($ccx + $round * [Math]::Cos($ang))), ([float]($ccy + $round * [Math]::Sin($ang)))))
        }
    }
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon($pts.ToArray())
    $path
}

function New-RoundRect([double]$x, [double]$y, [double]$w, [double]$radius) {
    $d = 2 * $radius
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function Render([int]$size) {
    $k = 8; $big = $size * $k
    $bmp = New-Object Drawing.Bitmap $big, $big, ([Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)

    # Small sizes use the whole canvas; larger ones keep the usual icon margin.
    $margin = if ($size -le 24) { 0 } else { $big * 0.04 }
    $side = $big - 2 * $margin
    $square = New-RoundRect $margin $margin $side ($side * 0.22)
    $g.FillPath((New-Object Drawing.SolidBrush $BackColor), $square)

    # Star: straight edges, all corners rounded alike.
    $starPath = New-StarPath ($big / 2) ($big / 2) ($side * 0.40) ($side * 0.145) ($side * 0.033)
    $g.FillPath((New-Object Drawing.SolidBrush $StarColor), $starPath)
    $g.Dispose()

    $small = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($bmp, (New-Object Drawing.Rectangle 0, 0, $size, $size))
    $g.Dispose(); $bmp.Dispose()
    $ms = New-Object IO.MemoryStream
    if ($size -ge 256) {
        $small.Save($ms, [Drawing.Imaging.ImageFormat]::Png) # 256 px: PNG, as Windows expects
    }
    else {
        # Smaller sizes: a classic 32-bit DIB entry, which every icon API reads.
        $bw = New-Object IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]($size * $size * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $small.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $size))) # AND mask unused with alpha
        $bw.Flush()
    }
    if ($size -eq 256) { $small.Save((Join-Path $PSScriptRoot 'omnisound-256.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $small.Dispose()
    , $ms.ToArray()
}

$images = foreach ($s in $Sizes) { , (Render $s) }
$fs = [IO.File]::Create($Out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$Sizes.Count)
$offset = 6 + 16 * $Sizes.Count
for ($i = 0; $i -lt $Sizes.Count; $i++) {
    $s = $Sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"wrote $Out"

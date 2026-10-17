# Renders SoundDeck.ico: the Segoe Fluent "Volume" glyph, white on an accent-blue rounded square,
# as a multi-size PNG-based .ico. (The tray icon is drawn at runtime by TrayIconRenderer.)
param([string]$OutDir = "$PSScriptRoot\..\src\SoundDeck.App\Assets")

Add-Type -AssemblyName System.Drawing
$glyph = [string][char]0xE995   # Volume3
$fontName = if ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [Math]::Max(2, [int]($size * 0.22))
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2; $w = $size - 1
    $tile.AddArc(0, 0, $d, $d, 180, 90); $tile.AddArc($w - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $tile.AddArc(0, $w - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $size, $size), ([System.Drawing.Color]::FromArgb(255, 76, 194, 255)), ([System.Drawing.Color]::FromArgb(255, 0, 103, 192))
    $g.FillPath($brush, $tile)

    # Glyph as a path, scaled and centred on its real ink bounds (font metrics would push it off-centre).
    $em = [float]($size * 0.6)
    $family = New-Object System.Drawing.FontFamily $fontName
    $glyphPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glyphPath.AddString($glyph, $family, 0, $em, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $ink = $glyphPath.GetBounds()
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate([float](($size - $ink.Width) / 2 - $ink.X), [float](($size - $ink.Height) / 2 - $ink.Y))
    $glyphPath.Transform($m)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $glyphPath)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $frames = foreach ($s in $sizes) { , (New-Frame $s) }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $bw.Write($f) }
    $bw.Close()
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Write-Ico "$OutDir\SoundDeck.ico" (16, 20, 24, 32, 48, 64, 128, 256)
Get-ChildItem $OutDir *.ico | Select-Object Name, Length

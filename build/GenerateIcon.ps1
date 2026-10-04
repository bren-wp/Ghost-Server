param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$size = 256
$bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$navy = [System.Drawing.ColorTranslator]::FromHtml("#0B1E36")
$panel = [System.Drawing.ColorTranslator]::FromHtml("#132D52")
$blue = [System.Drawing.ColorTranslator]::FromHtml("#38ABFF")
$ice = [System.Drawing.ColorTranslator]::FromHtml("#EAF6FF")

$background = New-Object System.Drawing.SolidBrush $navy
$panelBrush = New-Object System.Drawing.SolidBrush $panel
$blueBrush = New-Object System.Drawing.SolidBrush $blue
$iceBrush = New-Object System.Drawing.SolidBrush $ice
$bluePen = New-Object System.Drawing.Pen $blue, 10
$linePen = New-Object System.Drawing.Pen $ice, 8

# Ghost family badge.
$graphics.FillEllipse($background, 12, 12, 232, 232)
$graphics.DrawEllipse($bluePen, 18, 18, 220, 220)

# Stylized three-tier server rack.
$graphics.FillRectangle($panelBrush, 58, 66, 140, 42)
$graphics.FillRectangle($panelBrush, 58, 112, 140, 42)
$graphics.FillRectangle($panelBrush, 58, 158, 140, 42)

foreach ($y in @(78, 124, 170)) {
    $graphics.FillEllipse($blueBrush, 72, $y, 14, 14)
    $graphics.DrawLine($linePen, 100, $y + 7, 177, $y + 7)
}

# Small connection mark.
$graphics.FillEllipse($blueBrush, 190, 38, 22, 22)
$graphics.DrawLine($bluePen, 188, 59, 174, 72)

$hIcon = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)
$stream = [System.IO.File]::Create($OutputPath)
try {
    $icon.Save($stream)
}
finally {
    $stream.Dispose()
    $icon.Dispose()
    $graphics.Dispose()
    $background.Dispose()
    $panelBrush.Dispose()
    $blueBrush.Dispose()
    $iceBrush.Dispose()
    $bluePen.Dispose()
    $linePen.Dispose()
    $bitmap.Dispose()
}

Write-Host "Generated Ghost Server icon: $OutputPath"

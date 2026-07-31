<#
.SYNOPSIS
  Draw the Plugboard toggle-switch icon and write it as a PNG-in-ICO to
  src/Plugboard.Host/plugboard.ico. Run once (or after a design change); the .ico is
  committed, so a normal build does not need this.
#>
param(
  [string]$Out = "$PSScriptRoot\..\src\Plugboard.Host\plugboard.ico"
)

Add-Type -AssemblyName System.Drawing

$sz  = 64
$bmp = New-Object System.Drawing.Bitmap($sz, $sz)
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

# dark rounded background
$bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 41, 59))
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 14
$path.AddArc(0, 0, $r, $r, 180, 90)
$path.AddArc($sz - $r, 0, $r, $r, 270, 90)
$path.AddArc($sz - $r, $sz - $r, $r, $r, 0, 90)
$path.AddArc(0, $sz - $r, $r, $r, 90, 90)
$path.CloseFigure()
$g.FillPath($bgBrush, $path)

# green toggle track (a pill) - "on"
$green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, 197, 94))
$g.FillEllipse($green, 10, 22, 20, 20)
$g.FillEllipse($green, 34, 22, 20, 20)
$g.FillRectangle($green, 20, 22, 24, 20)

# white knob on the right (switched on)
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$g.FillEllipse($white, 37, 25, 14, 14)

$g.Dispose()

# encode as PNG, then wrap in a single-image ICO (PNG-in-ICO, Vista+)
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$bmp.Dispose()

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type = icon
$bw.Write([UInt16]1)              # image count
$bw.Write([Byte]$sz)              # width
$bw.Write([Byte]$sz)              # height
$bw.Write([Byte]0)                # palette
$bw.Write([Byte]0)                # reserved
$bw.Write([UInt16]1)              # color planes
$bw.Write([UInt16]32)             # bits per pixel
$bw.Write([UInt32]$png.Length)    # image size
$bw.Write([UInt32]22)             # offset (6 + 16)
$bw.Write($png)
$bw.Flush(); $fs.Close()

Write-Host "Wrote $Out ($($png.Length) bytes PNG)"

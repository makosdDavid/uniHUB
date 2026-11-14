# USB Mapper - fordítás a Windowsba épített C# fordítóval (.NET Framework 4.8, C# 5).
#   .\build.ps1             fordít, majd lefuttatja az öntesztet (billentyűt nem küld)
#   .\build.ps1 -Shortcut   ezen felül elkészíti az asztali parancsikont
param([switch]$Shortcut, [switch]$NoTest)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$exe = Join-Path $root 'UsbMapper.exe'
$ico = Join-Path $root 'app.ico'

if (Get-Process -Name UsbMapper -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'A USB Mapper fut. Lépj ki belőle (tálcaikon → Kilépés), és futtasd újra.'
}

function New-Icon([string]$path) {
    Add-Type -AssemblyName System.Drawing
    $pngs = @()
    foreach ($s in 256, 48, 32, 16) {
        $bmp = New-Object Drawing.Bitmap $s, $s
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([Drawing.Color]::Transparent)
        $k = $s / 256.0
        $r = 56 * $k
        $bg = New-Object Drawing.Drawing2D.GraphicsPath
        $bg.AddArc(8 * $k, 8 * $k, $r, $r, 180, 90); $bg.AddArc(248 * $k - $r, 8 * $k, $r, $r, 270, 90)
        $bg.AddArc(248 * $k - $r, 248 * $k - $r, $r, $r, 0, 90); $bg.AddArc(8 * $k, 248 * $k - $r, $r, $r, 90, 90); $bg.CloseFigure()
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(217, 98, 15))), $bg)
        $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
        $g.FillRectangle($white, [float](58 * $k), [float](176 * $k), [float](140 * $k), [float](40 * $k))
        $g.FillEllipse($white, [float](48 * $k), [float](176 * $k), [float](40 * $k), [float](40 * $k))
        $g.FillEllipse($white, [float](168 * $k), [float](176 * $k), [float](40 * $k), [float](40 * $k))
        $g.FillRectangle($white, [float](116 * $k), [float](92 * $k), [float](24 * $k), [float](90 * $k))
        $g.FillEllipse($white, [float](88 * $k), [float](40 * $k), [float](80 * $k), [float](80 * $k))
        $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(217, 98, 15))), [float](114 * $k), [float](58 * $k), [float](28 * $k), [float](28 * $k))
        $g.Dispose()
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngs += , @($s, $ms.ToArray())
    }
    $out = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
        $dim = if ($p[0] -ge 256) { 0 } else { $p[0] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$p[1].Length); $w.Write([uint32]$offset)
        $offset += $p[1].Length
    }
    foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $out.ToArray())
}

if (-not (Test-Path $ico)) { New-Icon $ico; Write-Host 'Ikon elkészült: app.ico' }

$sources = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName })
$argsList = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', '/warnaserror+',
    "/win32icon:$ico", "/out:$exe",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', '/r:System.Management.dll') + $sources
& $csc @argsList
if ($LASTEXITCODE -ne 0) { throw "A fordítás nem sikerült (csc kilépési kód: $LASTEXITCODE)." }
Write-Host "Lefordítva: $exe"

if (-not $NoTest) {
    $p = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru
    Get-Content (Join-Path $root 'selftest.log') -Encoding UTF8
    if ($p.ExitCode -ne 0) { throw "Az önteszt hibát jelzett (kilépési kód: $($p.ExitCode))." }
}

if ($Shortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desktop 'USB Mapper.lnk'
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $root
    $s.IconLocation = "$exe,0"
    $s.Description = 'USB Mapper - játékvezérlő gombjai billentyűre, játékonkénti profillal'
    $s.Save()
    Write-Host "Asztali parancsikon: $lnk"
}

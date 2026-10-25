# Builds Imago into .\dist: hardened native core (C), app (C#), icon, checks.
# Usage:  pwsh -File build.ps1            (needs .NET 10 Desktop Runtime to run)
#         pwsh -File build.ps1 -SelfContained   (bundles the runtime, bigger exe)
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root $(if ($SelfContained) { 'dist-standalone' } else { 'dist' })
$app  = Join-Path $root 'app'

function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }

# 1. Toolchain
$llvm = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\MartinStorsjo.LLVM-MinGW*\llvm-mingw-*\bin" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $llvm -and -not (Get-Command clang -ErrorAction SilentlyContinue)) { throw 'clang not found. Install with: winget install MartinStorsjo.LLVM-MinGW.UCRT' }
if ($llvm) { $env:PATH = "$($llvm.FullName);$env:PATH" }

# 2. Native core: CFG, ASLR (high entropy), DEP, stack protector, fortify, static CRT.
Step 'Native core (C)'
New-Item -ItemType Directory -Force (Join-Path $root 'build') | Out-Null
$dll = Join-Path $root 'build\snapcore.dll'
$cflags = @('-std=c11', '-O2', '-Wall', '-Wextra', '-Werror', '-D_FORTIFY_SOURCE=2', '-fstack-protector-strong',
            '-mguard=cf', '-shared', '-static', '-Wl,--dynamicbase,--nxcompat,--high-entropy-va',
            (Join-Path $root 'native\snapcore.c'), '-o', $dll, '-ldwmapi', '-lgdi32', '-lshcore')
& clang @cflags
if ($LASTEXITCODE) { throw 'clang failed' }

# 3. Icon: the blue line camera mark on a white tile, PNG frames in one .ico.
Step 'Icon'
$ico = Join-Path $app 'Imago.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    # Rounded rectangle, same geometry as the SVG rect with rx.
    function Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
        $d = 2 * $r
        $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure()
        $p
    }
    $blue = [System.Drawing.Color]::FromArgb(47, 111, 235)
    $frames = foreach ($s in 16, 24, 32, 48, 256) {
        $bmp = [System.Drawing.Bitmap]::new($s, $s)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::White), (Rounded 0 0 $s $s ([single]($s * 0.22))))
        # Draw in the 24-unit icon space. Thin strokes get a minimum width so they stay visible at 16 px.
        $k = [single]($s / 24)
        $g.ScaleTransform($k, $k)
        $lw = [single][Math]::Max(1.0, 1.5 * 24 / $s)
        $pen = [System.Drawing.Pen]::new($blue, $lw)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        $g.DrawPath($pen, (Rounded 2.5 2.5 19 19 4.5))
        # Camera: the SVG group is translated 4.8, scaled 0.6, with stroke width 1.6.
        $state = $g.Save()
        $g.MultiplyTransform([System.Drawing.Drawing2D.Matrix]::new(0.6, 0, 0, 0.6, 4.8, 4.8))
        $cam = [System.Drawing.Pen]::new($blue, [single]($lw * 1.6))
        $cam.StartCap = 'Round'; $cam.EndCap = 'Round'; $cam.LineJoin = 'Round'
        $g.DrawPath($cam, (Rounded 0 2 24 14 2))
        $g.DrawPath($cam, (Rounded 0 16.5 24 5.5 1))
        $g.DrawEllipse($cam, [single]7.5, [single]4.5, [single]9, [single]9)
        $g.DrawEllipse($cam, [single]10, [single]7, [single]4, [single]4)
        $g.DrawPath($cam, (Rounded 17.8 3.8 4.4 4.4 0.5))
        $g.DrawEllipse($cam, [single]2.2, [single]8.5, [single]3.2, [single]3.2)
        $g.Restore($state)
        $g.Dispose()
        $ms = [System.IO.MemoryStream]::new(); $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        , @($s, $ms.ToArray())
    }
    $out = [System.IO.MemoryStream]::new(); $w = [System.IO.BinaryWriter]::new($out)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $sz = if ($f[0] -ge 256) { 0 } else { $f[0] }
        $w.Write([byte]$sz); $w.Write([byte]$sz); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$f[1].Length); $w.Write([uint32]$offset)
        $offset += $f[1].Length
    }
    foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
    $w.Flush(); [System.IO.File]::WriteAllBytes($ico, $out.ToArray())
}

# 4. App (C#): single file, CET shadow stack compatible, warnings are errors.
Step 'App (C#)'
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $app 'Imago.csproj') -c Release -o $dist -nologo -v q -p:SelfContained=$sc -p:EnableCompressionInSingleFile=$sc -p:IncludeNativeLibrariesForSelfExtract=false
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
Copy-Item $dll $dist
Copy-Item (Join-Path $app 'Fonts\OFL.txt') (Join-Path $dist 'Lato-OFL.txt')
# ONNX Runtime (MIT) runs the optional on-device AI check; its DLLs come from the NuGet package.
$ort = (Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.ml.onnxruntime') -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1).FullName
Copy-Item (Join-Path $ort 'LICENSE') (Join-Path $dist 'onnxruntime-LICENSE.txt')
foreach ($f in 'onnxruntime.dll', 'onnxruntime_providers_shared.dll') { if (-not (Test-Path (Join-Path $dist $f))) { throw "$f missing from publish" } }
Get-ChildItem $dist -Include *.pdb, *.lib -Recurse | Remove-Item

# 5. Check the security flags actually landed in both binaries.
Step 'Binary hardening check'
$need = @{ 'snapcore.dll' = @('HIGH_ENTROPY_VA', 'DYNAMIC_BASE', 'NX_COMPAT', 'GUARD_CF'); 'Imago.exe' = @('HIGH_ENTROPY_VA', 'DYNAMIC_BASE', 'NX_COMPAT', 'GUARD_CF')
          'onnxruntime.dll' = @('HIGH_ENTROPY_VA', 'DYNAMIC_BASE', 'NX_COMPAT', 'GUARD_CF'); 'onnxruntime_providers_shared.dll' = @('HIGH_ENTROPY_VA', 'DYNAMIC_BASE', 'NX_COMPAT', 'GUARD_CF') }
$bad = 0
foreach ($k in $need.Keys) {
    $hdr = (& llvm-objdump -p (Join-Path $dist $k)) -join "`n"
    foreach ($flag in $need[$k]) {
        $ok = $hdr -match "(?m)^\s+$flag\s*$"
        Write-Host ("  {0,-34} {1,-16} {2}" -f $k, $flag, $(if ($ok) { 'ok' } else { 'MISSING' }))
        if (-not $ok) { $bad++ }
    }
}
if ($bad) { throw "$bad hardening flags missing" }

# 6. Built-in tests against the published exe.
Step 'Self-test'
& (Join-Path $dist 'Imago.exe') --selftest | Out-String | Write-Host
if ($LASTEXITCODE) { throw 'self-test failed' }

# 7. Checksums and a Defender scan of everything shipped.
Step 'Checksums'
Get-ChildItem $dist -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Get-Content (Join-Path $dist 'SHA256SUMS.txt') | Write-Host

Step 'Microsoft Defender scan'
$mp = "$env:ProgramFiles\Windows Defender\MpCmdRun.exe"
if (Test-Path $mp) {
    & $mp -Scan -ScanType 3 -File $dist -DisableRemediation | Out-String | Write-Host
    if ($LASTEXITCODE -eq 2) { throw 'Defender flagged a file' }
} else { Write-Host '  Defender command line not found, skipped' }

# 8. Installer (Inno Setup). Skipped when Inno Setup is not installed.
Step 'Installer'
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    $suffix = if ($SelfContained) { '-standalone' } else { '' }
    & $iscc /Q "/DDistDir=$dist" "/DSuffix=$suffix" (Join-Path $root 'installer\imago.iss') | Out-String | Write-Host
    if ($LASTEXITCODE) { throw 'Inno Setup failed' }
    Write-Host "  $(Join-Path $root "installer\out\Imago-Setup-1.0.0$suffix.exe")"
} else { Write-Host '  Inno Setup not found, installer skipped (winget install JRSoftware.InnoSetup)' }

Step "Done: $dist"

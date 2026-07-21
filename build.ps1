# Builds LoadView.exe using the in-box .NET Framework C# compiler (no SDK required).
# Produces a single native exe under .\bin that runs on any Windows 10/11 machine.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$bin = Join-Path $root 'bin'
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

$out      = Join-Path $bin 'LoadView.exe'
$manifest = Join-Path $root 'app.manifest'
$icon     = Join-Path $root 'LoadView.ico'
$sources  = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'        # deterministic PDH struct layout
    '/optimize+'
    '/codepage:65001'      # read sources as UTF-8
    "/out:$out"
    "/win32manifest:$manifest"
    '/r:System.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Management.dll'
    '/r:System.IO.Compression.dll'          # unzip the optional temp-driver download
    '/r:System.IO.Compression.FileSystem.dll'
)
if (Test-Path $icon) { $cscArgs += "/win32icon:$icon" }
$cscArgs += $sources

Write-Host "Compiling $($sources.Count) source file(s)..."
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

Write-Host "Built $out"

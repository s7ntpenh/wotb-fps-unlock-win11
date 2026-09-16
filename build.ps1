# Builds both tools with the C# compiler that ships with Windows.
# Nothing to install - no .NET SDK, no Visual Studio.
#
#   bin\WotbFpsUnlock.exe   the GUI
#   bin\dvpl.exe            standalone .dvpl codec, handy for poking at game configs

$ErrorActionPreference = 'Stop'

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "No .NET Framework C# compiler under $env:WINDIR\Microsoft.NET." }

$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

# --- GUI ---------------------------------------------------------------------
$gui = Join-Path $bin 'WotbFpsUnlock.exe'
$sources = @('Program.cs', 'MainForm.cs', 'Theme.cs', 'FpsSlider.cs', 'Patcher.cs') |
    ForEach-Object { Join-Path $PSScriptRoot "src\$_" }
$ico = Join-Path $PSScriptRoot 'src\app.ico'
$png = Join-Path $PSScriptRoot 'src\app128.png'

& $csc /nologo /optimize+ /target:winexe /platform:anycpu `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "/win32icon:$ico" "/resource:$ico,app.ico" "/resource:$png,app128.png" `
    "/out:$gui" $sources
if ($LASTEXITCODE -ne 0) { throw "GUI build failed." }
Write-Host "built -> $gui" -ForegroundColor Green

# --- dvpl CLI ----------------------------------------------------------------
$dvpl = Join-Path $bin 'dvpl.exe'
& $csc /nologo /optimize+ /platform:anycpu "/out:$dvpl" (Join-Path $PSScriptRoot 'src\Dvpl.cs')
if ($LASTEXITCODE -ne 0) { throw "dvpl build failed." }
Write-Host "built -> $dvpl" -ForegroundColor Green

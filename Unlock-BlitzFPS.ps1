<#
.SYNOPSIS
    Raises the 120 FPS ceiling in World of Tanks Blitz (Steam, app 444200).

.DESCRIPTION
    Blitz exposes exactly three frame-rate options (30 / 60 / 120). They come from
    DAVA::eFPSLimit, an enum whose members are 0/1/2; the numbers live in
    GraphicsOptionsApplier::SwitchT<int, eFPSLimit>::operator int, which the
    compiler emitted as three `lea edi, [eax + imm8]` instructions.

    This script rewrites the third case so the "120" menu entry yields any value
    you ask for, and relabels the menu text to match. Everything it touches is
    backed up first and can be undone with -Restore.

.EXAMPLE
    .\Unlock-BlitzFPS.ps1 -TargetFps 240
.EXAMPLE
    .\Unlock-BlitzFPS.ps1 -Status
.EXAMPLE
    .\Unlock-BlitzFPS.ps1 -Restore
#>
[CmdletBinding(DefaultParameterSetName = 'Apply')]
param(
    [Parameter(ParameterSetName = 'Apply')]
    [ValidateRange(30, 2000)]
    [int]$TargetFps = 240,

    # Off = force VSync off, On = restore the stock read, Leave = don't touch it.
    [Parameter(ParameterSetName = 'Apply')]
    [ValidateSet('Leave', 'Off', 'On')]
    [string]$VSync = 'Leave',

    # On = drop the frame-time quantisation that makes the camera syrupy above
    # ~200 FPS, Off = put the stock code back, Leave = don't touch it.
    [Parameter(ParameterSetName = 'Apply')]
    [ValidateSet('On', 'Off', 'Leave')]
    [string]$CameraFix = 'On',

    [Parameter(ParameterSetName = 'Restore')]
    [switch]$Restore,

    [Parameter(ParameterSetName = 'Status')]
    [switch]$Status,

    [string]$GamePath
)

$ErrorActionPreference = 'Stop'
$AppId = '444200'

# ---------------------------------------------------------------- signatures --
# Offsets are relative to the start of the match.
#   +33  8D 78 76        lea edi, [eax + 118]   ; case 2 -> 2 + 118 = 120
#   +36  E9 <rel32>      jmp  <epilogue>
# The replacement keeps the same 8 bytes:
#   +33  BF <imm32>      mov edi, <TargetFps>
#   +38  EB EF           jmp  short (case 1's jmp, which reaches the same epilogue)
#   +40  90              nop
$SigOriginal = @(
    0x85,0xC0, 0x75,0x08, 0x8D,0x78,0x1E, 0xE9,-1,-1,-1,-1,
    0x8B,0x7D,-1, 0x83,0xF8,0x01, 0x75,0x08, 0x8D,0x78,0x3B, 0xE9,-1,-1,-1,-1,
    0x83,0xF8,0x02, 0x75,0x08, 0x8D,0x78,0x76, 0xE9,-1,-1,-1,-1
)
$SigPatched = @(
    0x85,0xC0, 0x75,0x08, 0x8D,0x78,0x1E, 0xE9,-1,-1,-1,-1,
    0x8B,0x7D,-1, 0x83,0xF8,0x01, 0x75,0x08, 0x8D,0x78,0x3B, 0xE9,-1,-1,-1,-1,
    0x83,0xF8,0x02, 0x75,0x08, 0xBF,-1,-1,-1,-1, 0xEB,0xEF, 0x90
)
$CaseBodyOffset = 33

# rhi::dx11_PresentBuffer builds the SyncInterval argument for
# IDXGISwapChain::Present out of bit 1 of a render flags word:
#
#   mov ecx, ds:[flags] ; shr ecx,1 ; and ecx,1   ; ecx = vsync ? 1 : 0
#   ...
#   push 0 ; push ecx ; push edx ; call [eax+20h] ; Present(SyncInterval, Flags)
#
# Turning `and ecx,1` into `and ecx,0` forces SyncInterval to 0, so Present never waits
# for vblank. One byte, no instruction lengths change, and the flags `and` leaves behind
# are overwritten by the `test edx,edx` that follows before anything branches on them.
#
# (An earlier attempt patched GraphicsOptionsApplier::Apply instead - that only changed
# the option the engine had already consumed, and the swapchain kept syncing.)
$SigVSyncOn = @(
    0x8B,0x0D,-1,-1,-1,-1,    # mov  ecx,ds:[renderFlags]
    0x8B,0x15,-1,-1,-1,-1,    # mov  edx,ds:[swapChain]
    0xD1,0xE9,                # shr  ecx,1
    0x83,0xE1,0x01,           # and  ecx,1        <- byte at +16
    0x85,0xD2,                # test edx,edx
    0x74,-1,                  # je   (no swapchain)
    0x8B,0x02                 # mov  eax,[edx]
)
$SigVSyncOff = @(
    0x8B,0x0D,-1,-1,-1,-1,
    0x8B,0x15,-1,-1,-1,-1,
    0xD1,0xE9,
    0x83,0xE1,0x00,
    0x85,0xD2,
    0x74,-1,
    0x8B,0x02
)
$VSyncByteOffset = 16

# The per-camera update called from GameCameraUpdateSystem::Process derives its
# exponential-smoothing factor from a 7-frame moving average of frame time:
#
#     f = clamp(roundf(avgFrameTime * 100.0f) / 100.0f * coef, 0.01f, 1.0f)
#
# That middle step quantises frame time to 10 ms. Above ~200 FPS the average frame
# time is under 5 ms, roundf returns 0, and f collapses onto the 0.01 floor - which
# is why the camera feels identical (and syrupy) at 240 and at 1000 FPS.
#
# The fix repoints the two operands from the 100.0f constant to a 100000.0f one that
# already exists in .rdata, making the quantum 0.01 ms instead of 10 ms. Only those
# two 4-byte addresses change: every instruction, call and stack adjustment stays
# exactly as the compiler emitted it.
$SigCamQuant = @(
    0xF3,0x0F,0x10,0x45,0x0C,            # movss xmm0,[ebp+0Ch]
    0xF3,0x0F,0x59,0x05,-1,-1,-1,-1,     # mulss xmm0,[K]        <- address at +9
    0x51,                                # push ecx
    0xC6,0x45,0xFF,0x00,                 # mov byte ptr [ebp-1],0
    0xF3,0x0F,0x11,0x45,0xF8,            # movss [ebp-8],xmm0
    0xD9,0x45,0xF8,                      # fld  [ebp-8]
    0xD9,0x1C,0x24,                      # fstp [esp]
    0xE8,-1,-1,-1,-1,                    # call roundf
    0xD9,0x5D,0xF8,                      # fstp [ebp-8]
    0xF3,0x0F,0x10,0x45,0xF8,            # movss xmm0,[ebp-8]
    0x83,0xC4,0x04,                      # add  esp,4
    0xF3,0x0F,0x5E,0x05,-1,-1,-1,-1      # divss xmm0,[K]        <- address at +49
)
$CamConstSlots = @(9, 49)
$FineQuantBytes = @(0x00, 0x50, 0xC3, 0x47)   # 100000.0f

# ---------------------------------------------------------------- utilities ---
function Write-Step { param($m) Write-Host "  $m" }
function Write-Ok   { param($m) Write-Host "  [ok] $m"   -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  [!]  $m"   -ForegroundColor Yellow }

function Find-GamePath {
    if ($GamePath) {
        if (-not (Test-Path (Join-Path $GamePath 'wotblitz.exe'))) {
            throw "No wotblitz.exe under -GamePath '$GamePath'."
        }
        return (Resolve-Path $GamePath).Path
    }

    $steam = $null
    try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamPath).SteamPath } catch { }
    if (-not $steam) { $steam = "${env:ProgramFiles(x86)}\Steam" }
    $steam = $steam -replace '/', '\'

    $roots = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $roots += $m.Groups[1].Value -replace '\\\\', '\'
        }
    }

    foreach ($root in ($roots | Select-Object -Unique)) {
        $acf = Join-Path $root "steamapps\appmanifest_$AppId.acf"
        $dir = $null
        if (Test-Path $acf) {
            $m = [regex]::Match((Get-Content $acf -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $dir = $m.Groups[1].Value }
        }
        if (-not $dir) { $dir = 'World of Tanks Blitz' }
        $candidate = Join-Path $root "steamapps\common\$dir"
        if (Test-Path (Join-Path $candidate 'wotblitz.exe')) { return $candidate }
    }
    throw "World of Tanks Blitz not found in any Steam library. Pass -GamePath explicitly."
}

function Find-Pattern {
    param([byte[]]$Buffer, [int[]]$Pattern, [int]$MaxHits = 4)
    # comma keeps PowerShell from unrolling a single-element result into a scalar
    return , [BlitzSig]::Find($Buffer, $Pattern, $MaxHits)
}

function Get-ExeState {
    param([string]$ExePath)
    $bytes = [IO.File]::ReadAllBytes($ExePath)
    $orig = Find-Pattern -Buffer $bytes -Pattern $SigOriginal
    if ($orig.Count -eq 1) {
        return [pscustomobject]@{ Kind = 'Original'; Offset = $orig[0]; Fps = 120; Bytes = $bytes }
    }
    $done = Find-Pattern -Buffer $bytes -Pattern $SigPatched
    if ($done.Count -eq 1) {
        $fps = [BitConverter]::ToInt32($bytes, $done[0] + $CaseBodyOffset + 1)
        return [pscustomobject]@{ Kind = 'Patched'; Offset = $done[0]; Fps = $fps; Bytes = $bytes }
    }
    $kind = if ($orig.Count -gt 1 -or $done.Count -gt 1) { 'Ambiguous' } else { 'Unknown' }
    return [pscustomobject]@{ Kind = $kind; Offset = -1; Fps = 0; Bytes = $bytes }
}

function Get-ImageBaseAndSections {
    param([byte[]]$B)
    $pe = [BitConverter]::ToInt32($B, 0x3C)
    $nSec = [BitConverter]::ToUInt16($B, $pe + 6)
    $optSize = [BitConverter]::ToUInt16($B, $pe + 20)
    $opt = $pe + 24
    $base = [BitConverter]::ToUInt32($B, $opt + 28)
    $tab = $opt + $optSize
    $secs = @()
    for ($i = 0; $i -lt $nSec; $i++) {
        $o = $tab + $i * 40
        $secs += [pscustomobject]@{
            VA   = [BitConverter]::ToUInt32($B, $o + 12)
            Raw  = [BitConverter]::ToUInt32($B, $o + 20)
            RSz  = [BitConverter]::ToUInt32($B, $o + 16)
        }
    }
    return [pscustomobject]@{ Base = $base; Sections = $secs }
}

# File offset -> virtual address, or 0 if the offset is outside every section.
function ConvertTo-Va {
    param([byte[]]$B, [int]$Offset)
    $pe = Get-ImageBaseAndSections -B $B
    foreach ($s in $pe.Sections) {
        if ($Offset -ge $s.Raw -and $Offset -lt $s.Raw + $s.RSz) {
            return [uint32]($pe.Base + $s.VA + ($Offset - $s.Raw))
        }
    }
    return [uint32]0
}

# Address of the 100000.0f constant the fix repoints to. It must be unique so we
# cannot accidentally aim at something a different build happens to keep there.
function Get-FineQuantVa {
    param([byte[]]$B)
    $hits = Find-Pattern -Buffer $B -Pattern $FineQuantBytes -MaxHits 3
    $aligned = @($hits | Where-Object { (ConvertTo-Va -B $B -Offset $_) % 4 -eq 0 })
    if ($aligned.Count -ne 1) { return [uint32]0 }
    return ConvertTo-Va -B $B -Offset $aligned[0]
}

function Get-CameraState {
    param([byte[]]$Bytes)
    $q = Find-Pattern -Buffer $Bytes -Pattern $SigCamQuant -MaxHits 2
    if ($q.Count -ne 1) { return [pscustomobject]@{ Kind = 'Unknown'; Offset = -1; ConstVa = 0 } }
    $va = [BitConverter]::ToUInt32($Bytes, $q[0] + $CamConstSlots[0])
    $fine = Get-FineQuantVa -B $Bytes
    $kind = if ($fine -ne 0 -and $va -eq $fine) { 'Fixed' } else { 'Stock' }
    return [pscustomobject]@{ Kind = $kind; Offset = $q[0]; ConstVa = $va }
}

function Get-VSyncState {
    param([byte[]]$Bytes)
    $on = Find-Pattern -Buffer $Bytes -Pattern $SigVSyncOn -MaxHits 2
    if ($on.Count -eq 1) { return [pscustomobject]@{ Kind = 'Stock'; Offset = $on[0] } }
    $off = Find-Pattern -Buffer $Bytes -Pattern $SigVSyncOff -MaxHits 2
    if ($off.Count -eq 1) { return [pscustomobject]@{ Kind = 'ForcedOff'; Offset = $off[0] } }
    return [pscustomobject]@{ Kind = 'Unknown'; Offset = -1 }
}

function Assert-GameClosed {
    if (Get-Process -Name 'wotblitz' -ErrorAction SilentlyContinue) {
        throw "World of Tanks Blitz is running. Close it (and the Steam overlay) first."
    }
}

# ------------------------------------------------------------------ commands --
function Invoke-Status {
    param([string]$Root)
    $exe = Join-Path $Root 'wotblitz.exe'
    $state = Get-ExeState -ExePath $exe
    Write-Host ""
    Write-Host "World of Tanks Blitz - FPS unlock status" -ForegroundColor Cyan
    Write-Step "install : $Root"
    Write-Step "exe     : $([IO.Path]::GetFileName($exe)) ($([math]::Round((Get-Item $exe).Length/1MB,1)) MB)"
    switch ($state.Kind) {
        'Original'  { Write-Step "state   : stock - top menu option gives 120 FPS" }
        'Patched'   { Write-Ok   "state   : patched - top menu option gives $($state.Fps) FPS" }
        'Ambiguous' { Write-Warn "state   : signature matched more than once; not safe to patch" }
        default     { Write-Warn "state   : signature not found (game updated, or already modified another way)" }
    }
    $vs = Get-VSyncState -Bytes $state.Bytes
    switch ($vs.Kind) {
        'Stock'     { Write-Step "vsync   : stock - Present syncs to vblank, so refresh rate is the ceiling" }
        'ForcedOff' { Write-Ok   "vsync   : off - Present called with SyncInterval 0" }
        default     { Write-Warn "vsync   : site not recognised" }
    }
    $cam = Get-CameraState -Bytes $state.Bytes
    switch ($cam.Kind) {
        'Stock' { Write-Step "camera  : stock - smoothing quantised to 10 ms, goes syrupy above ~200 FPS" }
        'Fixed' { Write-Ok   "camera  : quantum 0.01 ms - smoothing tracks the real frame rate" }
        default { Write-Warn "camera  : site not recognised" }
    }
    $bak = Join-Path $Root 'BlitzFpsUnlock.backup\manifest.json'
    if (Test-Path $bak) {
        $m = Get-Content $bak -Raw | ConvertFrom-Json
        Write-Step "backup  : taken $($m.appliedUtc) UTC, original SHA256 $($m.originalSha256.Substring(0,16))..."
    } else {
        Write-Step "backup  : none"
    }
    Write-Host ""
}

function Invoke-Apply {
    param([string]$Root, [int]$Fps)

    Assert-GameClosed
    $exe = Join-Path $Root 'wotblitz.exe'
    $backupDir = Join-Path $Root 'BlitzFpsUnlock.backup'
    $state = Get-ExeState -ExePath $exe

    if ($state.Kind -eq 'Ambiguous') { throw "Signature matched more than once - refusing to patch." }
    if ($state.Kind -eq 'Unknown') {
        throw "Could not locate the FPS switch in wotblitz.exe. The game was probably updated; the signature needs refreshing."
    }
    $bytes = $state.Bytes
    $vs = Get-VSyncState -Bytes $bytes
    $cam = Get-CameraState -Bytes $bytes
    $wantVSync = $VSync -ne 'Leave' -and $vs.Kind -ne 'Unknown' -and
                 $vs.Kind -ne $(if ($VSync -eq 'Off') { 'ForcedOff' } else { 'Stock' })
    $wantCam = $CameraFix -ne 'Leave' -and $cam.Kind -ne 'Unknown' -and
               $cam.Kind -ne $(if ($CameraFix -eq 'On') { 'Fixed' } else { 'Stock' })

    if ($state.Kind -eq 'Patched' -and $state.Fps -eq $Fps -and -not $wantVSync -and -not $wantCam) {
        Write-Ok "Already patched to $Fps FPS (VSync: $($vs.Kind), camera: $($cam.Kind)) - nothing to do."
        return
    }

    $at = $state.Offset + $CaseBodyOffset

    New-Item -ItemType Directory -Force $backupDir | Out-Null
    $exeBackup = Join-Path $backupDir 'wotblitz.exe'
    if (-not (Test-Path $exeBackup)) {
        Write-Step "backing up wotblitz.exe ..."
        Copy-Item $exe $exeBackup
    }
    $originalSha = (Get-FileHash $exeBackup -Algorithm SHA256).Hash

    # mov edi, <Fps> ; jmp short (case 1 epilogue) ; nop
    $patch = @(0xBF) + [BitConverter]::GetBytes([int]$Fps) + @(0xEB, 0xEF, 0x90)
    for ($i = 0; $i -lt $patch.Length; $i++) { $bytes[$at + $i] = $patch[$i] }

    if ($VSync -ne 'Leave') {
        if ($vs.Kind -eq 'Unknown') {
            Write-Warn "VSync site not recognised - leaving it alone"
        } else {
            $vat = $vs.Offset + $VSyncByteOffset
            $bytes[$vat] = if ($VSync -eq 'Off') { 0x00 } else { 0x01 }
            $what = if ($VSync -eq 'Off') { "Present SyncInterval pinned to 0 (VSync off)" }
                    else { "Present SyncInterval follows the engine again" }
            Write-Ok "$what at 0x$('{0:X}' -f $vat)"
        }
    }

    if ($CameraFix -ne 'Leave') {
        if ($cam.Kind -eq 'Unknown') {
            Write-Warn "camera smoothing site not recognised - leaving it alone"
        } elseif ($CameraFix -eq 'On') {
            $fine = Get-FineQuantVa -B $bytes
            if ($fine -eq 0) {
                Write-Warn "no unique 100000.0f constant to point at - camera left alone"
            } else {
                $addr = [BitConverter]::GetBytes([uint32]$fine)
                foreach ($slot in $CamConstSlots) {
                    for ($i = 0; $i -lt 4; $i++) { $bytes[$cam.Offset + $slot + $i] = $addr[$i] }
                }
                Write-Ok ("camera quantum 10 ms -> 0.01 ms (operands at 0x{0:X} now point at 0x{1:X8})" -f $cam.Offset, $fine)
            }
        } else {
            $orig = [IO.File]::ReadAllBytes($exeBackup)
            foreach ($slot in $CamConstSlots) {
                for ($i = 0; $i -lt 4; $i++) { $bytes[$cam.Offset + $slot + $i] = $orig[$cam.Offset + $slot + $i] }
            }
            Write-Ok "camera smoothing restored to stock at 0x$('{0:X}' -f $cam.Offset)"
        }
    }

    [IO.File]::WriteAllBytes($exe, $bytes)
    Write-Ok "wotblitz.exe patched at 0x$('{0:X}' -f $at): top FPS option now returns $Fps"

    $manifest = [ordered]@{
        appliedUtc     = (Get-Date).ToUniversalTime().ToString('s')
        targetFps      = $Fps
        patchOffset    = $at
        vsync          = (Get-VSyncState -Bytes $bytes).Kind
        cameraFix      = (Get-CameraState -Bytes $bytes).Kind
        originalSha256 = $originalSha
        patchedSha256  = (Get-FileHash $exe -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json | Set-Content (Join-Path $backupDir 'manifest.json') -Encoding UTF8

    Write-Host ""
    Write-Host "Done. In game: Settings -> Graphics -> Frames per second -> pick the rightmost option." -ForegroundColor Cyan
    Write-Host "It is still labelled 120 (the label is the enum name, not a localized string) but now delivers $Fps."
}

function Invoke-Undo {
    param([string]$Root)
    Assert-GameClosed
    $backupDir = Join-Path $Root 'BlitzFpsUnlock.backup'
    if (-not (Test-Path $backupDir)) { throw "No backup found at $backupDir." }

    $exeBackup = Join-Path $backupDir 'wotblitz.exe'
    if (Test-Path $exeBackup) {
        Copy-Item $exeBackup (Join-Path $Root 'wotblitz.exe') -Force
        Write-Ok "wotblitz.exe restored"
    }
    foreach ($f in Get-ChildItem (Join-Path $backupDir 'Data') -Recurse -File -ErrorAction SilentlyContinue) {
        $rel = $f.FullName.Substring($backupDir.Length).TrimStart('\')
        Copy-Item $f.FullName (Join-Path $Root $rel) -Force
        Write-Ok "restored $rel"
    }
    Remove-Item (Join-Path $backupDir 'manifest.json') -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "Reverted to stock." -ForegroundColor Cyan
}

# ---------------------------------------------------------------------- main --
Add-Type -Path (Join-Path $PSScriptRoot 'src\Sig.cs')
$root = Find-GamePath

if ($Status)      { Invoke-Status -Root $root }
elseif ($Restore) { Invoke-Undo   -Root $root }
else              { Invoke-Apply  -Root $root -Fps $TargetFps }

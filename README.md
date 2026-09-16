<div align="center">

# 🎯 WoT Blitz FPS Unlocker

**Lifts the 120 FPS ceiling in World of Tanks Blitz — and fixes the camera bug that shows up once it's gone.**

[![Release](https://img.shields.io/github/v/release/s7ntpenh/wotb-fps-unlock-win11?style=flat-square&color=f0a030)](../../releases/latest)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-0078d4?style=flat-square)](#)
[![Steam](https://img.shields.io/badge/Steam-app%20444200-1b2838?style=flat-square)](https://store.steampowered.com/app/444200/)

![screenshot](docs/screenshot.png)

</div>

---

## 📖 Table of contents

- [🚀 Quick start](#-quick-start)
- [🔧 What it changes](#-what-it-changes)
- [🔬 How it works](#-how-it-works)
  - [1. The FPS ceiling](#1-the-fps-ceiling)
  - [2. VSync](#2-vsync)
  - [3. Camera smoothing](#3-camera-smoothing)
- [💻 Command line](#-command-line)
- [🛠 Building from source](#-building-from-source)
- [📦 What's in the repo](#-whats-in-the-repo)
- [⚠️ Things worth knowing](#️-things-worth-knowing)
- [🧩 Known limitations](#-known-limitations)
- [💛 Donate](#-donate)

---

## 🚀 Quick start

1. Grab **`WotbFpsUnlock.exe`** from [Releases](../../releases/latest) and run it.
2. Close the game — the patcher will not touch a running client.
3. Drag the slider to the frame rate you want and hit **Apply**.
4. In game: **Settings → Graphics → Frames per second → rightmost option**.

> [!NOTE]
> That option still reads **120**. It is not a bug: the label comes from the enum's
> *name*, not from a localization string ([details below](#6-the-menu-label)). It
> delivers whatever you set.

**Restore original** puts everything back in one click.

Nothing to install — the app targets .NET Framework, which ships with every Windows.
If your Steam library lives under `Program Files`, the tool offers to restart itself
elevated.

---

## 🔧 What it changes

Exactly **13 bytes**, all inside `wotblitz.exe`:

| Offset | Bytes | What |
|---|---|---|
| `0x2AC5A4` | 8 | `lea edi,[eax+0x76]` → `mov edi, <fps>` — the FPS ceiling |
| `0x9651B6` | 1 | `and ecx,1` → `and ecx,0` — Present's `SyncInterval` |
| `0x12098E3`, `0x120990B` | 2 × 4 | the quantisation constant the camera smoothing divides by |

The original executable is copied to `<game folder>\BlitzFpsUnlock.backup\` before the
first patch, and every site is located by **signature scan**, never by a hardcoded
offset. A rebuilt client either matches or is refused — it is never patched blindly.

Disabling VSync and fixing the camera are not optional. Without the first, your
monitor's refresh rate stays the real ceiling; without the second, the camera turns
syrupy the moment the ceiling is gone.

---

## 🔬 How it works

The in-game menu offers exactly three values — 30, 60 and 120. This is not a setting
you can edit in a config: the numbers are compiled in.

### The config format

Everything under `Data\` is packed into `.dvpl` containers — a DAVA engine format:
an LZ4-compressed payload plus a 20-byte footer
(`srcSize | compSize | crc32 | type | "DVPL"`). A standalone codec lives in
[`src/Dvpl.cs`](src/Dvpl.cs) and builds to `dvpl.exe`.

`Data\GraphicsPresets.yaml` does contain an `FPSLimit` field, but it only accepts
`30`/`60`/`120`. The old recipe floating around the internet — *"edit the yaml and get
144"* — does not work on current builds: the value goes through an enum and anything
else is rejected.

### 1. The FPS ceiling

`FPSLimit` is `enum DAVA::eFPSLimit`, and its members are **0, 1, 2**. The strings
`"30"`, `"60"`, `"120"` are merely names, visible in the enum registry at
`.text:0x49EC0D`:

```asm
push offset "30"  ; push 0 ; call Register
push offset "60"  ; push 1 ; call Register
push offset "120" ; push 2 ; call Register
```

The real numbers appear in `GraphicsOptionsApplier::SwitchT<int, eFPSLimit>::operator int`.
The client is 32-bit, and the compiler folded the switch into three `lea` instructions
that add a constant to the case index itself:

```asm
test eax, eax        ; case 0
lea  edi, [eax+0x1E] ;  0 + 30  = 30
cmp  eax, 1          ; case 1
lea  edi, [eax+0x3B] ;  1 + 59  = 60
cmp  eax, 2          ; case 2
lea  edi, [eax+0x76] ;  2 + 118 = 120   <-- the ceiling
```

That displacement is a **signed byte**, so the most you could reach by editing it is
`2 + 127 = 129`. A real 32-bit operand is needed, and it has to fit in the same 8 bytes:

```asm
; before:  8D 78 76           lea edi, [eax+0x76]
;          E9 <rel32>         jmp  epilogue
; after:   BF <imm32>         mov edi, <fps>
;          EB EF              jmp  short - onto case 1's jmp, which lands in the same place
;          90                 nop
```

Nothing shifts, no code cave is needed, and the `jne` that skips this block when
`eax != 2` still lands exactly where it did before.

### 2. VSync

Blitz has no VSync entry in its menu at all, and the log says `VSync: true` — it is
hard-enabled, which makes the refresh rate the true ceiling.

It is decided in `rhi::dx11_PresentBuffer`, where the `SyncInterval` argument for
`IDXGISwapChain::Present` gets built:

```asm
mov  ecx, ds:[4033B30h]   ; render flags word
mov  edx, ds:[40339ACh]   ; dx11.swapChain
shr  ecx, 1
and  ecx, 1               ; ecx = vsync ? 1 : 0   <-- here
test edx, edx
je   <no swapchain>
mov  eax, [edx]           ; vtable
push 0                    ; Flags
push ecx                  ; SyncInterval
push edx                  ; this
call dword ptr [eax+20h]  ; IDXGISwapChain::Present, vtable index 8
```

`and ecx,1` becomes `and ecx,0` — **one byte**, `01` → `00`. The instruction keeps its
length, control flow is untouched, and the flags the `and` leaves behind are overwritten
by the following `test edx, edx` before anything branches on them.

> An earlier attempt patched `GraphicsOptionsApplier::Apply` instead (the
> `GraphicsOptions::vsync` field at offset `0x50`, found via `push 0x50; push "VSync"`
> at `.text:0x493DDD`). It did nothing — the engine had already consumed the option and
> the swapchain kept syncing regardless. The pressure has to go on `Present` itself.

### 3. Camera smoothing

The menu's own hint — *"30 fps saves battery power and 120 fps improves camera
behavior"* — is a clue. There is a genuine camera bug, and it surfaces precisely when
the ceiling comes off.

`GameCameraUpdateSystem::Process` calls a per-camera update at `VA 0x160A4B0`, which
derives its exponential smoothing factor like this:

```c
float avgDt = MovingAverageFrameTime();        // mean of the last <= 7 frames
float f = roundf(avgDt * 100.0f) / 100.0f;     // quantised to 10 ms
f *= coef;                                      // [esi+0x354], ~9.0
f = clamp(f, 0.01f, 1.0f);
```

The quantum is **10 milliseconds**. Above ~200 FPS a frame is shorter than 5 ms,
`roundf` returns **zero**, and the factor drops onto its lower clamp of `0.01`:

| FPS | avgDt | `roundf(dt·100)` | factor | time constant |
|---|---|---|---|---|
| 30 | 33 ms | 3 | 0.27 | 0.12 s |
| 60 | 17 ms | 2 | 0.18 | 0.09 s |
| 120 | 8.3 ms | 1 | 0.09 | 0.09 s |
| 240 | 4.2 ms | **0** | 0.01 *(clamped)* | **0.42 s** |
| 1000 | 1.0 ms | **0** | 0.01 *(clamped)* | **0.42 s** |

Hence the "cinematic" feel — the camera becomes four times as sluggish. And it explains
why 240 and 1000 FPS felt *identical*: both bottom out on the same clamp.

The fix touches no instruction at all. It **repoints both operands** from the `100.0f`
constant to a `100000.0f` one that already exists in `.rdata` (single occurrence,
4-byte aligned):

```asm
mulss xmm0,ds:[0362E054]   ->   mulss xmm0,ds:[0369507C]
divss xmm0,ds:[0362E054]   ->   divss xmm0,ds:[0369507C]
```

Two 4-byte addresses. The quantum becomes 0.01 ms instead of 10 ms, so `roundf` stops
flushing to zero all the way up to 100,000 FPS, and the time constant settles at a
frame-rate-independent 0.11 s. As a bonus, the 30/60/120 stepping disappears too.

> **Why repoint rather than delete the quantisation.** The first version of this patch
> replaced the whole 53-byte block with `movss` plus `nop` fill (`push ecx` and
> `add esp,4` only framed `roundf`'s argument, so they cancelled and the stack stayed
> balanced). The arithmetic was right, but the game crashed on entering battle with an
> access violation, and disassembly never explained why. So the approach was swapped for
> the minimal one: control flow, calls and stack handling stay exactly as the compiler
> emitted them.

### 6. The menu label

The localization keys `#settings:Graphics/FPSLimit/30|60|120` exist in
`Data\Strings\*.yaml.dvpl`, but they do not drive the buttons — verified by editing
them: the option's title and description from the same file render fine, the value
labels do not change. The combo prints the **enum name**, which lives in `.rdata` at
`0x0364FA54` and occupies exactly 4 bytes (`"120\0"`).

It could be renamed, but then the setting saved in Steam Cloud (`FPSLimit: "120"`) would
no longer match the enum map and would reset. Not worth it — the label is cosmetic, the
value behind it is real.

---

## 💻 Command line

The same logic without the GUI:

```powershell
.\Unlock-BlitzFPS.ps1 -Status                     # show current state
.\Unlock-BlitzFPS.ps1 -TargetFps 1000 -VSync Off  # maximum frames
.\Unlock-BlitzFPS.ps1 -TargetFps 240              # steady 240 to match a 240 Hz panel
.\Unlock-BlitzFPS.ps1 -Restore                    # undo everything
```

`-CameraFix` accepts `On` (default), `Off` and `Leave`; `-VSync` accepts `Off`, `On` and
`Leave` (default).

---

## 🛠 Building from source

```powershell
.\build.ps1
```

Produces `bin\WotbFpsUnlock.exe` and `bin\dvpl.exe` using the C# compiler included with
Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). **No .NET SDK, no
Visual Studio, nothing to install.**

---

## 📦 What's in the repo

| | |
|---|---|
| `src/Program.cs`, `src/MainForm.cs` | the GUI |
| `src/Theme.cs`, `src/FpsSlider.cs` | flat dark palette and an owner-drawn slider |
| `src/Patcher.cs` | all the logic: game discovery, signatures, patch, restore |
| `src/Dvpl.cs` | `.dvpl` container codec (LZ4 + CRC32 + footer) |
| `src/Sig.cs` | masked byte-signature search, used by the PowerShell version |
| `Unlock-BlitzFPS.ps1` | the command-line equivalent |
| `build.ps1` | builds both binaries |

`dvpl.exe` is useful on its own for digging through the game's configs:

```
dvpl unpack <file.dvpl> [out]     decode one file
dvpl pack   <file> [out.dvpl]     encode one file
dvpl info   <file.dvpl>           print the footer
dvpl grep   <dir> <text>          decode every .dvpl under a directory and search it
```

---

## ⚠️ Things worth knowing

- **Steam updates overwrite the patch.** Run the tool again after each game update —
  it detects the current state on start.
- **"Verify integrity of game files"** also reverts everything. That is the official
  escape hatch if anything ever goes wrong.
- **Tearing.** With VSync off, frames no longer line up with the display. That is the
  trade, not a defect. If it bothers you more than the extra frames help, set the limit
  to your refresh rate, or use `-VSync On` from the command line.
- **This modifies the game client**, and Wargaming's rules prohibit modified clients.
  Technically it is not a cheat — it exposes no game information and changes no
  mechanics, only frame pacing — but the risk of sanctions formally exists, and that
  call is yours.
- The app icon is taken from the game's own executable, i.e. it is Wargaming artwork.
  Fine for personal use; swap it for something of your own before distributing widely.

---

## 🧩 Known limitations

- **The menu label stays `120`** whatever you set — see [above](#6-the-menu-label).
- **A separate lobby cap exists in the engine's dev settings**
  (`Lobby fps limit enabled` in `Data\optionsGlobal.yaml`, stored in
  `%LOCALAPPDATA%\WoTBlitz\DAVAProject\optionsGlobal.bin`, a binary `KeyedArchive` with
  a string table). There are no direct references to the name in the executable — it is
  reached indirectly — so a signature alone cannot switch it off. In practice it does
  not get in the way: with the patches applied, the garage runs at the same rate as
  battle, so the flag is either inactive in release builds or gates something else.

---

## 💛 Donate

If this saved you an evening of hex-diving, a tip is welcome — entirely optional.

**USDT · TRC-20**

```
TDJJdAD5MXPzfUvA3wgfrn5TDxFfzMTx36
```

> Tron network only. Sending any other asset or using a different network will lose the
> funds.

---

<div align="center">
<sub>Not affiliated with or endorsed by Wargaming. World of Tanks Blitz is a trademark of Wargaming.net.</sub>
</div>

#!/usr/bin/env python3
"""
WoT Blitz FPS Unlocker - Linux / cross-platform edition.

Blitz has no native Linux build; on Linux it runs under Proton, so the file to patch
is still the Windows wotblitz.exe. The patching itself is plain byte surgery, so the
same three fixes apply unchanged:

  * FPS ceiling    - GraphicsOptionsApplier's inlined SwitchT<int, eFPSLimit> yields
                     120 from `lea edi, [eax+0x76]`; that displacement is a signed
                     byte, so case 2 is rewritten to a real `mov edi, <fps>`.
  * VSync          - rhi::dx11_PresentBuffer builds Present's SyncInterval from a
                     flags bit: `and ecx,1` becomes `and ecx,0`.
  * Camera         - the smoothing factor is quantised to 10 ms, which rounds to zero
                     above ~200 FPS and pins the factor on its 0.01 floor. Both
                     operands are repointed at a 100000.0f constant (quantum 0.01 ms).

Every site is located by signature, never by a hardcoded offset.

Usage:
    ./wotb-fps-unlock.py                    # GUI if tkinter is available, else status
    ./wotb-fps-unlock.py --status
    ./wotb-fps-unlock.py --fps 1000
    ./wotb-fps-unlock.py --fps 240 --vsync on
    ./wotb-fps-unlock.py --restore
    ./wotb-fps-unlock.py --game-path ~/Games/wotb --status
"""

import argparse
import os
import re
import shutil
import struct
import sys

APP_ID = "444200"
EXE_NAME = "wotblitz.exe"
BACKUP_DIR = "BlitzFpsUnlock.backup"
MIN_FPS = 60
MAX_FPS = 2000

W = None  # wildcard marker inside a signature

# --- FPS ceiling -----------------------------------------------------------
SIG_FPS_STOCK = [
    0x85, 0xC0, 0x75, 0x08, 0x8D, 0x78, 0x1E, 0xE9, W, W, W, W,
    0x8B, 0x7D, W, 0x83, 0xF8, 0x01, 0x75, 0x08, 0x8D, 0x78, 0x3B, 0xE9, W, W, W, W,
    0x83, 0xF8, 0x02, 0x75, 0x08, 0x8D, 0x78, 0x76, 0xE9, W, W, W, W,
]
SIG_FPS_PATCHED = [
    0x85, 0xC0, 0x75, 0x08, 0x8D, 0x78, 0x1E, 0xE9, W, W, W, W,
    0x8B, 0x7D, W, 0x83, 0xF8, 0x01, 0x75, 0x08, 0x8D, 0x78, 0x3B, 0xE9, W, W, W, W,
    0x83, 0xF8, 0x02, 0x75, 0x08, 0xBF, W, W, W, W, 0xEB, 0xEF, 0x90,
]
FPS_CASE_BODY_OFFSET = 33

# --- VSync -----------------------------------------------------------------
SIG_VSYNC_ON = [
    0x8B, 0x0D, W, W, W, W, 0x8B, 0x15, W, W, W, W,
    0xD1, 0xE9, 0x83, 0xE1, 0x01, 0x85, 0xD2, 0x74, W, 0x8B, 0x02,
]
SIG_VSYNC_OFF = [
    0x8B, 0x0D, W, W, W, W, 0x8B, 0x15, W, W, W, W,
    0xD1, 0xE9, 0x83, 0xE1, 0x00, 0x85, 0xD2, 0x74, W, 0x8B, 0x02,
]
VSYNC_BYTE_OFFSET = 16

# --- camera smoothing ------------------------------------------------------
SIG_CAMERA = [
    0xF3, 0x0F, 0x10, 0x45, 0x0C,
    0xF3, 0x0F, 0x59, 0x05, W, W, W, W,      # mulss xmm0,[K]  -> address at +9
    0x51,
    0xC6, 0x45, 0xFF, 0x00,
    0xF3, 0x0F, 0x11, 0x45, 0xF8,
    0xD9, 0x45, 0xF8,
    0xD9, 0x1C, 0x24,
    0xE8, W, W, W, W,                        # call roundf
    0xD9, 0x5D, 0xF8,
    0xF3, 0x0F, 0x10, 0x45, 0xF8,
    0x83, 0xC4, 0x04,
    0xF3, 0x0F, 0x5E, 0x05, W, W, W, W,      # divss xmm0,[K]  -> address at +49
]
CAM_CONST_SLOTS = (9, 49)
FINE_QUANT_BYTES = b"\x00\x50\xC3\x47"       # 100000.0f


# ===========================================================================
# signature scanning
# ===========================================================================
def _longest_literal_run(pattern):
    """Pick the longest wildcard-free stretch to anchor the search on."""
    best_start, best_len = 0, 0
    start = None
    for i, b in enumerate(pattern + [W]):
        if b is not W and start is None:
            start = i
        elif b is W and start is not None:
            if i - start > best_len:
                best_start, best_len = start, i - start
            start = None
    return best_start, best_len


def find_all(data, pattern, max_hits=4):
    """Offsets where pattern matches. Anchored on a literal run so bytes.find()
    does the scanning at C speed instead of a Python loop over 70 MB."""
    anchor_at, anchor_len = _longest_literal_run(pattern)
    if anchor_len == 0:
        return []
    needle = bytes(pattern[anchor_at:anchor_at + anchor_len])

    hits = []
    pos = 0
    end = len(data)
    while True:
        found = data.find(needle, pos)
        if found < 0:
            break
        pos = found + 1
        start = found - anchor_at
        if start < 0 or start + len(pattern) > end:
            continue
        chunk = data[start:start + len(pattern)]
        if all(p is W or chunk[i] == p for i, p in enumerate(pattern)):
            hits.append(start)
            if len(hits) >= max_hits:
                break
    return hits


# ===========================================================================
# PE helpers
# ===========================================================================
def offset_to_va(data, offset):
    """File offset -> virtual address, or 0 when outside every section."""
    pe = struct.unpack_from("<i", data, 0x3C)[0]
    n_sec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    image_base = struct.unpack_from("<I", data, opt + 28)[0]
    tab = opt + opt_size
    for i in range(n_sec):
        o = tab + i * 40
        va = struct.unpack_from("<I", data, o + 12)[0]
        raw_size = struct.unpack_from("<I", data, o + 16)[0]
        raw_ptr = struct.unpack_from("<I", data, o + 20)[0]
        if raw_ptr <= offset < raw_ptr + raw_size:
            return image_base + va + (offset - raw_ptr)
    return 0


def find_fine_quant_va(data):
    """The camera fix needs a 100000.0f that occurs exactly once, so the operands
    can never end up aimed at something a different build happens to keep there."""
    candidates = []
    for off in find_all(data, list(FINE_QUANT_BYTES), max_hits=8):
        va = offset_to_va(data, off)
        if va and va % 4 == 0:
            candidates.append(va)
    return candidates[0] if len(candidates) == 1 else 0


# ===========================================================================
# game discovery
# ===========================================================================
def _steam_roots():
    home = os.path.expanduser("~")
    roots = [
        os.path.join(home, ".steam", "steam"),
        os.path.join(home, ".steam", "root"),
        os.path.join(home, ".local", "share", "Steam"),
        # flatpak
        os.path.join(home, ".var", "app", "com.valvesoftware.Steam",
                     ".local", "share", "Steam"),
        # snap
        os.path.join(home, "snap", "steam", "common", ".local", "share", "Steam"),
    ]
    if os.name == "nt":
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
                roots.insert(0, winreg.QueryValueEx(k, "SteamPath")[0].replace("/", "\\"))
        except Exception:
            pass
        roots.append(r"C:\Program Files (x86)\Steam")

    out = []
    for r in roots:
        r = os.path.realpath(r) if os.path.exists(r) else r
        if r not in out:
            out.append(r)
    return out


def _libraries(root):
    """The root itself plus anything listed in libraryfolders.vdf (SD cards on a
    Steam Deck, extra drives, ...)."""
    libs = [root]
    vdf = os.path.join(root, "steamapps", "libraryfolders.vdf")
    try:
        with open(vdf, "r", encoding="utf-8", errors="replace") as f:
            for m in re.finditer(r'"path"\s+"([^"]+)"', f.read()):
                p = m.group(1).replace("\\\\", "\\")
                if p not in libs:
                    libs.append(p)
    except OSError:
        pass
    return libs


def _install_dir(lib):
    acf = os.path.join(lib, "steamapps", "appmanifest_%s.acf" % APP_ID)
    try:
        with open(acf, "r", encoding="utf-8", errors="replace") as f:
            m = re.search(r'"installdir"\s+"([^"]+)"', f.read())
            if m:
                return m.group(1)
    except OSError:
        pass
    return "World of Tanks Blitz"


def find_game_path():
    for root in _steam_roots():
        for lib in _libraries(root):
            common = os.path.join(lib, "steamapps", "common")
            name = _install_dir(lib)
            candidate = os.path.join(common, name)
            if os.path.isfile(os.path.join(candidate, EXE_NAME)):
                return candidate
            # Linux filesystems are case sensitive and the manifest may disagree
            try:
                for entry in os.listdir(common):
                    if entry.lower() == name.lower():
                        c = os.path.join(common, entry)
                        if os.path.isfile(os.path.join(c, EXE_NAME)):
                            return c
            except OSError:
                pass
    return None


def is_game_running():
    if os.name == "nt":
        try:
            import subprocess
            out = subprocess.check_output(["tasklist", "/FI", "IMAGENAME eq " + EXE_NAME],
                                          stderr=subprocess.DEVNULL, text=True)
            return EXE_NAME.lower() in out.lower()
        except Exception:
            return False

    # Linux: the game runs under Proton, so look for the exe name in any cmdline
    try:
        for pid in os.listdir("/proc"):
            if not pid.isdigit():
                continue
            try:
                with open("/proc/%s/cmdline" % pid, "rb") as f:
                    if EXE_NAME.encode() in f.read().lower():
                        return True
            except OSError:
                continue
    except OSError:
        pass
    return False


# ===========================================================================
# state / patching
# ===========================================================================
class State(object):
    def __init__(self):
        self.fps_kind = "unknown"      # stock | patched | ambiguous | unknown
        self.fps_value = 0
        self.fps_offset = -1
        self.vsync = "unknown"         # stock | off | unknown
        self.vsync_offset = -1
        self.camera = "unknown"        # stock | fixed | unknown
        self.camera_offset = -1
        self.data = b""

    @property
    def recognised(self):
        return self.fps_kind in ("stock", "patched")


def read_state(game_path):
    st = State()
    with open(os.path.join(game_path, EXE_NAME), "rb") as f:
        st.data = f.read()
    data = st.data

    stock = find_all(data, SIG_FPS_STOCK, 2)
    done = find_all(data, SIG_FPS_PATCHED, 2)
    if len(stock) > 1 or len(done) > 1:
        st.fps_kind = "ambiguous"
    elif len(stock) == 1:
        st.fps_kind, st.fps_offset, st.fps_value = "stock", stock[0], 120
    elif len(done) == 1:
        st.fps_kind, st.fps_offset = "patched", done[0]
        st.fps_value = struct.unpack_from("<i", data, done[0] + FPS_CASE_BODY_OFFSET + 1)[0]

    v_on = find_all(data, SIG_VSYNC_ON, 2)
    v_off = find_all(data, SIG_VSYNC_OFF, 2)
    if len(v_on) == 1:
        st.vsync, st.vsync_offset = "stock", v_on[0]
    elif len(v_off) == 1:
        st.vsync, st.vsync_offset = "off", v_off[0]

    cam = find_all(data, SIG_CAMERA, 2)
    if len(cam) == 1:
        st.camera_offset = cam[0]
        used = struct.unpack_from("<I", data, cam[0] + CAM_CONST_SLOTS[0])[0]
        fine = find_fine_quant_va(data)
        st.camera = "fixed" if (fine and used == fine) else "stock"
    return st


def apply_patches(game_path, fps, vsync_off=True, camera_fix=True, log=print):
    if is_game_running():
        raise RuntimeError("World of Tanks Blitz is running. Close it first.")
    if not (MIN_FPS <= fps <= MAX_FPS):
        raise ValueError("FPS must be between %d and %d." % (MIN_FPS, MAX_FPS))

    st = read_state(game_path)
    if st.fps_kind == "ambiguous":
        raise RuntimeError("The FPS signature matched more than once - refusing to patch.")
    if not st.recognised:
        raise RuntimeError("Could not find the FPS code in %s.\n"
                           "The game was probably updated and the signatures need refreshing."
                           % EXE_NAME)

    exe = os.path.join(game_path, EXE_NAME)
    backup_dir = os.path.join(game_path, BACKUP_DIR)
    os.makedirs(backup_dir, exist_ok=True)
    backup_exe = os.path.join(backup_dir, EXE_NAME)
    if not os.path.exists(backup_exe):
        shutil.copy2(exe, backup_exe)
        log("Backed up the original %s" % EXE_NAME)

    data = bytearray(st.data)

    at = st.fps_offset + FPS_CASE_BODY_OFFSET
    data[at:at + 8] = b"\xBF" + struct.pack("<i", fps) + b"\xEB\xEF\x90"
    log("FPS ceiling -> %d  (0x%X)" % (fps, at))

    if st.vsync == "unknown":
        log("VSync site not recognised - left alone")
    else:
        data[st.vsync_offset + VSYNC_BYTE_OFFSET] = 0x00 if vsync_off else 0x01
        log("VSync %s  (0x%X)" % ("off - Present SyncInterval pinned to 0" if vsync_off
                                  else "stock", st.vsync_offset + VSYNC_BYTE_OFFSET))

    if st.camera == "unknown":
        log("Camera site not recognised - left alone")
    elif camera_fix:
        fine = find_fine_quant_va(bytes(data))
        if not fine:
            log("No unique 100000.0f constant to point at - camera left alone")
        else:
            for slot in CAM_CONST_SLOTS:
                struct.pack_into("<I", data, st.camera_offset + slot, fine)
            log("Camera quantum 10 ms -> 0.01 ms  (0x%X -> 0x%08X)" % (st.camera_offset, fine))
    else:
        with open(backup_exe, "rb") as f:
            orig = f.read()
        for slot in CAM_CONST_SLOTS:
            o = st.camera_offset + slot
            data[o:o + 4] = orig[o:o + 4]
        log("Camera smoothing restored to stock")

    with open(exe, "wb") as f:
        f.write(data)
    log("Done.")


def restore(game_path, log=print):
    if is_game_running():
        raise RuntimeError("World of Tanks Blitz is running. Close it first.")
    backup_exe = os.path.join(game_path, BACKUP_DIR, EXE_NAME)
    if not os.path.exists(backup_exe):
        raise FileNotFoundError("No backup found at %s." % backup_exe)
    shutil.copy2(backup_exe, os.path.join(game_path, EXE_NAME))
    log("Restored the original %s." % EXE_NAME)


def print_status(game_path):
    st = read_state(game_path)
    print("")
    print("World of Tanks Blitz - FPS unlock status")
    print("  install : %s" % game_path)
    if st.fps_kind == "patched":
        print("  state   : patched - top menu option gives %d FPS" % st.fps_value)
    elif st.fps_kind == "stock":
        print("  state   : stock - top menu option gives 120 FPS")
    elif st.fps_kind == "ambiguous":
        print("  state   : signature matched more than once; not safe to patch")
    else:
        print("  state   : signature not found (game updated, or modified another way)")
    print("  vsync   : %s" % {"stock": "stock - Present syncs to vblank",
                              "off": "off - Present called with SyncInterval 0"}
          .get(st.vsync, "site not recognised"))
    print("  camera  : %s" % {"stock": "stock - smoothing quantised to 10 ms",
                              "fixed": "quantum 0.01 ms - tracks the real frame rate"}
          .get(st.camera, "site not recognised"))
    print("")


# ===========================================================================
# GUI (tkinter - part of the standard library; on Debian/Ubuntu it lives in
# the python3-tk package)
# ===========================================================================
BG = "#1b1d21"
PANEL = "#24272c"
BORDER = "#33373e"
TEXT = "#e6e8eb"
MUTED = "#939aa3"
ACCENT = "#f0a030"
GOOD = "#6fcf77"
BAD = "#e5715f"


def _set_window_icon(root, tk):
    """Optional: use the game icon when it sits next to the script (it ships in the
    repo as src/app128.png). A lone .py simply keeps the default icon."""
    here = os.path.dirname(os.path.abspath(__file__))
    for rel in ("app128.png", os.path.join("src", "app128.png")):
        candidate = os.path.join(here, rel)
        if os.path.isfile(candidate):
            try:
                img = tk.PhotoImage(file=candidate)
                root.iconphoto(True, img)
                root._icon_ref = img          # keep a reference alive
                return
            except Exception:
                pass


def run_gui():
    import tkinter as tk
    from tkinter import filedialog, messagebox

    root = tk.Tk()
    root.title("WoT Blitz FPS Unlocker")
    root.configure(bg=BG)
    root.resizable(False, False)
    _set_window_icon(root, tk)

    state = {"path": find_game_path()}

    # header ---------------------------------------------------------------
    header = tk.Frame(root, bg=PANEL, height=58)
    header.pack(fill="x")
    tk.Frame(root, bg=ACCENT, height=2).place(x=0, y=0, relwidth=1)
    tk.Label(header, text="WoT Blitz FPS Unlocker", bg=PANEL, fg=TEXT,
             font=("DejaVu Sans", 14, "bold")).pack(anchor="w", padx=16, pady=(10, 0))
    tk.Label(header, text="Steam  \u00b7  app 444200  \u00b7  Proton", bg=PANEL, fg=MUTED,
             font=("DejaVu Sans", 8)).pack(anchor="w", padx=18, pady=(0, 10))

    body = tk.Frame(root, bg=BG)
    body.pack(fill="both", expand=True, padx=16, pady=14)

    # game -----------------------------------------------------------------
    tk.Label(body, text="GAME", bg=BG, fg=MUTED,
             font=("DejaVu Sans", 7, "bold")).pack(anchor="w")
    game_box = tk.Frame(body, bg=PANEL, highlightbackground=BORDER, highlightthickness=1)
    game_box.pack(fill="x", pady=(2, 12))

    path_var = tk.StringVar(value=state["path"] or "not found")
    tk.Label(game_box, textvariable=path_var, bg=PANEL, fg=TEXT, anchor="w",
             font=("DejaVu Sans", 9)).pack(fill="x", padx=12, pady=(10, 2))
    status_var = tk.StringVar(value="")
    status_lbl = tk.Label(game_box, textvariable=status_var, bg=PANEL, fg=MUTED,
                          anchor="w", font=("DejaVu Sans", 9))
    status_lbl.pack(fill="x", padx=12, pady=(0, 10))

    # fps ------------------------------------------------------------------
    tk.Label(body, text="FPS LIMIT", bg=BG, fg=MUTED,
             font=("DejaVu Sans", 7, "bold")).pack(anchor="w")
    fps_box = tk.Frame(body, bg=PANEL, highlightbackground=BORDER, highlightthickness=1)
    fps_box.pack(fill="x", pady=(2, 12))

    fps_var = tk.IntVar(value=1000)
    tk.Label(fps_box, textvariable=fps_var, bg=PANEL, fg=ACCENT,
             font=("DejaVu Sans", 20, "bold")).pack(anchor="w", padx=12, pady=(8, 0))
    tk.Label(fps_box, text="frames per second", bg=PANEL, fg=MUTED,
             font=("DejaVu Sans", 8)).pack(anchor="w", padx=14)
    # An owner-drawn slider on a Canvas. ttk themes differ far too much between
    # distributions to rely on for colours, and tk.Scale paints its handle with the
    # widget background, so neither can be made to look right everywhere.
    class Slider(object):
        PAD = 10

        def __init__(self, parent, value):
            self.value = value
            self.canvas = tk.Canvas(parent, height=26, bg=PANEL, highlightthickness=0,
                                    bd=0, cursor="hand2")
            self.canvas.bind("<Configure>", lambda e: self.draw())
            self.canvas.bind("<Button-1>", self.on_click)
            self.canvas.bind("<B1-Motion>", self.on_click)
            self.canvas.bind("<MouseWheel>",
                             lambda e: self.nudge(10 if e.delta > 0 else -10))
            self.canvas.bind("<Button-4>", lambda e: self.nudge(10))    # X11 wheel up
            self.canvas.bind("<Button-5>", lambda e: self.nudge(-10))   # X11 wheel down

        def pack(self, **kw):
            self.canvas.pack(**kw)

        def span(self):
            return self.PAD, max(self.PAD + 1, self.canvas.winfo_width() - self.PAD)

        def set(self, v):
            self.value = max(MIN_FPS, min(MAX_FPS, int(v)))
            fps_var.set(self.value)
            self.draw()

        def nudge(self, delta):
            self.set(self.value + delta)

        def on_click(self, event):
            left, right = self.span()
            t = (event.x - left) / float(right - left)
            t = max(0.0, min(1.0, t))
            self.set(round((MIN_FPS + t * (MAX_FPS - MIN_FPS)) / 10.0) * 10)

        def draw(self):
            c = self.canvas
            c.delete("all")
            left, right = self.span()
            cy = c.winfo_height() // 2
            t = (self.value - MIN_FPS) / float(MAX_FPS - MIN_FPS)
            knob = left + t * (right - left)
            c.create_rectangle(left, cy - 2, right, cy + 2, fill=BORDER, outline="")
            if knob > left:
                c.create_rectangle(left, cy - 2, knob, cy + 2, fill=ACCENT, outline="")
            c.create_oval(knob - 8, cy - 8, knob + 8, cy + 8, fill=ACCENT, outline=PANEL,
                          width=2)

    scale = Slider(fps_box, fps_var.get())
    scale.pack(fill="x", padx=12, pady=(6, 14))

    # log ------------------------------------------------------------------
    log_box = tk.Text(body, height=6, bg=PANEL, fg=MUTED, bd=0,
                      highlightbackground=BORDER, highlightthickness=1,
                      font=("DejaVu Sans Mono", 8), state="disabled", wrap="word")

    def log(line):
        log_box.configure(state="normal")
        log_box.insert("end", line + "\n")
        log_box.see("end")
        log_box.configure(state="disabled")

    def refresh():
        if not state["path"]:
            status_var.set("\u25cf  Pick the folder that holds wotblitz.exe")
            status_lbl.configure(fg=BAD)
            return
        try:
            st = read_state(state["path"])
        except Exception as exc:
            status_var.set("\u25cf  Could not read the game")
            status_lbl.configure(fg=BAD)
            log("Error: %s" % exc)
            return
        if st.fps_kind == "patched":
            status_var.set("\u25cf  Unlocked at %d FPS" % st.fps_value)
            status_lbl.configure(fg=GOOD)
            clamped = max(MIN_FPS, min(MAX_FPS, st.fps_value))
            fps_var.set(clamped)
            scale.set(clamped)
        elif st.fps_kind == "stock":
            status_var.set("\u25cf  Stock - the game caps at 120 FPS")
            status_lbl.configure(fg=MUTED)
        else:
            status_var.set("\u25cf  Unrecognised build - signatures need refreshing")
            status_lbl.configure(fg=BAD)

    def do_browse():
        chosen = filedialog.askdirectory(title="Select the World of Tanks Blitz folder")
        if not chosen:
            return
        if not os.path.isfile(os.path.join(chosen, EXE_NAME)):
            messagebox.showwarning("WoT Blitz FPS Unlocker", "No %s in that folder." % EXE_NAME)
            return
        state["path"] = chosen
        path_var.set(chosen)
        log("Using: %s" % chosen)
        refresh()

    def do_apply():
        if not state["path"]:
            return
        try:
            apply_patches(state["path"], int(fps_var.get()), True, True, log)
            refresh()
            log("In game: Settings > Graphics > Frames per second > rightmost option.")
        except Exception as exc:
            log("Error: %s" % exc)
            messagebox.showwarning("WoT Blitz FPS Unlocker", str(exc))

    def do_restore():
        if not state["path"]:
            return
        try:
            restore(state["path"], log)
            refresh()
        except Exception as exc:
            log("Error: %s" % exc)
            messagebox.showwarning("WoT Blitz FPS Unlocker", str(exc))

    def do_launch():
        try:
            import webbrowser
            webbrowser.open("steam://rungameid/%s" % APP_ID)
            log("Asked Steam to start the game.")
        except Exception as exc:
            log("Error: %s" % exc)

    def button(parent, text, command, primary=False):
        return tk.Button(parent, text=text, command=command,
                         bg=ACCENT if primary else PANEL,
                         fg="#1b1406" if primary else TEXT,
                         activebackground="#ffba52" if primary else BORDER,
                         activeforeground="#1b1406" if primary else TEXT,
                         relief="flat", bd=0, padx=14, pady=8, cursor="hand2",
                         font=("DejaVu Sans", 9, "bold" if primary else "normal"))

    tk.Button(game_box, text="Change", command=do_browse, bg=PANEL, fg=TEXT,
              activebackground=BORDER, activeforeground=TEXT, relief="flat", bd=0,
              padx=10, pady=3, cursor="hand2",
              font=("DejaVu Sans", 8)).place(relx=1.0, x=-12, y=8, anchor="ne")

    row = tk.Frame(body, bg=BG)
    row.pack(fill="x", pady=(0, 12))
    button(row, "Apply", do_apply, True).pack(side="left")
    button(row, "Restore original", do_restore).pack(side="left", padx=8)
    button(row, "Launch game", do_launch).pack(side="left")

    log_box.pack(fill="both", expand=True)

    if state["path"]:
        log("Found: %s" % state["path"])
    else:
        log("World of Tanks Blitz was not found in any Steam library.")
    refresh()

    root.mainloop()


# ===========================================================================
def main():
    ap = argparse.ArgumentParser(
        description="Unlock the FPS ceiling in World of Tanks Blitz (Steam / Proton).")
    ap.add_argument("--game-path", help="folder holding %s" % EXE_NAME)
    ap.add_argument("--status", action="store_true", help="show current state and exit")
    ap.add_argument("--fps", type=int, metavar="N",
                    help="what the top menu option should deliver (%d-%d)" % (MIN_FPS, MAX_FPS))
    ap.add_argument("--vsync", choices=["on", "off"], default="off",
                    help="VSync handling when patching (default: off)")
    ap.add_argument("--camera", choices=["on", "off"], default="on",
                    help="camera smoothing fix when patching (default: on)")
    ap.add_argument("--restore", action="store_true", help="undo everything")
    ap.add_argument("--cli", action="store_true", help="never start the GUI")
    args = ap.parse_args()

    game_path = os.path.expanduser(args.game_path) if args.game_path else find_game_path()

    wants_cli = args.status or args.restore or args.fps is not None or args.cli
    if not wants_cli:
        try:
            run_gui()
            return 0
        except ImportError:
            print("tkinter is not available - falling back to the command line.")
            print("On Debian/Ubuntu: sudo apt install python3-tk\n")

    if not game_path:
        print("World of Tanks Blitz was not found. Pass --game-path explicitly.",
              file=sys.stderr)
        return 1

    try:
        if args.restore:
            restore(game_path)
        elif args.fps is not None:
            apply_patches(game_path, args.fps,
                          vsync_off=(args.vsync == "off"),
                          camera_fix=(args.camera == "on"))
        else:
            print_status(game_path)
    except Exception as exc:
        print("error: %s" % exc, file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())

// Patcher.cs - locating World of Tanks Blitz and applying the three byte patches.
// No UI in here: the form just drives these calls.
//
// Every site is found by signature, never by a hardcoded offset, so a rebuilt client
// either matches or is refused - it is never patched blindly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WotbFpsUnlock
{
    public enum FpsState { Unknown, Ambiguous, Stock, Patched }
    public enum ToggleState { Unknown, Stock, Changed }

    public class GameState
    {
        public string ExePath;
        public FpsState Fps = FpsState.Unknown;
        public int FpsValue;
        public int FpsOffset = -1;
        public ToggleState VSync = ToggleState.Unknown;   // Changed = forced off
        public int VSyncOffset = -1;
        public ToggleState Camera = ToggleState.Unknown;  // Changed = quantisation widened
        public int CameraOffset = -1;
        public byte[] Bytes;

        public bool Recognised
        {
            get { return Fps == FpsState.Stock || Fps == FpsState.Patched; }
        }
    }

    public static class Patcher
    {
        public const string AppId = "444200";
        public const string ExeName = "wotblitz.exe";
        public const string BackupDirName = "BlitzFpsUnlock.backup";

        public const int MinFps = 60;
        public const int MaxFps = 2000;

        // --- FPS ceiling -----------------------------------------------------
        // GraphicsOptionsApplier::Apply inlines SwitchT<int, eFPSLimit>::operator int.
        // The compiler turned the three cases into `lea edi, [eax + imm8]`, so case 2
        // yields 2 + 0x76 = 120. imm8 is signed, hence the rewrite to a real mov.
        static readonly int[] SigFpsStock = {
            0x85,0xC0, 0x75,0x08, 0x8D,0x78,0x1E, 0xE9,-1,-1,-1,-1,
            0x8B,0x7D,-1, 0x83,0xF8,0x01, 0x75,0x08, 0x8D,0x78,0x3B, 0xE9,-1,-1,-1,-1,
            0x83,0xF8,0x02, 0x75,0x08, 0x8D,0x78,0x76, 0xE9,-1,-1,-1,-1
        };
        static readonly int[] SigFpsPatched = {
            0x85,0xC0, 0x75,0x08, 0x8D,0x78,0x1E, 0xE9,-1,-1,-1,-1,
            0x8B,0x7D,-1, 0x83,0xF8,0x01, 0x75,0x08, 0x8D,0x78,0x3B, 0xE9,-1,-1,-1,-1,
            0x83,0xF8,0x02, 0x75,0x08, 0xBF,-1,-1,-1,-1, 0xEB,0xEF, 0x90
        };
        const int FpsCaseBodyOffset = 33;

        // --- VSync -----------------------------------------------------------
        // rhi::dx11_PresentBuffer derives IDXGISwapChain::Present's SyncInterval from
        // bit 1 of a render flags word. `and ecx,1` -> `and ecx,0` pins it to zero.
        static readonly int[] SigVSyncOn = {
            0x8B,0x0D,-1,-1,-1,-1, 0x8B,0x15,-1,-1,-1,-1,
            0xD1,0xE9, 0x83,0xE1,0x01, 0x85,0xD2, 0x74,-1, 0x8B,0x02
        };
        static readonly int[] SigVSyncOff = {
            0x8B,0x0D,-1,-1,-1,-1, 0x8B,0x15,-1,-1,-1,-1,
            0xD1,0xE9, 0x83,0xE1,0x00, 0x85,0xD2, 0x74,-1, 0x8B,0x02
        };
        const int VSyncByteOffset = 16;

        // --- camera smoothing -------------------------------------------------
        // The per-camera update computes its smoothing factor as
        //     clamp(roundf(avgFrameTime * 100) / 100 * coef, 0.01, 1.0)
        // The 10 ms quantum rounds to zero above ~200 FPS, pinning the factor on its
        // 0.01 floor. Repointing both operands at a 100000.0f constant makes the
        // quantum 0.01 ms; not one instruction moves.
        static readonly int[] SigCamera = {
            0xF3,0x0F,0x10,0x45,0x0C,
            0xF3,0x0F,0x59,0x05,-1,-1,-1,-1,   // mulss xmm0,[K]  -> address at +9
            0x51,
            0xC6,0x45,0xFF,0x00,
            0xF3,0x0F,0x11,0x45,0xF8,
            0xD9,0x45,0xF8,
            0xD9,0x1C,0x24,
            0xE8,-1,-1,-1,-1,                  // call roundf
            0xD9,0x5D,0xF8,
            0xF3,0x0F,0x10,0x45,0xF8,
            0x83,0xC4,0x04,
            0xF3,0x0F,0x5E,0x05,-1,-1,-1,-1    // divss xmm0,[K]  -> address at +49
        };
        static readonly int[] CamConstSlots = { 9, 49 };
        static readonly byte[] FineQuantBytes = { 0x00, 0x50, 0xC3, 0x47 };  // 100000.0f

        // =====================================================================
        // locating the game
        // =====================================================================
        public static string FindGamePath()
        {
            var roots = new List<string>();
            string steam = null;
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
                if (v != null) steam = v.ToString();
            }
            catch { }
            if (string.IsNullOrEmpty(steam))
                steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            steam = steam.Replace('/', '\\');
            roots.Add(steam);

            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    roots.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }

            var seen = new List<string>();
            foreach (string root in roots)
            {
                if (seen.Contains(root)) continue;
                seen.Add(root);

                string dir = null;
                string acf = Path.Combine(root, "steamapps\\appmanifest_" + AppId + ".acf");
                if (File.Exists(acf))
                {
                    Match m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (m.Success) dir = m.Groups[1].Value;
                }
                if (string.IsNullOrEmpty(dir)) dir = "World of Tanks Blitz";

                string candidate = Path.Combine(root, "steamapps\\common\\" + dir);
                if (File.Exists(Path.Combine(candidate, ExeName))) return candidate;
            }
            return null;
        }

        public static bool IsGameRunning()
        {
            return Process.GetProcessesByName("wotblitz").Length > 0;
        }

        // =====================================================================
        // reading current state
        // =====================================================================
        public static GameState Read(string gamePath)
        {
            var st = new GameState();
            st.ExePath = Path.Combine(gamePath, ExeName);
            st.Bytes = File.ReadAllBytes(st.ExePath);
            byte[] b = st.Bytes;

            int[] stock = FindAll(b, SigFpsStock, 2);
            int[] done = FindAll(b, SigFpsPatched, 2);
            if (stock.Length > 1 || done.Length > 1)
            {
                st.Fps = FpsState.Ambiguous;
            }
            else if (stock.Length == 1)
            {
                st.Fps = FpsState.Stock; st.FpsOffset = stock[0]; st.FpsValue = 120;
            }
            else if (done.Length == 1)
            {
                st.Fps = FpsState.Patched; st.FpsOffset = done[0];
                st.FpsValue = BitConverter.ToInt32(b, done[0] + FpsCaseBodyOffset + 1);
            }

            int[] vOn = FindAll(b, SigVSyncOn, 2);
            int[] vOff = FindAll(b, SigVSyncOff, 2);
            if (vOn.Length == 1) { st.VSync = ToggleState.Stock; st.VSyncOffset = vOn[0]; }
            else if (vOff.Length == 1) { st.VSync = ToggleState.Changed; st.VSyncOffset = vOff[0]; }

            int[] cam = FindAll(b, SigCamera, 2);
            if (cam.Length == 1)
            {
                st.CameraOffset = cam[0];
                uint used = BitConverter.ToUInt32(b, cam[0] + CamConstSlots[0]);
                uint fine = FindFineQuantVa(b);
                st.Camera = (fine != 0 && used == fine) ? ToggleState.Changed : ToggleState.Stock;
            }
            return st;
        }

        // =====================================================================
        // applying
        // =====================================================================
        public static void Apply(string gamePath, int fps, bool vsyncOff, bool cameraFix, Action<string> log)
        {
            if (IsGameRunning())
                throw new InvalidOperationException("World of Tanks Blitz is running. Close it first.");
            if (fps < MinFps || fps > MaxFps)
                throw new ArgumentOutOfRangeException("fps", "FPS must be between " + MinFps + " and " + MaxFps + ".");

            GameState st = Read(gamePath);
            if (st.Fps == FpsState.Ambiguous)
                throw new InvalidOperationException("The FPS signature matched more than once - refusing to patch.");
            if (!st.Recognised)
                throw new InvalidOperationException(
                    "Could not find the FPS code in " + ExeName + ".\r\n" +
                    "The game was probably updated and the signatures need refreshing.");

            string backupDir = Path.Combine(gamePath, BackupDirName);
            Directory.CreateDirectory(backupDir);
            string backupExe = Path.Combine(backupDir, ExeName);
            if (!File.Exists(backupExe))
            {
                File.Copy(st.ExePath, backupExe);
                log("Backed up the original " + ExeName);
            }

            byte[] b = st.Bytes;

            // mov edi, <fps> ; jmp short (case 1's jmp, same epilogue) ; nop
            int at = st.FpsOffset + FpsCaseBodyOffset;
            byte[] patch = new byte[] { 0xBF, 0, 0, 0, 0, 0xEB, 0xEF, 0x90 };
            Buffer.BlockCopy(BitConverter.GetBytes(fps), 0, patch, 1, 4);
            Buffer.BlockCopy(patch, 0, b, at, patch.Length);
            log("FPS ceiling -> " + fps + "  (0x" + at.ToString("X") + ")");

            if (st.VSync == ToggleState.Unknown)
                log("VSync site not recognised - left alone");
            else
            {
                b[st.VSyncOffset + VSyncByteOffset] = vsyncOff ? (byte)0x00 : (byte)0x01;
                log("VSync " + (vsyncOff ? "off - Present SyncInterval pinned to 0" : "stock")
                    + "  (0x" + (st.VSyncOffset + VSyncByteOffset).ToString("X") + ")");
            }

            if (st.Camera == ToggleState.Unknown)
                log("Camera site not recognised - left alone");
            else if (cameraFix)
            {
                uint fine = FindFineQuantVa(b);
                if (fine == 0) log("No unique 100000.0f constant to point at - camera left alone");
                else
                {
                    byte[] addr = BitConverter.GetBytes(fine);
                    foreach (int slot in CamConstSlots)
                        Buffer.BlockCopy(addr, 0, b, st.CameraOffset + slot, 4);
                    log("Camera quantum 10 ms -> 0.01 ms  (0x" + st.CameraOffset.ToString("X") + ")");
                }
            }
            else
            {
                byte[] orig = File.ReadAllBytes(backupExe);
                foreach (int slot in CamConstSlots)
                    Buffer.BlockCopy(orig, st.CameraOffset + slot, b, st.CameraOffset + slot, 4);
                log("Camera smoothing restored to stock");
            }

            File.WriteAllBytes(st.ExePath, b);
            log("Done.");
        }

        public static void Restore(string gamePath, Action<string> log)
        {
            if (IsGameRunning())
                throw new InvalidOperationException("World of Tanks Blitz is running. Close it first.");

            string backupExe = Path.Combine(gamePath, BackupDirName, ExeName);
            if (!File.Exists(backupExe))
                throw new FileNotFoundException("No backup found at " + backupExe + ".");

            File.Copy(backupExe, Path.Combine(gamePath, ExeName), true);
            log("Restored the original " + ExeName + ".");
        }

        public static void LaunchGame()
        {
            Process.Start("steam://rungameid/" + AppId);
        }

        // =====================================================================
        // helpers
        // =====================================================================
        static int[] FindAll(byte[] buf, int[] pattern, int maxHits)
        {
            var hits = new List<int>();
            int last = buf.Length - pattern.Length;
            int first = pattern[0];
            for (int i = 0; i <= last; i++)
            {
                if (first >= 0 && buf[i] != (byte)first) continue;
                int j = 1;
                for (; j < pattern.Length; j++)
                {
                    int p = pattern[j];
                    if (p >= 0 && buf[i + j] != (byte)p) break;
                }
                if (j != pattern.Length) continue;
                hits.Add(i);
                if (hits.Count >= maxHits) break;
            }
            return hits.ToArray();
        }

        // The camera fix needs a 100000.0f that exists exactly once, so we can never
        // aim the operands at something a different build happens to keep there.
        static uint FindFineQuantVa(byte[] b)
        {
            var pattern = new int[FineQuantBytes.Length];
            for (int i = 0; i < FineQuantBytes.Length; i++) pattern[i] = FineQuantBytes[i];

            uint found = 0;
            int count = 0;
            foreach (int off in FindAll(b, pattern, 8))
            {
                uint va = ToVa(b, off);
                if (va == 0 || va % 4 != 0) continue;
                found = va;
                count++;
            }
            return count == 1 ? found : 0;
        }

        static uint ToVa(byte[] b, int offset)
        {
            int pe = BitConverter.ToInt32(b, 0x3C);
            int nSec = BitConverter.ToUInt16(b, pe + 6);
            int optSize = BitConverter.ToUInt16(b, pe + 20);
            int opt = pe + 24;
            uint imageBase = BitConverter.ToUInt32(b, opt + 28);
            int tab = opt + optSize;
            for (int i = 0; i < nSec; i++)
            {
                int o = tab + i * 40;
                uint va = BitConverter.ToUInt32(b, o + 12);
                uint rawSize = BitConverter.ToUInt32(b, o + 16);
                uint rawPtr = BitConverter.ToUInt32(b, o + 20);
                if (offset >= rawPtr && offset < rawPtr + rawSize)
                    return imageBase + va + ((uint)offset - rawPtr);
            }
            return 0;
        }

        public static bool CanWrite(string gamePath)
        {
            try
            {
                using (var fs = new FileStream(Path.Combine(gamePath, ExeName), FileMode.Open,
                                               FileAccess.ReadWrite, FileShare.ReadWrite))
                    return fs.CanWrite;
            }
            catch { return false; }
        }
    }
}

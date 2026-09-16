// MainForm.cs - the whole UI. Built by hand rather than by a designer so the layout
// stays readable in source and the project needs nothing but csc.exe to build.

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WotbFpsUnlock
{
    public class MainForm : Form
    {
        string gamePath;
        bool syncing;   // guards the slider <-> spinner round trip

        Label lblPath, lblState;
        NumericUpDown numFps;
        TrackBar barFps;
        CheckBox chkVSync, chkCamera;
        Button btnApply, btnRestore, btnLaunch;
        TextBox txtLog;

        public MainForm()
        {
            Text = "WoT Blitz FPS Unlocker";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(470, 405);
            Font = new Font("Segoe UI", 9f);

            BuildGameBox();
            BuildOptionsBox();
            BuildButtons();
            BuildLog();

            Load += delegate { Detect(); };
        }

        // ------------------------------------------------------------- layout --
        void BuildGameBox()
        {
            var box = new GroupBox { Text = "Game", Location = new Point(12, 8), Size = new Size(446, 78) };

            lblPath = new Label
            {
                Location = new Point(12, 22),
                Size = new Size(330, 17),
                Text = "searching...",
                AutoEllipsis = true
            };
            lblState = new Label
            {
                Location = new Point(12, 46),
                Size = new Size(420, 19),
                Text = ""
            };
            var btnBrowse = new Button { Text = "Change...", Location = new Point(352, 18), Size = new Size(82, 25) };
            btnBrowse.Click += delegate { Browse(); };

            box.Controls.AddRange(new Control[] { lblPath, lblState, btnBrowse });
            Controls.Add(box);
        }

        void BuildOptionsBox()
        {
            var box = new GroupBox { Text = "Options", Location = new Point(12, 92), Size = new Size(446, 132) };

            box.Controls.Add(new Label { Text = "FPS limit:", Location = new Point(12, 26), Size = new Size(64, 17) });

            numFps = new NumericUpDown
            {
                Location = new Point(80, 23),
                Size = new Size(72, 23),
                Minimum = Patcher.MinFps,
                Maximum = Patcher.MaxFps,
                Increment = 10,
                Value = 1000
            };
            numFps.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                barFps.Value = (int)numFps.Value;
                syncing = false;
            };

            barFps = new TrackBar
            {
                Location = new Point(160, 20),
                Size = new Size(274, 45),
                Minimum = Patcher.MinFps,
                Maximum = Patcher.MaxFps,
                TickFrequency = 120,
                LargeChange = 60,
                Value = 1000
            };
            barFps.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                numFps.Value = barFps.Value;
                syncing = false;
            };

            chkVSync = new CheckBox
            {
                Text = "Disable VSync - go past the refresh rate (tearing)",
                Location = new Point(14, 68),
                Size = new Size(424, 20),
                Checked = true
            };
            chkCamera = new CheckBox
            {
                Text = "Fix camera smoothing above ~200 FPS",
                Location = new Point(14, 94),
                Size = new Size(424, 20),
                Checked = true
            };

            var tips = new ToolTip { AutoPopDelay = 15000 };
            tips.SetToolTip(chkVSync,
                "The client hard-enables VSync and hides the setting, so the monitor's\r\n" +
                "refresh rate is the real ceiling. This calls Present with SyncInterval 0.\r\n" +
                "Frames stop lining up with the display, so expect tearing.");
            tips.SetToolTip(chkCamera,
                "Stock camera smoothing quantises frame time to 10 ms. Above ~200 FPS a\r\n" +
                "frame is under 5 ms, that rounds to zero, and the smoothing factor drops\r\n" +
                "onto its 0.01 floor - the camera turns syrupy. This widens the quantum\r\n" +
                "to 0.01 ms so smoothing tracks the real frame rate.");
            tips.SetToolTip(barFps,
                "What the rightmost option in the game's FPS menu delivers.\r\n" +
                "The label there still reads 120 - that text is the enum name.");

            box.Controls.AddRange(new Control[] { numFps, barFps, chkVSync, chkCamera });
            Controls.Add(box);
        }

        void BuildButtons()
        {
            btnApply = new Button { Text = "Apply", Location = new Point(12, 232), Size = new Size(140, 32) };
            btnApply.Click += delegate { Apply(); };

            btnRestore = new Button { Text = "Restore original", Location = new Point(160, 232), Size = new Size(140, 32) };
            btnRestore.Click += delegate { Restore(); };

            btnLaunch = new Button { Text = "Launch game", Location = new Point(318, 232), Size = new Size(140, 32) };
            btnLaunch.Click += delegate { Launch(); };

            Controls.AddRange(new Control[] { btnApply, btnRestore, btnLaunch });
        }

        void BuildLog()
        {
            txtLog = new TextBox
            {
                Location = new Point(12, 274),
                Size = new Size(446, 118),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                Font = new Font("Consolas", 8.5f)
            };
            Controls.Add(txtLog);
        }

        // -------------------------------------------------------------- logic --
        void Log(string line)
        {
            txtLog.AppendText(line + Environment.NewLine);
        }

        void Detect()
        {
            gamePath = Patcher.FindGamePath();
            if (gamePath == null)
            {
                lblPath.Text = "not found";
                lblState.Text = "Pick the folder that holds wotblitz.exe.";
                Log("World of Tanks Blitz was not found in any Steam library.");
                SetEnabled(false);
                return;
            }
            lblPath.Text = gamePath;
            Log("Found: " + gamePath);
            Refresh_();
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select the World of Tanks Blitz folder (the one with wotblitz.exe)";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!File.Exists(Path.Combine(dlg.SelectedPath, Patcher.ExeName)))
                {
                    MessageBox.Show(this, "No " + Patcher.ExeName + " in that folder.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                gamePath = dlg.SelectedPath;
                lblPath.Text = gamePath;
                Log("Using: " + gamePath);
                Refresh_();
            }
        }

        void SetEnabled(bool on)
        {
            btnApply.Enabled = on;
            btnRestore.Enabled = on;
            numFps.Enabled = on;
            barFps.Enabled = on;
            chkVSync.Enabled = on;
            chkCamera.Enabled = on;
        }

        void Refresh_()
        {
            try
            {
                GameState st = Patcher.Read(gamePath);

                if (st.Fps == FpsState.Ambiguous)
                {
                    lblState.Text = "Signature matched more than once - not safe to patch.";
                    lblState.ForeColor = Color.Firebrick;
                    SetEnabled(false);
                    return;
                }
                if (!st.Recognised)
                {
                    lblState.Text = "Unrecognised build - signatures need refreshing.";
                    lblState.ForeColor = Color.Firebrick;
                    SetEnabled(false);
                    return;
                }

                SetEnabled(true);
                if (st.Fps == FpsState.Patched)
                {
                    lblState.ForeColor = Color.ForestGreen;
                    lblState.Text = string.Format("Patched: {0} FPS, VSync {1}, camera {2}",
                        st.FpsValue,
                        st.VSync == ToggleState.Changed ? "off" : "stock",
                        st.Camera == ToggleState.Changed ? "fixed" : "stock");

                    syncing = true;
                    int v = Math.Max(Patcher.MinFps, Math.Min(Patcher.MaxFps, st.FpsValue));
                    numFps.Value = v;
                    barFps.Value = v;
                    syncing = false;
                    chkVSync.Checked = st.VSync == ToggleState.Changed;
                    chkCamera.Checked = st.Camera == ToggleState.Changed;
                }
                else
                {
                    lblState.ForeColor = SystemColors.ControlText;
                    lblState.Text = "Stock: the top menu option gives 120 FPS.";
                }
            }
            catch (Exception ex)
            {
                lblState.Text = "Could not read the game.";
                lblState.ForeColor = Color.Firebrick;
                Log("Error: " + ex.Message);
                SetEnabled(false);
            }
        }

        void Apply()
        {
            if (!EnsureWritable()) return;
            try
            {
                Patcher.Apply(gamePath, (int)numFps.Value, chkVSync.Checked, chkCamera.Checked, Log);
                Refresh_();
                Log("In game: Settings -> Graphics -> Frames per second -> rightmost option.");
                Log("It still reads 120 (that label is the enum name) but delivers " + (int)numFps.Value + ".");
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void Restore()
        {
            if (!EnsureWritable()) return;
            try
            {
                Patcher.Restore(gamePath, Log);
                Refresh_();
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void Launch()
        {
            try { Patcher.LaunchGame(); Log("Asked Steam to start the game."); }
            catch (Exception ex) { Log("Error: " + ex.Message); }
        }

        // Steam libraries under Program Files need elevation; offer it instead of
        // failing with a bare "access denied".
        bool EnsureWritable()
        {
            if (gamePath == null) return false;
            if (Patcher.CanWrite(gamePath)) return true;

            if (Patcher.IsGameRunning())
            {
                MessageBox.Show(this, "World of Tanks Blitz is running. Close it first.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            DialogResult r = MessageBox.Show(this,
                "wotblitz.exe is not writable - the game folder needs administrator rights.\r\n\r\n" +
                "Restart this tool as administrator?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return false;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
                Application.Exit();
            }
            catch (Exception ex)
            {
                Log("Could not elevate: " + ex.Message);
            }
            return false;
        }
    }
}

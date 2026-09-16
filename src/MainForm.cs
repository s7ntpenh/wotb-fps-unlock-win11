// MainForm.cs - the whole UI, laid out in code so the project needs nothing but the
// C# compiler that ships with Windows.
//
// Disabling VSync and fixing the camera are not offered as choices: without the first
// the refresh rate stays the ceiling, and without the second the camera turns syrupy
// the moment the ceiling is gone. Both are simply part of unlocking the frame rate.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace WotbFpsUnlock
{
    public class MainForm : Form
    {
        string gamePath;

        Label lblPath, lblState, lblFps;
        FpsSlider slider;
        Button btnApply, btnRestore, btnLaunch;
        TextBox txtLog;
        Color stateDot = Theme.Muted;

        public MainForm()
        {
            Text = "WoT Blitz FPS Unlocker";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(462, 428);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI(9f, FontStyle.Regular);
            LoadAppIcon();

            BuildHeader();
            BuildGameCard();
            BuildFpsCard();
            BuildButtons();
            BuildLog();

            Load += delegate { Detect(); };
        }

        Bitmap logo;

        void LoadAppIcon()
        {
            var asm = Assembly.GetExecutingAssembly();
            try
            {
                Stream s = asm.GetManifestResourceStream("app.ico");
                if (s != null) Icon = new Icon(s);
            }
            catch { }
            try
            {
                // Icon.ToBitmap() mangles PNG-compressed .ico entries, so the header
                // draws from a plain PNG instead.
                Stream s = asm.GetManifestResourceStream("app128.png");
                if (s != null) logo = new Bitmap(s);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar on Windows 10 20H1+ / 11. Older builds used attribute 19,
            // and anything that does not know either simply returns an error.
            int on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
        }

        // ------------------------------------------------------------- layout --
        void BuildHeader()
        {
            var header = new Panel
            {
                Bounds = new Rectangle(0, 0, ClientSize.Width, 64),
                BackColor = Theme.Panel
            };
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(Theme.Border))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
                using (var p = new Pen(Theme.Accent, 3f))
                    e.Graphics.DrawLine(p, 0, 0, header.Width, 0);

                if (logo != null)
                {
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    e.Graphics.DrawImage(logo, new Rectangle(16, 12, 40, 40));
                }
            };

            header.Controls.Add(new Label
            {
                Text = "WoT Blitz FPS Unlocker",
                Location = new Point(68, 13),
                Size = new Size(320, 24),
                ForeColor = Theme.Text,
                Font = Theme.UI(13f, FontStyle.Bold),
                BackColor = Color.Transparent
            });
            header.Controls.Add(new Label
            {
                Text = "Steam  ·  app 444200",
                Location = new Point(70, 37),
                Size = new Size(320, 16),
                ForeColor = Theme.Muted,
                Font = Theme.UI(8f, FontStyle.Regular),
                BackColor = Color.Transparent
            });

            Controls.Add(header);
        }

        void BuildGameCard()
        {
            Panel card = Theme.MakeCard("Game", new Rectangle(18, 98, 426, 76), this);

            lblPath = new Label
            {
                Location = new Point(14, 12),
                Size = new Size(300, 17),
                ForeColor = Theme.Text,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Text = "searching..."
            };

            lblState = new Label
            {
                Location = new Point(26, 38),
                Size = new Size(380, 19),
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                Text = ""
            };

            // status dot, drawn next to lblState
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(stateDot))
                    e.Graphics.FillEllipse(b, 14, 44, 8, 8);
            };

            Button change = Theme.MakeButton("Change", new Rectangle(330, 9, 82, 26), false);
            change.Click += delegate { Browse(); };

            card.Controls.AddRange(new Control[] { lblPath, lblState, change });
        }

        void BuildFpsCard()
        {
            Panel card = Theme.MakeCard("FPS limit", new Rectangle(18, 200, 426, 84), this);

            lblFps = new Label
            {
                Location = new Point(14, 8),
                Size = new Size(140, 32),
                ForeColor = Theme.Accent,
                Font = Theme.UI(19f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Text = "1000"
            };
            var unit = new Label
            {
                Location = new Point(16, 42),
                Size = new Size(160, 15),
                ForeColor = Theme.Muted,
                Font = Theme.UI(7.5f, FontStyle.Regular),
                BackColor = Color.Transparent,
                Text = "frames per second"
            };

            slider = new FpsSlider
            {
                Bounds = new Rectangle(150, 30, 260, 28),
                BackColor = Theme.Panel,
                Minimum = Patcher.MinFps,
                Maximum = Patcher.MaxFps,
                Value = 1000
            };
            slider.ValueChanged += delegate { lblFps.Text = slider.Value.ToString(); };

            var hint = new Label
            {
                Location = new Point(150, 60),
                Size = new Size(262, 15),
                ForeColor = Theme.Muted,
                Font = Theme.UI(7.5f, FontStyle.Regular),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
                Text = Patcher.MinFps + " – " + Patcher.MaxFps + "   (wheel / arrows to fine-tune)"
            };

            card.Controls.AddRange(new Control[] { lblFps, unit, slider, hint });
        }

        void BuildButtons()
        {
            btnApply = Theme.MakeButton("Apply", new Rectangle(18, 300, 158, 38), true);
            btnApply.Click += delegate { Apply(); };

            btnRestore = Theme.MakeButton("Restore original", new Rectangle(186, 300, 130, 38), false);
            btnRestore.Click += delegate { Restore(); };

            btnLaunch = Theme.MakeButton("Launch game", new Rectangle(326, 300, 118, 38), false);
            btnLaunch.Click += delegate { Launch(); };

            Controls.AddRange(new Control[] { btnApply, btnRestore, btnLaunch });
        }

        void BuildLog()
        {
            txtLog = new TextBox
            {
                Bounds = new Rectangle(18, 350, 426, 62),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Panel,
                ForeColor = Theme.Muted,
                Font = new Font("Consolas", 8.5f)
            };
            Controls.Add(txtLog);
        }

        // -------------------------------------------------------------- logic --
        void Log(string line)
        {
            txtLog.AppendText(line + Environment.NewLine);
        }

        void SetState(string text, Color dot, Color fore)
        {
            lblState.Text = text;
            lblState.ForeColor = fore;
            stateDot = dot;
            lblState.Parent.Invalidate();
        }

        void Detect()
        {
            gamePath = Patcher.FindGamePath();
            if (gamePath == null)
            {
                lblPath.Text = "not found";
                SetState("Pick the folder that holds wotblitz.exe", Theme.Bad, Theme.Bad);
                Log("World of Tanks Blitz was not found in any Steam library.");
                SetEnabled(false);
                return;
            }
            lblPath.Text = gamePath;
            Log("Found: " + gamePath);
            RefreshState();
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
                RefreshState();
            }
        }

        void SetEnabled(bool on)
        {
            btnApply.Enabled = on;
            btnRestore.Enabled = on;
            slider.Enabled = on;
        }

        void RefreshState()
        {
            try
            {
                GameState st = Patcher.Read(gamePath);

                if (st.Fps == FpsState.Ambiguous)
                {
                    SetState("Signature matched more than once - not safe to patch", Theme.Bad, Theme.Bad);
                    SetEnabled(false);
                    return;
                }
                if (!st.Recognised)
                {
                    SetState("Unrecognised build - signatures need refreshing", Theme.Bad, Theme.Bad);
                    SetEnabled(false);
                    return;
                }

                SetEnabled(true);
                if (st.Fps == FpsState.Patched)
                {
                    SetState("Unlocked at " + st.FpsValue + " FPS", Theme.Good, Theme.Good);
                    slider.Value = Math.Max(Patcher.MinFps, Math.Min(Patcher.MaxFps, st.FpsValue));
                    lblFps.Text = slider.Value.ToString();
                }
                else
                {
                    SetState("Stock - the game caps at 120 FPS", Theme.Muted, Theme.Muted);
                }
            }
            catch (Exception ex)
            {
                SetState("Could not read the game", Theme.Bad, Theme.Bad);
                Log("Error: " + ex.Message);
                SetEnabled(false);
            }
        }

        void Apply()
        {
            if (!EnsureWritable()) return;
            try
            {
                Patcher.Apply(gamePath, slider.Value, true, true, Log);
                RefreshState();
                Log("In game: Settings > Graphics > Frames per second > rightmost option.");
                Log("It still reads 120 - that label is the enum name - but gives " + slider.Value + ".");
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
                RefreshState();
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

        // Steam libraries under Program Files need elevation; offer it rather than
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

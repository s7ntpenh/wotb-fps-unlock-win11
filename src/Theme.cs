// Theme.cs - the flat dark palette and the few styled controls the form needs.
// WinForms draws GroupBox/Button chrome from the OS theme, which looks wrong on a dark
// background, so buttons are flat-styled by hand and panels replace group boxes.

using System.Drawing;
using System.Windows.Forms;

namespace WotbFpsUnlock
{
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x1B, 0x1D, 0x21);
        public static readonly Color Panel = Color.FromArgb(0x24, 0x27, 0x2C);
        public static readonly Color Border = Color.FromArgb(0x33, 0x37, 0x3E);
        public static readonly Color Text = Color.FromArgb(0xE6, 0xE8, 0xEB);
        public static readonly Color Muted = Color.FromArgb(0x93, 0x9A, 0xA3);
        public static readonly Color Accent = Color.FromArgb(0xF0, 0xA0, 0x30);
        public static readonly Color AccentHot = Color.FromArgb(0xFF, 0xBA, 0x52);
        public static readonly Color Good = Color.FromArgb(0x6F, 0xCF, 0x77);
        public static readonly Color Bad = Color.FromArgb(0xE5, 0x71, 0x5F);

        public static Font UI(float size, FontStyle style)
        {
            return new Font("Segoe UI", size, style);
        }

        /// Primary = filled accent button. Otherwise a quiet outlined one.
        public static Button MakeButton(string text, Rectangle bounds, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Bounds = bounds,
                FlatStyle = FlatStyle.Flat,
                Font = UI(9.5f, primary ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;

            if (primary)
            {
                b.BackColor = Accent;
                b.ForeColor = Color.FromArgb(0x1B, 0x14, 0x06);
                b.FlatAppearance.BorderColor = Accent;
                b.FlatAppearance.MouseOverBackColor = AccentHot;
                b.FlatAppearance.MouseDownBackColor = Accent;
            }
            else
            {
                b.BackColor = Panel;
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x2E, 0x32, 0x38);
                b.FlatAppearance.MouseDownBackColor = Panel;
            }
            return b;
        }

        /// A titled card. Returns the panel; put children at coordinates inside it.
        public static Panel MakeCard(string caption, Rectangle bounds, Control parent)
        {
            var title = new Label
            {
                Text = caption.ToUpperInvariant(),
                Location = new Point(bounds.X + 2, bounds.Y - 17),
                Size = new Size(bounds.Width, 14),
                ForeColor = Muted,
                Font = UI(7.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            parent.Controls.Add(title);

            var card = new Panel { Bounds = bounds, BackColor = Panel };
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                var r = card.ClientRectangle;
                r.Width -= 1; r.Height -= 1;
                using (var p = new Pen(Border)) e.Graphics.DrawRectangle(p, r);
            };
            parent.Controls.Add(card);
            return card;
        }
    }
}

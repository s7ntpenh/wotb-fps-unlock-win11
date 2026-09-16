// FpsSlider.cs - a small owner-drawn slider. The stock TrackBar is painted by the OS
// theme and refuses to sit on a dark background, so this draws its own track and knob.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WotbFpsUnlock
{
    public class FpsSlider : Control
    {
        int minimum = 60;
        int maximum = 2000;
        int value = 1000;
        bool dragging;

        const int KnobRadius = 8;
        const int TrackHeight = 4;

        public event EventHandler ValueChanged;

        public FpsSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Height = 28;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum
        {
            get { return minimum; }
            set { minimum = value; Invalidate(); }
        }

        public int Maximum
        {
            get { return maximum; }
            set { maximum = value; Invalidate(); }
        }

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(minimum, Math.Min(maximum, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        int TrackLeft { get { return KnobRadius + 1; } }
        int TrackRight { get { return Width - KnobRadius - 1; } }

        int KnobX
        {
            get
            {
                float t = (float)(value - minimum) / Math.Max(1, maximum - minimum);
                return TrackLeft + (int)Math.Round(t * (TrackRight - TrackLeft));
            }
        }

        void SetFromMouse(int x)
        {
            int span = TrackRight - TrackLeft;
            if (span <= 0) return;
            float t = (float)(x - TrackLeft) / span;
            t = Math.Max(0f, Math.Min(1f, t));
            Value = minimum + (int)Math.Round(t * (maximum - minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true;
            SetFromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value += Math.Sign(e.Delta) * 10;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) Value -= 10;
            else if (e.KeyCode == Keys.Right) Value += 10;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int cy = Height / 2;
            int knob = KnobX;

            using (var b = new SolidBrush(Theme.Border))
                g.FillRectangle(b, TrackLeft, cy - TrackHeight / 2, TrackRight - TrackLeft, TrackHeight);

            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, TrackLeft, cy - TrackHeight / 2, knob - TrackLeft, TrackHeight);

            var rect = new Rectangle(knob - KnobRadius, cy - KnobRadius, KnobRadius * 2, KnobRadius * 2);
            using (var b = new SolidBrush(dragging ? Theme.AccentHot : Theme.Accent))
                g.FillEllipse(b, rect);
            using (var p = new Pen(Theme.Bg, 2f))
                g.DrawEllipse(p, rect);
        }
    }
}

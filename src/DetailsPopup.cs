using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace CodexUsageBar
{
    /// 위젯을 누르면 작업표시줄 위에 뜨는 상세 창. Mac 버전 메뉴처럼 두 앱의 한도를 카드로 나란히 보여준다.
    internal sealed class DetailsPopup : Form
    {
        public event EventHandler RefreshRequested;
        public event EventHandler QuitRequested;

        private const float BaseWidth = 600f;
        private const float LayoutPadding = 12f;
        private const float CardGap = 10f;
        private const float ActionBarHeight = 36f;

        private readonly Func<UsageProvider, ProviderUsageState> stateFor;
        private readonly Func<UsageProvider> activeProvider;
        private readonly Timer clockTimer;
        private readonly Dictionary<string, Rectangle> buttons = new Dictionary<string, Rectangle>();
        private string hoveredButton;
        private float scale = 1f;
        private bool dark;
        private DateTime lastHiddenAt = DateTime.MinValue;

        public DetailsPopup(Func<UsageProvider, ProviderUsageState> stateFor, Func<UsageProvider> activeProvider)
        {
            this.stateFor = stateFor;
            this.activeProvider = activeProvider;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Codex Usage Bar";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            clockTimer = new Timer { Interval = 30000 };
            clockTimer.Tick += delegate { Invalidate(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return false; }
        }

        /// 방금 바깥을 눌러 닫힌 경우(같은 클릭으로 위젯을 누른 경우) 다시 열지 않는다.
        public bool ClosedJustNow
        {
            get { return (DateTime.UtcNow - lastHiddenAt).TotalMilliseconds < 400; }
        }

        public void ShowAbove(Rectangle anchor, float newScale, bool newDark)
        {
            scale = newScale;
            dark = newDark;
            BackColor = dark ? Color.FromArgb(30, 30, 32) : Color.FromArgb(245, 245, 248);

            int width = (int)(BaseWidth * scale);
            int height;
            using (var g = CreateGraphics())
            {
                height = (int)Math.Ceiling(MeasureHeight(g, width));
            }

            var area = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Left + anchor.Width / 2 - width / 2;
            x = Math.Max(area.Left + (int)(8 * scale), Math.Min(x, area.Right - width - (int)(8 * scale)));
            int y = anchor.Top - height - (int)(10 * scale);
            if (y < area.Top) y = anchor.Bottom + (int)(10 * scale);
            Bounds = new Rectangle(x, y, width, height);

            if (!Visible)
            {
                Show();
            }
            Activate();
            ApplyWindowStyle();
            clockTimer.Start();
            Invalidate();
        }

        public void Refresh(bool resize)
        {
            if (!Visible) return;
            if (resize)
            {
                using (var g = CreateGraphics())
                {
                    int height = (int)Math.Ceiling(MeasureHeight(g, Width));
                    if (height != Height)
                    {
                        Top += Height - height;
                        Height = height;
                    }
                }
            }
            Invalidate();
        }

        private void ApplyWindowStyle()
        {
            try
            {
                int corner = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch (Exception) { }
        }

        private float MeasureHeight(Graphics g, int width)
        {
            float cardWidth = (width - (2 * LayoutPadding + CardGap) * scale) / 2;
            float cardHeight = Math.Max(CardHeight(g, UsageProvider.Codex, cardWidth), CardHeight(g, UsageProvider.Claude, cardWidth));
            return cardHeight + (3 * LayoutPadding + ActionBarHeight) * scale;
        }

        private float StatusHeight(Graphics g, string text, float width)
        {
            using (var font = new Font("맑은 고딕", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                return (float)Math.Ceiling(g.MeasureString(text, font, (int)Math.Floor(width)).Height) + 3 * scale;
        }

        private float CardHeight(Graphics g, UsageProvider provider, float width)
        {
            var state = stateFor(provider);
            int rows = state.Snapshot == null ? 1 : Math.Max(1, Math.Min(2, state.Snapshot.Windows.Count));
            return (60 + rows * 86 + 8 + 10 + 22 + 16) * scale
                + StatusHeight(g, state.Status ?? "연결 대기 중", width - 28 * scale);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // SmoothingMode affects shapes, not text. Explicit ClearType avoids the
            // jagged SystemDefault glyphs when Windows font smoothing is disabled.
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TextContrast = 4;
            var foreground = dark ? Color.WhiteSmoke : Color.FromArgb(30, 30, 35);
            float padding = LayoutPadding * scale;
            float cardWidth = (ClientSize.Width - 2 * padding - CardGap * scale) / 2;
            float cardHeight = ClientSize.Height - (3 * LayoutPadding + ActionBarHeight) * scale;
            DrawCard(g, UsageProvider.Codex, new RectangleF(padding, padding, cardWidth, cardHeight), foreground);
            DrawCard(g, UsageProvider.Claude, new RectangleF(padding + cardWidth + CardGap * scale, padding, cardWidth, cardHeight), foreground);
            buttons.Clear();
            float buttonY = ClientSize.Height - ActionBarHeight * scale - padding;
            DrawButton(g, "refresh", "새로고침", new Rectangle((int)padding, (int)buttonY, (int)(100 * scale), (int)(ActionBarHeight * scale)), foreground);
            DrawButton(g, "autostart", AutoStart.IsEnabled ? "자동 시작: 켜짐" : "자동 시작: 꺼짐",
                new Rectangle((int)(padding + 110 * scale), (int)buttonY, (int)(160 * scale), (int)(ActionBarHeight * scale)), foreground);
            DrawButton(g, "quit", "종료", new Rectangle(ClientSize.Width - (int)(80 * scale + padding), (int)buttonY,
                (int)(80 * scale), (int)(ActionBarHeight * scale)), foreground);
        }

        private void DrawCard(Graphics g, UsageProvider provider, RectangleF bounds, Color foreground)
        {
            using (var path = BatteryRenderer.RoundedRect(bounds, 10 * scale))
            using (var brush = new SolidBrush(dark ? Color.FromArgb(43, 43, 46) : Color.White)) g.FillPath(brush, path);
            var state = stateFor(provider);
            float x = bounds.X + 14 * scale, y = bounds.Y + 15 * scale, width = bounds.Width - 28 * scale;
            ProviderIcons.Draw(g, provider, dark, new RectangleF(x, y, 23 * scale, 23 * scale));
            DrawText(g, provider.DisplayName() + (activeProvider() == provider ? "  · 표시 중" : ""),
                new RectangleF(x + 32 * scale, y - scale, width - 32 * scale, 28 * scale), 16, foreground, true);
            y = bounds.Y + 60 * scale;
            var snapshot = state.Snapshot;
            if (snapshot == null)
            {
                DrawText(g, "사용량 확인 대기 중", new RectangleF(x, y, width, 50 * scale), 14, foreground, false);
                y += 86 * scale;
            }
            else
            {
                foreach (var window in snapshot.Windows.Take(2))
                {
                    DrawText(g, window.Label + " · " + window.RemainingPercent + "% 남음",
                        new RectangleF(x, y, width, 25 * scale), 14, foreground, true);
                    var bar = Rectangle.Round(new RectangleF(x, y + 30 * scale, width, 7 * scale));
                    using (var brush = new SolidBrush(dark ? Color.FromArgb(70, 70, 75) : Color.FromArgb(230, 231, 235)))
                        g.FillRectangle(brush, bar);
                    bar.Width = (int)Math.Round(bar.Width * window.RemainingPercent / 100f);
                    using (var brush = new SolidBrush(Theme.Level(window.RemainingPercent, dark))) g.FillRectangle(brush, bar);
                    DrawText(g, window.ResetsAt.HasValue ? UsageFormat.Reset(window.ResetsAt.Value) + " 초기화" : "초기화 시각 정보 없음",
                        new RectangleF(x, y + 46 * scale, width, 25 * scale), 12, foreground, false);
                    y += 86 * scale;
                }
            }
            var status = state.Status ?? "연결 대기 중";
            float statusHeight = StatusHeight(g, status, width);
            y += 8 * scale;
            // Text needs more contrast than the brighter colours used by the bars.
            var muted = dark ? Color.FromArgb(190, 194, 202) : Color.FromArgb(87, 94, 106);
            var statusColor = state.Health == ConnectionHealth.Ok
                ? (dark ? Color.FromArgb(116, 216, 156) : Color.FromArgb(30, 119, 66))
                : state.Health == ConnectionHealth.Error
                    ? (dark ? Color.FromArgb(255, 155, 155) : Color.FromArgb(175, 38, 38))
                    : state.Health == ConnectionHealth.Degraded
                        ? (dark ? Color.FromArgb(241, 197, 117) : Color.FromArgb(139, 86, 15)) : muted;
            DrawText(g, status, new RectangleF(x, y, width, statusHeight), 12, statusColor, false);
            y += statusHeight + 10 * scale;
            DrawText(g, snapshot == null ? "아직 받은 값 없음" : "데이터: " + UsageFormat.Age(snapshot.FetchedAt, DateTime.UtcNow),
                new RectangleF(x, y, width, 22 * scale), 12, muted, false);
        }

        private void DrawText(Graphics g, string text, RectangleF bounds, float size, Color color, bool bold, bool centerVertically = false)
        {
            using (var font = new Font("맑은 고딕", size * scale, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat {
                Trimming = StringTrimming.EllipsisCharacter,
                LineAlignment = centerVertically ? StringAlignment.Center : StringAlignment.Near,
                FormatFlags = centerVertically ? StringFormatFlags.NoWrap : 0 })
                g.DrawString(text, font, brush, Rectangle.Round(bounds), format);
        }

        private void DrawButton(Graphics g, string key, string text, Rectangle bounds, Color foreground)
        {
            buttons[key] = bounds;
            using (var path = BatteryRenderer.RoundedRect(bounds, 6 * scale))
            using (var brush = new SolidBrush(key == hoveredButton
                ? (dark ? Color.FromArgb(70, 70, 76) : Color.FromArgb(220, 225, 235))
                : (dark ? Color.FromArgb(49, 49, 54) : Color.FromArgb(235, 236, 240)))) g.FillPath(brush, path);
            DrawText(g, text, new RectangleF(bounds.X + 12 * scale, bounds.Y, bounds.Width - 24 * scale, bounds.Height),
                13, foreground, false, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var key = buttons.FirstOrDefault(pair => pair.Value.Contains(e.Location)).Key;
            if (key == hoveredButton) return;
            hoveredButton = key;
            Cursor = key == null ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            var key = buttons.FirstOrDefault(pair => pair.Value.Contains(e.Location)).Key;
            if (key == "refresh" && RefreshRequested != null) RefreshRequested(this, EventArgs.Empty);
            else if (key == "quit" && QuitRequested != null) QuitRequested(this, EventArgs.Empty);
            else if (key == "autostart")
            {
                try { AutoStart.SetEnabled(!AutoStart.IsEnabled); }
                catch (Exception) { MessageBox.Show(this, "자동 시작 설정을 저장할 수 없습니다.", Text); }
                Invalidate();
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
            lastHiddenAt = DateTime.UtcNow;
            clockTimer.Stop();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Hide(); clockTimer.Stop(); }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) clockTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}

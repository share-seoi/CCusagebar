using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace CodexUsageBar
{
    internal static class Theme
    {
        /// 작업표시줄·플라이아웃은 "Windows 모드" 설정(SystemUsesLightTheme)을 따른다.
        public static bool SystemIsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static readonly Color ClaudeBrand = Color.FromArgb(0xD9, 0x77, 0x57);

        public static Color Level(int remainingPercent, bool dark)
        {
            if (remainingPercent >= 50) return dark ? Color.FromArgb(0x2E, 0xB8, 0x5C) : Color.FromArgb(0x1E, 0x9E, 0x4A);
            if (remainingPercent >= 20) return dark ? Color.FromArgb(0xF0, 0x9A, 0x1A) : Color.FromArgb(0xE0, 0x86, 0x00);
            return dark ? Color.FromArgb(0xF0, 0x4E, 0x4E) : Color.FromArgb(0xDC, 0x35, 0x3B);
        }

        public static Color Health(ConnectionHealth health)
        {
            switch (health)
            {
                case ConnectionHealth.Ok: return Color.FromArgb(0x22, 0xB0, 0x55);
                case ConnectionHealth.Degraded: return Color.FromArgb(0xE8, 0x92, 0x0C);
                case ConnectionHealth.Error: return Color.FromArgb(0xE0, 0x3E, 0x3E);
                default: return Color.FromArgb(0x9A, 0x9A, 0x9A);
            }
        }
    }

    internal static class ProviderIcons
    {
        private static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>();

        /// 작업표시줄·상세 창에 쓰는 앱 아이콘. 없으면 null.
        public static Bitmap Get(UsageProvider provider, bool dark)
        {
            var key = provider.Key() + (dark ? "-dark" : "-light");
            Bitmap cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            Bitmap icon = null;
            try
            {
                icon = provider == UsageProvider.Codex ? CodexIcon(dark) : ClaudeIcon();
            }
            catch (Exception)
            {
            }
            Cache[key] = icon;
            return icon;
        }

        private static Bitmap CodexIcon(bool dark)
        {
            // Mac 버전과 같은 아이콘을 빌드할 때 64px로 줄여 exe에 넣는다.
            var name = dark ? "codex-dark.png" : "codex-light.png";
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                return stream == null ? null : new Bitmap(stream);
            }
        }

        // Claude 앱의 트레이용 템플릿 아이콘(검은 실루엣)을 Claude 브랜드 색으로 칠해 쓴다.
        private static Bitmap ClaudeIcon()
        {
            var roots = ProviderApps.RunningExecutablePaths(UsageProvider.Claude)
                .Select(path => Path.GetDirectoryName(path))
                .Concat(ProviderApps.PackageRoots("Claude_").Select(root => Path.Combine(root, "app")));
            foreach (var root in roots)
            {
                foreach (var name in new[] { "TrayIconTemplate@3x.png", "TrayIconTemplate@2x.png", "TrayIconTemplate.png" })
                {
                    var path = Path.Combine(root, "resources", name);
                    if (!File.Exists(path)) continue;
                    using (var source = new Bitmap(path))
                    {
                        return Tint(source, Theme.ClaudeBrand);
                    }
                }
            }
            return null;
        }

        private static Bitmap Tint(Bitmap source, Color color)
        {
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    result.SetPixel(x, y, Color.FromArgb(source.GetPixel(x, y).A, color));
                }
            }
            return result;
        }

        /// 아이콘이 없을 때 쓰는 대체 그림: 앱 이니셜이 들어간 원.
        public static void DrawFallback(Graphics g, UsageProvider provider, RectangleF bounds)
        {
            var color = provider == UsageProvider.Claude ? Theme.ClaudeBrand : Color.FromArgb(0x5B, 0x6C, 0xF5);
            using (var brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, bounds);
            }
            using (var font = new Font("Segoe UI", bounds.Height * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(provider == UsageProvider.Claude ? "C" : ">", font, Brushes.White, bounds, format);
            }
        }

        public static void Draw(Graphics g, UsageProvider provider, bool dark, RectangleF bounds)
        {
            var icon = Get(provider, dark);
            if (icon == null)
            {
                DrawFallback(g, provider, bounds);
                return;
            }
            var previous = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, bounds);
            g.InterpolationMode = previous;
        }
    }

    /// 배터리 하나: 한도 이름(5h, W)과 남은 비율.
    internal sealed class Gauge
    {
        public string Label;
        public int RemainingPercent;
    }

    /// 앱 하나의 "아이콘 + 배터리" 묶음. 한도가 없으면 "--" 배터리 하나를 그린다.
    internal sealed class WidgetSection
    {
        public UsageProvider Provider;
        public List<Gauge> Gauges = new List<Gauge>();
        public bool Stale;
    }

    /// 작업표시줄 위젯에 그릴 내용. 보통은 앱 하나, "둘 다 표시"면 Codex와 Claude를 나란히 그린다.
    internal sealed class WidgetContent
    {
        public List<WidgetSection> Sections = new List<WidgetSection>();
        public string Tooltip;

        public static WidgetContent Single(UsageProvider provider, List<Gauge> gauges)
        {
            return new WidgetContent { Sections = new List<WidgetSection> { new WidgetSection { Provider = provider, Gauges = gauges } } };
        }
    }

    /// 작업표시줄에 들어가는 "앱 아이콘 + 배터리" 그림. 투명 배경 위에 알파 채널로 그린다.
    /// 한도가 여러 개면 "5h [배터리] W [배터리]"처럼 이름을 붙여 나란히 그린다.
    internal static class BatteryRenderer
    {
        private const float PaddingX = 7f;
        private const float IconSize = 16f;
        private const float Gap = 6f;
        private const float BodyWidth = 38f;
        private const float BodyHeight = 18f;
        private const float NubWidth = 2.5f;
        private const float NubGap = 1f;
        private const float LabelWidth = 14f;
        private const float LabelGap = 2f;
        private const float GaugeGap = 7f;
        private const float SectionGap = 12f;

        public static int Width(float scale, WidgetContent content)
        {
            var sections = content.Sections.Count == 0 ? new List<WidgetSection> { new WidgetSection() } : content.Sections;
            float width = sections.Sum(section => SectionWidth(section.Gauges)) + SectionGap * (sections.Count - 1);
            return (int)Math.Ceiling((PaddingX + width + PaddingX) * scale);
        }

        public static int Width(float scale, IList<Gauge> gauges)
        {
            return (int)Math.Ceiling((PaddingX + SectionWidth(gauges) + PaddingX) * scale);
        }

        private static float SectionWidth(IList<Gauge> gauges)
        {
            int count = Math.Max(1, gauges.Count);
            float width = (BodyWidth + NubGap + NubWidth) * count + GaugeGap * (count - 1);
            width += gauges.Count(HasLabel) * (LabelWidth + LabelGap);
            return IconSize + Gap + width;
        }

        // Label every battery, even a lone one, so a weekly-only account reads "W 96%"
        // like Claude's "5h … W …". Long fallback names ("개인 한도") do not fit and stay unlabeled.
        private static bool HasLabel(Gauge gauge)
        {
            return !string.IsNullOrEmpty(gauge.Label) && gauge.Label.Length <= 3;
        }

        public static Bitmap Render(WidgetContent content, Size size, float scale, bool dark, bool hover)
        {
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                // 거의 투명한 바탕을 깔아야 빈 곳을 눌러도 클릭이 작업표시줄로 새지 않는다.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                if (hover)
                {
                    var hoverRect = new RectangleF(2 * scale, 4 * scale, size.Width - 4 * scale, size.Height - 8 * scale);
                    using (var path = RoundedRect(hoverRect, 5 * scale))
                    using (var brush = new SolidBrush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255)))
                    {
                        g.FillPath(brush, path);
                    }
                }

                // 기록이 오래된 앱은 그 묶음만 흐리게 그려 한눈에 알 수 있게 한다.
                var layer = content.Sections.Any(section => section.Stale) ? new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb) : null;
                var faded = layer == null ? null : Graphics.FromImage(layer);
                try
                {
                    if (faded != null)
                    {
                        faded.SmoothingMode = SmoothingMode.AntiAlias;
                        faded.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        faded.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    }
                    float x = PaddingX * scale;
                    var sections = content.Sections.Count == 0 ? new List<WidgetSection> { new WidgetSection() } : content.Sections;
                    foreach (var section in sections)
                    {
                        DrawSection(section.Stale ? faded : g, section, x, size.Height / 2f, scale, dark);
                        x += (SectionWidth(section.Gauges) + SectionGap) * scale;
                    }
                }
                finally
                {
                    if (faded != null) faded.Dispose();
                }
                if (layer != null)
                {
                    using (layer)
                    using (var attributes = new ImageAttributes())
                    {
                        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.45f });
                        g.DrawImage(layer, new Rectangle(0, 0, size.Width, size.Height), 0, 0, size.Width, size.Height, GraphicsUnit.Pixel, attributes);
                    }
                }
            }
            return bitmap;
        }

        private static void DrawSection(Graphics g, WidgetSection section, float x, float centerY, float scale, bool dark)
        {
            var foreground = dark ? Color.White : Color.FromArgb(0x1B, 0x1B, 0x1B);
            ProviderIcons.Draw(g, section.Provider, dark, new RectangleF(x, centerY - IconSize * scale / 2, IconSize * scale, IconSize * scale));
            x += (IconSize + Gap) * scale;

            if (section.Gauges.Count == 0)
            {
                DrawBattery(g, x, centerY, null, scale, dark, foreground);
                return;
            }

            foreach (var gauge in section.Gauges)
            {
                if (HasLabel(gauge))
                {
                    using (var font = new Font("Segoe UI", 10f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.FromArgb(dark ? 200 : 180, foreground)))
                    using (var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                    {
                        g.DrawString(gauge.Label, font, brush, new RectangleF(x - 4f * scale, centerY - BodyHeight * scale / 2 + 0.5f * scale, (LabelWidth + 4f) * scale, BodyHeight * scale), format);
                    }
                    x += (LabelWidth + LabelGap) * scale;
                }
                DrawBattery(g, x, centerY, gauge.RemainingPercent, scale, dark, foreground);
                x += (BodyWidth + NubGap + NubWidth + GaugeGap) * scale;
            }
        }

        private static void DrawBattery(Graphics g, float x, float centerY, int? remainingPercent, float scale, bool dark, Color foreground)
        {
            var body = new RectangleF(x, centerY - BodyHeight * scale / 2, BodyWidth * scale, BodyHeight * scale);
            float stroke = Math.Max(1f, 1.2f * scale);

            // 배터리 머리
            var nub = new RectangleF(body.Right + NubGap * scale, centerY - 3.5f * scale, NubWidth * scale, 7f * scale);
            using (var path = RoundedRect(nub, 1.2f * scale))
            using (var brush = new SolidBrush(Color.FromArgb(dark ? 150 : 130, foreground)))
            {
                g.FillPath(brush, path);
            }

            // 남은 양만큼 채우기
            var inner = RectangleF.Inflate(body, -2.2f * scale, -2.2f * scale);
            RectangleF fill = RectangleF.Empty;
            if (remainingPercent.HasValue && remainingPercent.Value > 0)
            {
                float width = Math.Max(2f * scale, inner.Width * remainingPercent.Value / 100f);
                fill = new RectangleF(inner.X, inner.Y, width, inner.Height);
                using (var path = RoundedRect(fill, 2f * scale))
                using (var brush = new SolidBrush(Theme.Level(remainingPercent.Value, dark)))
                {
                    g.FillPath(brush, path);
                }
            }

            // 배터리 몸통 테두리
            using (var path = RoundedRect(RectangleF.Inflate(body, -stroke / 2, -stroke / 2), 4f * scale))
            using (var pen = new Pen(Color.FromArgb(dark ? 170 : 150, foreground), stroke))
            {
                g.DrawPath(pen, path);
            }

            // 퍼센트: 채워진 부분 위는 흰 글자, 빈 부분 위는 기본 글자색으로 나눠 그린다.
            var text = remainingPercent.HasValue ? remainingPercent.Value + "%" : "--";
            using (var font = new Font("Segoe UI", (remainingPercent == 100 ? 10f : 11f) * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                var textRect = new RectangleF(body.X, body.Y + 0.5f * scale, body.Width, body.Height);
                var state = g.Save();
                g.SetClip(fill, CombineMode.Exclude);
                using (var brush = new SolidBrush(foreground))
                {
                    g.DrawString(text, font, brush, textRect, format);
                }
                g.Restore(state);

                if (!fill.IsEmpty)
                {
                    state = g.Save();
                    g.SetClip(fill);
                    g.DrawString(text, font, Brushes.White, textRect, format);
                    g.Restore(state);
                }
            }
        }

        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal static class UsageFormat
    {
        private static readonly CultureInfo Korean = new CultureInfo("ko-KR");

        public static string Time(DateTime utc)
        {
            return utc.ToLocalTime().ToString("tt h:mm:ss", Korean);
        }

        public static string Reset(DateTime utc)
        {
            return utc.ToLocalTime().ToString("M월 d일 tt h:mm", Korean);
        }

        public static string Relative(DateTime utc, DateTime nowUtc)
        {
            int minutes = (int)((utc - nowUtc).TotalMinutes);
            if (minutes <= 0) return "곧 초기화";
            int days = minutes / 1440, hours = (minutes % 1440) / 60, mins = minutes % 60;
            if (days > 0) return hours > 0 ? days + "일 " + hours + "시간 후" : days + "일 후";
            if (hours > 0) return mins > 0 ? hours + "시간 " + mins + "분 후" : hours + "시간 후";
            return mins + "분 후";
        }

        public static string Age(DateTime utc, DateTime nowUtc)
        {
            int minutes = (int)((nowUtc - utc).TotalMinutes);
            if (minutes < 1) return "방금";
            if (minutes < 60) return minutes + "분 전";
            int hours = minutes / 60;
            if (hours < 24) return hours + "시간 전";
            return (hours / 24) + "일 전";
        }
    }
}

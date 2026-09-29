using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageBar
{
    internal static class Tests
    {
        private static int assertions;
        // Entirely fabricated usage values and timestamps; no captured account response.
        private const string Payload = "{\"five_hour\":{\"utilization\":25,\"resets_at\":\"2030-01-01T17:00:00.123456+00:00\"},\"seven_day\":{\"utilization\":40,\"resets_at\":\"2030-01-08T12:00:00.654321+00:00\"}}";

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                var snapshot = ClaudeLiveFetcher.ParseUsage(Payload, now);
                Check(snapshot.Windows.Count == 2 && snapshot.OverallRemainingPercent == 60, "API percentages");
                Check(snapshot.Windows[1].RemainingPercent == 60, "weekly remaining");
                Check(snapshot.Windows[0].ShortLabel == "5h" && snapshot.Windows[1].ShortLabel == "W", "short labels");
                Check(snapshot.Windows[0].ResetsAt.HasValue && snapshot.Windows[0].ResetsAt.Value.Kind == DateTimeKind.Utc, "fractional ISO reset date");
                Check(ClaudeLiveFetcher.ParseUsage("{\"five_hour\":{\"utilization\":0},\"seven_day\":null}", now).OverallRemainingPercent == 100, "zero is valid");
                Expect(() => ClaudeLiveFetcher.ParseUsage("{}", now), "missing windows");
                Expect(() => ClaudeLiveFetcher.ParseUsage("{\"five_hour\":{\"utilization\":\"20\"}}", now), "string utilization rejected");
                Expect(() => ClaudeLiveFetcher.ParseUsage("{\"five_hour\":{\"utilization\":-1}}", now), "negative utilization rejected");
                Expect(() => ClaudeLiveFetcher.ParseUsage("{\"five_hour\":{\"utilization\":200}}", now), "invalid utilization rejected");
                Expect(() => ClaudeLiveFetcher.ParseUsage("not JSON", now), "invalid JSON");
                var credentials = ClaudeLiveFetcher.ParseCodeCredentials("{\"claudeAiOauth\":{\"accessToken\":\"synthetic-token\",\"expiresAt\":1893499200000}}");
                Check(credentials.AccessToken == "synthetic-token" && credentials.ExpiresAt.HasValue, "Code credentials parsed");
                Check(ClaudeLiveFetcher.ParseCodeCredentials("{}") == null, "missing credentials");
                Check(new ClaudeToken("synthetic", now.AddSeconds(59), "test").IsExpired(now), "expiry margin");
                Check(!UsageApplicationContext.IsStale(new UsageSnapshot(snapshot.Windows, null, now.AddMinutes(-3)), now), "3-minute-old record stays opaque despite API failure");
                Check(UsageApplicationContext.IsStale(new UsageSnapshot(snapshot.Windows, null, now.AddMinutes(-11)), now), "11-minute-old record fades");
                Check(UsageApplicationContext.IsStale(null, now), "missing record fades");
                Check(UsageApplicationContext.ClaudeShownInterval == TimeSpan.FromMinutes(1) && UsageApplicationContext.ClaudeHiddenInterval == TimeSpan.FromMinutes(3), "Claude poll intervals");

                int calls = 0, reads = 0;
                var fakeResponse = new ClaudeUsageResponse { StatusCode = 200, Body = Payload };
                var fetcher = new ClaudeLiveFetcher(() => { reads++; return new ClaudeToken("synthetic", null, "test"); },
                    token => { calls++; return fakeResponse; }, () => now);
                Check(fetcher.FetchSynchronously().OverallRemainingPercent == 60 && calls == 1, "successful fetch");
                now = now.AddSeconds(20);
                fetcher.FetchSynchronously();
                Check(reads == 2, "20-second refresh and renewed token re-read");
                now = now.AddSeconds(61);
                fakeResponse = new ClaudeUsageResponse { StatusCode = 401, Body = "SECRET-MUST-NOT-APPEAR" };
                var error = Expect(() => fetcher.FetchSynchronously(), "authentication error");
                Check(!error.Contains("SECRET") && error.Contains("401"), "error body redacted");
                now = now.AddSeconds(61);
                fakeResponse = new ClaudeUsageResponse { StatusCode = 429, RetryAfter = "300" };
                Expect(() => fetcher.FetchSynchronously(), "429 handled");
                int limitedCalls = calls;
                now = now.AddSeconds(299);
                Expect(() => fetcher.FetchSynchronously(), "Retry-After cooldown");
                Check(calls == limitedCalls, "429 cannot be bypassed");
                now = now.AddSeconds(2);
                fakeResponse = new ClaudeUsageResponse { StatusCode = 200, Body = Payload };
                fetcher.FetchSynchronously();
                Check(calls == limitedCalls + 1, "recovers after cooldown");
                Check(ClaudeLiveFetcher.RetryTime(now.AddMinutes(20).ToString("R"), now, 120) == now.AddMinutes(20), "HTTP date retry header");
                Check(ClaudeLiveFetcher.RetryTime("NaN", now, 120) == now.AddSeconds(120), "invalid retry header");
                Check(ClaudeLiveFetcher.RetryTime("1", now, 120) == now.AddSeconds(120), "minimum backoff respected");
                foreach (var code in new[] { 403, 500, 302 })
                {
                    now = now.AddMinutes(20);
                    fakeResponse = new ClaudeUsageResponse { StatusCode = code, Body = "SECRET" };
                    Check(Expect(() => fetcher.FetchSynchronously(), "HTTP error").Contains(code.ToString()), "HTTP status preserved");
                }
                var expired = new ClaudeLiveFetcher(() => new ClaudeToken("synthetic", now, "test"),
                    token => { throw new Exception("must not send"); }, () => now);
                Expect(() => expired.FetchSynchronously(), "expired token makes no request");
                var missing = new ClaudeLiveFetcher(() => null, token => { throw new Exception("must not send"); }, () => now);
                Expect(() => missing.FetchSynchronously(), "missing token makes no request");
                var fallback = ClaudeUsageStore.ParseHistory("{\"samples\":[{\"t\":1,\"u\":{\"fh\":85,\"sd\":22}}]}", now);
                Check(fallback.Windows[0].UsedPercent == 85, "old local history is not invented as zero");
                Check(!Json.Serialize(snapshot.ToJson()).Contains("synthetic"), "snapshot has no credentials");
                Check(ProviderApps.IsProviderApp(UsageProvider.Claude, @"C:\Program Files\WindowsApps\Claude_1\app\Claude.exe"), "Store desktop detected");
                Check(!ProviderApps.IsProviderApp(UsageProvider.Claude, @"C:\Users\test\AppData\Roaming\Claude\claude-code\1\claude.exe"), "CLI excluded");
                Check(ProviderApps.IsProviderApp(UsageProvider.Codex, @"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\ChatGPT.exe"), "current Codex Desktop detected");
                Check(!ProviderApps.IsProviderApp(UsageProvider.Codex, @"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1\app\ChatGPT.exe"), "ordinary ChatGPT excluded");
                Check(!ProviderApps.IsProviderApp(UsageProvider.Codex, @"C:\Users\test\AppData\Local\OpenAI\Codex\bin\version\codex.exe"), "Codex CLI excluded from desktop watcher");

                Application.EnableVisualStyles();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                TestCoordinator(snapshot);
                RenderPopup(snapshot, args[0]);
                Console.WriteLine("PASS: " + assertions + " assertions; light/dark popup and battery renders saved.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void TestCoordinator(UsageSnapshot snapshot)
        {
            var statuses = new List<string>();
            int liveCalls = 0;
            using (var coordinator = new UsageCoordinator(() => snapshot,
                () => { Interlocked.Increment(ref liveCalls); throw new LiveUsageException("synthetic auth failure"); }, "Test", "local", "live", TimeSpan.FromSeconds(10)))
            {
                coordinator.StatusChanged += (status, health) => statuses.Add(status + ":" + health);
                coordinator.Start(null);
                PumpUntil(() => statuses.Any(status => status.Contains("synthetic auth failure")));
                coordinator.RefreshLive(true);
                coordinator.RequestLocalRefresh();
                Pump(150);
                Check(liveCalls == 1, "manual request cannot bypass coordinator's 20-second interval");
                Check(statuses.Last().Contains("synthetic auth failure") && statuses.Last().EndsWith("Degraded"), "local poll preserves API failure");
                coordinator.Stop();
            }
            using (var release = new ManualResetEvent(false))
            using (var coordinator = new UsageCoordinator(() => null,
                () => { release.WaitOne(2000); return snapshot; }, "Test", "local", "live", TimeSpan.FromSeconds(10)))
            {
                int delivered = 0;
                coordinator.SnapshotChanged += value => delivered++;
                coordinator.Start(null);
                coordinator.Stop();
                release.Set();
                Pump(150);
                Check(delivered == 0, "stopped coordinator ignores late responses");
            }
        }

        private static void RenderPopup(UsageSnapshot snapshot, string directory)
        {
            var claudeState = new ProviderUsageState { Snapshot = snapshot, Status = "Claude 로그인 토큰 API · 실시간", Health = ConnectionHealth.Ok };
            var codexState = new ProviderUsageState {
                Snapshot = new UsageSnapshot(new List<UsageWindow> { new UsageWindow("주간 한도", 20, 10080, DateTime.UtcNow.AddDays(5)) }, "test-plan", DateTime.UtcNow),
                Status = "Codex 계정 API · 실시간", Health = ConnectionHealth.Ok };
            foreach (var dark in new[] { false, true })
            foreach (var renderScale in new[] { 1f, 1.25f, 1.5f, 2f })
            foreach (var error in new[] { false, true })
            {
                claudeState.Status = error ? "Claude 토큰 인증 실패 (HTTP 401) · Claude 앱 로그인 확인 필요 · 마지막 기록 표시" : "Claude 로그인 토큰 API · 실시간";
                claudeState.Health = error ? ConnectionHealth.Degraded : ConnectionHealth.Ok;
                var suffix = (renderScale == 1f ? "" : "-" + (int)(renderScale * 100)) + (error ? "-error" : "");
                using (var popup = new DetailsPopup(provider => provider == UsageProvider.Claude ? claudeState : codexState, () => UsageProvider.Codex))
                {
                    popup.ShowAbove(new Rectangle(-2000, -2000, 100, 50), renderScale, dark);
                    popup.Hide();
                    using (var image = new Bitmap(popup.Width, popup.Height))
                    {
                        popup.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(directory, (dark ? "popup-dark" : "popup-light") + suffix + ".png"));
                    }
                }
                var single = new WidgetContent { Provider = UsageProvider.Codex, Gauges = new List<Gauge> { new Gauge { Label = "W", RemainingPercent = 75 } } };
                using (var image = BatteryRenderer.Render(single, new Size(BatteryRenderer.Width(renderScale, 1), (int)(48 * renderScale)), renderScale, dark, false))
                    image.Save(Path.Combine(directory, (dark ? "battery-dark" : "battery-light") + suffix + ".png"));
                var dual = new WidgetContent
                {
                    Provider = UsageProvider.Claude,
                    Gauges = new List<Gauge> { new Gauge { Label = "5h", RemainingPercent = 63 }, new Gauge { Label = "W", RemainingPercent = 12 } }
                };
                using (var image = BatteryRenderer.Render(dual, new Size(BatteryRenderer.Width(renderScale, 2), (int)(48 * renderScale)), renderScale, dark, false))
                    image.Save(Path.Combine(directory, (dark ? "battery-dual-dark" : "battery-dual-light") + suffix + ".png"));
            }
            Check(true, "UI rendered");
        }

        private static void PumpUntil(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            while (!condition() && DateTime.UtcNow < until) Pump(10);
            Check(condition(), "async completion");
        }
        private static void Pump(int milliseconds)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            do { Application.DoEvents(); Thread.Sleep(5); } while (DateTime.UtcNow < until);
        }
        private static void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL: " + message);
        }
        private static string Expect(Action action, string message)
        {
            try { action(); } catch (LiveUsageException error) { Check(true, message); return error.Message; }
            throw new Exception("FAIL: " + message + " (no error)");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageBar
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("--print-", StringComparison.Ordinal))
            {
                try
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                    Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
                    UsageSnapshot snapshot;
                    switch (args[0])
                    {
                        case "--print-claude-live-usage": snapshot = new ClaudeLiveFetcher().FetchSynchronously(); break;
                        case "--print-claude-usage": snapshot = new ClaudeUsageStore().LatestSnapshot(); break;
                        case "--print-live-usage": snapshot = new CodexLiveFetcher().FetchSynchronously(); break;
                        case "--print-usage": snapshot = new CodexLocalStore().LatestSnapshot(); break;
                        default: Console.Error.WriteLine("알 수 없는 진단 옵션"); return 2;
                    }
                    if (snapshot == null) { Console.Error.WriteLine("사용량 기록 없음"); return 1; }
                    Console.WriteLine(Json.Serialize(snapshot.ToJson()));
                    return 0;
                }
                catch (Exception error)
                {
                    // Only display our own sanitized errors, never credentials/HTTP bodies.
                    Console.Error.WriteLine(error is LiveUsageException || error is UsageStoreException
                        ? error.Message : "사용량 진단 실패 (" + error.GetType().Name + ")");
                    return 1;
                }
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\CodexUsageBar.Windows", out created))
            {
                if (!created) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using (var context = new UsageApplicationContext()) Application.Run(context);
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }

    internal sealed class UsageApplicationContext : ApplicationContext
    {
        private readonly AppSettings settings = AppSettings.Load();
        private readonly ProviderWatcher watcher = new ProviderWatcher();
        private readonly TaskbarWidget widget = new TaskbarWidget();
        private readonly Dictionary<UsageProvider, ProviderUsageState> states = new Dictionary<UsageProvider, ProviderUsageState>();
        private readonly Dictionary<UsageProvider, UsageCoordinator> coordinators = new Dictionary<UsageProvider, UsageCoordinator>();
        private readonly DetailsPopup popup;
        private readonly System.Windows.Forms.Timer clockTimer;
        private UsageProvider active;

        // The usage endpoint rate-limits per token (HTTP 429), and the Claude app polls it too.
        // Poll often only while the widget shows Claude; switching to Claude or opening the
        // popup still refreshes at once (RefreshLive keeps its own 20-second floor).
        internal static readonly TimeSpan ClaudeShownInterval = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan ClaudeHiddenInterval = TimeSpan.FromMinutes(3);
        internal static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(10);
        // Codex has no periodic live lookup (each one starts a helper process); switching to
        // Codex refreshes it instead, at most once a minute so alt-tabbing stays cheap.
        internal static readonly TimeSpan CodexSwitchRefreshAge = TimeSpan.FromMinutes(1);
        // Codex 앱이 꺼져 있어도 Claude에서 부른 Codex CLI 등이 로컬 기록을 남기므로 이 간격으로 확인한다.
        internal static readonly TimeSpan CodexClosedLocalInterval = TimeSpan.FromMinutes(1);

        public UsageApplicationContext()
        {
            foreach (UsageProvider provider in Enum.GetValues(typeof(UsageProvider)))
                states[provider] = new ProviderUsageState { Snapshot = settings.Snapshot(provider), Status = "앱 실행 대기 중", Health = ConnectionHealth.Working };
            var codexLocal = new CodexLocalStore();
            var codexLive = new CodexLiveFetcher();
            var claudeLocal = new ClaudeUsageStore();
            var claudeLive = new ClaudeLiveFetcher();
            AddCoordinator(UsageProvider.Codex, new UsageCoordinator(codexLocal.LatestSnapshot, codexLive.FetchSynchronously,
                "Codex", "Codex 로컬 세션 기록", "Codex 계정 API", TimeSpan.FromSeconds(20)));
            AddCoordinator(UsageProvider.Claude, new UsageCoordinator(claudeLocal.LatestSnapshot, claudeLive.FetchSynchronously,
                "Claude", "Claude 로컬 기록", "Claude 계정 API", TimeSpan.FromSeconds(20), ClaudeHiddenInterval));
            active = settings.LastActiveProvider ?? UsageProvider.Codex;
            popup = new DetailsPopup(provider => states[provider], IsShown, () => settings.ShowBoth);
            popup.DisplayModeToggled += delegate { settings.SetShowBoth(!settings.ShowBoth); UpdateDisplay(); };
            popup.RefreshRequested += delegate { foreach (var coordinator in coordinators.Values) coordinator.RefreshLive(true); };
            popup.QuitRequested += delegate { ExitThread(); };
            widget.LeftClick += delegate { TogglePopup(); };
            widget.RightClick += delegate { TogglePopup(); };
            watcher.Launched += provider => { coordinators[provider].Start(settings.Snapshot(provider)); SyncClosedCodex(); UpdateProvider(); };
            watcher.Terminated += provider =>
            {
                coordinators[provider].Stop();
                states[provider].Status = "앱 종료됨 · 마지막 기록 표시";
                states[provider].Health = ConnectionHealth.Degraded;
                SyncClosedCodex();
                UpdateProvider();
            };
            watcher.Activated += provider =>
            {
                active = provider;
                settings.SetLastActiveProvider(provider);
                if (provider == UsageProvider.Claude) coordinators[provider].RefreshLive(false);
                else coordinators[provider].RefreshLiveIfOlderThan(CodexSwitchRefreshAge);
                UpdateDisplay();
            };
            watcher.Start();
            foreach (var provider in watcher.RunningProviders) coordinators[provider].Start(settings.Snapshot(provider));
            SyncClosedCodex();
            UpdateProvider();
            clockTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            clockTimer.Tick += delegate { UpdateDisplay(); };
            clockTimer.Start();
        }

        private void AddCoordinator(UsageProvider provider, UsageCoordinator coordinator)
        {
            coordinators[provider] = coordinator;
            coordinator.SnapshotChanged += snapshot => { states[provider].Snapshot = snapshot; settings.SetSnapshot(provider, snapshot); UpdateDisplay(); };
            coordinator.Checked += at => { states[provider].CheckedAt = at; };
            coordinator.StatusChanged += (status, health) => { states[provider].Status = status; states[provider].Health = health; UpdateDisplay(); };
        }

        /// Codex 앱이 꺼져 있어도 Claude 앱이 켜져 있으면(Claude에서 Codex CLI를 부르는 경우)
        /// 로컬 기록만 1분마다 확인한다. 두 앱이 모두 꺼지면 아무것도 조회하지 않는다.
        private void SyncClosedCodex()
        {
            var running = watcher.RunningProviders.ToList();
            if (running.Contains(UsageProvider.Codex)) return;
            var codex = coordinators[UsageProvider.Codex];
            if (running.Contains(UsageProvider.Claude)) codex.StartBackground(settings.Snapshot(UsageProvider.Codex), CodexClosedLocalInterval);
            else codex.Stop();
        }

        private void UpdateProvider()
        {
            active = ProviderWatcher.PreferredProvider(watcher.FrontmostProvider(), watcher.RunningProviders.ToList(), active);
            settings.SetLastActiveProvider(active);
            widget.SetVisible(watcher.IsAnyProviderRunning);
            if (!watcher.IsAnyProviderRunning) popup.Hide();
            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            coordinators[UsageProvider.Claude].SetLiveInterval(IsShown(UsageProvider.Claude) ? ClaudeShownInterval : ClaudeHiddenInterval);
            var now = DateTime.UtcNow;
            var content = new WidgetContent();
            var tooltip = new List<string>();
            foreach (var provider in ShownProviders())
            {
                var state = states[provider];
                var snapshot = state.Snapshot;
                tooltip.Add(provider.DisplayName() + " · " + (state.Status ?? "연결 중"));
                if (snapshot != null)
                {
                    tooltip.AddRange(snapshot.Windows.Select(window => window.Label + ": " + window.RemainingPercent + "% 남음"));
                    tooltip.Add("데이터: " + UsageFormat.Age(snapshot.FetchedAt, now));
                }
                content.Sections.Add(new WidgetSection
                {
                    Provider = provider,
                    Gauges = snapshot == null
                        ? new List<Gauge>()
                        : snapshot.Windows.Select(window => new Gauge { Label = window.ShortLabel, RemainingPercent = window.RemainingPercent }).ToList(),
                    // Fade only when the numbers themselves are old. A failed live request with a
                    // fresh local record keeps full opacity; the popup still shows the error.
                    Stale = IsStale(snapshot, now)
                });
            }
            content.Tooltip = string.Join("\n", tooltip);
            widget.SetContent(content);
            if (popup != null) popup.Refresh(true);
        }

        private bool IsShown(UsageProvider provider)
        {
            return ShownProviders().Contains(provider);
        }

        /// 자동 전환이면 앞에 띄운 앱 하나. "둘 다"면 실행 중이거나 받은 값이 있는 앱을 Codex, Claude 순으로.
        private List<UsageProvider> ShownProviders()
        {
            if (!settings.ShowBoth) return new List<UsageProvider> { active };
            var running = watcher.RunningProviders.ToList();
            var both = new[] { UsageProvider.Codex, UsageProvider.Claude }
                .Where(provider => running.Contains(provider) || states[provider].Snapshot != null).ToList();
            return both.Count == 0 ? new List<UsageProvider> { active } : both;
        }

        internal static bool IsStale(UsageSnapshot snapshot, DateTime now)
        {
            return snapshot == null || now - snapshot.FetchedAt > StaleAge;
        }

        private void TogglePopup()
        {
            widget.HideTooltip();
            if (popup.Visible) { popup.Hide(); return; }
            coordinators[UsageProvider.Claude].RefreshLive(false);
            if (!popup.ClosedJustNow) popup.ShowAbove(widget.ScreenBounds, widget.Scale, widget.IsDark);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clockTimer.Dispose();
                watcher.Dispose();
                foreach (var coordinator in coordinators.Values) coordinator.Dispose();
                popup.Dispose();
                widget.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

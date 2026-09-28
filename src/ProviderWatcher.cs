using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace CodexUsageBar
{
    /// Codex·Claude 데스크톱 앱의 실행/종료와 맨 앞 창 전환을 감지한다. 이벤트는 UI 스레드에서 호출된다.
    internal sealed class ProviderWatcher : IDisposable
    {
        public event Action<UsageProvider> Launched;
        public event Action<UsageProvider> Terminated;
        public event Action<UsageProvider> Activated;

        private readonly Timer processTimer;
        // 네이티브 콜백이 수거되지 않도록 대리자를 필드로 붙잡아 둔다.
        private readonly Native.WinEventDelegate foregroundCallback;
        private IntPtr foregroundHook;
        private HashSet<UsageProvider> running = new HashSet<UsageProvider>();

        public ProviderWatcher()
        {
            processTimer = new Timer { Interval = 3000 };
            processTimer.Tick += delegate { PollProcesses(); };
            foregroundCallback = OnForegroundChanged;
        }

        public IEnumerable<UsageProvider> RunningProviders
        {
            get { return running; }
        }

        public bool IsAnyProviderRunning
        {
            get { return running.Count > 0; }
        }

        public void Start()
        {
            running = CurrentlyRunning();
            processTimer.Start();
            foregroundHook = Native.SetWinEventHook(
                Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, foregroundCallback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        }

        public UsageProvider? FrontmostProvider()
        {
            return ProviderForWindow(Native.GetForegroundWindow());
        }

        /// 작업표시줄에 보여줄 앱: 앞에 있는 앱 → 직전에 보던 앱 → 실행 중인 앱 순서.
        public static UsageProvider PreferredProvider(
            UsageProvider? frontmost,
            ICollection<UsageProvider> runningProviders,
            UsageProvider? previous)
        {
            if (frontmost.HasValue)
            {
                return frontmost.Value;
            }
            if (previous.HasValue && (runningProviders.Count == 0 || runningProviders.Contains(previous.Value)))
            {
                return previous.Value;
            }
            foreach (UsageProvider provider in Enum.GetValues(typeof(UsageProvider)))
            {
                if (runningProviders.Contains(provider)) return provider;
            }
            return previous ?? UsageProvider.Codex;
        }

        private void PollProcesses()
        {
            var now = CurrentlyRunning();
            var launched = now.Except(running).ToList();
            var terminated = running.Except(now).ToList();
            running = now;

            foreach (var provider in launched)
            {
                var handler = Launched;
                if (handler != null) handler(provider);
            }
            foreach (var provider in terminated)
            {
                var handler = Terminated;
                if (handler != null) handler(provider);
            }
        }

        private static HashSet<UsageProvider> CurrentlyRunning()
        {
            var result = new HashSet<UsageProvider>();
            foreach (UsageProvider provider in Enum.GetValues(typeof(UsageProvider)))
            {
                if (ProviderApps.IsRunning(provider)) result.Add(provider);
            }
            return result;
        }

        private void OnForegroundChanged(
            IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            var provider = ProviderForWindow(hwnd);
            if (!provider.HasValue) return;

            // 프로세스 확인 주기(3초)보다 먼저 창이 뜨면 여기서 실행으로 처리한다.
            if (running.Add(provider.Value))
            {
                var launched = Launched;
                if (launched != null) launched(provider.Value);
            }
            var handler = Activated;
            if (handler != null) handler(provider.Value);
        }

        private static UsageProvider? ProviderForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            uint processId;
            Native.GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0) return null;
            return ProviderApps.ProviderForPath(Native.ProcessImagePath(processId));
        }

        public void Dispose()
        {
            processTimer.Dispose();
            if (foregroundHook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(foregroundHook);
                foregroundHook = IntPtr.Zero;
            }
        }
    }
}

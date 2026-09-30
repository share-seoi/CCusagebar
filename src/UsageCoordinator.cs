using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CCusagebar
{
    internal enum UsageProvider
    {
        Codex,
        Claude
    }

    internal static class UsageProviderExtensions
    {
        public static string DisplayName(this UsageProvider provider)
        {
            return provider == UsageProvider.Codex ? "Codex" : "Claude";
        }

        public static string Key(this UsageProvider provider)
        {
            return provider == UsageProvider.Codex ? "codex" : "claude";
        }

        public static UsageProvider? FromKey(string key)
        {
            if (key == "codex") return UsageProvider.Codex;
            if (key == "claude") return UsageProvider.Claude;
            return null;
        }
    }

    internal enum ConnectionHealth
    {
        Ok,
        Working,
        Degraded,
        Error
    }

    internal sealed class ProviderUsageState
    {
        public UsageSnapshot Snapshot;
        public DateTime? CheckedAt;
        public string Status;
        public ConnectionHealth Health;
    }

    /// 로컬 기록 추적과 실시간 조회의 우선순위, 새로고침 상태를 관리한다. 모든 콜백은 UI 스레드에서 호출된다.
    internal sealed class UsageCoordinator : IDisposable
    {
        public event Action<UsageSnapshot> SnapshotChanged;
        public event Action<DateTime> Checked;
        public event Action<string, ConnectionHealth> StatusChanged;

        private readonly Func<UsageSnapshot> loadLocal;
        // null이면 실시간 조회 없이 로컬 기록만 추적한다.
        private readonly Func<UsageSnapshot> fetchLive;
        private readonly string sourceName;
        private readonly string localStatus;
        private readonly string liveSuccessStatus;
        private readonly Timer pollTimer;

        private TaskScheduler uiScheduler;
        private UsageSnapshot lastSnapshot;
        private DateTime? lastLiveCheckedAt;
        private bool liveHealthy;
        private bool liveRefreshInProgress;
        private bool scanning;
        private bool started;
        private bool restoredCache;
        private TimeSpan? liveInterval;
        private readonly TimeSpan normalLocalInterval;
        private TimeSpan localInterval;
        // 앱이 꺼져 있을 때: 로컬 기록만 느리게 확인하고 실시간 조회는 하지 않는다.
        private bool backgroundOnly;
        private DateTime lastLocalAttempt = DateTime.MinValue;
        private DateTime lastLiveAttempt = DateTime.MinValue;
        private string liveError;
        private int generation;

        public UsageCoordinator(
            Func<UsageSnapshot> loadLocal,
            Func<UsageSnapshot> fetchLive,
            string sourceName,
            string localStatus,
            string liveSuccessStatus,
            TimeSpan pollInterval,
            TimeSpan? liveInterval = null)
        {
            this.loadLocal = loadLocal;
            this.fetchLive = fetchLive;
            this.sourceName = sourceName;
            this.localStatus = localStatus;
            this.liveSuccessStatus = liveSuccessStatus;
            this.liveInterval = liveInterval;
            this.normalLocalInterval = pollInterval;
            this.localInterval = pollInterval;
            // Check deadlines each second so a few milliseconds of timer jitter do not
            // skip an entire 20-second refresh period. File/API work still runs every 20s.
            pollTimer = new Timer { Interval = 1000 };
            pollTimer.Tick += delegate
            {
                if (DateTime.UtcNow - lastLocalAttempt >= localInterval) ScanLocal();
                if (!backgroundOnly && liveInterval.HasValue && DateTime.UtcNow - lastLiveAttempt >= liveInterval.Value)
                    RefreshLive(false);
            };
        }

        /// 실시간 자동 조회 간격을 바꾼다. null이면 시작·수동 새로고침 때만 조회한다.
        public void SetLiveInterval(TimeSpan? interval)
        {
            liveInterval = interval;
        }

        public bool IsStarted
        {
            get { return started; }
        }

        public void Start(UsageSnapshot cached)
        {
            if (started)
            {
                if (!backgroundOnly) return;
                // 꺼져 있던 앱이 켜졌다: 로컬 확인 간격을 되돌리고 실시간 조회를 재개한다.
                backgroundOnly = false;
                localInterval = normalLocalInterval;
                ScanLocal();
                if (fetchLive != null) RefreshLive(false);
                return;
            }
            StartPolling(cached);
            if (fetchLive != null)
            {
                RefreshLive(false);
            }
        }

        /// 앱은 꺼져 있지만 다른 경로(예: Claude에서 부른 Codex CLI)로 쓰일 수 있을 때.
        /// 프로세스나 네트워크 없이 로컬 기록만 interval마다 확인한다.
        public void StartBackground(UsageSnapshot cached, TimeSpan interval)
        {
            backgroundOnly = true;
            localInterval = interval;
            // 실시간 값 대신 로컬 기록 상태가 보이게 한다.
            liveHealthy = false;
            liveRefreshInProgress = false;
            generation++;
            if (started)
            {
                ScanLocal();
                return;
            }
            StartPolling(cached);
        }

        public bool IsBackground
        {
            get { return started && backgroundOnly; }
        }

        private void StartPolling(UsageSnapshot cached)
        {
            started = true;
            generation++;
            uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();

            if (!restoredCache && cached != null)
            {
                restoredCache = true;
                ApplyLocal(cached);
                RaiseStatus("저장된 값 · " + sourceName + " 연결 중…", ConnectionHealth.Working);
            }

            pollTimer.Start();
            ScanLocal();
        }

        public void Stop()
        {
            if (!started) return;
            started = false;
            backgroundOnly = false;
            localInterval = normalLocalInterval;
            generation++;
            liveRefreshInProgress = false;
            scanning = false;
            liveHealthy = false;
            pollTimer.Stop();
        }

        public void RequestLocalRefresh()
        {
            ScanLocal();
        }

        public void RefreshLive(bool isManual)
        {
            if (!started) return;
            if (fetchLive == null || backgroundOnly)
            {
                ScanLocal();
                return;
            }
            if (liveRefreshInProgress) return;
            if (DateTime.UtcNow - lastLiveAttempt < TimeSpan.FromSeconds(20)) return;
            lastLiveAttempt = DateTime.UtcNow;
            int requestGeneration = generation;
            liveRefreshInProgress = true;
            if (isManual || !liveHealthy)
            {
                RaiseStatus(isManual ? "실시간 계정 새로고침 중…" : "실시간 계정 확인 중…", ConnectionHealth.Working);
            }

            Task.Factory.StartNew(fetchLive, TaskCreationOptions.LongRunning).ContinueWith(task =>
            {
                var failure = task.IsFaulted ? task.Exception : null;
                if (!started || requestGeneration != generation) return;
                liveRefreshInProgress = false;
                if (failure != null || task.Result == null)
                {
                    liveHealthy = false;
                    liveError = failure == null ? "유효한 사용량 응답이 없음" : ErrorMessage(failure);
                    RaiseStatus(liveError + (lastSnapshot == null ? "" : " · 마지막 기록 표시"),
                        lastSnapshot == null ? ConnectionHealth.Error : ConnectionHealth.Degraded);
                    if (isManual) ScanLocal();
                    return;
                }

                liveHealthy = true;
                liveError = null;
                ApplyLive(task.Result);
                RaiseChecked(task.Result.FetchedAt);
                // The status stays on screen until the next lookup, so say when this one happened.
                RaiseStatus(liveSuccessStatus + " · 마지막 조회 " + UsageFormat.Time(DateTime.UtcNow), ConnectionHealth.Ok);
            }, uiScheduler);
        }

        /// 마지막 실시간 조회가 maxAge보다 오래됐을 때만 조회한다(앱 전환처럼 자주 불리는 곳용).
        public void RefreshLiveIfOlderThan(TimeSpan maxAge)
        {
            if (DateTime.UtcNow - lastLiveAttempt < maxAge) return;
            RefreshLive(false);
        }

        private void ScanLocal()
        {
            if (!started || scanning) return;
            lastLocalAttempt = DateTime.UtcNow;
            scanning = true;
            int requestGeneration = generation;

            Task.Factory.StartNew(loadLocal).ContinueWith(task =>
            {
                var failure = task.IsFaulted ? task.Exception : null;
                if (!started || requestGeneration != generation) return;
                scanning = false;
                // 실시간 조회가 정상이거나 진행 중이면 로컬 상태 문구로 덮어쓰지 않는다.
                bool showLocalStatus = !liveRefreshInProgress && !liveHealthy;

                if (failure != null)
                {
                    if (showLocalStatus && liveError == null) RaiseStatus(ErrorMessage(failure), ConnectionHealth.Error);
                    return;
                }
                if (task.Result == null)
                {
                    if (showLocalStatus && liveError == null) RaiseStatus(sourceName + " 사용 기록 대기 중", ConnectionHealth.Working);
                    return;
                }

                ApplyLocal(task.Result);
                if (showLocalStatus)
                {
                    RaiseChecked(task.Result.FetchedAt);
                    RaiseStatus(liveError == null ? LocalStatus() : liveError + " · 마지막 기록 표시",
                        liveError == null ? ConnectionHealth.Ok : ConnectionHealth.Degraded);
                }
            }, uiScheduler);
        }

        private string LocalStatus()
        {
            if (!backgroundOnly) return localStatus;
            var minutes = (int)Math.Round(localInterval.TotalMinutes);
            return localStatus + " · 앱 꺼짐 · " + (minutes >= 1 ? minutes + "분" : (int)localInterval.TotalSeconds + "초") + "마다 확인";
        }

        private void ApplyLocal(UsageSnapshot snapshot)
        {
            if (lastLiveCheckedAt.HasValue && snapshot.FetchedAt <= lastLiveCheckedAt.Value)
            {
                return;
            }
            Apply(snapshot);
        }

        private void ApplyLive(UsageSnapshot snapshot)
        {
            lastLiveCheckedAt = snapshot.FetchedAt;
            Apply(snapshot);
        }

        private void Apply(UsageSnapshot snapshot)
        {
            if (Equals(snapshot, lastSnapshot)) return;
            lastSnapshot = snapshot;
            var handler = SnapshotChanged;
            if (handler != null) handler(snapshot);
        }

        private void RaiseChecked(DateTime at)
        {
            var handler = Checked;
            if (handler != null) handler(at);
        }

        private void RaiseStatus(string status, ConnectionHealth health)
        {
            var handler = StatusChanged;
            if (handler != null) handler(status, health);
        }

        private static string ErrorMessage(AggregateException exception)
        {
            var inner = exception == null ? null : exception.Flatten().InnerException;
            return inner == null ? "알 수 없는 오류" : inner.Message;
        }

        public void Dispose()
        {
            Stop();
            pollTimer.Dispose();
        }
    }
}

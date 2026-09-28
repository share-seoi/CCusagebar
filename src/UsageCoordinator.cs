using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexUsageBar
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
        private readonly TimeSpan? liveInterval;
        private readonly TimeSpan localInterval;
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
            this.localInterval = pollInterval;
            // Check deadlines each second so a few milliseconds of timer jitter do not
            // skip an entire 20-second refresh period. File/API work still runs every 20s.
            pollTimer = new Timer { Interval = 1000 };
            pollTimer.Tick += delegate
            {
                if (DateTime.UtcNow - lastLocalAttempt >= localInterval) ScanLocal();
                if (liveInterval.HasValue && DateTime.UtcNow - lastLiveAttempt >= liveInterval.Value)
                    RefreshLive(false);
            };
        }

        public bool IsStarted
        {
            get { return started; }
        }

        public void Start(UsageSnapshot cached)
        {
            if (started) return;
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
            if (fetchLive != null)
            {
                RefreshLive(false);
            }
        }

        public void Stop()
        {
            if (!started) return;
            started = false;
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
            if (fetchLive == null)
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
                RaiseStatus(liveSuccessStatus, ConnectionHealth.Ok);
            }, uiScheduler);
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
                    RaiseStatus(liveError == null ? localStatus : liveError + " · 마지막 기록 표시",
                        liveError == null ? ConnectionHealth.Ok : ConnectionHealth.Degraded);
                }
            }, uiScheduler);
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CCusagebar
{
    internal sealed class UsageStoreException : Exception
    {
        public UsageStoreException(string message) : base(message)
        {
        }
    }

    /// Codex가 로컬 세션 기록에 이미 남긴 최신 rate-limit 스냅샷을 읽는다.
    /// Windows에는 SQLite가 기본으로 없어서, 상태 DB 대신 세션 파일의 수정 시각으로 최근 세션을 고른다.
    internal sealed class CodexLocalStore
    {
        private const int RecentFileLimit = 12;
        private const long MaximumTailBytes = 512 * 1024;
        private static readonly byte[] Marker = Encoding.UTF8.GetBytes("\"rate_limits\"");

        private readonly string codexHome;
        // 파일이 바뀌지 않았으면 다시 읽지 않는다.
        private readonly Dictionary<string, CachedFile> cache = new Dictionary<string, CachedFile>();

        public CodexLocalStore(string codexHome = null)
        {
            this.codexHome = codexHome ?? DefaultCodexHome();
        }

        public static string DefaultCodexHome()
        {
            var overridden = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(overridden))
            {
                return overridden;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        public UsageSnapshot LatestSnapshot()
        {
            var files = RecentRolloutFiles();
            UsageSnapshot newest = null;
            foreach (var file in files)
            {
                var snapshot = SnapshotFor(file);
                if (snapshot != null && (newest == null || snapshot.FetchedAt > newest.FetchedAt))
                {
                    newest = snapshot;
                }
            }
            return newest == null ? null : newest.AdjustedForCurrentTime(DateTime.UtcNow);
        }

        private List<FileInfo> RecentRolloutFiles()
        {
            var roots = new[] { "sessions", "archived_sessions" }
                .Select(name => new DirectoryInfo(Path.Combine(codexHome, name)))
                .Where(dir => dir.Exists)
                .ToList();
            if (roots.Count == 0)
            {
                throw new UsageStoreException("Codex 세션 기록 폴더를 찾을 수 없음");
            }

            var files = roots
                .SelectMany(dir => dir.EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(RecentFileLimit)
                .ToList();
            if (files.Count == 0)
            {
                throw new UsageStoreException("최근 Codex 세션을 찾을 수 없음");
            }
            return files;
        }

        private UsageSnapshot SnapshotFor(FileInfo file)
        {
            CachedFile cached;
            if (cache.TryGetValue(file.FullName, out cached)
                && cached.Length == file.Length
                && cached.LastWrite == file.LastWriteTimeUtc)
            {
                return cached.Snapshot;
            }

            UsageSnapshot snapshot = null;
            try
            {
                snapshot = ReadLatestSnapshot(file.FullName);
            }
            catch (IOException)
            {
                // Codex가 쓰는 중이면 다음 확인 때 다시 읽는다.
                return cached != null ? cached.Snapshot : null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            if (cache.Count > 64)
            {
                cache.Clear();
            }
            cache[file.FullName] = new CachedFile
            {
                Length = file.Length,
                LastWrite = file.LastWriteTimeUtc,
                Snapshot = snapshot
            };
            return snapshot;
        }

        private static UsageSnapshot ReadLatestSnapshot(string path)
        {
            byte[] data;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long offset = Math.Max(0, stream.Length - MaximumTailBytes);
                stream.Seek(offset, SeekOrigin.Begin);
                data = new byte[stream.Length - offset];
                int read = 0;
                while (read < data.Length)
                {
                    int count = stream.Read(data, read, data.Length - read);
                    if (count <= 0) break;
                    read += count;
                }
                if (read < data.Length)
                {
                    Array.Resize(ref data, read);
                }

                if (offset > 0)
                {
                    // 중간에서 잘린 첫 줄은 버린다.
                    int firstNewline = Array.IndexOf(data, (byte)'\n');
                    if (firstNewline < 0) return null;
                    data = data.Skip(firstNewline + 1).ToArray();
                }
            }

            int end = data.Length;
            while (end > 0)
            {
                int start = end - 1;
                while (start >= 0 && data[start] != (byte)'\n') start--;
                int lineStart = start + 1;
                int lineLength = end - lineStart;
                if (lineLength > 0 && Contains(data, lineStart, lineLength, Marker))
                {
                    var snapshot = RateLimitParser.ParseSessionEventLine(Encoding.UTF8.GetString(data, lineStart, lineLength));
                    if (snapshot != null)
                    {
                        return snapshot;
                    }
                }
                end = start;
            }
            return null;
        }

        private static bool Contains(byte[] data, int start, int length, byte[] pattern)
        {
            int last = start + length - pattern.Length;
            for (int i = start; i <= last; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return true;
            }
            return false;
        }

        private sealed class CachedFile
        {
            public long Length;
            public DateTime LastWrite;
            public UsageSnapshot Snapshot;
        }
    }

    /// Claude 데스크톱 앱이 직접 기록하는 plan-usage-history.json에서 최신 사용률을 읽는다.
    /// 로그인 토큰이나 네트워크 요청을 사용하지 않는다.
    internal sealed class ClaudeUsageStore
    {
        private const int FiveHourMinutes = 300;
        private const int SevenDayMinutes = 10080;

        public UsageSnapshot LatestSnapshot()
        {
            var path = HistoryPath();
            if (path == null)
            {
                throw new UsageStoreException("Claude 사용량 기록 파일을 찾을 수 없음");
            }

            string text;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }
            }
            catch (IOException)
            {
                throw new UsageStoreException("Claude 사용량 기록 파일을 읽을 수 없음");
            }
            return ParseHistory(text, DateTime.UtcNow);
        }

        /// 일반 설치(%APPDATA%\Claude)와 MSIX 가상화 경로 중 가장 최근에 기록된 파일을 쓴다.
        public static string HistoryPath()
        {
            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json")
            };
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            try
            {
                if (Directory.Exists(packages))
                {
                    candidates.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                        .Select(dir => Path.Combine(dir, "LocalCache", "Roaming", "Claude", "plan-usage-history.json")));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return candidates
                .Where(candidate => File.Exists(candidate))
                .OrderByDescending(candidate => File.GetLastWriteTimeUtc(candidate))
                .FirstOrDefault();
        }

        public static UsageSnapshot ParseHistory(string text, DateTime nowUtc)
        {
            var root = Json.TryParse(text) as Dictionary<string, object>;
            var samples = Json.Get(root, "samples") as object[];
            if (samples == null)
            {
                throw new UsageStoreException("Claude 사용량 기록을 읽을 수 없음");
            }

            DateTime? latestAt = null;
            Dictionary<string, object> latestUsage = null;
            foreach (var sample in samples.OfType<Dictionary<string, object>>())
            {
                var milliseconds = Json.Number(Json.Get(sample, "t"));
                var usage = Json.Object(sample, "u");
                if (!milliseconds.HasValue || usage == null)
                {
                    continue;
                }
                var sampledAt = Json.FromUnixMilliseconds(milliseconds.Value);
                if (!latestAt.HasValue || sampledAt > latestAt.Value)
                {
                    latestAt = sampledAt;
                    latestUsage = usage;
                }
            }
            if (!latestAt.HasValue)
            {
                return null;
            }

            var windows = new List<UsageWindow>();
            var fiveHour = Window(Json.Get(latestUsage, "fh"), "5시간 한도", FiveHourMinutes, latestAt.Value, nowUtc);
            if (fiveHour != null) windows.Add(fiveHour);
            var sevenDay = Window(Json.Get(latestUsage, "sd"), "주간 한도", SevenDayMinutes, latestAt.Value, nowUtc);
            if (sevenDay != null) windows.Add(sevenDay);

            return windows.Count == 0 ? null : new UsageSnapshot(windows, null, latestAt.Value);
        }

        // Fallback records have no reset timestamp. Keep the observed value and its age;
        // elapsed time alone cannot tell us how much was used on another device.
        private static UsageWindow Window(object value, string label, int minutes, DateTime sampledAt, DateTime nowUtc)
        {
            var utilization = Json.Number(value);
            if (!utilization.HasValue)
            {
                return null;
            }
            return new UsageWindow(
                label,
                (int)Math.Round(utilization.Value, MidpointRounding.AwayFromZero),
                minutes,
                null);
        }
    }
}

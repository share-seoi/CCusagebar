using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace CCusagebar
{
    internal sealed class ClaudeUsageResponse
    {
        public int StatusCode;
        public string Body;
        public string RetryAfter;
    }

    /// Read-only credentials; requests go only to Anthropic's usage endpoint.
    internal sealed class ClaudeLiveFetcher
    {
        private readonly Func<ClaudeToken> readToken;
        private readonly Func<ClaudeToken, ClaudeUsageResponse> send;
        private readonly Func<DateTime> clock;
        private readonly object gate = new object();
        private DateTime retryAt;
        private int backoffSeconds = 120;

        public ClaudeLiveFetcher() : this(ReadToken, Send, () => DateTime.UtcNow) { }

        internal ClaudeLiveFetcher(Func<ClaudeToken> readToken,
            Func<ClaudeToken, ClaudeUsageResponse> send, Func<DateTime> clock)
        {
            this.readToken = readToken;
            this.send = send;
            this.clock = clock;
        }

        public UsageSnapshot FetchSynchronously()
        {
            lock (gate)
            {
                if (clock() < retryAt)
                    throw new LiveUsageException("Claude 조회 대기 · " + retryAt.ToLocalTime().ToString("HH:mm:ss") + " 이후 재시도");
                // Re-read on every request to pick up tokens renewed by Claude itself.
                var token = readToken();
                if (token == null) throw new LiveUsageException("Claude 로그인 토큰 없음 · Claude 앱에 로그인 필요");
                if (token.IsExpired(clock())) throw new LiveUsageException("Claude 로그인 토큰 만료 · Claude 앱에서 로그인 갱신 필요");

                // UsageCoordinator owns the 20-second cadence (including manual requests).
                // Only server backoff lives here: two independent success timers can
                // miss each other's boundary and accidentally turn 20 seconds into 40.
                var response = send(token);
                if (response.StatusCode == 200)
                {
                    var result = ParseUsage(response.Body, clock());
                    backoffSeconds = 120;
                    return result;
                }
                if (response.StatusCode == 401 || response.StatusCode == 403)
                    throw new LiveUsageException("Claude 토큰 인증 실패 (HTTP " + response.StatusCode + ") · Claude 앱 로그인 확인 필요");
                if (response.StatusCode == 429)
                {
                    retryAt = RetryTime(response.RetryAfter, clock(), backoffSeconds);
                    backoffSeconds = Math.Min(backoffSeconds * 2, 900);
                    throw new LiveUsageException("Claude 조회 제한 · " + retryAt.ToLocalTime().ToString("HH:mm:ss") + " 이후 재시도");
                }
                throw new LiveUsageException("Claude 사용량 조회 실패 (HTTP " + response.StatusCode + ")");
            }
        }

        internal static DateTime RetryTime(string header, DateTime now, int fallbackSeconds)
        {
            double seconds;
            if (double.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                && !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0 && seconds <= 31536000)
                return now.AddSeconds(Math.Max(fallbackSeconds, seconds));
            DateTimeOffset date;
            if (DateTimeOffset.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)
                && date.UtcDateTime > now.AddSeconds(fallbackSeconds)) return date.UtcDateTime;
            return now.AddSeconds(fallbackSeconds);
        }

        internal static ClaudeToken ReadToken()
        {
            var explicitToken = Environment.GetEnvironmentVariable("CLAUDE_USAGE_ACCESS_TOKEN");
            if (!string.IsNullOrWhiteSpace(explicitToken))
                return new ClaudeToken(explicitToken.Trim(), null, "environment");

            // Prefer the desktop profile that is being displayed. Do not switch accounts
            // when its login is expired, unreadable, or rejected by the server.
            if (File.Exists(Path.Combine(ClaudeDesktopToken.ClaudeDataDirectory(), "config.json")))
            {
                try { return ClaudeDesktopToken.Read(); }
                catch (Exception) { throw new LiveUsageException("Claude 앱 로그인 토큰을 읽을 수 없음 · Claude 앱 로그인 확인 필요"); }
            }

            var directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            var path = Path.Combine(directory, ".credentials.json");
            if (!File.Exists(path)) return null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return ParseCodeCredentials(reader.ReadToEnd());
            }
            catch (Exception) { throw new LiveUsageException("Claude Code 로그인 토큰을 읽을 수 없음"); }
        }

        internal static ClaudeToken ParseCodeCredentials(string text)
        {
            var root = Json.TryParse(text) as Dictionary<string, object>;
            var oauth = Json.Object(root, "claudeAiOauth") ?? root;
            var token = Json.Get(oauth, "accessToken") as string;
            if (string.IsNullOrWhiteSpace(token)) return null;
            var expiry = Json.Number(Json.Get(oauth, "expiresAt"));
            return new ClaudeToken(token, expiry.HasValue ? (DateTime?)Json.FromUnixMilliseconds(expiry.Value) : null, "claude-code");
        }

        private static ClaudeUsageResponse Send(ClaudeToken token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.Accept = "application/json";
            request.UserAgent = "CCusagebar/1.2-windows";
            request.Headers["Authorization"] = "Bearer " + token.AccessToken;
            request.Headers["anthropic-beta"] = "oauth-2025-04-20";
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse()) return ReadResponse(response);
            }
            catch (WebException error)
            {
                using (var response = error.Response as HttpWebResponse)
                {
                    if (response != null) return ReadResponse(response);
                    throw new LiveUsageException(error.Status == WebExceptionStatus.Timeout
                        ? "Claude 사용량 조회 시간 초과" : "Claude 사용량 API 네트워크 연결 실패");
                }
            }
        }

        private static ClaudeUsageResponse ReadResponse(HttpWebResponse response)
        {
            // Never surface error bodies; they can contain authentication details.
            var result = new ClaudeUsageResponse { StatusCode = (int)response.StatusCode, RetryAfter = response.Headers["Retry-After"] };
            if (result.StatusCode == 200)
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) result.Body = reader.ReadToEnd();
            return result;
        }

        internal static UsageSnapshot ParseUsage(string text, DateTime fetchedAt)
        {
            var root = Json.TryParse(text) as Dictionary<string, object>;
            var windows = new List<UsageWindow>();
            AddWindow(windows, root, "five_hour", "5시간 한도", 300);
            AddWindow(windows, root, "seven_day", "주간 한도", 10080);
            if (windows.Count == 0) throw new LiveUsageException("Claude 사용량 응답에 유효한 한도가 없음");
            return new UsageSnapshot(windows, null, fetchedAt);
        }

        private static void AddWindow(List<UsageWindow> windows, Dictionary<string, object> root,
            string key, string label, int minutes)
        {
            var value = Json.Object(root, key);
            var used = Json.Number(Json.Get(value, "utilization"));
            if (!used.HasValue || double.IsNaN(used.Value) || double.IsInfinity(used.Value) || used < 0 || used > 100) return;
            windows.Add(new UsageWindow(label, (int)Math.Round(used.Value, MidpointRounding.AwayFromZero),
                minutes, Json.ParseIsoDate(Json.Get(value, "resets_at") as string)));
        }
    }
}

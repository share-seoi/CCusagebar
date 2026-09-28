using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace CodexUsageBar
{
    internal sealed class UsageWindow
    {
        public readonly string Label;
        public readonly int UsedPercent;
        public readonly int? WindowDurationMinutes;
        public readonly DateTime? ResetsAt;

        public UsageWindow(string label, int usedPercent, int? windowDurationMinutes, DateTime? resetsAt)
        {
            Label = label;
            UsedPercent = usedPercent;
            WindowDurationMinutes = windowDurationMinutes;
            ResetsAt = resetsAt;
        }

        public int RemainingPercent
        {
            get { return Math.Max(0, Math.Min(100, 100 - UsedPercent)); }
        }

        public override bool Equals(object obj)
        {
            var other = obj as UsageWindow;
            return other != null
                && other.Label == Label
                && other.UsedPercent == UsedPercent
                && other.WindowDurationMinutes == WindowDurationMinutes
                && other.ResetsAt == ResetsAt;
        }

        public override int GetHashCode()
        {
            return (Label ?? "").GetHashCode() ^ UsedPercent;
        }
    }

    internal sealed class UsageSnapshot
    {
        public readonly List<UsageWindow> Windows;
        public readonly string PlanType;
        public readonly DateTime FetchedAt;

        public UsageSnapshot(List<UsageWindow> windows, string planType, DateTime fetchedAt)
        {
            Windows = windows;
            PlanType = planType;
            FetchedAt = fetchedAt;
        }

        public int OverallRemainingPercent
        {
            get { return Windows.Count == 0 ? 0 : Windows.Min(w => w.RemainingPercent); }
        }

        public UsageWindow LimitingWindow
        {
            get { return Windows.OrderBy(w => w.RemainingPercent).FirstOrDefault(); }
        }

        /// 초기화 시각이 지난 한도는 사용량 0으로 본다.
        public UsageSnapshot AdjustedForCurrentTime(DateTime nowUtc)
        {
            var adjusted = Windows.Select(w =>
                w.ResetsAt.HasValue && w.ResetsAt.Value <= nowUtc
                    ? new UsageWindow(w.Label, 0, w.WindowDurationMinutes, null)
                    : w).ToList();
            return new UsageSnapshot(adjusted, PlanType, FetchedAt);
        }

        public override bool Equals(object obj)
        {
            var other = obj as UsageSnapshot;
            return other != null
                && other.PlanType == PlanType
                && other.FetchedAt == FetchedAt
                && other.Windows.SequenceEqual(Windows);
        }

        public override int GetHashCode()
        {
            return FetchedAt.GetHashCode();
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "planType", PlanType },
                { "fetchedAt", Json.ToUnixMilliseconds(FetchedAt) },
                { "windows", Windows.Select(w => new Dictionary<string, object>
                    {
                        { "label", w.Label },
                        { "usedPercent", w.UsedPercent },
                        { "windowDurationMinutes", w.WindowDurationMinutes },
                        { "resetsAt", w.ResetsAt.HasValue ? (object)Json.ToUnixMilliseconds(w.ResetsAt.Value) : null }
                    }).ToArray() }
            };
        }

        public static UsageSnapshot FromJson(object value)
        {
            var root = value as Dictionary<string, object>;
            if (root == null)
            {
                return null;
            }
            var fetchedAt = Json.Number(Json.Get(root, "fetchedAt"));
            var items = Json.Get(root, "windows") as object[];
            if (!fetchedAt.HasValue || items == null)
            {
                return null;
            }

            var windows = new List<UsageWindow>();
            foreach (var item in items.OfType<Dictionary<string, object>>())
            {
                var used = Json.Number(Json.Get(item, "usedPercent"));
                if (!used.HasValue)
                {
                    continue;
                }
                var duration = Json.Number(Json.Get(item, "windowDurationMinutes"));
                var resetsAt = Json.Number(Json.Get(item, "resetsAt"));
                windows.Add(new UsageWindow(
                    Json.Get(item, "label") as string ?? "한도",
                    (int)used.Value,
                    duration.HasValue ? (int?)(int)duration.Value : null,
                    resetsAt.HasValue ? (DateTime?)Json.FromUnixMilliseconds(resetsAt.Value) : null));
            }
            if (windows.Count == 0)
            {
                return null;
            }
            return new UsageSnapshot(windows, Json.Get(root, "planType") as string, Json.FromUnixMilliseconds(fetchedAt.Value));
        }
    }

    internal static class Json
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static object Parse(string text)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 256;
            return serializer.DeserializeObject(text);
        }

        public static object TryParse(string text)
        {
            try
            {
                return Parse(text);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Serialize(object value)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.Serialize(value);
        }

        public static object Get(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        public static Dictionary<string, object> Object(Dictionary<string, object> dictionary, string key)
        {
            return Get(dictionary, key) as Dictionary<string, object>;
        }

        public static double? Number(object value)
        {
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is decimal) return (double)(decimal)value;
            if (value is double) return (double)value;
            if (value is float) return (float)value;
            return null;
        }

        public static int? Int(object value)
        {
            var number = Number(value);
            return number.HasValue ? (int?)(int)number.Value : null;
        }

        public static long ToUnixMilliseconds(DateTime utc)
        {
            return (long)(utc.ToUniversalTime() - Epoch).TotalMilliseconds;
        }

        public static DateTime FromUnixMilliseconds(double milliseconds)
        {
            return Epoch.AddMilliseconds(milliseconds);
        }

        public static DateTime FromUnixSeconds(double seconds)
        {
            return Epoch.AddSeconds(seconds);
        }

        public static DateTime? ParseIsoDate(string text)
        {
            DateTime parsed;
            if (text != null && DateTime.TryParse(
                    text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }
            return null;
        }
    }

    internal static class RateLimitParser
    {
        /// Codex App Server의 account/rateLimits/read 응답(id 2)을 읽는다.
        public static UsageSnapshot ParseResponseObject(Dictionary<string, object> root, DateTime fetchedAt)
        {
            var result = Json.Object(root, "result");
            var snapshot = PreferredSnapshot(result);
            if (snapshot == null)
            {
                return null;
            }

            var windows = new List<UsageWindow>();
            var primary = ParseWindow(Json.Object(snapshot, "primary"), "기본 한도", "usedPercent", "windowDurationMins", "resetsAt");
            if (primary != null) windows.Add(primary);
            var secondary = ParseWindow(Json.Object(snapshot, "secondary"), "보조 한도", "usedPercent", "windowDurationMins", "resetsAt");
            if (secondary != null) windows.Add(secondary);

            if (windows.Count == 0)
            {
                var individual = Json.Object(snapshot, "individualLimit");
                var remaining = Json.Int(Json.Get(individual, "remainingPercent"));
                if (remaining.HasValue)
                {
                    var resetsAt = Json.Number(Json.Get(individual, "resetsAt"));
                    windows.Add(new UsageWindow(
                        "개인 한도",
                        100 - remaining.Value,
                        null,
                        resetsAt.HasValue ? (DateTime?)Json.FromUnixSeconds(resetsAt.Value) : null));
                }
            }

            if (windows.Count == 0)
            {
                return null;
            }
            return new UsageSnapshot(SortByDuration(windows), Json.Get(snapshot, "planType") as string, fetchedAt);
        }

        /// Codex 세션 기록(rollout-*.jsonl)의 token_count 이벤트 한 줄을 읽는다.
        public static UsageSnapshot ParseSessionEventLine(string line)
        {
            var root = Json.TryParse(line) as Dictionary<string, object>;
            var payload = Json.Object(root, "payload");
            if (payload == null || (Json.Get(payload, "type") as string) != "token_count")
            {
                return null;
            }
            var snapshot = Json.Object(payload, "rate_limits");
            if (snapshot == null)
            {
                return null;
            }

            var windows = new List<UsageWindow>();
            var primary = ParseWindow(Json.Object(snapshot, "primary"), "기본 한도", "used_percent", "window_minutes", "resets_at");
            if (primary != null) windows.Add(primary);
            var secondary = ParseWindow(Json.Object(snapshot, "secondary"), "보조 한도", "used_percent", "window_minutes", "resets_at");
            if (secondary != null) windows.Add(secondary);

            if (windows.Count == 0)
            {
                return null;
            }

            var fetchedAt = Json.ParseIsoDate(Json.Get(root, "timestamp") as string) ?? DateTime.UtcNow;
            return new UsageSnapshot(SortByDuration(windows), Json.Get(snapshot, "plan_type") as string, fetchedAt);
        }

        private static Dictionary<string, object> PreferredSnapshot(Dictionary<string, object> result)
        {
            var codex = Json.Object(Json.Object(result, "rateLimitsByLimitId"), "codex");
            return codex ?? Json.Object(result, "rateLimits");
        }

        private static UsageWindow ParseWindow(
            Dictionary<string, object> dictionary, string fallbackLabel,
            string usedKey, string durationKey, string resetsKey)
        {
            var used = Json.Int(Json.Get(dictionary, usedKey));
            if (!used.HasValue)
            {
                return null;
            }
            var duration = Json.Int(Json.Get(dictionary, durationKey));
            var resetsAt = Json.Number(Json.Get(dictionary, resetsKey));
            return new UsageWindow(
                Label(duration, fallbackLabel),
                used.Value,
                duration,
                resetsAt.HasValue ? (DateTime?)Json.FromUnixSeconds(resetsAt.Value) : null);
        }

        private static List<UsageWindow> SortByDuration(List<UsageWindow> windows)
        {
            return windows.OrderBy(w => w.WindowDurationMinutes ?? int.MaxValue).ToList();
        }

        private static string Label(int? duration, string fallback)
        {
            if (!duration.HasValue)
            {
                return fallback;
            }
            switch (duration.Value)
            {
                case 300:
                    return "5시간 한도";
                case 1440:
                    return "일일 한도";
                case 10080:
                    return "주간 한도";
            }
            if (duration.Value % 1440 == 0)
            {
                return (duration.Value / 1440) + "일 한도";
            }
            if (duration.Value % 60 == 0)
            {
                return (duration.Value / 60) + "시간 한도";
            }
            return fallback;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace CodexUsageBar
{
    /// 마지막으로 표시한 앱과 마지막 스냅샷을 %LOCALAPPDATA%\CodexUsageBar\state.json에 저장한다.
    internal sealed class AppSettings
    {
        private readonly string path;
        private readonly Dictionary<UsageProvider, UsageSnapshot> snapshots = new Dictionary<UsageProvider, UsageSnapshot>();

        public UsageProvider? LastActiveProvider;

        private AppSettings()
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageBar",
                "state.json");
        }

        public static AppSettings Load()
        {
            var settings = new AppSettings();
            try
            {
                if (!File.Exists(settings.path)) return settings;
                var root = Json.TryParse(File.ReadAllText(settings.path, Encoding.UTF8)) as Dictionary<string, object>;
                settings.LastActiveProvider = UsageProviderExtensions.FromKey(Json.Get(root, "lastActiveProvider") as string);
                var saved = Json.Object(root, "snapshots");
                foreach (UsageProvider provider in Enum.GetValues(typeof(UsageProvider)))
                {
                    var snapshot = UsageSnapshot.FromJson(Json.Get(saved, provider.Key()));
                    if (snapshot != null) settings.snapshots[provider] = snapshot;
                }
            }
            catch (Exception)
            {
                // 저장 파일이 깨졌으면 빈 상태로 시작한다.
            }
            return settings;
        }

        public UsageSnapshot Snapshot(UsageProvider provider)
        {
            UsageSnapshot snapshot;
            return snapshots.TryGetValue(provider, out snapshot) ? snapshot : null;
        }

        public void SetSnapshot(UsageProvider provider, UsageSnapshot snapshot)
        {
            snapshots[provider] = snapshot;
            Save();
        }

        public void SetLastActiveProvider(UsageProvider provider)
        {
            if (LastActiveProvider == provider) return;
            LastActiveProvider = provider;
            Save();
        }

        private void Save()
        {
            try
            {
                var saved = new Dictionary<string, object>();
                foreach (var pair in snapshots)
                {
                    saved[pair.Key.Key()] = pair.Value.ToJson();
                }
                var root = new Dictionary<string, object>
                {
                    { "lastActiveProvider", LastActiveProvider.HasValue ? LastActiveProvider.Value.Key() : null },
                    { "snapshots", saved }
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(root), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// 로그인할 때 자동 실행 여부. HKCU Run 키만 사용하므로 관리자 권한이 필요 없다.
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CodexUsageBar";

        public static bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var value = key == null ? null : key.GetValue(ValueName) as string;
                    return value != null && value.IndexOf(ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + ExecutablePath + "\"");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }

        private static string ExecutablePath
        {
            get { return System.Windows.Forms.Application.ExecutablePath; }
        }
    }
}

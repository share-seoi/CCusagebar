using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace CodexUsageBar
{
    internal sealed class LiveUsageException : Exception
    {
        public LiveUsageException(string message) : base(message)
        {
        }
    }

    /// 사용자가 요청했을 때만 Codex App Server를 잠깐 띄워 계정 rate-limit을 1회 조회하고 바로 종료한다.
    internal sealed class CodexLiveFetcher
    {
        private readonly TimeSpan timeout;

        public CodexLiveFetcher()
        {
            timeout = TimeSpan.FromSeconds(15);
        }

        public UsageSnapshot FetchSynchronously()
        {
            var executable = CodexLocator.ExecutablePath();
            if (executable == null)
            {
                throw new LiveUsageException("Codex 실행 파일을 찾을 수 없음");
            }

            var startInfo = new ProcessStartInfo(executable, "app-server")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath()
            };

            var state = new ResponseState();
            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        state.Fail(new LiveUsageException("Codex 실시간 조회 프로세스가 먼저 종료됨"));
                        return;
                    }
                    state.Consume(e.Data);
                };
                process.ErrorDataReceived += (sender, e) => { };

                try
                {
                    process.Start();
                }
                catch (Exception error)
                {
                    throw new LiveUsageException("Codex 실시간 조회 실행 실패: " + error.Message);
                }

                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var messages = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "method", "initialize" },
                            { "id", 1 },
                            { "params", new Dictionary<string, object>
                                {
                                    { "clientInfo", new Dictionary<string, object>
                                        {
                                            { "name", "codex_usage_bar" },
                                            { "title", "Codex Usage Bar" },
                                            { "version", "1.2.0-windows" }
                                        }
                                    }
                                }
                            }
                        },
                        new Dictionary<string, object>
                        {
                            { "method", "initialized" },
                            { "params", new Dictionary<string, object>() }
                        },
                        new Dictionary<string, object>
                        {
                            { "method", "account/rateLimits/read" },
                            { "id", 2 }
                        }
                    };

                    // StreamWriter의 BOM이나 콘솔 코드 페이지가 섞이지 않도록 UTF-8 바이트를 직접 쓴다.
                    var stdin = process.StandardInput.BaseStream;
                    foreach (var message in messages)
                    {
                        var bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(message) + "\n");
                        stdin.Write(bytes, 0, bytes.Length);
                    }
                    stdin.Flush();

                    if (!state.Done.WaitOne(timeout))
                    {
                        throw new LiveUsageException("Codex 실시간 조회 시간 초과");
                    }
                    return state.Result();
                }
                finally
                {
                    try
                    {
                        process.StandardInput.BaseStream.Close();
                    }
                    catch (Exception)
                    {
                    }
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(2000);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private sealed class ResponseState
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            private readonly object gate = new object();
            private UsageSnapshot snapshot;
            private Exception error;
            private bool resolved;

            public void Consume(string line)
            {
                var root = Json.TryParse(line) as Dictionary<string, object>;
                if (root == null || Json.Int(Json.Get(root, "id")) != 2)
                {
                    return;
                }

                var parsed = RateLimitParser.ParseResponseObject(root, DateTime.UtcNow);
                if (parsed != null)
                {
                    Succeed(parsed);
                    return;
                }

                var message = Json.Get(Json.Object(root, "error"), "message") as string;
                Fail(message != null
                    ? new LiveUsageException("Codex 실시간 조회 실패: " + message)
                    : new LiveUsageException("Codex 실시간 사용량 응답을 읽을 수 없음"));
            }

            public void Fail(Exception failure)
            {
                Resolve(null, failure);
            }

            public UsageSnapshot Result()
            {
                lock (gate)
                {
                    if (error != null) throw error;
                    if (snapshot == null) throw new LiveUsageException("Codex 실시간 사용량 응답을 읽을 수 없음");
                    return snapshot;
                }
            }

            private void Succeed(UsageSnapshot value)
            {
                Resolve(value, null);
            }

            private void Resolve(UsageSnapshot value, Exception failure)
            {
                lock (gate)
                {
                    if (resolved) return;
                    resolved = true;
                    snapshot = value;
                    error = failure;
                }
                Done.Set();
            }
        }
    }

    internal static class CodexLocator
    {
        public static string ExecutablePath()
        {
            var candidates = new List<string>();

            var overridden = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
            if (!string.IsNullOrEmpty(overridden))
            {
                candidates.Add(overridden);
            }

            // 실행 중인 Codex 앱과 같은 버전의 CLI를 우선 쓴다.
            foreach (var appPath in ProviderApps.RunningExecutablePaths(UsageProvider.Codex))
            {
                candidates.Add(Path.Combine(Path.GetDirectoryName(appPath), "resources", "codex.exe"));
            }
            foreach (var root in ProviderApps.PackageRoots("OpenAI.Codex_").Concat(ProviderApps.PackageRoots("OpenAI.ChatGPT-Desktop_")))
            {
                candidates.Add(Path.Combine(root, "app", "resources", "codex.exe"));
            }

            // Current Codex Desktop installs its CLI separately from the Store package.
            foreach (var process in Process.GetProcessesByName("codex"))
            {
                using (process)
                {
                    var path = Native.ProcessImagePath((uint)process.Id);
                    if (path != null && path.IndexOf(@"\OpenAI\Codex\bin\", StringComparison.OrdinalIgnoreCase) >= 0)
                        candidates.Add(path);
                }
            }
            var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bin))
            {
                try
                {
                    candidates.AddRange(Directory.GetDirectories(bin).Select(dir => Path.Combine(dir, "codex.exe"))
                        .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var directory in pathVariable.Split(Path.PathSeparator))
            {
                if (directory.Trim().Length == 0) continue;
                try
                {
                    candidates.Add(Path.Combine(directory.Trim(), "codex.exe"));
                }
                catch (ArgumentException)
                {
                }
            }

            return candidates.FirstOrDefault(File.Exists);
        }
    }

    internal static class ProviderApps
    {
        private const string PackageRepository =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        /// MSIX로 설치된 앱의 설치 폴더를 최신 버전부터 돌려준다.
        public static List<string> PackageRoots(string packagePrefix)
        {
            var roots = new List<KeyValuePair<Version, string>>();
            try
            {
                using (var repository = Registry.CurrentUser.OpenSubKey(PackageRepository))
                {
                    if (repository == null) return new List<string>();
                    foreach (var name in repository.GetSubKeyNames())
                    {
                        if (!name.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        using (var key = repository.OpenSubKey(name))
                        {
                            var root = key == null ? null : key.GetValue("PackageRootFolder") as string;
                            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                            Version version;
                            var parts = name.Split('_');
                            if (parts.Length < 2 || !Version.TryParse(parts[1], out version))
                            {
                                version = new Version(0, 0);
                            }
                            roots.Add(new KeyValuePair<Version, string>(version, root));
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return roots.OrderByDescending(pair => pair.Key).Select(pair => pair.Value).ToList();
        }

        public static List<string> RunningExecutablePaths(UsageProvider provider)
        {
            var paths = new List<string>();
            var names = provider == UsageProvider.Codex ? new[] { "Codex", "ChatGPT" } : new[] { "Claude" };
            foreach (var process in names.SelectMany(Process.GetProcessesByName))
            {
                try
                {
                    var path = Native.ProcessImagePath((uint)process.Id);
                    if (path != null && IsProviderApp(provider, path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
            return paths;
        }

        public static bool IsRunning(UsageProvider provider)
        {
            return RunningExecutablePaths(provider).Count > 0;
        }

        /// 데스크톱 앱 본체만 인정한다. Codex CLI(resources\codex.exe)나 Claude Code CLI(claude.exe)는 제외한다.
        public static bool IsProviderApp(UsageProvider provider, string path)
        {
            if (path == null) return false;
            var fileName = Path.GetFileName(path);
            var directory = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
            switch (provider)
            {
                case UsageProvider.Codex:
                    return (string.Equals(fileName, "Codex.exe", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(fileName, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                        && !string.Equals(directory, "resources", StringComparison.OrdinalIgnoreCase)
                        && path.IndexOf(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
                case UsageProvider.Claude:
                    return string.Equals(fileName, "Claude.exe", StringComparison.OrdinalIgnoreCase)
                        && path.IndexOf(@"\claude-code\", StringComparison.OrdinalIgnoreCase) < 0
                        && (path.IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0
                            || path.IndexOf(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return false;
        }

        public static UsageProvider? ProviderForPath(string path)
        {
            foreach (UsageProvider provider in Enum.GetValues(typeof(UsageProvider)))
            {
                if (IsProviderApp(provider, path)) return provider;
            }
            return null;
        }

        private static string ProcessName(UsageProvider provider)
        {
            return provider == UsageProvider.Codex ? "Codex" : "Claude";
        }
    }
}

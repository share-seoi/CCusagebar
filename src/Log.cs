using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CodexUsageBar
{
    /// 앱 이름과 데이터 폴더(%LOCALAPPDATA%\CCusagebar).
    internal static class AppInfo
    {
        public const string Name = "CCusagebar";
        private const string LegacyName = "CodexUsageBar";

        public static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

        /// 예전 이름(CodexUsageBar)의 데이터 폴더가 있으면 새 이름으로 옮겨 저장값과 기록을 이어 쓴다.
        public static void MigrateLegacyData()
        {
            var legacy = Path.Combine(Path.GetDirectoryName(DataDirectory), LegacyName);
            try
            {
                if (Directory.Exists(legacy) && !Directory.Exists(DataDirectory)) Directory.Move(legacy, DataDirectory);
            }
            catch (Exception)
            {
                // 옮기지 못하면 새 폴더에서 빈 상태로 시작한다.
            }
        }
    }

    /// 시작·종료·오류·상태 전환 같은 드문 사건만 %LOCALAPPDATA%\CCusagebar\log.txt에 한 줄씩 남긴다.
    /// 주기 작업마다 쓰지 않으며, 256KB를 넘으면 log.old.txt 하나로 돌린다. 토큰이나 HTTP 본문은 기록하지 않는다.
    internal static class Log
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object gate = new object();
        private static readonly int processId = Process.GetCurrentProcess().Id;
        private static readonly string directory = AppInfo.DataDirectory;

        public static string FilePath
        {
            get { return Path.Combine(directory, "log.txt"); }
        }

        public static void Write(string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + processId + "] "
                + message.Replace("\r", "").Replace("\n", "\n    ") + Environment.NewLine;
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var old = Path.Combine(directory, "log.old.txt");
                        File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line, Encoding.UTF8);
                }
                catch (Exception)
                {
                    // 기록 실패로 위젯이 멈추면 안 된다.
                }
            }
        }

        public static void Error(string context, Exception error)
        {
            Write(context + ": " + (error == null ? "알 수 없는 오류" : error.ToString()));
        }
    }
}

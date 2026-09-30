using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CCusagebar
{
    internal sealed class ClaudeToken
    {
        public readonly string AccessToken;
        public readonly DateTime? ExpiresAt;
        public readonly string CacheKey;

        public ClaudeToken(string accessToken, DateTime? expiresAt, string cacheKey)
        {
            AccessToken = accessToken;
            ExpiresAt = expiresAt;
            CacheKey = cacheKey;
        }

        public bool IsExpired(DateTime nowUtc)
        {
            return ExpiresAt.HasValue && ExpiresAt.Value <= nowUtc.AddSeconds(60);
        }
    }

    /// Claude 데스크톱 앱이 저장한 로그인 토큰(config.json의 oauth:tokenCacheV2)을 읽기만 한다.
    /// Electron safeStorage 형식: Local State의 DPAPI 보호 키 + "v10" AES-256-GCM.
    /// 토큰 갱신은 Claude 앱이 하므로 여기서는 갱신하거나 파일에 쓰지 않는다.
    internal static class ClaudeDesktopToken
    {
        private const string CacheKeyName = "oauth:tokenCacheV2";

        public static string ClaudeDataDirectory()
        {
            return DataDirectories().FirstOrDefault(dir => File.Exists(Path.Combine(dir, "config.json")))
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        }

        public static IEnumerable<string> DataDirectories()
        {
            var directories = new List<string>();
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(packages))
            {
                try
                {
                    directories.AddRange(Directory.GetDirectories(packages, "Claude_*")
                        .Select(dir => Path.Combine(dir, "LocalCache", "Roaming", "Claude")));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude"));
            return directories.Where(dir => File.Exists(Path.Combine(dir, "config.json")))
                .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "config.json")));
        }

        /// 만료되지 않은 토큰 중 가장 늦게 만료되는 것을 고른다. 모두 만료됐으면 가장 최근 것을 돌려준다.
        public static ClaudeToken Read()
        {
            var entries = ReadEntries();
            var now = DateTime.UtcNow;
            return entries.Where(e => !e.IsExpired(now)).OrderByDescending(e => e.ExpiresAt ?? DateTime.MaxValue).FirstOrDefault()
                ?? entries.OrderByDescending(e => e.ExpiresAt ?? DateTime.MinValue).FirstOrDefault();
        }

        public static List<ClaudeToken> ReadEntries()
        {
            var tokens = new List<ClaudeToken>();
            foreach (var directory in DataDirectories())
            {
                // Only the most recently active desktop profile is used: never mix accounts.
                tokens.AddRange(ReadEntries(directory));
                break;
            }
            return tokens;
        }

        private static List<ClaudeToken> ReadEntries(string directory)
        {
            var config = Json.TryParse(ReadShared(Path.Combine(directory, "config.json"))) as Dictionary<string, object>;
            var blob = Json.Get(config, CacheKeyName) as string;
            if (string.IsNullOrEmpty(blob))
            {
                throw new UsageStoreException("Claude 앱 로그인 정보를 찾을 수 없음");
            }

            var plaintext = Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(blob), MasterKey(directory)));
            var cache = Json.TryParse(plaintext) as Dictionary<string, object>;
            if (cache == null)
            {
                throw new UsageStoreException("Claude 앱 로그인 정보를 읽을 수 없음");
            }

            var tokens = new List<ClaudeToken>();
            foreach (var pair in cache)
            {
                var entry = pair.Value as Dictionary<string, object>;
                var token = Json.Get(entry, "token") as string;
                if (string.IsNullOrEmpty(token)) continue;
                var expiresAt = Json.Number(Json.Get(entry, "expiresAt"));
                tokens.Add(new ClaudeToken(
                    token,
                    expiresAt.HasValue ? (DateTime?)Json.FromUnixMilliseconds(expiresAt.Value) : null,
                    pair.Key));
            }
            return tokens;
        }

        private static byte[] MasterKey(string directory)
        {
            var localState = Json.TryParse(ReadShared(Path.Combine(directory, "Local State"))) as Dictionary<string, object>;
            var encoded = Json.Get(Json.Object(localState, "os_crypt"), "encrypted_key") as string;
            if (string.IsNullOrEmpty(encoded))
            {
                throw new UsageStoreException("Claude 앱 암호화 키를 찾을 수 없음");
            }
            var protectedKey = Convert.FromBase64String(encoded);
            var prefix = Encoding.ASCII.GetBytes("DPAPI");
            if (protectedKey.Length <= prefix.Length || !protectedKey.Take(prefix.Length).SequenceEqual(prefix))
            {
                throw new UsageStoreException("Claude 앱 암호화 키 형식이 다름");
            }
            return ProtectedData.Unprotect(protectedKey.Skip(prefix.Length).ToArray(), null, DataProtectionScope.CurrentUser);
        }

        private static byte[] Decrypt(byte[] blob, byte[] key)
        {
            const int nonceLength = 12;
            const int tagLength = 16;
            if (blob.Length < 3 + nonceLength + tagLength || Encoding.ASCII.GetString(blob, 0, 3) != "v10")
            {
                throw new UsageStoreException("Claude 앱 로그인 정보 형식이 다름");
            }
            var nonce = new byte[nonceLength];
            Buffer.BlockCopy(blob, 3, nonce, 0, nonceLength);
            var cipherLength = blob.Length - 3 - nonceLength - tagLength;
            var cipher = new byte[cipherLength];
            Buffer.BlockCopy(blob, 3 + nonceLength, cipher, 0, cipherLength);
            var tag = new byte[tagLength];
            Buffer.BlockCopy(blob, blob.Length - tagLength, tag, 0, tagLength);
            return AesGcm.Decrypt(key, nonce, cipher, tag);
        }

        // Claude 앱이 쓰는 중이어도 읽을 수 있게 공유 모드로 연다.
        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }

    /// .NET Framework에는 AES-GCM이 없어 Windows CNG(bcrypt.dll)를 직접 호출한다.
    internal static class AesGcm
    {
        private const uint STATUS_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct AuthInfo
        {
            public int cbSize;
            public int dwInfoVersion;
            public IntPtr pbNonce;
            public int cbNonce;
            public IntPtr pbAuthData;
            public int cbAuthData;
            public IntPtr pbTag;
            public int cbTag;
            public IntPtr pbMacContext;
            public int cbMacContext;
            public int cbAAD;
            public long cbData;
            public int dwFlags;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern uint BCryptOpenAlgorithmProvider(out IntPtr algorithm, string algId, string implementation, uint flags);

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern uint BCryptSetProperty(IntPtr handle, string property, byte[] input, int inputSize, uint flags);

        [DllImport("bcrypt.dll")]
        private static extern uint BCryptGenerateSymmetricKey(
            IntPtr algorithm, out IntPtr key, IntPtr keyObject, int keyObjectSize, byte[] secret, int secretSize, uint flags);

        [DllImport("bcrypt.dll")]
        private static extern uint BCryptDecrypt(
            IntPtr key, byte[] input, int inputSize, ref AuthInfo paddingInfo, byte[] iv, int ivSize,
            byte[] output, int outputSize, out int result, uint flags);

        [DllImport("bcrypt.dll")]
        private static extern uint BCryptDestroyKey(IntPtr key);

        [DllImport("bcrypt.dll")]
        private static extern uint BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);

        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] cipher, byte[] tag)
        {
            IntPtr algorithm, keyHandle = IntPtr.Zero;
            Check(BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0));
            var nonceHandle = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            var tagHandle = GCHandle.Alloc(tag, GCHandleType.Pinned);
            try
            {
                var mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                Check(BCryptSetProperty(algorithm, "ChainingMode", mode, mode.Length, 0));
                Check(BCryptGenerateSymmetricKey(algorithm, out keyHandle, IntPtr.Zero, 0, key, key.Length, 0));

                var info = new AuthInfo
                {
                    cbSize = Marshal.SizeOf(typeof(AuthInfo)),
                    dwInfoVersion = 1,
                    pbNonce = nonceHandle.AddrOfPinnedObject(),
                    cbNonce = nonce.Length,
                    pbTag = tagHandle.AddrOfPinnedObject(),
                    cbTag = tag.Length
                };
                var output = new byte[cipher.Length];
                int written;
                Check(BCryptDecrypt(keyHandle, cipher, cipher.Length, ref info, null, 0, output, output.Length, out written, 0));
                if (written != output.Length) Array.Resize(ref output, written);
                return output;
            }
            finally
            {
                nonceHandle.Free();
                tagHandle.Free();
                if (keyHandle != IntPtr.Zero) BCryptDestroyKey(keyHandle);
                BCryptCloseAlgorithmProvider(algorithm, 0);
            }
        }

        private static void Check(uint status)
        {
            if (status != STATUS_SUCCESS)
            {
                throw new UsageStoreException("Claude 앱 로그인 정보 복호화 실패 (0x" + status.ToString("X8") + ")");
            }
        }
    }
}

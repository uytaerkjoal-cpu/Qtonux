using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

public static class API
{
    private static readonly Qtonux _instance = new Qtonux();

    public static void AutoAttach() => _instance.SetAutoAttach();
    public static Task Attach() => _instance.Attach();
    public static VelocityState Execute(string script) => _instance.Execute(script);

    public static class Roblox
    {
        public static bool Run => _instance.IsRobloxRunning();
        public static bool Attached => _instance.IsAttached();
        public static void Kill() => _instance.KillRoblox();

        // Событие для получения логов. Формат строки: "[Тип] [Время]: Сообщение"
        public static event Action<string> Log;

        internal static void RaiseLog(string message, string type, string timestamp)
        {
            if (string.IsNullOrEmpty(type))
            {
                // Если тип не указан (например, стек вызовов), выводим строку сырой
                Log?.Invoke(message);
            }
            else if (string.IsNullOrEmpty(timestamp))
            {
                Log?.Invoke($"[{type}] {message}");
            }
            else
            {
                Log?.Invoke($"[{type}] [{timestamp}]: {message}");
            }
        }
    }
}

public enum VelocityState { Attaching, Attached, NotAttached, NoProcessFound, TamperDetected, Error, Executed }

public class DownloadUrlData
{
    public string L1 { get; set; }
    public string L2 { get; set; }
    public string question { get; set; }
}

internal class Qtonux : IDisposable
{
    private const string BaseDir = @"Bin\Velocity";
    private const string VersionUrl = "https://realvelocity.xyz/assets/current_version.txt";
    private const string LinksUrl = "https://realvelocity.xyz/assets/download_links.json";

    private readonly string InjectExe = Path.Combine(BaseDir, "erto3e4rortoergn.exe");
    private readonly string DecompExe = Path.Combine(BaseDir, "Decompiler.exe");
    private readonly string VersionFile = Path.Combine(BaseDir, "current_version.txt");

    private readonly HttpClient _http = new HttpClient();
    private readonly List<int> _pids = new List<int>();
    private readonly object _pidLock = new object();

    private bool _autoAttachEnabled = false;
    private bool _isAttaching = false;

    private Process _decompiler;
    private System.Timers.Timer _timer;

    // Поля для фонового чтения перехваченных логов
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly string _logFileName;
    private readonly string _logFile;
    private long _lastPosition = 0;

    public Qtonux()
    {
        Directory.CreateDirectory(BaseDir);
        foreach (string dir in new[] { "AutoExec", "Workspace", "Scripts" })
        {
            Directory.CreateDirectory(Path.Combine(BaseDir, dir));
        }

        // Генерируем уникальное имя файла для этой сессии (избегая символа ':' для совместимости с Windows)
        _logFileName = $"log_{DateTime.Now:dd.MM.yyyy_HH.mm.ss.fff}.log";
        _logFile = Path.Combine(BaseDir, "Workspace", _logFileName);

        // Перезаписываем Lua-логгер в автовыполнении с актуальным уникальным именем лога
        CreateLuaLogger(_logFileName);

        Task.Run(() => AutoUpdate()).Wait();

        if (File.Exists(DecompExe))
        {
            _decompiler = new Process
            {
                StartInfo = new ProcessStartInfo(DecompExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetFullPath(BaseDir)
                },
                EnableRaisingEvents = true
            };
            try { _decompiler.Start(); } catch { }
        }

        _timer = new System.Timers.Timer(100);
        _timer.Elapsed += OnTick;
        _timer.Start();

        // Запуск асинхронного чтения чистого файла логов
        StartLogReader(_cts.Token);
    }

    // Создание логгера на стороне Lua
    private void CreateLuaLogger(string logFileName)
    {
        string autoExecPath = Path.Combine(BaseDir, "AutoExec", "velocity_logger.lua");

        string luaCode = $@"-- Auto-generated logger for Velocity console
local LogService = game:GetService('LogService')
local filename = '{logFileName}'

pcall(function()
    writefile(filename, '')
end)

local function getTimeString()
    local t = DateTime.now().UnixTimestampMillis
    local ms = string.format('%03d', t % 1000)
    local timePart = os.date('%H:%M:%S')
    return timePart .. ':' .. ms
end

LogService.MessageOut:Connect(function(message, messageType)
    local prefix = 'Output'
    if messageType == Enum.MessageType.MessageWarning then
        prefix = 'Warning'
    elseif messageType == Enum.MessageType.MessageError then
        prefix = 'Error'
    elseif messageType == Enum.MessageType.MessageInfo then
        prefix = 'Info'
    end
    pcall(function()
        local timeStr = getTimeString()
        appendfile(filename, '[' .. prefix .. '] [' .. timeStr .. ']: ' .. message .. '\n')
    end)
end)";
        try { File.WriteAllText(autoExecPath, luaCode); } catch { }
    }

    public bool IsRobloxRunning() => FindRobloxPid() != -1;
    public bool IsAttached() { lock (_pidLock) return _pids.Count > 0; }

    public void KillRoblox()
    {
        foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
            try { p.Kill(); } catch { }
    }

    public void SetAutoAttach() => _autoAttachEnabled = true;

    public async Task Attach()
    {
        int pid = FindRobloxPid();
        if (pid == -1 || !File.Exists(InjectExe)) return;

        lock (_pidLock) if (_pids.Contains(pid)) return;

        if (_isAttaching) return;
        _isAttaching = true;

        try
        {
            await Task.Run(() =>
            {
                using (Process p = Process.Start(new ProcessStartInfo(InjectExe, pid.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetFullPath(BaseDir)
                }))
                {
                    p?.WaitForExit();
                }

                lock (_pidLock)
                {
                    if (!_pids.Contains(pid)) _pids.Add(pid);
                }
            });
        }
        catch { }
        finally
        {
            _isAttaching = false;
        }
    }

    public VelocityState Execute(string script)
    {
        List<int> snapshot;
        lock (_pidLock) snapshot = new List<int>(_pids);

        if (snapshot.Count == 0) return VelocityState.NotAttached;

        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        foreach (int pid in snapshot)
        {
            coms.NamedPipes.LuaPipe(encoded, pid);
        }

        return VelocityState.Executed;
    }

    private static int FindRobloxPid() => Process.GetProcessesByName("RobloxPlayerBeta").FirstOrDefault()?.Id ?? -1;

    private void OnTick(object src, ElapsedEventArgs e)
    {
        _timer.Stop();
        try
        {
            lock (_pidLock) _pids.RemoveAll(pid => !IsRunning(pid));

            if (_autoAttachEnabled && IsRobloxRunning() && !IsAttached())
            {
                _ = Attach();
            }

            if (IsAttached())
            {
                string workspacePath = Path.GetFullPath(Path.Combine(BaseDir, "Workspace"));
                string workspaceCmd = Convert.ToBase64String(Encoding.UTF8.GetBytes("setworkspacefolder: " + workspacePath));

                List<int> snapshot;
                lock (_pidLock) snapshot = new List<int>(_pids);

                foreach (int pid in snapshot)
                {
                    coms.NamedPipes.LuaPipe(workspaceCmd, pid);
                }
            }
        }
        finally
        {
            _timer.Start();
        }
    }

    private static bool IsRunning(int pid)
    {
        try { Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    private async Task AutoUpdate()
    {
        try
        {
            string json = await _http.GetStringAsync(LinksUrl);
            DownloadUrlData data = ParseJson(json);

            string key = data.question;
            string url1 = AesGcm.Decrypt(data.L1, key);
            string url2 = AesGcm.Decrypt(data.L2, key);

            string remote = await _http.GetStringAsync(VersionUrl);
            string local = File.Exists(VersionFile) ? File.ReadAllText(VersionFile) : "";

            if (remote != local)
            {
                if (File.Exists(InjectExe)) File.Delete(InjectExe);
                if (File.Exists(DecompExe)) File.Delete(DecompExe);

                await DownloadTo(url2, InjectExe);
                await DownloadTo(url1, DecompExe);
                File.WriteAllText(VersionFile, remote);
            }
        }
        catch { }
    }

    private async Task DownloadTo(string url, string path)
    {
        try { File.WriteAllBytes(path, await _http.GetByteArrayAsync(url)); } catch { }
    }

    private static DownloadUrlData ParseJson(string json)
    {
        var data = new DownloadUrlData();
        data.L1 = GetJsonValue(json, "L1");
        data.L2 = GetJsonValue(json, "L2");
        data.question = GetJsonValue(json, "question");
        return data;
    }

    private static string GetJsonValue(string json, string key)
    {
        var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(.*?)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    // --- Логика чтения файла логов ---

    private void StartLogReader(CancellationToken token)
    {
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    CheckAndReadLogs();
                }
                catch { }
                await Task.Delay(100, token);
            }
        }, token);
    }

    private void CheckAndReadLogs()
    {
        if (!File.Exists(_logFile)) return;

        var fi = new FileInfo(_logFile);
        if (fi.Length > _lastPosition)
        {
            using (var fs = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(_lastPosition, SeekOrigin.Begin);
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrEmpty(line)) continue;

                        // Шаблон для поиска формата "[Тип] [Время]: Сообщение"
                        var match = Regex.Match(line, @"^\[(.*?)\]\s*\[(.*?)\]:\s*(.*)$");
                        if (match.Success)
                        {
                            string type = match.Groups[1].Value;       // "Output", "Warning", "Error", "Info"
                            string timestamp = match.Groups[2].Value;  // "16:33:12:012"
                            string message = match.Groups[3].Value;    // Само сообщение

                            API.Roblox.RaiseLog(message, type, timestamp);
                        }
                        else
                        {
                            // Оставляем строки без разметки сырыми (например, продолжение многострочного Stack Trace)
                            API.Roblox.RaiseLog(line, "", "");
                        }
                    }
                    _lastPosition = fs.Position;
                }
            }
        }
        else if (fi.Length < _lastPosition)
        {
            _lastPosition = 0; // Сброс позиции, если файл был перезаписан
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer?.Stop();
        _timer?.Dispose();
        try { _decompiler?.Kill(); } catch { }
        _decompiler?.Dispose();
    }
}

namespace coms
{
    internal static class NamedPipes
    {
        private static readonly string luapipename = "uoQcySKXSUxxJNpVQyatpHQwYoGfhcbh";

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipe(string name, int timeout);

        public static bool NamedPipeExist(string pipeName)
        {
            try
            {
                string fullPath = "\\\\.\\pipe\\" + pipeName;
                if (WaitNamedPipe(fullPath, 0)) return true;

                int err = Marshal.GetLastWin32Error();
                if (err == 0) return false;
                if (err == 2) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void LuaPipe(string script, int pid)
        {
            string pipeName = $"{luapipename}_{pid}";

            if (!NamedPipeExist(pipeName)) return;

            Task.Run(() =>
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                    {
                        pipe.Connect(50);
                        using (var writer = new StreamWriter(pipe, Encoding.Default, 999999))
                        {
                            writer.Write(script);
                        }
                    }
                }
                catch { }
            });
        }
    }
}

internal static class AesGcm
{
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 100000;

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    static extern uint BCryptOpenAlgorithmProvider(out IntPtr hAlg, string algId, string impl, uint flags);

    [DllImport("bcrypt.dll")]
    static extern uint BCryptCloseAlgorithmProvider(IntPtr hAlg, uint flags);

    [DllImport("bcrypt.dll")]
    static extern uint BCryptGenerateSymmetricKey(IntPtr hAlg, out IntPtr hKey, IntPtr obj, uint objLen, byte[] secret, uint secretLen, uint flags);

    [DllImport("bcrypt.dll")]
    static extern uint BCryptDestroyKey(IntPtr hKey);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    static extern uint BCryptSetProperty(IntPtr hObj, string prop, byte[] input, uint inputLen, uint flags);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    static extern uint BCryptDecrypt(IntPtr hKey, byte[] input, uint inputLen, ref AuthInfo info, byte[] iv, uint ivLen, byte[] output, uint outputLen, out uint result, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthInfo
    {
        public uint cbSize, dwInfoVersion;
        public IntPtr pbNonce; public uint cbNonce;
        public IntPtr pbAuthData; public uint cbAuthData;
        public IntPtr pbTag; public uint cbTag;
        public IntPtr pbMacContext; public uint cbMacContext, cbAAD;
        public ulong cbData; public uint dwFlags;
    }

    public static string Decrypt(string ciphertext, string password)
    {
        byte[] blob = Convert.FromBase64String(ciphertext);
        byte[] salt = blob.Take(SaltSize).ToArray();
        byte[] nonce = blob.Skip(SaltSize).Take(NonceSize).ToArray();
        int cLen = blob.Length - SaltSize - NonceSize - TagSize;
        byte[] cipher = blob.Skip(SaltSize + NonceSize).Take(cLen).ToArray();
        byte[] tag = blob.Skip(SaltSize + NonceSize + cLen).ToArray();
        byte[] key = DeriveKey(password, salt);

        return Encoding.UTF8.GetString(RunBCrypt(key, delegate (IntPtr hKey)
        {
            GCHandle gcN = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            GCHandle gcT = GCHandle.Alloc(tag, GCHandleType.Pinned);
            try
            {
                AuthInfo info = BuildInfo(nonce, tag, gcN, gcT);
                byte[] output = new byte[cipher.Length];
                uint res;
                Check(BCryptDecrypt(hKey, cipher, (uint)cipher.Length, ref info, null, 0, output, (uint)output.Length, out res, 0), "BCryptDecrypt");
                return output;
            }
            finally { gcN.Free(); gcT.Free(); }
        }));
    }

    private static byte[] RunBCrypt(byte[] key, Func<IntPtr, byte[]> action)
    {
        IntPtr hAlg;
        Check(BCryptOpenAlgorithmProvider(out hAlg, "AES", null, 0), "BCryptOpenAlgorithmProvider");
        try
        {
            byte[] gcmMode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
            Check(BCryptSetProperty(hAlg, "ChainingMode", gcmMode, (uint)gcmMode.Length, 0), "BCryptSetProperty");
            IntPtr hKey;
            Check(BCryptGenerateSymmetricKey(hAlg, out hKey, IntPtr.Zero, 0, key, (uint)key.Length, 0), "BCryptGenerateSymmetricKey");
            try { return action(hKey); }
            finally { BCryptDestroyKey(hKey); }
        }
        finally { BCryptCloseAlgorithmProvider(hAlg, 0); }
    }

    private static AuthInfo BuildInfo(byte[] nonce, byte[] tag, GCHandle gcN, GCHandle gcT) =>
        new AuthInfo
        {
            cbSize = (uint)Marshal.SizeOf(typeof(AuthInfo)),
            dwInfoVersion = 1,
            pbNonce = gcN.AddrOfPinnedObject(),
            cbNonce = (uint)nonce.Length,
            pbTag = gcT.AddrOfPinnedObject(),
            cbTag = (uint)tag.Length
        };

    private static void Check(uint s, string op) { if (s != 0) throw new CryptographicException(op + " failed: 0x" + s.ToString("X8")); }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(password)))
        {
            int bLen = hmac.HashSize / 8;
            int blocks = (int)Math.Ceiling((double)KeySize / bLen);
            byte[] derived = new byte[blocks * bLen];
            for (int i = 1; i <= blocks; i++)
            {
                byte[] u = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, u, 0, salt.Length);
                byte[] iBytes = BitConverter.GetBytes(i);
                if (BitConverter.IsLittleEndian) Array.Reverse(iBytes);
                Buffer.BlockCopy(iBytes, 0, u, salt.Length, 4);
                byte[] prev = hmac.ComputeHash(u);
                byte[] xored = (byte[])prev.Clone();
                for (int j = 1; j < Iterations; j++)
                {
                    prev = hmac.ComputeHash(prev);
                    for (int k = 0; k < xored.Length; k++) xored[k] ^= prev[k];
                }
                Buffer.BlockCopy(xored, 0, derived, (i - 1) * bLen, bLen);
            }
            byte[] result = new byte[KeySize];
            Buffer.BlockCopy(derived, 0, result, 0, KeySize);
            return result;
        }
    }
}

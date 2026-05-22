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
    public static Task Attach() => _instance.Attach();
    public static VelocityState Execute(string script) => _instance.Execute(script);
}

internal class Qtonux : IDisposable
{
    private const string VersionUrl = "https://realvelocity.xyz/assets/current_version.txt";
    private const string LinksUrl = "https://realvelocity.xyz/assets/download_links.json";
    private const string InjectExe = "Bin\\erto3e4rortoergn.exe";
    private const string DecompExe = "Bin\\Decompiler.exe";
    private const string VersionFile = "Bin\\current_version.txt";

    private readonly HttpClient _http = new HttpClient();
    private readonly List<int> _pids = new List<int>();
    private readonly object _pidLock = new object();

    private Process _decompiler;
    private System.Timers.Timer _timer;

    public Qtonux()
    {
        foreach (string dir in new[] { "Bin", "AutoExec", "Workspace", "Scripts" })
            Directory.CreateDirectory(dir);

        AutoUpdate();

        _decompiler = new Process
        {
            StartInfo = new ProcessStartInfo(DecompExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true
        };
        _decompiler.Start();

        _timer = new System.Timers.Timer(100);
        _timer.Elapsed += OnTick;
        _timer.Start();
    }

    public async Task Attach()
    {
        int pid = FindRobloxPid();
        if (pid == -1) return;

        lock (_pidLock)
            if (_pids.Contains(pid)) return;

        await Task.Run(() =>
        {
            try
            {
                Process p = Process.Start(new ProcessStartInfo(InjectExe, pid.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                p?.WaitForExit();

                lock (_pidLock) _pids.Add(pid);
            }
            catch { }
        });
    }

    public VelocityState Execute(string script)
    {
        List<int> snapshot;
        lock (_pidLock) snapshot = new List<int>(_pids);

        if (snapshot.Count == 0) return VelocityState.NotAttached;

        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        foreach (int pid in snapshot)
            LuaPipe.Send(encoded, pid);

        return VelocityState.Executed;
    }

    private static int FindRobloxPid()
    {
        return Process.GetProcessesByName("RobloxPlayerBeta")
                      .FirstOrDefault()?.Id ?? -1;
    }

    private void OnTick(object src, ElapsedEventArgs e)
    {
        lock (_pidLock)
            _pids.RemoveAll(pid => !IsRunning(pid));

        string workspace = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("setworkspacefolder: " + Directory.GetCurrentDirectory() + "\\Workspace"));

        List<int> snapshot;
        lock (_pidLock) snapshot = new List<int>(_pids);
        foreach (int pid in snapshot)
            LuaPipe.Send(workspace, pid);
    }

    private static bool IsRunning(int pid)
    {
        try { Process.GetProcessById(pid); return true; }
        catch (ArgumentException) { return false; }
    }

    private void AutoUpdate()
    {
        try
        {
            string json = _http.GetStringAsync(LinksUrl).Result;
            string key = ExtractJson(json, "question");
            string url1 = AesGcm.Decrypt(ExtractJson(json, "L1"), key);
            string url2 = AesGcm.Decrypt(ExtractJson(json, "L2"), key);
            string remote = _http.GetStringAsync(VersionUrl).Result;
            string local = File.Exists(VersionFile) ? File.ReadAllText(VersionFile) : "";

            if (remote != local)
            {
                DownloadTo(url2, InjectExe);
                DownloadTo(url1, DecompExe);
            }
            File.WriteAllText(VersionFile, remote);
        }
        catch { }
    }

    private void DownloadTo(string url, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        File.WriteAllBytes(path, _http.GetByteArrayAsync(url).Result);
    }

    private static string ExtractJson(string json, string key)
    {
        var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(.*?)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        try { _decompiler?.Kill(); } catch { }
        _decompiler?.Dispose();
        _decompiler = null;
        lock (_pidLock) _pids.Clear();
    }
}

public enum VelocityState { Attaching, Attached, NotAttached, NoProcessFound, TamperDetected, Error, Executed }

internal static class LuaPipe
{
    private const string PipeName = "uoQcySKXSUxxJNpVQyatpHQwYoGfhcbh";

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, int timeout);

    private static bool PipeExists(int pid)
    {
        try
        {
            bool ok = WaitNamedPipe("\\\\.\\pipe\\" + PipeName + "_" + pid, 0);
            if (ok) return true;
            int err = Marshal.GetLastWin32Error();
            return err != 0 && err != 2;
        }
        catch { return false; }
    }

    public static void Send(string script, int pid)
    {
        if (!PipeExists(pid)) return;
        new Thread(delegate ()
        {
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName + "_" + pid, PipeDirection.Out))
                {
                    pipe.Connect();
                    using (StreamWriter writer = new StreamWriter(pipe, Encoding.Default, 999999))
                    {
                        writer.Write(script);
                    }
                }
            }
            catch { }
        }).Start();
    }
}

internal static class AesGcm
{
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 100000;

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern uint BCryptOpenAlgorithmProvider(out IntPtr hAlg, string algId, string impl, uint flags);
    [DllImport("bcrypt.dll")] static extern uint BCryptCloseAlgorithmProvider(IntPtr hAlg, uint flags);
    [DllImport("bcrypt.dll")] static extern uint BCryptGenerateSymmetricKey(IntPtr hAlg, out IntPtr hKey, IntPtr obj, uint objLen, byte[] secret, uint secretLen, uint flags);
    [DllImport("bcrypt.dll")] static extern uint BCryptDestroyKey(IntPtr hKey);
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern uint BCryptSetProperty(IntPtr hObj, string prop, byte[] input, uint inputLen, uint flags);
    [DllImport("bcrypt.dll")] static extern uint BCryptEncrypt(IntPtr hKey, byte[] input, uint inputLen, ref AuthInfo info, byte[] iv, uint ivLen, byte[] output, uint outputLen, out uint result, uint flags);
    [DllImport("bcrypt.dll")] static extern uint BCryptDecrypt(IntPtr hKey, byte[] input, uint inputLen, ref AuthInfo info, byte[] iv, uint ivLen, byte[] output, uint outputLen, out uint result, uint flags);

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

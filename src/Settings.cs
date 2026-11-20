// Hallow Assist settings: %APPDATA%\HallowAssist\settings.json. Unattended-access secrets are kept encrypted for this Windows
// user (DPAPI) and never written in plain text; the unattended password itself is never stored, only a key derived from it.
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HallowAssist
{
    [DataContract]
    public class AssistSettings
    {
        // developer tests: HALLOW_ASSIST_TESTDIR = data folder (and a separate pipe); HALLOW_ASSIST_FAKE=1 = synthetic screen and
        // recorded input instead of the real ones; HALLOW_ASSIST_HEADLESS=1 = no windows at all (answers come through the test pipe).
        public static readonly string TestDir = Env("HALLOW_ASSIST_TESTDIR");
        public static readonly bool Fake = TestDir != null && Env("HALLOW_ASSIST_FAKE") == "1";
        public static readonly bool Headless = TestDir != null && Env("HALLOW_ASSIST_HEADLESS") == "1";
        public static bool Frozen;   // --snap: never saved
        static string Env(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrWhiteSpace(v) ? null : v; }
        public static string Dir { get { return TestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HallowAssist"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        [DataMember] public string RelayUrl = "";
        [DataMember] public string Name = "";
        [DataMember] public string Quality = "auto";
        [DataMember] public bool UseHallowIcon = true;
        [DataMember] public bool UseHallowAccent = true;
        [DataMember] public string LastUpdateCheck;
        [DataMember] public string LastCrashShown;
        // unattended access (your own PCs only): off unless turned on here with a separate strong password and Start with Windows
        [DataMember] public bool Unattended;
        [DataMember] public string UnattendedSecret;   // DPAPI-protected: the secret behind this PC's fixed code
        [DataMember] public string UnattendedKey;      // DPAPI-protected: PBKDF2 of the unattended password with that code
        [DataMember] public string UnattendedCode;     // shown in the window so you can note it down
        [DataMember] public string UnattendedLockedUntil;

        public static AssistSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var f = File.OpenRead(FilePath))
                    {
                        var s = (AssistSettings)new DataContractJsonSerializer(typeof(AssistSettings)).ReadObject(f);
                        if (s != null) { s.Fix(); return s; }
                    }
            }
            catch { }
            var d = new AssistSettings(); d.Fix(); return d;
        }
        void Fix()
        {
            if (RelayUrl == null) RelayUrl = ""; if (Name == null) Name = "";
            if (Quality != "auto" && Quality != "best" && Quality != "balanced" && Quality != "fast") Quality = "auto";
            var r = Env("HALLOW_ASSIST_RELAY"); if (TestDir != null && r != null) RelayUrl = r;
            // unattended needs all of its parts; anything missing = off
            if (Unattended && (UnattendedSecret == null || UnattendedKey == null)) Unattended = false;
        }
        static readonly object saveLock = new object();
        public void Save()
        {
            if (Frozen) return;
            lock (saveLock)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var tmp = FilePath + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(AssistSettings)).WriteObject(f, this);
                    for (int i = 0; ; i++) { try { if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
                }
                catch { }
            }
        }

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hallow Assist unattended v1");
        public static string Protect(byte[] data) { return Convert.ToBase64String(ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser)); }
        public static byte[] Unprotect(string s) { try { return s == null ? null : ProtectedData.Unprotect(Convert.FromBase64String(s), Entropy, DataProtectionScope.CurrentUser); } catch { return null; } }

        // the relay address as a WebSocket URL (https://x -> wss://x/ws); null if it isn't a usable address
        public static Uri WsUri(string relay)
        {
            var s = (relay ?? "").Trim().TrimEnd('/');
            if (s.Length == 0) return null;
            if (!s.Contains("://")) s = "https://" + s;
            Uri u; if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return null;
            var scheme = u.Scheme == "https" || u.Scheme == "wss" ? "wss" : u.Scheme == "http" || u.Scheme == "ws" ? "ws" : null;
            if (scheme == null) return null;
            // plain ws:// only to this PC or a home network address (tests, a relay at home); anything else must be wss://
            if (scheme == "ws" && !(u.IsLoopback || u.Host.StartsWith("192.168.") || u.Host.StartsWith("10.") || u.Host.EndsWith(".local"))) return null;
            return new Uri(scheme + "://" + u.Authority + "/ws");
        }
        public static string WebUrl(string relay)
        {
            var w = WsUri(relay); if (w == null) return null;
            return (w.Scheme == "wss" ? "https://" : "http://") + w.Authority + "/";
        }
    }
}

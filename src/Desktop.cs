// Hallow Assist: things a normal (not administrator) app can't do, detected so both people know why.
// The UAC prompt and the lock screen run on the secure desktop, which no user app can see or click; windows that run as
// administrator can be seen but not clicked (Windows blocks input from lower apps). An admin service is a later version.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HallowAssist
{
    public static class Desktop
    {
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(int flags, bool inherit, uint access);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int index, StringBuilder buf, int len, out int need);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int cls, out int info, int len, out int ret);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        // the UAC prompt or lock screen is up
        public static bool SecureDesktopActive()
        {
            var h = OpenInputDesktop(0, false, 0x0001);   // DESKTOP_READOBJECTS
            if (h == IntPtr.Zero) return true;
            try { var sb = new StringBuilder(64); int need; return GetUserObjectInformation(h, 2, sb, sb.Capacity * 2, out need) && !string.Equals(sb.ToString(), "Default", StringComparison.OrdinalIgnoreCase); }
            finally { CloseDesktop(h); }
        }

        static bool Elevated(IntPtr process)
        {
            IntPtr tok; if (!OpenProcessToken(process, 0x0008, out tok)) return true;   // can't even look: treat as admin
            try { int e, ret; return GetTokenInformation(tok, 20, out e, 4, out ret) && e != 0; }
            finally { CloseHandle(tok); }
        }
        public static bool SelfElevated() { return Elevated(GetCurrentProcess()); }

        // the window in front runs as administrator (and we don't), so clicks and keys won't reach it
        public static bool ForegroundElevated()
        {
            try
            {
                if (SelfElevated()) return false;
                var w = GetForegroundWindow(); if (w == IntPtr.Zero) return false;
                int pid; GetWindowThreadProcessId(w, out pid); if (pid == 0) return false;
                var p = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
                if (p == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
                try { return Elevated(p); } finally { CloseHandle(p); }
            }
            catch { return false; }
        }
    }
}

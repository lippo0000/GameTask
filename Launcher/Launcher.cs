using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

class Program
{
    [DllImport("kernel32.dll")]
    static extern bool FreeConsole();

    static string _cmdFile = null;
    static string _logFile = null;
    static DateTime _lastProcessed = DateTime.MinValue;

    // ── Win32 ────────────────────────────────────────────────────────
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] static extern bool LockSetForegroundWindow(uint uLockCode);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    const int SW_RESTORE = 9, SW_MAXIMIZE = 3, SW_SHOWNOACTIVATE = 4, SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    const int SW_SHOWMINIMIZED = 2;
    const uint LSFW_LOCK = 1, LSFW_UNLOCK = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    // ── SendInput structures ─────────────────────────────────────────
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_UNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint dwProcessId);
    const uint ASFW_ANY = 0xFFFFFFFF;

    static IntPtr GetMainWindowHandle(string processName)
    {
        return Process.GetProcessesByName(processName)
            .FirstOrDefault(p => !p.HasExited && p.MainWindowHandle != IntPtr.Zero)
            ?.MainWindowHandle ?? IntPtr.Zero;
    }

    static bool IsMinimized(IntPtr hwnd)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        return GetWindowPlacement(hwnd, ref wp) && wp.showCmd == SW_SHOWMINIMIZED;
    }

    // Reliable foreground focus using AttachThreadInput to bypass Windows foreground lock
    static void FocusWindow(IntPtr hwnd)
    {
        try
        {
            // Only restore if minimized — avoids un-maximizing fullscreen/borderless apps
            if (IsMinimized(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
                Thread.Sleep(100);
            }

            var foreground = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(foreground, out _);
            uint tgtThread = GetWindowThreadProcessId(hwnd, out _);
            uint myThread = GetCurrentThreadId();

            bool attachedFg = fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
            bool attachedTgt = tgtThread != myThread && AttachThreadInput(myThread, tgtThread, true);

            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            SwitchToThisWindow(hwnd, true);

            if (attachedFg) AttachThreadInput(myThread, fgThread, false);
            if (attachedTgt) AttachThreadInput(myThread, tgtThread, false);
        }
        catch { }
    }

    static bool IsWindowFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        return r.Left <= 0 && r.Top <= 0 && r.Right >= sw && r.Bottom >= sh;
    }

    static readonly Dictionary<string, byte> _keyMap = new Dictionary<string, byte>
    {
        {"F1",0x70},{"F2",0x71},{"F3",0x72},{"F4",0x73},
        {"F5",0x74},{"F6",0x75},{"F7",0x76},{"F8",0x77},
        {"F9",0x78},{"F10",0x79},{"F11",0x7A},{"F12",0x7B},
        {"ESC",0x1B},{"ESCAPE",0x1B},{"ENTER",0x0D},
        {"TAB",0x09},{"SPACE",0x20},{"HOME",0x24},{"END",0x23},
        {"PAGEUP",0x21},{"PAGEDOWN",0x22},{"INSERT",0x2D},{"DELETE",0x2E}
    };

    static void SendComboKey(string combo)
    {
        try
        {
            var parts = (combo ?? "F11").ToUpper().Split('+');
            var modifiers = new List<ushort>();
            ushort mainKey = 0x7A; // F11

            foreach (var part in parts)
            {
                var p = part.Trim();
                switch (p)
                {
                    case "CTRL": case "CONTROL": modifiers.Add(0x11); break;
                    case "ALT": modifiers.Add(0x12); break;
                    case "SHIFT": modifiers.Add(0x10); break;
                    case "WIN": modifiers.Add(0x5B); break;
                    default:
                        if (_keyMap.TryGetValue(p, out var vk)) mainKey = vk;
                        break;
                }
            }

            // Build SendInput array: modifiers down, key down, key up, modifiers up
            var inputs = new List<INPUT>();
            foreach (var m in modifiers)
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = m } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = mainKey } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = mainKey, dwFlags = KEYEVENTF_KEYUP } } });
            for (int i = modifiers.Count - 1; i >= 0; i--)
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = modifiers[i], dwFlags = KEYEVENTF_KEYUP } } });

            var arr = inputs.ToArray();
            SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        }
        catch { }
    }

    static void Main(string[] args)
    {
        FreeConsole();
        RegisterStartup();

        var current = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(current.ProcessName))
        {
            if (p.Id != current.Id)
            {
                try { p.Kill(); } catch { }
            }
        }

        _logFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "AppDeck_LaunchLog.txt");

        Log("Helper started, searching for package...");

        _cmdFile = FindCmdFile();
        if (_cmdFile == null)
            Log("WARN: cmd file not found yet, will keep watching...");
        else
            Log($"Watching: {_cmdFile}");

        StartWatcher();
        StartHeartbeat();

        Thread.Sleep(Timeout.Infinite);
    }

    static void RegisterStartup()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule.FileName;
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                key?.SetValue("AppDeckHelper", $"\"{exePath}\"");
            }
            Log("Startup: registered in HKCU Run.");
        }
        catch (Exception ex) { Log($"RegisterStartup ERROR: {ex.Message}"); }
    }

    static void StartHeartbeat()
    {
        WriteHeartbeat();
        var timer = new System.Timers.Timer(2000);
        timer.Elapsed += (s, e) => WriteHeartbeat();
        timer.AutoReset = true;
        timer.Start();
    }

    static void WriteHeartbeat()
    {
        try
        {
            if (_cmdFile == null) return;
            var dir = Path.GetDirectoryName(_cmdFile);
            File.WriteAllText(Path.Combine(dir, "launcher_heartbeat.txt"), DateTime.UtcNow.ToString("O"));
            File.WriteAllText(Path.Combine(dir, "launcher_path.txt"), Process.GetCurrentProcess().MainModule.FileName);
        }
        catch { }
    }

    static void StartWatcher()
    {
        try
        {
            if (_cmdFile == null)
            {
                var timer = new System.Timers.Timer(3000);
                timer.Elapsed += (s, e) =>
                {
                    _cmdFile = FindCmdFile();
                    if (_cmdFile != null)
                    {
                        Log($"Found cmd file: {_cmdFile}");
                        timer.Stop();
                        StartWatcher();
                    }
                };
                timer.Start();
                return;
            }

            var dir = Path.GetDirectoryName(_cmdFile);
            var file = Path.GetFileName(_cmdFile);

            var watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            watcher.Changed += (s, e) =>
            {
                var now = DateTime.Now;
                if ((now - _lastProcessed).TotalMilliseconds < 500) return;
                _lastProcessed = now;
                new Thread(() => { Thread.Sleep(50); ProcessCmd(); }) { IsBackground = true }.Start();
            };

            Log("FileSystemWatcher active — ready for commands.");
        }
        catch (Exception ex)
        {
            Log($"Watcher ERROR: {ex.Message}");
        }
    }

    static void ProcessCmd()
    {
        try
        {
            var lines = File.ReadAllLines(_cmdFile);
            string cmd = lines.Length > 0 ? lines[0].Trim() : "";
            string path = lines.Length > 1 ? lines[1].Trim() : "";
            string arguments = lines.Length > 2 ? lines[2].Trim() : "";

            Log($"CMD: {cmd} | PATH: {path} | ARGS: {arguments}");

            if (cmd == "launch" && !string.IsNullOrEmpty(path))
            {
                if (!File.Exists(path)) { Log("FAIL: file not found"); return; }

                // Capture widget HWND now — it's the current foreground window
                var widgetHwnd = GetForegroundWindow();

                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path)
                };
                // Allow the new process to exist but lock foreground so it can't steal focus
                AllowSetForegroundWindow(ASFW_ANY);
                var p = Process.Start(psi);
                LockSetForegroundWindow(LSFW_LOCK);

                Thread.Sleep(800);

                if (p == null || p.HasExited)
                {
                    Process.Start(new ProcessStartInfo { FileName = path, Arguments = arguments, UseShellExecute = true });
                    Log("OK: launched via fallback");
                }
                else Log("OK: launched");

                // Re-focus the widget so it keeps foreground while its re-anchor loop runs
                if (widgetHwnd != IntPtr.Zero)
                {
                    Thread.Sleep(300);
                    FocusWindow(widgetHwnd);
                    Log($"OK: re-focused widget after launch");
                }
            }
            else if (cmd == "kill" && !string.IsNullOrEmpty(path))
            {
                foreach (var p in Process.GetProcessesByName(path))
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
                Log("OK: killed");
            }
            else if (cmd == "focus" && !string.IsNullOrEmpty(path))
            {
                var hwnd = GetMainWindowHandle(path);
                if (hwnd == IntPtr.Zero) { Log($"WARN: no window found for '{path}'"); return; }
                FocusWindow(hwnd);
                Log("OK: focused");
            }
            else if (cmd == "fullscreen" && !string.IsNullOrEmpty(path))
            {
                var parts = (arguments ?? "").Split('|');
                var mode = parts.Length > 0 ? parts[0] : "FocusThenFullscreen";
                var key = parts.Length > 1 ? parts[1] : "F11";

                var hwnd = GetMainWindowHandle(path);
                if (hwnd == IntPtr.Zero) { Log($"WARN: no window found for '{path}'"); return; }

                if (mode == "Maximize")
                {
                    FocusWindow(hwnd);
                    Thread.Sleep(150);
                    ShowWindow(hwnd, SW_MAXIMIZE);
                    Log("OK: maximized");
                }
                else
                {
                    // Always focus the target window first so the key goes to the right place
                    FocusWindow(hwnd);
                    Thread.Sleep(500); // give the window time to actually gain focus
                    SendComboKey(key);
                    Log($"OK: key sent ({key}), mode={mode}");
                }
            }
            else if (cmd == "emoji")
            {
                keybd_event(0x5B, 0, 0, IntPtr.Zero);
                Thread.Sleep(50);
                keybd_event(0xBE, 0, 0, IntPtr.Zero);
                keybd_event(0xBE, 0, 2, IntPtr.Zero);
                Thread.Sleep(50);
                keybd_event(0x5B, 0, 2, IntPtr.Zero);
                Log("OK: emoji picker opened");
            }
            else if (cmd == "extract_icon" && !string.IsNullOrEmpty(path))
            {
                // path      = full path to the .exe
                // arguments = response filename (e.g. "icon_response_abc123.txt")
                var exePath          = path;
                var responseFileName = arguments;

                // Resolve LocalFolder: Launcher writes its own path to launcher_path.txt,
                // which is already in LocalState. We can derive LocalState from _cmdFile.
                var localState = Path.GetDirectoryName(_cmdFile);

                if (!string.IsNullOrEmpty(localState) && !string.IsNullOrEmpty(responseFileName))
                {
                    var responsePath = Path.Combine(localState, responseFileName);
                    try
                    {
                        if (!File.Exists(exePath))
                        {
                            File.WriteAllText(responsePath, "");
                            Log($"extract_icon WARN: exe not found '{exePath}'");
                            return;
                        }

                        // Build a stable PNG filename from the exe path (base64, filesystem-safe)
                        var safeName = Convert.ToBase64String(Encoding.UTF8.GetBytes(exePath))
                                               .Replace('/', '_').Replace('+', '-').Replace('=', '~');
                        if (safeName.Length > 80) safeName = safeName.Substring(0, 80);
                        var pngFileName = $"appicon_{safeName}.png";
                        var pngPath     = Path.Combine(localState, pngFileName);

                        // Extract the embedded icon and save as PNG
                        using (var icon   = Icon.ExtractAssociatedIcon(exePath))
                        using (var bitmap = icon.ToBitmap())
                        {
                            bitmap.Save(pngPath, ImageFormat.Png);
                        }

                        // Write response: just the full PNG path
                        File.WriteAllText(responsePath, pngPath);
                        Log($"OK: icon extracted → {pngPath}");
                    }
                    catch (Exception ex)
                    {
                        // Write empty response so the widget stops polling
                        try { File.WriteAllText(responsePath, ""); } catch { }
                        Log($"extract_icon ERROR: {ex.Message}");
                    }
                }
            }
            else
            {
                Log($"WARN: unknown cmd '{cmd}'");
            }
        }
        catch (Exception ex)
        {
            Log($"ProcessCmd ERROR: {ex.Message}");
        }
    }

    static string FindCmdFile()
    {
        try
        {
            string packagesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");

            string best = null;
            foreach (var dir in Directory.GetDirectories(packagesPath))
            {
                string candidate = Path.Combine(dir, "LocalState", "launcher_cmd.txt");
                if (File.Exists(candidate))
                {
                    if (best == null || File.GetLastWriteTime(candidate) > File.GetLastWriteTime(best))
                        best = candidate;
                }
            }
            return best;
        }
        catch { return null; }
    }

    static void Log(string msg)
    {
        try { File.AppendAllText(_logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); }
        catch { }
    }
}
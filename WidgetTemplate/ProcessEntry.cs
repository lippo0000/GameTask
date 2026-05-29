using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WidgetTemplate
{
    public enum FocusFullscreenMode { Maximize, FullscreenOnly, FocusThenFullscreen }
    public enum PlayBehaviour { RemainOnWidget, CloseWidget, FocusApp }
    public enum CloseBehaviour { RemainOnWidget, CloseWidget }

    public class ProcessEntry : INotifyPropertyChanged
    {
        // ── Win32 ────────────────────────────────────────────────────
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(System.IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern System.IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
        private const int SW_MAXIMIZE = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        private System.IntPtr GetMainWindowHandle()
        {
            var name = Path.GetFileNameWithoutExtension(ExePath);
            return Process.GetProcessesByName(name)
                .FirstOrDefault(p => !p.HasExited)?.MainWindowHandle ?? System.IntPtr.Zero;
        }

        public bool IsWindowFullscreen()
        {
            var hwnd = GetMainWindowHandle();
            if (hwnd == System.IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out var r)) return false;
            int sw = (int)Windows.Graphics.Display.DisplayInformation.GetForCurrentView().ScreenWidthInRawPixels;
            int sh = (int)Windows.Graphics.Display.DisplayInformation.GetForCurrentView().ScreenHeightInRawPixels;
            return r.Left <= 0 && r.Top <= 0 && r.Right >= sw && r.Bottom >= sh;
        }

        public void BringToFocus()
        {
            var hwnd = GetMainWindowHandle();
            if (hwnd != System.IntPtr.Zero)
                SetForegroundWindow(hwnd);
        }

        public void MaximizeWindow()
        {
            var hwnd = GetMainWindowHandle();
            if (hwnd != System.IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_MAXIMIZE);
                SetForegroundWindow(hwnd);
            }
        }
        public string ExePath { get; set; }
        public string Arguments { get; set; }
        public string CloseExePath { get; set; }
        public string FocusExePath { get; set; }

        private string _description = "";
        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDescription)); }
        }
        public bool HasDescription => !string.IsNullOrEmpty(_description);

        private string _infoUrl = "";
        public string InfoUrl
        {
            get => _infoUrl;
            set { _infoUrl = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasInfoUrl)); }
        }
        public bool HasInfoUrl => !string.IsNullOrEmpty(_infoUrl);

        private string _updateUrl = "";
        public string UpdateUrl
        {
            get => _updateUrl;
            set { _updateUrl = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUpdateUrl)); }
        }
        public bool HasUpdateUrl => !string.IsNullOrEmpty(_updateUrl);

        private string _customName = "";
        public string CustomName
        {
            get => _customName;
            set { _customName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string DisplayName => !string.IsNullOrEmpty(_customName)
            ? _customName
            : Path.GetFileNameWithoutExtension(ExePath);

        public string ShortPath
        {
            get
            {
                if (string.IsNullOrEmpty(ExePath)) return "";
                var fileName = Path.GetFileName(ExePath);
                var dir = Path.GetDirectoryName(ExePath) ?? "";
                if (string.IsNullOrEmpty(dir)) return fileName;
                var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .Where(p => !string.IsNullOrEmpty(p)).ToArray();
                if (parts.Length == 0) return fileName;
                var root = parts[0] + @"\";
                return root + @"...\" + fileName;
            }
        }

        private string _icon = "🎮";
        public string Icon { get => _icon; set { _icon = value; OnPropertyChanged(); } }

        private string _iconImagePath = "";
        public string IconImagePath
        {
            get => _iconImagePath;
            set { _iconImagePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImageIcon)); }
        }
        public bool HasImageIcon => !string.IsNullOrEmpty(_iconImagePath);

        private string _iconColor = "";
        public string IconColor { get => _iconColor; set { _iconColor = value; OnPropertyChanged(); } }

        private string _category = "Apps";
        public string Category
        {
            get => _category;
            set { _category = value; OnPropertyChanged(); }
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); }
        }

        private bool _isFirstItem;
        public bool IsFirstItem
        {
            get => _isFirstItem;
            set { _isFirstItem = value; OnPropertyChanged(); }
        }

        private bool _isLaunched;
        public bool IsLaunched
        {
            get => _isLaunched;
            set { _isLaunched = value; OnPropertyChanged(); OnPropertyChanged(nameof(LaunchedPlayTooltip)); }
        }

        public string OriginalCategory { get; set; }

        private bool _hasPlayOverride;
        public bool HasPlayOverride
        {
            get => _hasPlayOverride;
            set { _hasPlayOverride = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayTooltip)); }
        }

        private bool _hasCloseOverride;
        public bool HasCloseOverride
        {
            get => _hasCloseOverride;
            set { _hasCloseOverride = value; OnPropertyChanged(); OnPropertyChanged(nameof(StopTooltip)); }
        }

        private bool _hasFsOverride;
        public bool HasFsOverride
        {
            get => _hasFsOverride;
            set { _hasFsOverride = value; OnPropertyChanged(); OnPropertyChanged(nameof(FsTooltip)); }
        }

        private FocusFullscreenMode _focusMode = FocusFullscreenMode.FocusThenFullscreen;
        public FocusFullscreenMode FocusMode
        {
            get => _focusMode;
            set { _focusMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(FsTooltip)); OnPropertyChanged(nameof(FocusModeLabel)); }
        }

        private string _fullscreenKey = "F11";
        public string FullscreenKey
        {
            get => _fullscreenKey;
            set { _fullscreenKey = value; OnPropertyChanged(); OnPropertyChanged(nameof(FsTooltip)); OnPropertyChanged(nameof(FocusModeLabel)); }
        }

        private PlayBehaviour _playBehaviour = PlayBehaviour.RemainOnWidget;
        public PlayBehaviour PlayBehaviour
        {
            get => _playBehaviour;
            set { _playBehaviour = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayTooltip)); OnPropertyChanged(nameof(PlayBehaviourLabel)); }
        }

        private CloseBehaviour _closeBehaviour = CloseBehaviour.RemainOnWidget;
        public CloseBehaviour CloseBehaviour
        {
            get => _closeBehaviour;
            set { _closeBehaviour = value; OnPropertyChanged(); OnPropertyChanged(nameof(StopTooltip)); OnPropertyChanged(nameof(CloseBehaviourLabel)); }
        }

        // ── Manual override flag — set only via ··· menu, gates reset dialog ──
        private bool _isManualOverride;
        public bool IsManualOverride
        {
            get => _isManualOverride;
            set { _isManualOverride = value; OnPropertyChanged(); }
        }

        // ── Activity toast — overlays the Launched row during key actions ──────
        // ToastMessage: non-empty = toast visible; empty = hidden.
        // ToastGlyph:   Segoe Fluent Icons glyph code shown left of the message.
        // HasToast:     drives Visibility + IsHitTestVisible on the overlay Border.
        private string _toastMessage = "";
        public string ToastMessage
        {
            get => _toastMessage;
            set { _toastMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasToast)); }
        }

        private string _toastGlyph = "";
        public string ToastGlyph
        {
            get => _toastGlyph;
            set { _toastGlyph = value; OnPropertyChanged(); }
        }

        // True while any toast is showing — used to block row interaction.
        public bool HasToast => !string.IsNullOrEmpty(_toastMessage);

        // ── Tooltip computed properties ───────────────────────────────
        public string PlayBehaviourLabel
        {
            get
            {
                if (PlayBehaviour == PlayBehaviour.CloseWidget) return "Close Game Bar";
                if (PlayBehaviour == PlayBehaviour.FocusApp) return "Focus app";
                return "Stay on Game Bar";
            }
        }
        public string CloseBehaviourLabel => CloseBehaviour == CloseBehaviour.CloseWidget
            ? "Close Game Bar" : "Stay on Game Bar";
        public string FocusModeLabel
        {
            get
            {
                if (FocusMode == FocusFullscreenMode.Maximize) return "Maximize window";
                if (FocusMode == FocusFullscreenMode.FullscreenOnly) return $"{FullscreenKey} only";
                return $"Focus → {FullscreenKey}";
            }
        }

        // ── §11 tier label ────────────────────────────────────────────
        private DefaultTier _defaultTier;
        public DefaultTier DefaultTier
        {
            get => _defaultTier;
            set
            {
                if (_defaultTier != value)
                {
                    _defaultTier = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PlayTooltip));
                    OnPropertyChanged(nameof(StopTooltip));
                    OnPropertyChanged(nameof(FsTooltip));
                }
            }
        }

        private string GlobalSuffix(bool hasOverride) => hasOverride ? "" : "  (Global Default 🌐)";

        public string PlayTooltip => $"Launch behaviour  =  {PlayBehaviourLabel}{GlobalSuffix(HasPlayOverride)}";
        public string StopTooltip => $"Stop behaviour  =  {CloseBehaviourLabel}{GlobalSuffix(HasCloseOverride)}";
        public string FsTooltip => $"Fullscreen behaviour  =  {FocusModeLabel}{GlobalSuffix(HasFsOverride)}";
        public string FocusTooltip => "Bring to front";
        public string LaunchedPlayTooltip => IsLaunched
            ? "Already launched. Press Stop then relaunch from its category."
            : PlayTooltip;

        public void RefreshStatus()
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(ExePath);
                if (string.IsNullOrEmpty(name)) { IsRunning = false; return; }
                IsRunning = Process.GetProcessesByName(name).Any(p => !p.HasExited);
            }
            catch { IsRunning = false; }
        }

        public void Kill()
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(ExePath);
                foreach (var p in Process.GetProcessesByName(name))
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
            }
            catch { }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
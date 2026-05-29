using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WidgetTemplate
{
    public enum DefaultTier { Global, Category }

    public class CategoryGroup : INotifyPropertyChanged
    {
        // ── Identity ──────────────────────────────────────────────────
        private string _name = "";
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLaunched)); }
        }

        // ── Entries ───────────────────────────────────────────────────
        public ObservableCollection<ProcessEntry> Entries { get; }
            = new ObservableCollection<ProcessEntry>();

        // ── Visual metadata (persisted via SaveCategoryOrder) ─────────
        private string _icon = "";
        public string Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasIcon)); }
        }

        public bool HasIcon => !string.IsNullOrEmpty(_icon);

        private string _accentColor = "";
        public string AccentColor
        {
            get => _accentColor;
            set { _accentColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(EntryBorderBackground)); }
        }

        private bool _isHidden;
        public bool IsHidden
        {
            get => _isHidden;
            set { _isHidden = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsVisibleAndHasEntries)); }
        }

        // ── Per-category default behaviour (§10) ──────────────────────
        // Set by RebuildCategoryView / ApplyCatDefaultsToNonOverridden;
        // not used by the XAML directly but kept in sync for code-behind use.
        public PlayBehaviour CatDefaultPlay { get; set; }
        public CloseBehaviour CatDefaultClose { get; set; }
        public FocusFullscreenMode CatDefaultFocusMode { get; set; }

        // ── Computed properties bound by XAML ─────────────────────────

        /// <summary>True when this is the special "Launched" category.</summary>
        public bool IsLaunched => _name == "Launched";

        /// <summary>
        /// Drives outer StackPanel visibility — collapses the whole group when
        /// the category is hidden or contains no entries.
        /// </summary>
        public bool IsVisibleAndHasEntries => !_isHidden && Entries.Count > 0;

        /// <summary>
        /// Subtle tinted border background when an accent colour is set;
        /// transparent otherwise so the card blends into the widget background.
        /// </summary>
        public string EntryBorderBackground
            => string.IsNullOrEmpty(_accentColor) ? "Transparent" : "#18" + _accentColor.TrimStart('#');

        // ── Helpers called by code-behind ─────────────────────────────

        /// <summary>
        /// Recalculates IsFirstItem on every entry so the XAML divider
        /// (hidden for the first row) renders correctly after any reorder.
        /// </summary>
        public void RefreshFirstItemFlags()
        {
            for (int i = 0; i < Entries.Count; i++)
                Entries[i].IsFirstItem = i == 0;

            // Re-raise IsVisibleAndHasEntries so the outer StackPanel
            // collapses immediately when the last entry is removed.
            OnPropertyChanged(nameof(IsVisibleAndHasEntries));
        }

        // ── INotifyPropertyChanged ────────────────────────────────────
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

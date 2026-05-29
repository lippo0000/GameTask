using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace WidgetTemplate
{
    public sealed partial class Widget1 : Page
    {
        private XboxGameBarWidget _widget;
        private ObservableCollection<ProcessEntry> _processes = new ObservableCollection<ProcessEntry>();
        private ObservableCollection<CategoryGroup> _categories = new ObservableCollection<CategoryGroup>();
        private DispatcherTimer _statusTimer;
        private DispatcherTimer _heartbeatTimer;
        private bool _helperWarningDismissed = false; // set true when user manually closes the panel; cleared when service is healthy again
        private bool _helperShownOnce = false;        // auto-show only fires once per service-down event
        private ProcessEntry _iconEditTarget;
        private ProcessEntry _infoEditTarget;

        // Persisted category order/metadata separate from process list
        private ObservableCollection<CategoryGroup> _categoryMeta = new ObservableCollection<CategoryGroup>();

        // ── LOGGER ───────────────────────────────────────────────────
        private async void Log(string msg)
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync("widget_log.txt", CreationCollisionOption.OpenIfExists);
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                await FileIO.AppendLinesAsync(file, new[] { line });
            }
            catch { }
        }

        // ── RE-ANCHOR WIDGET FOCUS ───────────────────────────────────
        private async Task RetainWidgetFocusAsync()
        {
            try
            {
                await Task.Delay(200);
                // Don't steal focus if a text box (or other input) is active
                if (FocusManager.GetFocusedElement() is TextBox) return;
                if (_widget != null)
                    await _widget.TryResizeWindowAsync(new Windows.Foundation.Size(320, 600));
                Log("RetainWidgetFocus: re-anchored");
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, FocusFirstPlayButton);
            }
            catch (Exception ex) { Log($"RetainWidgetFocus ERROR: {ex.Message}"); }
        }

        private static readonly string[] PresetIconsGames = new[]
        {
            "🎮","🕹️","👾","🏆","⚔️","🛡️","🔫","🧩","🎯","🚀","🐉","💀","🗡️","🏹","🔥","⚡","💣","🎲","🃏","🧙"
        };
        private static readonly string[] PresetIconsApps = new[]
        {
            "💻","🖥️","📁","📂","📊","💾","🖱️","⌨️","🗂️","📋","📌","🗃️","🔍","💡","🔑"
        };
        private static readonly string[] PresetIconsTools = new[]
        {
            "⚙️","🔧","🔨","🛠️","🔬","🔭","🧭","⏱️","🧲","🪄","🔮","🤖","🧪","📐","📏"
        };
        private static readonly string[] PresetIconsMedia = new[]
        {
            "🎵","🎬","📷","🎥","🎧","📻","🎙️","🎨","🖌️","🎤","📺","🔊","🎼","🎹","🎸",
            "💬","📧","📞","📱","🌐","📡","🔔","✉️","🗣️","🌍"
        };
        private static readonly string[] PresetIconsMisc = new[]
        {
            "⭐","🌙","🌈","🏠","🚗","📝","✅","❤️","👑","🦊","🐺","🌟","💫","✨","🎭",
            "🌀","🦋","🐬","🦁","🌺","🍀","🌵","🦄","🦅","💎","🏅","☄️","🌌","🏔️","🌊","🎪","🗺️","🌋"
        };

        public Widget1()
        {
            this.InitializeComponent();
            CategoryList.ItemsSource = _categories;
            foreach (var icon in PresetIconsGames) EmojiGridGames.Items.Add(icon);
            foreach (var icon in PresetIconsApps) EmojiGridApps.Items.Add(icon);
            foreach (var icon in PresetIconsTools) EmojiGridTools.Items.Add(icon);
            foreach (var icon in PresetIconsMedia) EmojiGridMedia.Items.Add(icon);
            foreach (var icon in PresetIconsMisc) EmojiGridMisc.Items.Add(icon);
            LoadDefaults();
            LoadData();
            RebuildCategoryView();
            LoadCategoryOrder();
            RefreshOverrideDots();
            StartStatusPolling();
            PopulateCategoryCombo();
            _ = TryLaunchHelperAsync();
            StartHeartbeatPolling();
            // After helper is (hopefully) launched, extract icons for any entries missing one
            _ = ExtractMissingIconsAsync();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
            if (_widget != null)
                await _widget.TryResizeWindowAsync(new Windows.Foundation.Size(320, 600));

            // Intercept keys that would otherwise dismiss the Game Bar widget (e.g. CapsLock)
            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;

            // Restore focus whenever the widget regains activation (e.g. after external flyouts steal it)
            Window.Current.CoreWindow.Activated += (s, activatedArgs) =>
            {
                if (activatedArgs.WindowActivationState != Windows.UI.Core.CoreWindowActivationState.Deactivated)
                    _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, FocusFirstPlayButton);
            };

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, FocusFirstPlayButton);
        }

        private void CoreWindow_KeyDown(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.KeyEventArgs args)
        {
            // Suppress keys that dismiss the widget without being useful here
            switch (args.VirtualKey)
            {
                case Windows.System.VirtualKey.CapitalLock:
                case Windows.System.VirtualKey.Scroll:
                    args.Handled = true;
                    break;
            }
        }

        private void FocusFirstPlayButton()
        {
            try
            {
                var btn = FindFirstPlayButton(CategoryList);
                btn?.Focus(FocusState.Programmatic);
            }
            catch { }
        }

        private Button FindFirstPlayButton(DependencyObject parent)
        {
            int count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is Button b && b.IsTabStop) return b;
                var result = FindFirstPlayButton(child);
                if (result != null) return result;
            }
            return null;
        }

        private bool IsPlayButton(Button b)
        {
            // Play buttons have a FontIcon with the play glyph \uF5B0
            if (b.Content is FontIcon fi && fi.Glyph == "\uF5B0") return true;
            return false;
        }

        private DateTime _lastLaunchTime = DateTime.MinValue;

        private void StartStatusPolling()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (s, e) =>
            {
                try
                {
                    foreach (var p in _processes)
                    {
                        var wasRunning = p.IsRunning;
                        p.RefreshStatus();

                        // Guard: don't auto-return within 10s of a launch (process may not be up yet)
                        if (wasRunning && !p.IsRunning && p.IsLaunched
                            && (DateTime.Now - _lastLaunchTime).TotalSeconds > 10)
                            AutoReturnFromLaunched(p);
                    }
                }
                catch { }
            };
            _statusTimer.Start();
        }

        private void AutoReturnFromLaunched(ProcessEntry entry)
        {
            try
            {
                entry.IsLaunched = false;
                entry.Category = entry.OriginalCategory ?? "Apps";
                entry.OriginalCategory = null;
                SaveData();

                var launchedGroup = _categories.FirstOrDefault(c => c.Name == "Launched");
                launchedGroup?.Entries.Remove(entry);

                var origGroup = _categories.FirstOrDefault(c => c.Name == entry.Category);
                if (origGroup != null)
                {
                    origGroup.Entries.Add(entry);
                    origGroup.RefreshFirstItemFlags();
                }

                if (launchedGroup != null && launchedGroup.Entries.Count == 0)
                    _categories.Remove(launchedGroup);
                else
                    launchedGroup?.RefreshFirstItemFlags();

                Log($"AutoReturn: '{entry.DisplayName}' moved back to '{entry.Category}'");
            }
            catch (Exception ex) { Log($"AutoReturn ERROR: {ex.Message}"); }
        }

        private ProcessEntry GetEntry(object sender)
            => (sender as FrameworkElement)?.DataContext as ProcessEntry;

        // ── EMOJI PICKER ─────────────────────────────────────────────
        private void IconButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;
            _iconEditTarget = entry;
            EmojiSection.Visibility = Visibility.Visible;
            EmojiGridGames.Focus(FocusState.Programmatic);
        }

        private void EmojiGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is string chosen && _iconEditTarget != null)
            {
                _iconEditTarget.Icon = chosen;
                _iconEditTarget.IconImagePath = null; // clear real icon; null is valid for ImageSource binding (empty string is not)
                _iconEditTarget = null;
                EmojiSection.Visibility = Visibility.Collapsed;
                SaveData();
            }
        }

        private async void EmojiMenu_Click(object sender, RoutedEventArgs e)
        {
            await WriteCmdFile("emoji", "");
            // Give the emoji board a moment to open, then refocus the textbox
            // so the picked emoji lands in the input field
            await Task.Delay(600);
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                CustomEmojiBox.Focus(FocusState.Programmatic);
            });
        }

        private async Task PickCustomImageAsync(ProcessEntry entry)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".ico");

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                // Copy to LocalState so the path remains valid across sessions
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var destName = $"customicon_{System.Guid.NewGuid():N}{System.IO.Path.GetExtension(file.Name)}";
                var dest = await file.CopyAsync(folder, destName, Windows.Storage.NameCollisionOption.ReplaceExisting);

                entry.IconImagePath = dest.Path;
                SaveData();
                Log($"PickCustomImage: set icon for '{entry.DisplayName}' → {dest.Path}");
            }
            catch (Exception ex) { Log($"PickCustomImage ERROR: {ex.Message}"); }
        }

        private void IconButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;

            // Prevent the right-tap from also triggering Click on the parent button
            e.Handled = true;

            var menu = new MenuFlyout();
            menu.MenuFlyoutPresenterStyle = (Style)Resources["RoundedMenuFlyoutPresenter"];

            var exeItem = new MenuFlyoutItem
            {
                Text = "Use EXE icon",
                Icon = new FontIcon { Glyph = "\uE7EF", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }
            };

            var emojiItem = new MenuFlyoutItem
            {
                Text = "Pick emoji",
                Icon = new FontIcon { Glyph = "\uE76E", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }
            };

            var imageItem = new MenuFlyoutItem
            {
                Text = "Custom image file",
                Icon = new FontIcon { Glyph = "\uEB9F", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }
            };

            // Track which item the user chose — read it in the Closed handler
            // so we act only after the flyout has fully closed (avoids visual tree conflicts)
            bool pickEmoji = false;
            bool pickExe   = false;
            bool pickImage = false;

            exeItem.Click   += (s, _) => pickExe   = true;
            emojiItem.Click += (s, _) => pickEmoji = true;
            imageItem.Click += (s, _) => pickImage = true;

            menu.Closed += async (s, _) =>
            {
                if (pickExe)
                {
                    await RequestIconExtractionAsync(entry);
                }
                else if (pickImage)
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                    {
                        await PickCustomImageAsync(entry);
                    });
                }
                else if (pickEmoji)
                {
                    // Do NOT touch IconImagePath here — the flyout's sender element (the image
                    // icon button) is still in a transitional input state when Closed fires.
                    // Mutating the Image source binding at this point crashes the visual tree.
                    // Instead, just open the emoji picker; IconImagePath is cleared in
                    // EmojiGrid_ItemClick only after the user confirms a selection.
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        _iconEditTarget = entry;
                        EmojiSection.Visibility = Visibility.Visible;
                        CustomEmojiBox.Focus(FocusState.Programmatic);
                    });
                }
            };

            menu.Items.Add(exeItem);
            menu.Items.Add(emojiItem);
            menu.Items.Add(imageItem);
            menu.ShowAt(sender as FrameworkElement);
        }

        private void EmojiCancel_Click(object sender, RoutedEventArgs e)
        {
            _iconEditTarget = null;
            EmojiSection.Visibility = Visibility.Collapsed;
        }

        private void CustomEmoji_Click(object sender, RoutedEventArgs e)
        {
            var text = CustomEmojiBox.Text.Trim();
            if (string.IsNullOrEmpty(text) || _iconEditTarget == null) return;
            _iconEditTarget.Icon = text;
            _iconEditTarget.IconImagePath = null; // clear real icon; null is valid for ImageSource binding (empty string is not)
            _iconEditTarget = null;
            CustomEmojiBox.Text = "";
            EmojiSection.Visibility = Visibility.Collapsed;
            SaveData();
        }

        // ── CATEGORY COMBO ───────────────────────────────────────────
        private void PopulateCategoryCombo()
        {
            CategoryCombo.Items.Clear();
            foreach (var cat in _categories)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new FontIcon
                {
                    Glyph = !string.IsNullOrEmpty(cat.Icon) ? cat.Icon : "\uE8EC",
                    FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Width = 16,
                    Opacity = !string.IsNullOrEmpty(cat.Icon) ? 1.0 : 0.5,
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock { Text = cat.Name, VerticalAlignment = VerticalAlignment.Center });
                var item = new ComboBoxItem { Content = sp, Tag = cat.Name };
                CategoryCombo.Items.Add(item);
            }

            // + New category entry
            var newSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            newSp.Children.Add(new FontIcon
            {
                Glyph = "\uE710",
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center
            });
            newSp.Children.Add(new TextBlock { Text = "New category…", VerticalAlignment = VerticalAlignment.Center });
            CategoryCombo.Items.Add(new ComboBoxItem { Content = newSp, Tag = "+ New category…" });
            // No default selection — placeholder is shown until user picks a category
        }

        private async void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tag = (CategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            // ContentStates is not driven automatically in a custom template — drive it manually
            VisualStateManager.GoToState(CategoryCombo,
                CategoryCombo.SelectedIndex >= 0 ? "HasContent" : "NoContent", false);
            if (tag == "+ New category…")
                await CreateNewCategoryFromCombo();
        }

        private async Task CreateNewCategoryFromCombo()
        {
            var input = new TextBox
            {
                PlaceholderText = "Category name",
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 44, 44, 54)),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 220, 220)),
                BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255))
            };
            var dialog = new ContentDialog
            {
                Title = "New Category",
                Content = input,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel"
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            {
                var newName = input.Text.Trim();
                if (!_categories.Any(c => c.Name == newName))
                {
                    _categories.Add(new CategoryGroup { Name = newName });
                    SaveData();
                }
                PopulateCategoryCombo();
                var created = CategoryCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == newName);
                if (created != null) CategoryCombo.SelectedItem = created;
            }
            else
            {
                CategoryCombo.SelectedIndex = 0;
            }
        }

        private string GetSelectedCategory()
        {
            var tag = (CategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (tag == null || tag == "+ New category…") return _categories.FirstOrDefault()?.Name ?? "Apps";
            return tag;
        }

        // ── DEFAULT BEHAVIOURS ───────────────────────────────────────
        private PlayBehaviour _defaultPlayBehaviour = PlayBehaviour.RemainOnWidget;
        private CloseBehaviour _defaultCloseBehaviour = CloseBehaviour.RemainOnWidget;
        private FocusFullscreenMode _defaultFocusMode = FocusFullscreenMode.FocusThenFullscreen;
        private bool _launchPinEnabled = true;
        private string _launchedAccentColor = "#4CAF50";

        // ── PER-CATEGORY DEFAULTS ────────────────────────────────────
        private bool _perCatDefaultsEnabled = false;

        // Keyed by category name. Only populated entries need to exist;
        // missing = inherit master global.
        private Dictionary<string, (PlayBehaviour Play,
                                    CloseBehaviour Close,
                                    FocusFullscreenMode Fs)> _catDefaults
            = new Dictionary<string, (PlayBehaviour, CloseBehaviour, FocusFullscreenMode)>();

        private void RefreshOverrideDots()
        {
            foreach (var p in _processes)
            {
                var eff = EffectiveDefaults(p);
                p.HasPlayOverride  = p.PlayBehaviour != eff.Play;
                p.HasCloseOverride = p.CloseBehaviour != eff.Close;
                p.HasFsOverride    = p.FocusMode != eff.Fs;

                // §11 – cosmetic tier label used by GlobalSuffix in tooltips/menus.
                // Mirrors EffectiveDefaults resolution: category tier wins when
                // per-cat is on AND the entry's category has its own stored triple.
                if (_perCatDefaultsEnabled)
                {
                    var catName = (p.IsLaunched ? p.OriginalCategory : p.Category) ?? "Apps";
                    p.DefaultTier = _catDefaults.ContainsKey(catName)
                        ? DefaultTier.Category
                        : DefaultTier.Global;
                }
                else
                {
                    p.DefaultTier = DefaultTier.Global;
                }
            }
        }

        private void LoadDefaults()
        {
            var s = ApplicationData.Current.LocalSettings.Values;
            if (s.TryGetValue("def_play", out var p) && p is int pi) _defaultPlayBehaviour = (PlayBehaviour)pi;
            if (s.TryGetValue("def_close", out var c) && c is int ci) _defaultCloseBehaviour = (CloseBehaviour)ci;
            if (s.TryGetValue("def_fs", out var f) && f is int fi) _defaultFocusMode = (FocusFullscreenMode)fi;
            if (s.TryGetValue("def_launchpin", out var lp) && lp is bool lpb) _launchPinEnabled = lpb;
            if (s.TryGetValue("launched_color", out var lc) && lc is string lcs) _launchedAccentColor = lcs;

            // Per-category defaults
            if (s.TryGetValue("per_cat_defaults", out var pcd) && pcd is bool pcdb)
                _perCatDefaultsEnabled = pcdb;

            // Rebuild _catDefaults from stored keys
            _catDefaults.Clear();
            // We don't know the category names at load time, so scan all keys
            foreach (var key in s.Keys.Where(k => k.StartsWith("catdef_") && k.EndsWith("_play")))
            {
                var catName = key.Substring(7, key.Length - 12); // strip "catdef_" + "_play"
                if (s.TryGetValue($"catdef_{catName}_play", out var cp) && cp is int cpi &&
                    s.TryGetValue($"catdef_{catName}_close", out var cc) && cc is int cci &&
                    s.TryGetValue($"catdef_{catName}_fs", out var cf) && cf is int cfi)
                {
                    _catDefaults[catName] = ((PlayBehaviour)cpi,
                                             (CloseBehaviour)cci,
                                             (FocusFullscreenMode)cfi);
                }
            }
        }

        private void SaveDefaults()
        {
            var s = ApplicationData.Current.LocalSettings.Values;
            s["def_play"] = (int)_defaultPlayBehaviour;
            s["def_close"] = (int)_defaultCloseBehaviour;
            s["def_fs"] = (int)_defaultFocusMode;
            s["def_launchpin"] = _launchPinEnabled;
            s["launched_color"] = _launchedAccentColor;

            // Per-category defaults
            s["per_cat_defaults"] = _perCatDefaultsEnabled;

            foreach (var kvp in _catDefaults)
            {
                s[$"catdef_{kvp.Key}_play"] = (int)kvp.Value.Play;
                s[$"catdef_{kvp.Key}_close"] = (int)kvp.Value.Close;
                s[$"catdef_{kvp.Key}_fs"] = (int)kvp.Value.Fs;
            }
        }

        private bool EntryHasManualOverride(ProcessEntry p)
        {
            var eff = EffectiveDefaults(p);
            return p.PlayBehaviour != eff.Play ||
                   p.CloseBehaviour != eff.Close ||
                   p.FocusMode != eff.Fs;
        }

        // Returns the effective (Play, Close, Fs) triple for an entry.
        // When per-category defaults are enabled, looks up the entry's category
        // (using OriginalCategory for launched entries) and returns that
        // category's triple if one exists, otherwise falls back to master globals.
        private (PlayBehaviour Play, CloseBehaviour Close, FocusFullscreenMode Fs)
            EffectiveDefaults(ProcessEntry p)
        {
            if (_perCatDefaultsEnabled)
            {
                // Launched entries belong to their original category for default resolution
                var catName = (p.IsLaunched ? p.OriginalCategory : p.Category) ?? "Apps";
                if (_catDefaults.TryGetValue(catName, out var catTriple))
                    return catTriple;
            }
            return (_defaultPlayBehaviour, _defaultCloseBehaviour, _defaultFocusMode);
        }

        private void SetOverlay(bool visible)
        {
            if (MainOverlay == null) return;
            MainOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool IsAnyPanelOpen()
        {
            return AddSection.Visibility == Visibility.Visible
                || DefaultSettingsPanel.Visibility == Visibility.Visible
                || ReorderPanel.Visibility == Visibility.Visible
                || HelperErrorPanel.Visibility == Visibility.Visible
                || InfoPanel.Visibility == Visibility.Visible;
        }

        private void SetButtonActive(Button btn, bool active)
        {
            if (btn == null) return;
            btn.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                active
                    ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(128, 255, 255, 255));
            btn.Background = active
                ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255))
                : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
        }

        // Red variant for the warning button — always red, darker bg when panel is open
        private void SetWarningButtonActive(bool active)
        {
            if (LauncherRefreshButton == null) return;
            // Foreground: bright red always; background: dark red when open, transparent when closed
            LauncherRefreshButton.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 244, 67, 54)); // #F44336
            LauncherRefreshButton.Background = active
                ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(60, 244, 67, 54))
                : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
        }

        // ── THEME-SAFE FOREGROUND ─────────────────────────────────────
        // Returns a foreground brush that contrasts with dialog/panel backgrounds
        // regardless of Windows light or dark theme.
        private static Windows.UI.Xaml.Media.SolidColorBrush ThemeFg(byte alpha = 220)
        {
            bool isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            return new Windows.UI.Xaml.Media.SolidColorBrush(
                isDark
                    ? Windows.UI.Color.FromArgb(alpha, 255, 255, 255)   // light text on dark bg
                    : Windows.UI.Color.FromArgb(alpha, 30, 30, 30));   // dark text on light bg
        }

        private static Windows.UI.Xaml.Media.SolidColorBrush ThemeFgDim() => ThemeFg(140);
        private static Windows.UI.Xaml.Media.SolidColorBrush ThemeFgFaint() => ThemeFg(100);


        // ── Dims/undims the GlobalDefaultsGroup when per-category is toggled ─────
        private void ApplyGlobalDefaultsDimState()
        {
            if (GlobalDefaultsGroup == null) return;
            bool dim = _perCatDefaultsEnabled;
            GlobalDefaultsGroup.Opacity          = dim ? 0.35 : 1.0;
            GlobalDefaultsGroup.IsHitTestVisible = !dim;
        }

        private void ToggleDefaultSettings_Click(object sender, RoutedEventArgs e)
        {
            bool opening = DefaultSettingsPanel.Visibility == Visibility.Collapsed;
            AddSection.Visibility = Visibility.Collapsed;
            ReorderPanel.Visibility = Visibility.Collapsed;
            HelperErrorPanel.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = Visibility.Collapsed;
            _helperWarningDismissed = true;
            DefaultSettingsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            SaveButtonRow.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            SetOverlay(opening);
            SetButtonActive(DefaultSettingsButton, opening);
            SetButtonActive(ReorderCategoriesButton, false);
            SetButtonActive(ToggleAddButton, false);
            SetButtonActive(ToggleInfoButton, false);
            SetWarningButtonActive(false);
            if (opening)
            {
                DefaultPlayCombo.SelectedIndex = (int)_defaultPlayBehaviour;
                DefaultCloseCombo.SelectedIndex = (int)_defaultCloseBehaviour;
                DefaultFsCombo.SelectedIndex = (int)_defaultFocusMode;

                // Sync per-category toggle without triggering a re-entrant save
                PerCatDefaultsToggle.Toggled -= PerCatDefaultsToggle_Toggled;
                PerCatDefaultsToggle.IsOn = _perCatDefaultsEnabled;
                PerCatDefaultsToggle.Toggled += PerCatDefaultsToggle_Toggled;

                // Reflect the dim state of GlobalDefaultsGroup
                ApplyGlobalDefaultsDimState();

                RebuildCatDefaultsSection();
            }
        }

        // ── Shared reset-overrides dialog ────────────────────────────
        // Returns the entries the user confirmed to reset, or null if cancelled.
        private async Task<List<ProcessEntry>> ShowResetOverridesDialogAsync(List<ProcessEntry> overridden)
        {
            bool updatingMaster = false;

            var masterCheck = new CheckBox
            {
                Content = "Reset ALL manually configured apps",
                IsChecked = true,
                FontSize = 12,
                Foreground = ThemeFg(200),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var checkboxes = overridden.Select(p => new CheckBox
            {
                Content = p.DisplayName,
                IsChecked = true,
                FontSize = 12,
                Tag = p,
                Foreground = ThemeFg(220),
                Margin = new Thickness(20, 0, 0, 0)   // indent to imply hierarchy
            }).ToList();

            void SyncMasterFromChildren()
            {
                updatingMaster = true;
                int checkedCount = checkboxes.Count(cb => cb.IsChecked == true);
                masterCheck.IsChecked = checkedCount == checkboxes.Count ? true
                                      : checkedCount == 0 ? false
                                      : (bool?)null;                         // indeterminate
                updatingMaster = false;
            }

            masterCheck.Checked += (s, _) => { if (!updatingMaster) foreach (var cb in checkboxes) cb.IsChecked = true; };
            masterCheck.Unchecked += (s, _) => { if (!updatingMaster) foreach (var cb in checkboxes) cb.IsChecked = false; };

            foreach (var cb in checkboxes)
            {
                cb.Checked += (s, _) => SyncMasterFromChildren();
                cb.Unchecked += (s, _) => SyncMasterFromChildren();
            }

            var hintText = new TextBlock
            {
                Text = "✓ Checked = reset to new defaults   ✗ Unchecked = keep manual changes",
                FontSize = 10,
                Foreground = ThemeFgDim(),
                TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var listPanel = new StackPanel { Spacing = 4 };
            listPanel.Children.Add(masterCheck);
            listPanel.Children.Add(new Windows.UI.Xaml.Shapes.Rectangle
            {
                Height = 1,
                Fill = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                Margin = new Thickness(0, 2, 0, 4)
            });
            listPanel.Children.Add(hintText);
            foreach (var cb in checkboxes) listPanel.Children.Add(cb);

            var dialog = new ContentDialog
            {
                Title = "Also reset manually changed apps?",
                Content = listPanel,
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel"
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

            return checkboxes
                .Where(cb => cb.IsChecked == true && cb.Tag is ProcessEntry)
                .Select(cb => (ProcessEntry)cb.Tag)
                .ToList();
        }

        private async void DefaultSettingsSave_Click(object sender, RoutedEventArgs e)
        {
            var newPlay = (PlayBehaviour)DefaultPlayCombo.SelectedIndex;
            var newClose = (CloseBehaviour)DefaultCloseCombo.SelectedIndex;
            var newFs = (FocusFullscreenMode)DefaultFsCombo.SelectedIndex;

            bool behaviourChanged = newPlay != _defaultPlayBehaviour
                                 || newClose != _defaultCloseBehaviour
                                 || newFs != _defaultFocusMode;

            // Find apps the user has manually dotted — only those appear in the reset dialog
            var overridden = _processes.Where(p =>
                p.IsManualOverride).ToList();

            // Now update defaults
            _defaultPlayBehaviour = newPlay;
            _defaultCloseBehaviour = newClose;
            _defaultFocusMode = newFs;
            SaveDefaults();

            // Silently move non-manual entries to the new defaults so they don't get dots.
            // When per-category defaults are on, only touch entries whose category has no
            // custom triple of its own (those entries inherit the master globals).
            foreach (var p in _processes.Where(p => !p.IsManualOverride))
            {
                if (_perCatDefaultsEnabled)
                {
                    var catName = (p.IsLaunched ? p.OriginalCategory : p.Category) ?? "Apps";
                    if (_catDefaults.ContainsKey(catName)) continue; // category has its own triple — leave it
                }
                p.PlayBehaviour = _defaultPlayBehaviour;
                p.CloseBehaviour = _defaultCloseBehaviour;
                p.FocusMode = _defaultFocusMode;
            }
            RefreshOverrideDots();

            DefaultSettingsPanel.Visibility = Visibility.Collapsed;
            SaveButtonRow.Visibility = Visibility.Collapsed;
            SetOverlay(false);
            SetButtonActive(DefaultSettingsButton, false);

            if (overridden.Count > 0)
            {
                var toReset = await ShowResetOverridesDialogAsync(overridden);
                if (toReset != null)
                {
                    foreach (var p in toReset)
                    {
                        p.PlayBehaviour = _defaultPlayBehaviour;
                        p.CloseBehaviour = _defaultCloseBehaviour;
                        p.FocusMode = _defaultFocusMode;
                        p.IsManualOverride = false;
                    }
                    RefreshOverrideDots();
                    SaveData();
                }
            }
        }

        private async void DefaultSettings_Click(object sender, RoutedEventArgs e)
        {
            var playCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            playCombo.Items.Add("Stay on Game Bar");
            playCombo.Items.Add("Close Game Bar");
            playCombo.Items.Add("Focus launched app");
            playCombo.SelectedIndex = (int)_defaultPlayBehaviour;

            var closeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            closeCombo.Items.Add("Stay on Game Bar");
            closeCombo.Items.Add("Close Game Bar");
            closeCombo.SelectedIndex = (int)_defaultCloseBehaviour;

            var fsCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            fsCombo.Items.Add("Maximize window");
            fsCombo.Items.Add("Key only");
            fsCombo.Items.Add("Focus → Key");
            fsCombo.SelectedIndex = (int)_defaultFocusMode;

            var dim = ThemeFg(180);
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = "Default play behaviour", FontSize = 11, Foreground = dim });
            panel.Children.Add(playCombo);
            panel.Children.Add(new TextBlock { Text = "Default close behaviour", FontSize = 11, Foreground = dim });
            panel.Children.Add(closeCombo);
            panel.Children.Add(new TextBlock { Text = "Default fullscreen mode", FontSize = 11, Foreground = dim });
            panel.Children.Add(fsCombo);

            var dialog = new ContentDialog
            {
                Title = "Default settings",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel"
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var newPlay = (PlayBehaviour)playCombo.SelectedIndex;
            var newClose = (CloseBehaviour)closeCombo.SelectedIndex;
            var newFs = (FocusFullscreenMode)fsCombo.SelectedIndex;

            // Find apps with manual overrides BEFORE updating defaults
            var overridden = _processes.Where(p => EntryHasManualOverride(p)).ToList();

            // Update defaults
            _defaultPlayBehaviour = newPlay;
            _defaultCloseBehaviour = newClose;
            _defaultFocusMode = newFs;
            SaveDefaults();

            // Silently move non-manual entries to the new defaults so they don't get dots
            foreach (var p in _processes.Where(p => !p.IsManualOverride))
            {
                p.PlayBehaviour = _defaultPlayBehaviour;
                p.CloseBehaviour = _defaultCloseBehaviour;
                p.FocusMode = _defaultFocusMode;
            }
            RefreshOverrideDots();

            // Step 2: if any apps had manual overrides, ask which to reset
            if (overridden.Count > 0)
            {
                var toReset = await ShowResetOverridesDialogAsync(overridden);
                if (toReset != null)
                {
                    foreach (var p in toReset)
                    {
                        p.PlayBehaviour = _defaultPlayBehaviour;
                        p.CloseBehaviour = _defaultCloseBehaviour;
                        p.FocusMode = _defaultFocusMode;
                        p.IsManualOverride = false;
                    }
                    RefreshOverrideDots();
                    SaveData();
                }
            }
        }

        // ── ADD SECTION ──────────────────────────────────────────────
        // ── REORDER CATEGORIES ───────────────────────────────────────
        private void ReorderCategories_Click(object sender, RoutedEventArgs e)
        {
            bool opening = ReorderPanel.Visibility == Visibility.Collapsed;
            AddSection.Visibility = Visibility.Collapsed;
            DefaultSettingsPanel.Visibility = Visibility.Collapsed;
            SaveButtonRow.Visibility = Visibility.Collapsed;
            HelperErrorPanel.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = Visibility.Collapsed;
            _helperWarningDismissed = true;
            ReorderPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            SetOverlay(opening);
            SetButtonActive(ReorderCategoriesButton, opening);
            SetButtonActive(DefaultSettingsButton, false);
            SetButtonActive(ToggleInfoButton, false);
            SetButtonActive(ToggleAddButton, false);
            SetWarningButtonActive(false);
            if (opening)
            {
                // Sync XAML toggle state (unsubscribe first to avoid re-entrant save)
                PinLaunchToggle.Toggled -= PinLaunchToggle_Toggled;
                PinLaunchToggle.IsOn = _launchPinEnabled;
                PinLaunchToggle.Toggled += PinLaunchToggle_Toggled;
                RebuildReorderPanel();
            }
        }

        private void PinLaunchToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _launchPinEnabled = PinLaunchToggle.IsOn;
            SaveDefaults();
            RebuildReorderPanel();
        }

        private void PerCatDefaultsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _perCatDefaultsEnabled = PerCatDefaultsToggle.IsOn;

            if (_perCatDefaultsEnabled)
            {
                // Seed any category that doesn't yet have its own triple from master globals
                foreach (var cat in _categories.Where(c => c.Name != "Launched"))
                {
                    if (!_catDefaults.ContainsKey(cat.Name))
                        _catDefaults[cat.Name] = (_defaultPlayBehaviour,
                                                   _defaultCloseBehaviour,
                                                   _defaultFocusMode);
                }
            }

            SaveDefaults();
            RefreshOverrideDots();
            ApplyGlobalDefaultsDimState();
            RebuildCatDefaultsSection();
        }

        // ── PER-CATEGORY DEFAULTS SECTION (Task 15) ──────────────────
        // Guard flag: true while programmatically setting SelectedIndex on category
        // combo boxes, prevents SelectionChanged handlers from firing spuriously.
        private bool _rebuilding = false;

        // Applies the per-category default triple to all non-manually-overridden
        // entries in the given category, then persists and refreshes override dots.
        private void ApplyCatDefaultsToNonOverridden(string catName)
        {
            if (!_catDefaults.TryGetValue(catName, out var triple)) return;

            foreach (var p in _processes.Where(p =>
                !p.IsManualOverride &&
                ((p.IsLaunched ? p.OriginalCategory : p.Category) ?? "Apps") == catName))
            {
                p.PlayBehaviour  = triple.Play;
                p.CloseBehaviour = triple.Close;
                p.FocusMode      = triple.Fs;
            }

            // Keep CategoryGroup model in sync
            var cat = _categories.FirstOrDefault(c => c.Name == catName);
            if (cat != null)
            {
                cat.CatDefaultPlay      = triple.Play;
                cat.CatDefaultClose     = triple.Close;
                cat.CatDefaultFocusMode = triple.Fs;
            }

            RefreshOverrideDots();
            SaveData();
        }

        // Rebuilds the per-category defaults UI section inside DefaultSettingsPanel.
        // Called whenever the panel opens or the PerCatDefaultsToggle changes.
        private void RebuildCatDefaultsSection()
        {
            // CatDefaultsSection is a StackPanel defined in XAML (Task 3).
            // Show/hide the whole section based on the toggle.
            CatDefaultsSection.Visibility = _perCatDefaultsEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!_perCatDefaultsEnabled)
                return;

            // Clear previous dynamically-built rows (keep any static header added in XAML)
            CatDefaultsSection.Children.Clear();

            // Combo item strings — must match the XAML combos exactly
            var playItems  = new[] { "Stay on Game Bar", "Close Game Bar", "Focus launched app" };
            var closeItems = new[] { "Stay on Game Bar", "Close Game Bar" };
            var fsItems    = new[] { "Maximize window",  "Key only",        "Focus → Key" };

            // Label+icon glyphs for each behaviour row (matches GlobalDefaultsGroup in XAML)
            var rowMeta = new[]
            {
                (Glyph: "\uF5B0", Label: "Play behaviour"),
                (Glyph: "\uE711", Label: "Close behaviour"),
                (Glyph: "\uE740", Label: "Fullscreen mode"),
            };

            foreach (var cat in _categories.Where(c => c.Name != "Launched"))
            {
                var catName = cat.Name; // capture for closures

                // ── thin divider ──────────────────────────────────────
                CatDefaultsSection.Children.Add(new Windows.UI.Xaml.Shapes.Rectangle
                {
                    Height = 1,
                    Fill = new Windows.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(35, 255, 255, 255)),
                    Margin = new Thickness(0, 4, 0, 0)
                });

                // ── collapsible header row: [chevron] [category icon] [category name] ──
                var bodyPanel = new StackPanel
                {
                    Spacing = 4,
                    Margin  = new Thickness(4, 2, 0, 4),
                    Visibility = Visibility.Collapsed   // collapsed by default
                };

                var chevronIcon = new FontIcon
                {
                    Glyph      = "\uE76C",   // right-pointing chevron (collapsed)
                    FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize   = 10
                };
                var chevronBtn = new Button
                {
                    Content            = chevronIcon,
                    Width              = 24,
                    Height             = 24,
                    Padding            = new Thickness(0),
                    IsTabStop          = true,
                    UseSystemFocusVisuals = true,
                    Style              = (Style)Resources["IconBtn"],
                    Foreground         = ThemeFg(160)
                };

                var headerRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing     = 6,
                    Margin      = new Thickness(0, 4, 0, 0)
                };

                // Chevron always first
                headerRow.Children.Add(chevronBtn);

                // Category icon — only shown when the CategoryGroup has one set
                if (!string.IsNullOrEmpty(cat.Icon))
                {
                    headerRow.Children.Add(new FontIcon
                    {
                        Glyph      = cat.Icon,
                        FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                        FontSize   = 12,
                        Foreground = ThemeFg(180),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                headerRow.Children.Add(new TextBlock
                {
                    Text              = catName,
                    FontSize          = 12,
                    FontWeight        = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground        = ThemeFg(220),
                    VerticalAlignment = VerticalAlignment.Center
                });

                // Toggle expand/collapse on chevron click
                chevronBtn.Click += (s, _) =>
                {
                    bool expanded = bodyPanel.Visibility == Visibility.Visible;
                    bodyPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
                    chevronIcon.Glyph    = expanded ? "\uE76C" : "\uE70D"; // right / down
                };

                CatDefaultsSection.Children.Add(headerRow);

                // ── body: 3 label+combo pairs (inline layout matching GlobalDefaultsGroup) ──
                if (!_catDefaults.ContainsKey(catName))
                    _catDefaults[catName] = (_defaultPlayBehaviour,
                                             _defaultCloseBehaviour,
                                             _defaultFocusMode);

                var triple = _catDefaults[catName];

                ComboBox MakeCombo(string[] items, int selectedIndex)
                {
                    var cb = new ComboBox
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        FontSize = 11,
                        Margin   = new Thickness(0, 0, 0, 2),
                        Style    = (Style)Resources["DarkComboBox"]
                    };
                    foreach (var item in items) cb.Items.Add(item);
                    return cb;
                }

                var playCombo  = MakeCombo(playItems,  (int)triple.Play);
                var closeCombo = MakeCombo(closeItems, (int)triple.Close);
                var fsCombo    = MakeCombo(fsItems,    (int)triple.Fs);

                // Set initial selection under the guard flag
                _rebuilding = true;
                playCombo.SelectedIndex  = (int)triple.Play;
                closeCombo.SelectedIndex = (int)triple.Close;
                fsCombo.SelectedIndex    = (int)triple.Fs;
                _rebuilding = false;

                // ── SelectionChanged handlers ─────────────────────────
                playCombo.SelectionChanged += (s, e) =>
                {
                    if (_rebuilding || playCombo.SelectedIndex < 0) return;
                    var current = _catDefaults.TryGetValue(catName, out var t) ? t
                                : (Play: _defaultPlayBehaviour, Close: _defaultCloseBehaviour, Fs: _defaultFocusMode);
                    _catDefaults[catName] = ((PlayBehaviour)playCombo.SelectedIndex,
                                             current.Close,
                                             current.Fs);
                    SaveDefaults();
                    ApplyCatDefaultsToNonOverridden(catName);
                };

                closeCombo.SelectionChanged += (s, e) =>
                {
                    if (_rebuilding || closeCombo.SelectedIndex < 0) return;
                    var current = _catDefaults.TryGetValue(catName, out var t) ? t
                                : (Play: _defaultPlayBehaviour, Close: _defaultCloseBehaviour, Fs: _defaultFocusMode);
                    _catDefaults[catName] = (current.Play,
                                             (CloseBehaviour)closeCombo.SelectedIndex,
                                             current.Fs);
                    SaveDefaults();
                    ApplyCatDefaultsToNonOverridden(catName);
                };

                fsCombo.SelectionChanged += (s, e) =>
                {
                    if (_rebuilding || fsCombo.SelectedIndex < 0) return;
                    var current = _catDefaults.TryGetValue(catName, out var t) ? t
                                : (Play: _defaultPlayBehaviour, Close: _defaultCloseBehaviour, Fs: _defaultFocusMode);
                    _catDefaults[catName] = (current.Play,
                                             current.Close,
                                             (FocusFullscreenMode)fsCombo.SelectedIndex);
                    SaveDefaults();
                    ApplyCatDefaultsToNonOverridden(catName);
                };

                // ── build inline-label body rows (icon+label left, combo right) ─────
                var combos = new[] { playCombo, closeCombo, fsCombo };
                for (int i = 0; i < rowMeta.Length; i++)
                {
                    var meta = rowMeta[i];
                    var combo = combos[i];
                    var row = new Grid { Margin = new Thickness(0, 1, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var labelPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0),
                        MinWidth = 100
                    };
                    labelPanel.Children.Add(new FontIcon
                    {
                        Glyph      = meta.Glyph,
                        FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                        FontSize   = 10,
                        Foreground = ThemeFgFaint(),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    labelPanel.Children.Add(new TextBlock
                    {
                        Text              = meta.Label,
                        FontSize          = 10,
                        Foreground        = ThemeFgFaint(),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    Grid.SetColumn(labelPanel, 0);
                    Grid.SetColumn(combo, 1);
                    row.Children.Add(labelPanel);
                    row.Children.Add(combo);
                    bodyPanel.Children.Add(row);
                }

                CatDefaultsSection.Children.Add(bodyPanel);
            }
        }

        private static readonly string[] CategoryAccentColors = new[]
        {
            "",         // none (default)
            "#F44336",  // red
            "#FF9800",  // orange
            "#FFC107",  // amber
            "#4CAF50",  // green
            "#4FC3FF",  // blue
            "#CE93D8",  // purple
            "#F48FB1",  // pink
        };

        private static readonly string[] CategoryIcons = new[]
        {
            "\uE7FC", // Apps (default grid)
            "\uE7BF", // Games (controller)
            "\uE74C", // Tools (wrench)
            "\uE8A5", // Media (music)
            "\uE718", // Folder
            "\uE8B7", // Star/Favorites
            "\uE716", // Work (briefcase)
            "\uE8EC", // Misc (tag)
        };

        private void RebuildReorderPanel()
        {
            ReorderList.Children.Clear();

            // ── Launched special row (when pin enabled) ────────────────
            if (_launchPinEnabled)
            {
                // Ensure a conceptual accent color is maintained
                var launchedGroup = _categories.FirstOrDefault(c => c.Name == "Launched");

                // Icon
                var lIconDisplay = new FontIcon
                {
                    Glyph = "\uE768",
                    FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 13,
                    Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(ParseColor(_launchedAccentColor))
                };
                var lIconBtn = new Button
                {
                    Content = lIconDisplay,
                    Width = 28,
                    Height = 28,
                    IsTabStop = false,
                    Style = (Style)Resources["IconBtn"],
                    IsEnabled = false
                };

                // Name
                var lName = new TextBlock
                {
                    Text = "Launched",
                    FontSize = 12,
                    Foreground = ThemeFg(220),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = Windows.UI.Xaml.TextTrimming.None,
                    TextWrapping = Windows.UI.Xaml.TextWrapping.NoWrap
                };

                // Color dot picker
                var lColor = string.IsNullOrEmpty(_launchedAccentColor)
                    ? Windows.UI.Color.FromArgb(60, 255, 255, 255)
                    : ParseColor(_launchedAccentColor);
                var lColorCircle = new Windows.UI.Xaml.Shapes.Ellipse
                {
                    Width = 13,
                    Height = 13,
                    Fill = new Windows.UI.Xaml.Media.SolidColorBrush(lColor),
                    IsHitTestVisible = false
                };
                var lDotBtn = new Button
                {
                    Content = lColorCircle,
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    IsTabStop = true,
                    UseSystemFocusVisuals = true,
                    Style = (Style)Resources["IconBtn"]
                };
                ToolTipService.SetToolTip(lDotBtn, "Change colour");
                lDotBtn.Click += (s, _) =>
                {
                    var dotGrid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Padding = new Thickness(4) };
                    foreach (var color in CategoryAccentColors)
                    {
                        var c = color;
                        var isSelected = _launchedAccentColor == c;
                        var dot = new Button
                        {
                            Width = 20,
                            Height = 20,
                            Padding = new Thickness(0),
                            CornerRadius = new CornerRadius(10),
                            BorderThickness = isSelected ? new Thickness(2) : new Thickness(0),
                            BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                            Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                                string.IsNullOrEmpty(c)
                                    ? Windows.UI.Color.FromArgb(60, 255, 255, 255)
                                    : ParseColor(c)),
                            IsTabStop = false
                        };
                        dot.Click += (s2, e2) =>
                        {
                            _launchedAccentColor = c;
                            SaveDefaults();
                            if (launchedGroup != null) launchedGroup.AccentColor = c;
                            RebuildReorderPanel();
                        };
                        dotGrid.Children.Add(dot);
                    }
                    new Flyout
                    {
                        Content = dotGrid,
                        Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top
                    }.ShowAt(lDotBtn);
                };

                var lRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                lRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Pixel) }); // aligns with regular rows (up+down buttons width)
                lRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                lRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                lRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                // col 0: spacer, col 1: icon, col 2: name, col 3: dots
                Grid.SetColumn(lIconBtn, 1); lIconBtn.Margin = new Thickness(0, 0, 4, 0);
                Grid.SetColumn(lName, 2);
                Grid.SetColumn(lDotBtn, 3);
                lRow.Children.Add(lIconBtn);
                lRow.Children.Add(lName);
                lRow.Children.Add(lDotBtn);
                ReorderList.Children.Add(lRow);

                // Divider
                ReorderList.Children.Add(new Windows.UI.Xaml.Shapes.Rectangle
                {
                    Height = 1,
                    Fill = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                    Margin = new Thickness(0, 6, 0, 6)
                });
            }

            // ── Regular category rows ──────────────────────────────────
            for (int i = 0; i < _categories.Count; i++)
            {
                var cat = _categories[i];
                if (cat.Name == "Launched") continue; // managed by pin row above

                var upBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE96D", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 11 },
                    Width = 26,
                    Height = 26,
                    IsTabStop = true,
                    UseSystemFocusVisuals = true,
                    Style = (Style)Resources["IconBtn"],
                    Foreground = ThemeFg(160)
                };
                ToolTipService.SetToolTip(upBtn, "Move up");
                upBtn.Click += (s, _) =>
                {
                    var nonLaunched = _categories.Where(c => c.Name != "Launched").ToList();
                    var curIdx = _categories.IndexOf(cat);
                    var prevNonLaunched = _categories.Take(curIdx).LastOrDefault(c => c.Name != "Launched");
                    if (prevNonLaunched != null) _categories.Move(curIdx, _categories.IndexOf(prevNonLaunched));
                    else _categories.Move(curIdx, _categories.Count - 1);
                    SaveCategoryOrder(); RebuildReorderPanel();
                };

                var downBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE96E", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 11 },
                    Width = 26,
                    Height = 26,
                    IsTabStop = true,
                    UseSystemFocusVisuals = true,
                    Style = (Style)Resources["IconBtn"],
                    Foreground = ThemeFg(160)
                };
                ToolTipService.SetToolTip(downBtn, "Move down");
                downBtn.Click += (s, _) =>
                {
                    var curIdx = _categories.IndexOf(cat);
                    var nextNonLaunched = _categories.Skip(curIdx + 1).FirstOrDefault(c => c.Name != "Launched");
                    if (nextNonLaunched != null) _categories.Move(curIdx, _categories.IndexOf(nextNonLaunched));
                    else
                    {
                        var firstNonLaunched = _categories.FirstOrDefault(c => c.Name != "Launched");
                        if (firstNonLaunched != null) _categories.Move(curIdx, _categories.IndexOf(firstNonLaunched));
                    }
                    SaveCategoryOrder(); RebuildReorderPanel();
                };

                bool hasIcon = !string.IsNullOrEmpty(cat.Icon);
                var iconDisplay = new FontIcon
                {
                    Glyph = hasIcon ? cat.Icon : "\uE8EC",
                    FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 13,
                    Opacity = hasIcon ? 1.0 : 0.5,
                    Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255))
                };
                var iconBtn = new Button
                {
                    Content = iconDisplay,
                    Width = 28,
                    Height = 28,
                    IsTabStop = true,
                    UseSystemFocusVisuals = true,
                    Style = (Style)Resources["IconBtn"],
                    Foreground = ThemeFg(180),
                    Margin = new Thickness(6, 0, 4, 0) // gap after arrows, close to name
                };
                ToolTipService.SetToolTip(iconBtn, "Change icon");
                iconBtn.Click += (s, _) =>
                {
                    var flyout = new MenuFlyout();
                    flyout.MenuFlyoutPresenterStyle = (Style)Resources["RoundedMenuFlyoutPresenter"];
                    var noIconItem = new MenuFlyoutItem { Text = "No icon" };
                    if (string.IsNullOrEmpty(cat.Icon)) noIconItem.Icon = new FontIcon { Glyph = "\uE73E" };
                    noIconItem.Click += (s2, e2) => { cat.Icon = ""; SaveCategoryOrder(); RebuildReorderPanel(); };
                    flyout.Items.Add(noIconItem);
                    flyout.Items.Add(new MenuFlyoutSeparator());
                    foreach (var glyph in CategoryIcons)
                    {
                        var g = glyph;
                        var item = new MenuFlyoutItem
                        {
                            Icon = new FontIcon { Glyph = g, FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }
                        };
                        if (cat.Icon == g) item.Icon = new FontIcon { Glyph = "\uE73E" };
                        item.Click += (s2, e2) => { cat.Icon = g; SaveCategoryOrder(); RebuildReorderPanel(); };
                        flyout.Items.Add(item);
                    }
                    flyout.ShowAt(iconBtn);
                };

                var nameBlock = new TextBlock
                {
                    Text = cat.Name,
                    FontSize = 12,
                    Foreground = ThemeFg(220),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = Windows.UI.Xaml.TextTrimming.None,
                    TextWrapping = Windows.UI.Xaml.TextWrapping.NoWrap
                };

                var eyeBtn = new Button
                {
                    Content = new FontIcon
                    {
                        Glyph = cat.IsHidden ? "\uED1A" : "\uE7B3",
                        FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                        FontSize = 13
                    },
                    Width = 28,
                    Height = 28,
                    IsTabStop = true,
                    UseSystemFocusVisuals = true,
                    Style = (Style)Resources["IconBtn"],
                    Foreground = cat.IsHidden ? ThemeFg(80) : ThemeFg(180)
                };
                ToolTipService.SetToolTip(eyeBtn, cat.IsHidden ? "Show category" : "Hide category");
                eyeBtn.Click += (s, _) => { cat.IsHidden = !cat.IsHidden; SaveCategoryOrder(); RebuildReorderPanel(); };

                var currentColor = string.IsNullOrEmpty(cat.AccentColor)
                    ? Windows.UI.Color.FromArgb(60, 255, 255, 255)
                    : ParseColor(cat.AccentColor);
                var colorCircle = new Windows.UI.Xaml.Shapes.Ellipse
                {
                    Width = 13,
                    Height = 13,
                    Fill = new Windows.UI.Xaml.Media.SolidColorBrush(currentColor),
                    IsHitTestVisible = false
                };
                var dotPanel = new Button
                {
                    Content = colorCircle,
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    IsTabStop = true,
                    UseSystemFocusVisuals = true,
                    Style = (Style)Resources["IconBtn"]
                };
                ToolTipService.SetToolTip(dotPanel, "Change colour");
                dotPanel.Click += (s, _) =>
                {
                    var dotGrid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Padding = new Thickness(4) };
                    foreach (var color in CategoryAccentColors)
                    {
                        var c = color;
                        var isSelected = cat.AccentColor == c;
                        var dot = new Button
                        {
                            Width = 20,
                            Height = 20,
                            Padding = new Thickness(0),
                            CornerRadius = new CornerRadius(10),
                            BorderThickness = isSelected ? new Thickness(2) : new Thickness(0),
                            BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                            Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                                string.IsNullOrEmpty(c)
                                    ? Windows.UI.Color.FromArgb(60, 255, 255, 255)
                                    : ParseColor(c)),
                            IsTabStop = false
                        };
                        dot.Click += (s2, e2) => { cat.AccentColor = c; SaveCategoryOrder(); RebuildReorderPanel(); };
                        dotGrid.Children.Add(dot);
                    }
                    new Flyout
                    {
                        Content = dotGrid,
                        Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top
                    }.ShowAt(dotPanel);
                };

                var renameBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE8AC", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 12 },
                    Width = 28,
                    Height = 28,
                    IsTabStop = true,
                    UseSystemFocusVisuals = true,
                    Style = (Style)Resources["IconBtn"],
                    Foreground = ThemeFg(160)
                };
                ToolTipService.SetToolTip(renameBtn, "Rename category");
                renameBtn.Click += async (s, _) =>
                {
                    var input = new TextBox
                    {
                        Text = cat.Name,
                        PlaceholderText = "Category name",
                        FontSize = 13,
                        Padding = new Thickness(8, 6, 8, 6),
                        Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 44, 44, 54)),
                        Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 220, 220)),
                        BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255))
                    };
                    input.PreviewKeyDown += (si, ke) => { if (ke.Key != Windows.System.VirtualKey.Escape) ke.Handled = true; };
                    var dialog = new ContentDialog { Title = "Rename Category", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
                    {
                        var oldName = cat.Name;          // capture before any mutation
                        var newName = input.Text.Trim();

                        // ── §12 catDefaults migration ──────────────────────────
                        // Must happen before cat.Name changes so oldName is still
                        // consistent with _catDefaults and LocalSettings keys.
                        if (_catDefaults.TryGetValue(oldName, out var triple))
                        {
                            // 1. In-memory dictionary: add new key, remove old
                            _catDefaults[newName] = triple;
                            _catDefaults.Remove(oldName);

                            // 2. LocalSettings: write the three new keys, delete the three old keys.
                            //    Both halves happen before any save call, keeping storage atomic
                            //    within the same synchronous block.
                            var settings = ApplicationData.Current.LocalSettings.Values;
                            settings[$"catdef_{newName}_play"]  = (int)triple.Play;
                            settings[$"catdef_{newName}_close"] = (int)triple.Close;
                            settings[$"catdef_{newName}_fs"]    = (int)triple.Fs;
                            settings.Remove($"catdef_{oldName}_play");
                            settings.Remove($"catdef_{oldName}_close");
                            settings.Remove($"catdef_{oldName}_fs");
                        }

                        // ── migrate process entries ────────────────────────────
                        // Category field on normal entries
                        foreach (var p in _processes.Where(p => p.Category == oldName))
                            p.Category = newName;
                        // OriginalCategory on currently-launched entries (their live
                        // Category is "Launched" so the loop above won't touch them)
                        foreach (var p in _processes.Where(p => p.OriginalCategory == oldName))
                            p.OriginalCategory = newName;

                        // ── rename the CategoryGroup itself ────────────────────
                        cat.Name = newName;

                        SaveData(); SaveCategoryOrder(); RebuildReorderPanel();
                    }
                };

                // Layout: up | down | [gap via margin on icon] | icon | name(*) | eye | rename | dots
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(upBtn, 0);
                Grid.SetColumn(downBtn, 1);
                Grid.SetColumn(iconBtn, 2);
                Grid.SetColumn(nameBlock, 3);
                Grid.SetColumn(eyeBtn, 4);
                Grid.SetColumn(renameBtn, 5);
                Grid.SetColumn(dotPanel, 6);

                row.Children.Add(upBtn);
                row.Children.Add(downBtn);
                row.Children.Add(iconBtn);
                row.Children.Add(nameBlock);
                row.Children.Add(eyeBtn);
                row.Children.Add(renameBtn);
                row.Children.Add(dotPanel);

                ReorderList.Children.Add(row);
            }
        }

        private static Windows.UI.Color ParseColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                return Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { return Windows.UI.Color.FromArgb(180, 255, 255, 255); }
        }

        private void SaveCategoryOrder()
        {
            try
            {
                // Format per entry: name|isHidden|accentColor
                var order = string.Join(",", _categories.Select(c =>
                    $"{c.Name}|{(c.IsHidden ? "1" : "0")}|{c.AccentColor}|{c.Icon}"));
                ApplicationData.Current.LocalSettings.Values["cat_order"] = order;
            }
            catch { }
        }

        private void LoadCategoryOrder()
        {
            try
            {
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue("cat_order", out var raw) || raw == null) return;
                var entries = raw.ToString().Split(',');
                var names = entries.Select(e => e.Split('|')[0]).ToArray();
                var ordered = names
                    .Select(n => _categories.FirstOrDefault(c => c.Name == n))
                    .Where(c => c != null)
                    .ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var curIdx = _categories.IndexOf(ordered[i]);
                    if (curIdx != i && i < _categories.Count)
                        _categories.Move(curIdx, i);
                }
                // Restore IsHidden and AccentColor
                foreach (var entry in entries)
                {
                    var parts = entry.Split('|');
                    if (parts.Length < 1) continue;
                    var cat = _categories.FirstOrDefault(c => c.Name == parts[0]);
                    if (cat == null) continue;
                    if (parts.Length > 1) cat.IsHidden = parts[1] == "1";
                    if (parts.Length > 2) cat.AccentColor = parts[2];
                    if (parts.Length > 3) cat.Icon = parts[3];
                }
            }
            catch { }
        }

        private void ToggleAdd_Click(object sender, RoutedEventArgs e)
        {
            bool opening = AddSection.Visibility == Visibility.Collapsed;
            ReorderPanel.Visibility = Visibility.Collapsed;
            DefaultSettingsPanel.Visibility = Visibility.Collapsed;
            SaveButtonRow.Visibility = Visibility.Collapsed;
            HelperErrorPanel.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = Visibility.Collapsed;
            _helperWarningDismissed = true;
            AddSection.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            if (!opening) { OptionalFieldsPanel.Visibility = Visibility.Collapsed; OptionalChevron.Glyph = "\uE76C"; }
            SetOverlay(opening);
            SetButtonActive(ToggleAddButton, opening);
            SetButtonActive(ReorderCategoriesButton, false);
            SetButtonActive(DefaultSettingsButton, false);
            SetButtonActive(ToggleInfoButton, false);
            SetWarningButtonActive(false);
        }

        // ── INFO PANEL ────────────────────────────────────────────────
        // Opens from the ··· menu on a process row; _infoEditTarget holds
        // the entry being edited so Save/URL handlers know which one to act on.

        public void OpenInfoPanel(ProcessEntry entry)
        {
            _infoEditTarget = entry;

            // Close every other panel first
            AddSection.Visibility = Visibility.Collapsed;
            DefaultSettingsPanel.Visibility = Visibility.Collapsed;
            SaveButtonRow.Visibility = Visibility.Collapsed;
            ReorderPanel.Visibility = Visibility.Collapsed;
            HelperErrorPanel.Visibility = Visibility.Collapsed;
            _helperWarningDismissed = true;

            // Populate fields
            InfoDescriptionBox.Text = entry.Description ?? "";
            InfoUrlBox.Text = entry.InfoUrl ?? "";
            InfoUpdateUrlBox.Text = entry.UpdateUrl ?? "";

            // Update action-button enabled state
            InfoOpenWebsiteButton.IsEnabled = !string.IsNullOrWhiteSpace(entry.InfoUrl);
            InfoCheckUpdatesButton.IsEnabled = !string.IsNullOrWhiteSpace(entry.UpdateUrl);

            InfoPanel.Visibility = Visibility.Visible;
            SetOverlay(true);
            SetButtonActive(ToggleInfoButton, true);
            SetButtonActive(ReorderCategoriesButton, false);
            SetButtonActive(DefaultSettingsButton, false);
            SetButtonActive(ToggleAddButton, false);
            SetWarningButtonActive(false);
        }

        private void ToggleInfo_Click(object sender, RoutedEventArgs e)
        {
            bool opening = InfoPanel.Visibility == Visibility.Collapsed;
            if (opening)
            {
                // If no entry is currently targeted, pick the first process (or do nothing)
                if (_infoEditTarget == null)
                    _infoEditTarget = _processes.FirstOrDefault();
                if (_infoEditTarget == null) return;
                OpenInfoPanel(_infoEditTarget);
            }
            else
            {
                InfoPanel.Visibility = Visibility.Collapsed;
                SetOverlay(false);
                SetButtonActive(ToggleInfoButton, false);
            }
        }

        private void InfoSave_Click(object sender, RoutedEventArgs e)
        {
            if (_infoEditTarget == null) return;
            _infoEditTarget.Description = InfoDescriptionBox.Text.Trim();
            _infoEditTarget.InfoUrl = InfoUrlBox.Text.Trim();
            _infoEditTarget.UpdateUrl = InfoUpdateUrlBox.Text.Trim();

            // Refresh button states in case the user just typed a URL
            InfoOpenWebsiteButton.IsEnabled = !string.IsNullOrWhiteSpace(_infoEditTarget.InfoUrl);
            InfoCheckUpdatesButton.IsEnabled = !string.IsNullOrWhiteSpace(_infoEditTarget.UpdateUrl);

            SaveData();

            InfoPanel.Visibility = Visibility.Collapsed;
            SetOverlay(false);
            SetButtonActive(ToggleInfoButton, false);
        }

        private async void InfoOpenWebsite_Click(object sender, RoutedEventArgs e)
        {
            // Save any edits first so the user doesn't lose unsaved URL changes
            if (_infoEditTarget != null)
            {
                _infoEditTarget.InfoUrl = InfoUrlBox.Text.Trim();
                SaveData();
            }
            var url = InfoUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                // Process.Start is blocked inside the AppX sandbox; use Launcher instead.
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch { }
        }

        private async void InfoCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_infoEditTarget != null)
            {
                _infoEditTarget.UpdateUrl = InfoUpdateUrlBox.Text.Trim();
                SaveData();
            }
            var url = InfoUpdateUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                // Process.Start is blocked inside the AppX sandbox; use Launcher instead.
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch { }
        }

        private void ExePathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            VisualStateManager.GoToState(ExePathBox, "Valid", true);
            var path = ExePathBox.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(AppNameBox.Text.Trim()) && !string.IsNullOrEmpty(path))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(name))
                    AppNameBox.Text = name;
            }
        }

        private void AppNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            VisualStateManager.GoToState(AppNameBox, "Valid", true);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var name = AppNameBox.Text.Trim();
            var path = ExePathBox.Text.Trim().Trim('"');

            bool valid = true;
            if (string.IsNullOrEmpty(path)) { VisualStateManager.GoToState(ExePathBox, "Error", true); valid = false; }
            if (string.IsNullOrEmpty(name)) { VisualStateManager.GoToState(AppNameBox, "Error", true); valid = false; }
            if (!valid) return;

            var catName = GetSelectedCategory();
            var closeExe = CloseExePathBox.Text.Trim().Trim('"');
            var focusExe = FocusExePathBox.Text.Trim().Trim('"');

            // Resolve the effective defaults for the chosen category (§7)
            var newEntryDefaults = (_perCatDefaultsEnabled && _catDefaults.TryGetValue(catName, out var catTriple))
                ? catTriple
                : (_defaultPlayBehaviour, _defaultCloseBehaviour, _defaultFocusMode);

            var entry = new ProcessEntry
            {
                ExePath = path,
                Arguments = ArgsBox.Text.Trim(),
                Icon = "🎮",
                CustomName = name,
                Category = catName,
                PlayBehaviour = newEntryDefaults.Item1,
                CloseBehaviour = newEntryDefaults.Item2,
                FocusMode = newEntryDefaults.Item3,
                CloseExePath = string.IsNullOrEmpty(closeExe) ? null : closeExe,
                FocusExePath = string.IsNullOrEmpty(focusExe) ? null : focusExe
            };

            _processes.Add(entry);
            RebuildCategoryView();
            AppNameBox.Text = ExePathBox.Text = ArgsBox.Text = CloseExePathBox.Text = FocusExePathBox.Text = "";
            ArgsCombo.SelectedIndex = -1;
            CategoryCombo.SelectedIndex = -1;
            VisualStateManager.GoToState(ArgsCombo, "NoContent", false);
            VisualStateManager.GoToState(CategoryCombo, "NoContent", false);
            SaveData();
            AddSection.Visibility = Visibility.Collapsed;
            SetOverlay(false);
            SetButtonActive(ToggleAddButton, false);

            // Request icon extraction from Launcher helper
            _ = RequestIconExtractionAsync(entry);
        }

        // ── LAUNCHER HELPER DETECTION ─────────────────────────────────
        private async Task<string> GetHelperPathAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync("launcher_path.txt") as StorageFile;
                if (item == null) return null;
                return (await FileIO.ReadTextAsync(item)).Trim();
            }
            catch { return null; }
        }

        private async Task TryLaunchHelperAsync()
        {
            try
            {
                var helperPath = await GetHelperPathAsync();
                if (string.IsNullOrEmpty(helperPath) || !File.Exists(helperPath))
                {
                    Log("TryLaunchHelper: path not found — " + (helperPath ?? "null"));
                    return;
                }
                // UWP/AppX sandbox does not allow Process.Start with UseShellExecute.
                // Use StorageFile + Windows.System.Launcher instead.
                var file = await StorageFile.GetFileFromPathAsync(helperPath);
                await Windows.System.Launcher.LaunchFileAsync(file);
                Log("TryLaunchHelper: launched — " + helperPath);
            }
            catch (Exception ex) { Log($"TryLaunchHelper ERROR: {ex.Message}"); }
        }

        private void StartHeartbeatPolling()
        {
            _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _heartbeatTimer.Tick += async (s, e) => await CheckHeartbeatAsync();
            _heartbeatTimer.Start();
        }

        private async Task CheckHeartbeatAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync("launcher_heartbeat.txt") as StorageFile;
                if (item == null) { if (!_helperWarningDismissed) OnHelperNotDetected(); return; }
                var text = await FileIO.ReadTextAsync(item);
                if (DateTime.TryParse(text.Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts)
                    && (DateTime.UtcNow - ts).TotalSeconds < 6)
                {
                    _helperWarningDismissed = false;
                    _helperShownOnce = false;
                    LauncherRefreshButton.Visibility = Visibility.Collapsed;
                    HelperErrorPanel.Visibility = Visibility.Collapsed;
                    if (!IsAnyPanelOpen()) SetOverlay(false);
                    SetWarningButtonActive(false);
                }
                else
                {
                    if (!_helperWarningDismissed) OnHelperNotDetected();
                }
            }
            catch { if (!_helperWarningDismissed) OnHelperNotDetected(); }
        }

        private async void OnHelperNotDetected()
        {
            // Always make the button visible so user can manually open the panel
            LauncherRefreshButton.Visibility = Visibility.Visible;

            // Update path hint text regardless
            var path = await GetHelperPathAsync();
            HelperPathHint.Text = !string.IsNullOrEmpty(path)
                ? $"Run manually: {path}"
                : "Run manually: Launcher.exe — check its install folder";

            // Auto-open panel only on the first detection, never again until service recovers
            if (_helperShownOnce || _helperWarningDismissed) return;
            _helperShownOnce = true;

            AddSection.Visibility = Visibility.Collapsed;
            DefaultSettingsPanel.Visibility = Visibility.Collapsed;
            ReorderPanel.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = Visibility.Collapsed;
            SaveButtonRow.Visibility = Visibility.Collapsed;
            SetButtonActive(ToggleAddButton, false);
            SetButtonActive(DefaultSettingsButton, false);
            SetButtonActive(ReorderCategoriesButton, false);
            SetButtonActive(ToggleInfoButton, false);

            HelperErrorPanel.Visibility = Visibility.Visible;
            SetOverlay(true);
            SetWarningButtonActive(true);
        }

        private async void OpenTaskManager_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Process.Start is blocked inside the AppX sandbox; use Launcher instead.
                await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:taskmgr"));
            }
            catch { }
        }


        private void LauncherRefresh_Click(object sender, RoutedEventArgs e)
        {
            bool isOpen = HelperErrorPanel.Visibility == Visibility.Visible;
            if (isOpen)
            {
                _helperWarningDismissed = true;
                HelperErrorPanel.Visibility = Visibility.Collapsed;
                AddSection.Visibility = Visibility.Collapsed;
                DefaultSettingsPanel.Visibility = Visibility.Collapsed;
                SaveButtonRow.Visibility = Visibility.Collapsed;
                ReorderPanel.Visibility = Visibility.Collapsed;
                InfoPanel.Visibility = Visibility.Collapsed;
                SetOverlay(false);
                SetWarningButtonActive(false);
                SetButtonActive(ToggleAddButton, false);
                SetButtonActive(DefaultSettingsButton, false);
                SetButtonActive(ReorderCategoriesButton, false);
                SetButtonActive(ToggleInfoButton, false);
            }
            else
            {
                _helperWarningDismissed = false;
                AddSection.Visibility = Visibility.Collapsed;
                DefaultSettingsPanel.Visibility = Visibility.Collapsed;
                SaveButtonRow.Visibility = Visibility.Collapsed;
                ReorderPanel.Visibility = Visibility.Collapsed;
                InfoPanel.Visibility = Visibility.Collapsed;
                HelperErrorPanel.Visibility = Visibility.Visible;
                SetOverlay(true);
                SetWarningButtonActive(true);
                SetButtonActive(ToggleAddButton, false);
                SetButtonActive(DefaultSettingsButton, false);
                SetButtonActive(ReorderCategoriesButton, false);
                SetButtonActive(ToggleInfoButton, false);
            }
        }

        private async void HelperErrorRelaunch_Click(object sender, RoutedEventArgs e)
        {
            await TryLaunchHelperAsync();
        }

        private async Task WriteCmdFile(string cmd, string path, string args = "")
        {
            var folder = ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync("launcher_cmd.txt", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteLinesAsync(file, new[] { cmd, path, args });
        }

        // ── ICON EXTRACTION ───────────────────────────────────────────
        // Sends an extract_icon command to Launcher.exe (which runs as a normal
        // Win32 process and can call Icon.ExtractAssociatedIcon freely).
        // Launcher saves the PNG to LocalFolder and writes a response file.
        // We poll for it for up to 8 seconds then give up gracefully.
        private async Task RequestIconExtractionAsync(ProcessEntry entry)
        {
            if (string.IsNullOrEmpty(entry.ExePath)) return;

            // Skip if this entry already has an image icon
            if (!string.IsNullOrEmpty(entry.IconImagePath) && File.Exists(entry.IconImagePath)) return;

            try
            {
                var id = Guid.NewGuid().ToString("N");
                var responseFileName = $"icon_response_{id}.txt";

                // Write command: extract_icon | exePath | responseFileName
                var folder = ApplicationData.Current.LocalFolder;
                var cmdFile = await folder.CreateFileAsync("launcher_cmd.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteLinesAsync(cmdFile, new[] { "extract_icon", entry.ExePath, responseFileName });

                Log($"RequestIconExtraction: sent for '{entry.DisplayName}', response={responseFileName}");

                // Poll up to 8 s (16 × 500 ms)
                for (int i = 0; i < 16; i++)
                {
                    await Task.Delay(500);
                    var responseItem = await folder.TryGetItemAsync(responseFileName) as StorageFile;
                    if (responseItem == null) continue;

                    var iconPath = (await FileIO.ReadTextAsync(responseItem)).Trim();
                    if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
                    {
                        Log($"RequestIconExtraction: response empty or file missing for '{entry.DisplayName}'");
                        break;
                    }

                    entry.IconImagePath = iconPath;
                    SaveData();

                    // Clean up the response file
                    try { await responseItem.DeleteAsync(); } catch { }

                    Log($"RequestIconExtraction: success for '{entry.DisplayName}' → {iconPath}");
                    break;
                }
            }
            catch (Exception ex) { Log($"RequestIconExtraction ERROR: {ex.Message}"); }
        }

        // On startup, request icon extraction for any existing entries that don't have one yet.
        // We stagger requests so the helper isn't flooded.
        private async Task ExtractMissingIconsAsync()
        {
            // Give the helper a moment to start up before we send commands
            await Task.Delay(3000);
            foreach (var entry in _processes.ToList())
            {
                if (!string.IsNullOrEmpty(entry.IconImagePath) && File.Exists(entry.IconImagePath))
                    continue;
                await RequestIconExtractionAsync(entry);
                await Task.Delay(300); // small gap between requests
            }
        }

        private async void Launch_Click(object sender, RoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;

            // Task 6: block if already launched
            if (entry.IsLaunched) return;

            Log($"Launch_Click: '{entry.DisplayName}' | PlayBehaviour={entry.PlayBehaviour}");
            _lastLaunchTime = DateTime.Now;

            // Pin to Launched category before launching
            if (_launchPinEnabled && !entry.IsLaunched)
            {
                entry.OriginalCategory = entry.Category;
                entry.IsLaunched = true;
                entry.Category = "Launched";
                SaveData();

                // Ensure Launched group exists and move entry into it
                var launchedGroup = _categories.FirstOrDefault(c => c.Name == "Launched");
                if (launchedGroup == null)
                {
                    launchedGroup = new CategoryGroup { Name = "Launched", AccentColor = _launchedAccentColor };
                    _categories.Insert(0, launchedGroup);
                }
                else if (_categories.IndexOf(launchedGroup) != 0)
                    _categories.Move(_categories.IndexOf(launchedGroup), 0);

                foreach (var cat in _categories)
                    cat.Entries.Remove(entry);
                launchedGroup.Entries.Insert(0, entry);
                launchedGroup.RefreshFirstItemFlags();
            }

            try
            {
                await WriteCmdFile("launch", entry.ExePath, entry.Arguments ?? "");
                Log("Launch_Click: cmd file written, helper will pick it up");
            }
            catch (Exception ex) { Log($"Launch_Click ERROR: {ex.Message}"); }

            if (entry.PlayBehaviour == PlayBehaviour.CloseWidget)
            {
                Log("Launch_Click: closing widget (CloseWidget behaviour)");
                Application.Current.Exit();
            }
            else if (entry.PlayBehaviour == PlayBehaviour.FocusApp)
            {
                Log("Launch_Click: focusing app (FocusApp behaviour)");
                await Task.Delay(1500);
                entry.BringToFocus();
            }
            else
            {
                // App launches minimized — focus never leaves Game Bar, nothing to do
                Log("Launch_Click: launched minimized, staying on widget (RemainOnWidget behaviour)");
            }
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;

            Log($"Stop_Click: '{entry.DisplayName}' | CloseBehaviour={entry.CloseBehaviour}");

            // Unpin from Launched category before stopping
            if (entry.IsLaunched)
            {
                entry.IsLaunched = false;
                entry.Category = entry.OriginalCategory ?? "Apps";
                entry.OriginalCategory = null;
                SaveData();

                // Move entry back to its original category group
                var launchedGroup = _categories.FirstOrDefault(c => c.Name == "Launched");
                launchedGroup?.Entries.Remove(entry);

                var origGroup = _categories.FirstOrDefault(c => c.Name == entry.Category);
                if (origGroup != null)
                {
                    origGroup.Entries.Add(entry);
                    origGroup.RefreshFirstItemFlags();
                }

                if (launchedGroup != null && launchedGroup.Entries.Count == 0)
                    _categories.Remove(launchedGroup);
                else
                    launchedGroup?.RefreshFirstItemFlags();
            }

            try
            {
                var killTarget = !string.IsNullOrEmpty(entry.CloseExePath)
                    ? Path.GetFileNameWithoutExtension(entry.CloseExePath)
                    : Path.GetFileNameWithoutExtension(entry.ExePath);
                await WriteCmdFile("kill", killTarget);
                Log("Stop_Click: cmd file written, helper will pick it up");
            }
            catch (Exception ex) { Log($"Stop_Click ERROR: {ex.Message}"); }

            if (entry.CloseBehaviour == CloseBehaviour.CloseWidget)
            {
                Log("Stop_Click: closing widget (CloseWidget behaviour)");
                Application.Current.Exit();
            }
            else
            {
                // RemainOnWidget: re-anchor focus back to the widget
                Log("Stop_Click: re-anchoring widget (RemainOnWidget behaviour)");
                await RetainWidgetFocusAsync();
            }
        }

        private async void Focus_Click(object sender, RoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;
            try
            {
                var focusTarget = !string.IsNullOrEmpty(entry.FocusExePath)
                    ? Path.GetFileNameWithoutExtension(entry.FocusExePath)
                    : Path.GetFileNameWithoutExtension(entry.ExePath);
                await WriteCmdFile("focus", focusTarget);
            }
            catch (Exception ex) { Log($"Focus_Click ERROR: {ex.Message}"); }
        }

        private async void FocusFullscreen_Click(object sender, RoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;
            try
            {
                var name = Path.GetFileNameWithoutExtension(entry.ExePath);
                var mode = entry.FocusMode.ToString();
                var key = entry.FullscreenKey ?? "F11";
                await WriteCmdFile("fullscreen", name, $"{mode}|{key}");
            }
            catch (Exception ex) { Log($"FocusFullscreen_Click ERROR: {ex.Message}"); }
        }



        private async Task ShowPlaySettingsDialog(ProcessEntry entry)
        {
            var eff = EffectiveDefaults(entry);
            var dim = ThemeFg(160);
            var combo = new ComboBox { HorizontalAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch, FontSize = 12 };
            combo.Items.Add("Stay on Game Bar" + (eff.Play == PlayBehaviour.RemainOnWidget ? " 🌐" : ""));
            combo.Items.Add("Close Game Bar" + (eff.Play == PlayBehaviour.CloseWidget ? " 🌐" : ""));
            combo.Items.Add("Focus launched app" + (eff.Play == PlayBehaviour.FocusApp ? " 🌐" : ""));
            combo.SelectedIndex = (int)entry.PlayBehaviour;

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Overrides the global default (🌐) for this app only.", FontSize = 11, Foreground = dim, TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap });
            panel.Children.Add(combo);

            var dialog = new ContentDialog
            {
                Title = "Play settings",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel"
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                entry.PlayBehaviour = (PlayBehaviour)combo.SelectedIndex;
                SaveData();
            }
        }

        private async Task ShowCloseSettingsDialog(ProcessEntry entry)
        {
            var eff = EffectiveDefaults(entry);
            var dim = ThemeFg(160);
            var combo = new ComboBox { HorizontalAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch, FontSize = 12 };
            combo.Items.Add("Stay on Game Bar" + (eff.Close == CloseBehaviour.RemainOnWidget ? " 🌐" : ""));
            combo.Items.Add("Close Game Bar" + (eff.Close == CloseBehaviour.CloseWidget ? " 🌐" : ""));
            combo.SelectedIndex = (int)entry.CloseBehaviour;

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Overrides the global default (🌐) for this app only.", FontSize = 11, Foreground = dim, TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap });
            panel.Children.Add(combo);

            var dialog = new ContentDialog
            {
                Title = "Close settings",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel"
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                entry.CloseBehaviour = (CloseBehaviour)combo.SelectedIndex;
                SaveData();
            }
        }

        private async Task ShowFullscreenSettingsDialog(ProcessEntry entry)
        {
            var eff = EffectiveDefaults(entry);
            var modeCombo = new ComboBox
            {
                HorizontalAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
            modeCombo.Items.Add("Maximize window" + (eff.Fs == FocusFullscreenMode.Maximize ? " 🌐" : ""));
            modeCombo.Items.Add("Key only" + (eff.Fs == FocusFullscreenMode.FullscreenOnly ? " 🌐" : ""));
            modeCombo.Items.Add("Focus → Key" + (eff.Fs == FocusFullscreenMode.FocusThenFullscreen ? " 🌐" : ""));
            modeCombo.SelectedIndex = (int)entry.FocusMode;

            var keyBox = new TextBox
            {
                Text = entry.FullscreenKey,
                PlaceholderText = "e.g. F11, Alt+Enter, Ctrl+Shift+F",
                FontSize = 12,
                Padding = new Thickness(8, 6, 8, 6),
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 44, 44, 54)),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 220, 220)),
                BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255))
            };

            var dim = ThemeFg(180);
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = "Overrides the global default (🌐) for this app only. Key-based modes send a keystroke to the app — use Maximize for apps that don't have a fullscreen key.", FontSize = 11, Foreground = dim, TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "Button action", FontSize = 11, Foreground = dim });
            panel.Children.Add(modeCombo);
            panel.Children.Add(new TextBlock { Text = "Key (used in Key-based modes)", FontSize = 11, Foreground = dim });
            panel.Children.Add(keyBox);

            var dialog = new ContentDialog
            {
                Title = "Fullscreen settings",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel"
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                entry.FocusMode = (FocusFullscreenMode)modeCombo.SelectedIndex;
                var k = keyBox.Text.Trim().ToUpper();
                entry.FullscreenKey = string.IsNullOrEmpty(k) ? "F11" : k;
                SaveData();
            }
        }

        // ── 3-DOTS MENU ──────────────────────────────────────────────
        private void MoreOptions_Click(object sender, RoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;
            var eff = EffectiveDefaults(entry);

            var menu = new MenuFlyout();
            menu.MenuFlyoutPresenterStyle = (Style)Resources["RoundedMenuFlyoutPresenter"];

            var moveUp = new MenuFlyoutItem { Text = "Move up", Icon = new FontIcon { Glyph = "\uE96D" } };
            moveUp.Click += (s, _) => MoveUp_Click(sender, e);

            var rename = new MenuFlyoutItem { Text = "Rename", Icon = new FontIcon { Glyph = "\uE8AC" } };
            rename.Click += async (s, _) => await ShowRenameDialog(entry);

            var moveToCategory = new MenuFlyoutSubItem { Text = "Move to category", Icon = new FontIcon { Glyph = "\uE8EC", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") } };

            // New Category at top
            var newCatItem = new MenuFlyoutItem { Text = "New Category", Icon = new FontIcon { Glyph = "\uE710", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") } };
            newCatItem.Click += async (s, _) => await CreateNewCategoryFromCombo();
            moveToCategory.Items.Add(newCatItem);
            moveToCategory.Items.Add(new MenuFlyoutSeparator());

            // Launched first with green dot — hidden if entry is already in Launched
            if (!entry.IsLaunched)
            {
                var launchedCat = _categories.FirstOrDefault(c => c.Name == "Launched");
                if (launchedCat != null)
                {
                    var launchedItem = new MenuFlyoutItem { Text = "Launched", Icon = new FontIcon { Glyph = "\uE768", FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") } };
                    launchedItem.Click += (s, _) => { entry.Category = "Launched"; RebuildCategoryView(); SaveData(); };
                    moveToCategory.Items.Add(launchedItem);
                    moveToCategory.Items.Add(new MenuFlyoutSeparator());
                }
            }

            // Other categories — use FontIcon on Icon property so Segoe glyphs render correctly
            foreach (var cat in _categories.Where(c => c.Name != "Launched"))
            {
                var catItem = new MenuFlyoutItem { Text = cat.Name };
                catItem.Icon = new FontIcon
                {
                    Glyph = !string.IsNullOrEmpty(cat.Icon) ? cat.Icon : "\uE8EC",
                    FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    Opacity = !string.IsNullOrEmpty(cat.Icon) ? 1.0 : 0.5
                };
                var catCapture = cat;
                catItem.Click += (s, _) =>
                {
                    if (entry.IsLaunched)
                    {
                        // Store target, move on Stop
                        entry.OriginalCategory = catCapture.Name;
                        SaveData();
                    }
                    else
                    {
                        entry.Category = catCapture.Name;
                        RebuildCategoryView();
                        SaveData();
                    }
                };
                moveToCategory.Items.Add(catItem);
            }

            var delete = new MenuFlyoutItem { Text = "Remove app", Icon = new FontIcon { Glyph = "\uE74D" } };
            delete.Click += (s, _) => { _processes.Remove(entry); RebuildCategoryView(); SaveData(); };

            string playLabel;
            if (entry.PlayBehaviour == PlayBehaviour.CloseWidget) playLabel = "Close Game Bar";
            else if (entry.PlayBehaviour == PlayBehaviour.FocusApp) playLabel = "Focus app";
            else playLabel = "Stay on Game Bar";
            if (entry.PlayBehaviour == eff.Play) playLabel += " 🌐";

            var closeLabel = entry.CloseBehaviour == CloseBehaviour.CloseWidget ? "Close Game Bar" : "Stay on Game Bar";
            if (entry.CloseBehaviour == eff.Close) closeLabel += " 🌐";

            string fsLabel;
            if (entry.FocusMode == FocusFullscreenMode.Maximize) fsLabel = "Maximize";
            else if (entry.FocusMode == FocusFullscreenMode.FullscreenOnly) fsLabel = entry.FullscreenKey + " only";
            else fsLabel = "Focus → " + entry.FullscreenKey;
            if (entry.FocusMode == eff.Fs) fsLabel += " 🌐";

            // ── Play behavior submenu ─────────────────────────────────
            var playMenu = new MenuFlyoutSubItem { Text = $"Play behavior : {playLabel}", Icon = new FontIcon { Glyph = "\uF5B0" } };
            var playOptions = new[]
            {
                ("Stay on Game Bar", PlayBehaviour.RemainOnWidget),
                ("Close Game Bar",     PlayBehaviour.CloseWidget),
                ("Focus app",          PlayBehaviour.FocusApp),
            };
            foreach (var (label, val) in playOptions)
            {
                var isDefault = val == eff.Play;
                var item = new MenuFlyoutItem { Text = label + (isDefault ? " 🌐" : "") };
                if (entry.PlayBehaviour == val) item.Icon = new FontIcon { Glyph = "\uE73E" }; // checkmark
                var capture = val;
                item.Click += (s, _) => { entry.PlayBehaviour = capture; entry.HasPlayOverride = capture != EffectiveDefaults(entry).Play; entry.IsManualOverride = entry.HasPlayOverride || entry.HasCloseOverride || entry.HasFsOverride; SaveData(); };
                playMenu.Items.Add(item);
            }

            // ── Close behavior submenu ────────────────────────────────
            var closeMenu = new MenuFlyoutSubItem { Text = $"Close behavior : {closeLabel}", Icon = new FontIcon { Glyph = "\uE711" } };
            var closeOptions = new[]
            {
                ("Stay on Game Bar", CloseBehaviour.RemainOnWidget),
                ("Close Game Bar",     CloseBehaviour.CloseWidget),
            };
            foreach (var (label, val) in closeOptions)
            {
                var isDefault = val == eff.Close;
                var item = new MenuFlyoutItem { Text = label + (isDefault ? " 🌐" : "") };
                if (entry.CloseBehaviour == val) item.Icon = new FontIcon { Glyph = "\uE73E" };
                var capture = val;
                item.Click += (s, _) => { entry.CloseBehaviour = capture; entry.HasCloseOverride = capture != EffectiveDefaults(entry).Close; entry.IsManualOverride = entry.HasPlayOverride || entry.HasCloseOverride || entry.HasFsOverride; SaveData(); };
                closeMenu.Items.Add(item);
            }

            // ── Fullscreen behavior submenu ───────────────────────────
            var fsMenu = new MenuFlyoutSubItem { Text = $"Fullscreen behavior : {fsLabel}", Icon = new FontIcon { Glyph = "\uE740" } };
            var fsOptions = new[]
            {
                ("Maximize window", FocusFullscreenMode.Maximize),
                ("Key only",        FocusFullscreenMode.FullscreenOnly),
                ("Focus → Key",     FocusFullscreenMode.FocusThenFullscreen),
            };
            foreach (var (label, val) in fsOptions)
            {
                var isDefault = val == eff.Fs;
                var item = new MenuFlyoutItem { Text = label + (isDefault ? " 🌐" : "") };
                if (entry.FocusMode == val) item.Icon = new FontIcon { Glyph = "\uE73E" };
                var capture = val;
                item.Click += (s, _) => { entry.FocusMode = capture; entry.HasFsOverride = capture != EffectiveDefaults(entry).Fs; entry.IsManualOverride = entry.HasPlayOverride || entry.HasCloseOverride || entry.HasFsOverride; SaveData(); };
                fsMenu.Items.Add(item);
            }

            menu.Items.Add(moveUp);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(rename);
            menu.Items.Add(moveToCategory);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(playMenu);
            menu.Items.Add(closeMenu);
            menu.Items.Add(fsMenu);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(delete);
            menu.ShowAt(sender as FrameworkElement);
        }

        // ── PER-BUTTON RIGHT-CLICK MENUS ─────────────────────────────
        private void Play_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;
            var eff = EffectiveDefaults(entry);

            var menu = new MenuFlyout();
            menu.MenuFlyoutPresenterStyle = (Style)Resources["RoundedMenuFlyoutPresenter"];

            var playOptions = new[]
            {
                ("Stay on Game Bar", PlayBehaviour.RemainOnWidget),
                ("Close Game Bar",   PlayBehaviour.CloseWidget),
                ("Focus app",        PlayBehaviour.FocusApp),
            };
            foreach (var (label, val) in playOptions)
            {
                var isDefault = val == eff.Play;
                var item = new MenuFlyoutItem { Text = label + (isDefault ? " 🌐" : "") };
                if (entry.PlayBehaviour == val) item.Icon = new FontIcon { Glyph = "\uE73E" };
                var capture = val;
                item.Click += (s, _) =>
                {
                    entry.PlayBehaviour = capture;
                    entry.HasPlayOverride = capture != EffectiveDefaults(entry).Play;
                    entry.IsManualOverride = entry.HasPlayOverride || entry.HasCloseOverride || entry.HasFsOverride;
                    SaveData();
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(sender as FrameworkElement);
        }

        private void Stop_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;
            var eff = EffectiveDefaults(entry);

            var menu = new MenuFlyout();
            menu.MenuFlyoutPresenterStyle = (Style)Resources["RoundedMenuFlyoutPresenter"];

            var closeOptions = new[]
            {
                ("Stay on Game Bar", CloseBehaviour.RemainOnWidget),
                ("Close Game Bar",   CloseBehaviour.CloseWidget),
            };
            foreach (var (label, val) in closeOptions)
            {
                var isDefault = val == eff.Close;
                var item = new MenuFlyoutItem { Text = label + (isDefault ? " 🌐" : "") };
                if (entry.CloseBehaviour == val) item.Icon = new FontIcon { Glyph = "\uE73E" };
                var capture = val;
                item.Click += (s, _) =>
                {
                    entry.CloseBehaviour = capture;
                    entry.HasCloseOverride = capture != EffectiveDefaults(entry).Close;
                    entry.IsManualOverride = entry.HasPlayOverride || entry.HasCloseOverride || entry.HasFsOverride;
                    SaveData();
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(sender as FrameworkElement);
        }

        private void Fs_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;
            var eff = EffectiveDefaults(entry);

            var menu = new MenuFlyout();
            menu.MenuFlyoutPresenterStyle = (Style)Resources["RoundedMenuFlyoutPresenter"];

            var fsOptions = new[]
            {
                ("Maximize window", FocusFullscreenMode.Maximize),
                ("Key only",        FocusFullscreenMode.FullscreenOnly),
                ("Focus → Key",     FocusFullscreenMode.FocusThenFullscreen),
            };
            foreach (var (label, val) in fsOptions)
            {
                var isDefault = val == eff.Fs;
                var item = new MenuFlyoutItem { Text = label + (isDefault ? " 🌐" : "") };
                if (entry.FocusMode == val) item.Icon = new FontIcon { Glyph = "\uE73E" };
                var capture = val;
                item.Click += (s, _) =>
                {
                    entry.FocusMode = capture;
                    entry.HasFsOverride = capture != EffectiveDefaults(entry).Fs;
                    entry.IsManualOverride = entry.HasPlayOverride || entry.HasCloseOverride || entry.HasFsOverride;
                    SaveData();
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(sender as FrameworkElement);
        }

        private async Task ShowRenameDialog(ProcessEntry entry)
        {
            var input = new TextBox
            {
                Text = entry.CustomName,
                PlaceholderText = "New name",
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 44, 44, 54)),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 220, 220)),
                BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255))
            };
            var dialog = new ContentDialog
            {
                Title = "Rename",
                Content = input,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel"
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            {
                entry.CustomName = input.Text.Trim();
                SaveData();
            }
        }

        private void ArgsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            VisualStateManager.GoToState(ArgsCombo,
                ArgsCombo.SelectedIndex >= 0 ? "HasContent" : "NoContent", false);

            if (ArgsCombo.SelectedItem is ComboBoxItem item)
            {
                var tag = item.Tag?.ToString();
                if (tag == "__custom__")
                {
                    ArgsBox.Focus(FocusState.Programmatic);
                }
                else if (tag != null)
                {
                    // Append the preset to whatever the user has already typed
                    var existing = ArgsBox.Text?.Trim() ?? "";
                    ArgsBox.Text = string.IsNullOrEmpty(existing) ? tag : existing + " " + tag;
                    // Reset combo so the same item can be picked again to append a second time
                    ArgsCombo.SelectedIndex = -1;
                    VisualStateManager.GoToState(ArgsCombo, "NoContent", false);
                }
            }
        }

        private void ToggleOptional_Click(object sender, RoutedEventArgs e)
        {
            var visible = OptionalFieldsPanel.Visibility == Visibility.Visible;
            OptionalFieldsPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            OptionalChevron.Glyph = visible ? "\uE76C" : "\uE70D"; // right / down chevron
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            var entry = GetEntry(sender);
            if (entry == null) return;
            var idx = _processes.IndexOf(entry);
            if (idx <= 0) _processes.Move(idx, _processes.Count - 1);
            else _processes.Move(idx, idx - 1);
            RebuildCategoryView();
            SaveData();
        }

        // ── CATEGORY VIEW ─────────────────────────────────────────────
        private void RebuildCategoryView()
        {
            // Preserve existing category objects to avoid UI flicker, just update entries
            var existingNames = _categories.Select(c => c.Name).ToList();

            // Collect all category names from processes, preserving existing order
            var allCatNames = existingNames.ToList();
            foreach (var p in _processes)
            {
                var cat = p.Category ?? "Apps";
                if (!allCatNames.Contains(cat))
                    allCatNames.Add(cat);
            }

            // Ensure defaults exist
            if (!allCatNames.Contains("Apps")) allCatNames.Insert(0, "Apps");
            if (!allCatNames.Contains("Games") && allCatNames.Count >= 1) allCatNames.Insert(1, "Games");

            // Launched always first if present
            if (allCatNames.Contains("Launched"))
            {
                allCatNames.Remove("Launched");
                allCatNames.Insert(0, "Launched");
            }

            // Remove categories no longer needed (empty and not default)
            var toRemove = _categories
                .Where(c => c.Name != "Apps" && c.Name != "Games" && !_processes.Any(p => (p.Category ?? "Apps") == c.Name))
                .ToList();
            foreach (var r in toRemove) _categories.Remove(r);

            // Add missing categories
            foreach (var name in allCatNames)
            {
                if (!_categories.Any(c => c.Name == name))
                    _categories.Add(new CategoryGroup { Name = name });
            }

            // Reorder _categories to match allCatNames
            for (int i = 0; i < allCatNames.Count; i++)
            {
                var cur = _categories.FirstOrDefault(c => c.Name == allCatNames[i]);
                if (cur == null) continue;
                var curIdx = _categories.IndexOf(cur);
                if (curIdx != i && i < _categories.Count)
                    _categories.Move(curIdx, i);
            }

            // Rebuild entries per category
            foreach (var cat in _categories)
            {
                cat.Entries.Clear();
                foreach (var p in _processes.Where(x => (x.Category ?? "Apps") == cat.Name))
                    cat.Entries.Add(p);
                cat.RefreshFirstItemFlags();

                // Keep CategoryGroup model in sync with persisted per-category defaults (§10)
                if (_catDefaults.TryGetValue(cat.Name, out var triple))
                {
                    cat.CatDefaultPlay      = triple.Play;
                    cat.CatDefaultClose     = triple.Close;
                    cat.CatDefaultFocusMode = triple.Fs;
                }
            }
        }

        private const string ProcKey = "appdeck_v3";

        private void SaveData()
        {
            try
            {
                var str = string.Join("||", _processes.Select(p =>
                    $"{p.ExePath}~~{p.Arguments}~~{p.Icon}~~{p.IconImagePath}~~{p.IconColor}~~{p.CustomName}~~{(p.IsLaunched ? p.OriginalCategory ?? "Apps" : p.Category)}~~{(int)p.FocusMode}~~{p.FullscreenKey}~~{(int)p.PlayBehaviour}~~{(int)p.CloseBehaviour}~~{(p.IsLaunched ? "1" : "0")}~~{(p.IsManualOverride ? "1" : "0")}~~{p.CloseExePath ?? ""}~~{p.FocusExePath ?? ""}~~{p.Description ?? ""}~~{p.InfoUrl ?? ""}~~{p.UpdateUrl ?? ""}"));
                ApplicationData.Current.LocalSettings.Values[ProcKey] = str;
            }
            catch { }
        }

        private void LoadData()
        {
            try
            {
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(ProcKey, out var raw) || raw == null) return;
                foreach (var part in raw.ToString().Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var bits = part.Split(new[] { "~~" }, StringSplitOptions.None);
                    if (bits.Length >= 1 && !string.IsNullOrEmpty(bits[0]))
                    {
                        var wasLaunched = bits.Length > 11 && bits[11] == "1";
                        var savedCat = bits.Length > 6 ? bits[6] : "Apps";
                        _processes.Add(new ProcessEntry
                        {
                            ExePath = bits[0],
                            Arguments = bits.Length > 1 ? bits[1] : "",
                            Icon = bits.Length > 2 ? bits[2] : "🎮",
                            IconImagePath = bits.Length > 3 && !string.IsNullOrEmpty(bits[3]) && File.Exists(bits[3]) ? bits[3] : null,
                            IconColor = bits.Length > 4 ? bits[4] : "",
                            CustomName = bits.Length > 5 ? bits[5] : "",
                            // If it was launched when saved, restore to original category (not "Launched")
                            Category = savedCat,
                            FocusMode = bits.Length > 7 && int.TryParse(bits[7], out var fm)
                                ? (FocusFullscreenMode)fm : FocusFullscreenMode.FocusThenFullscreen,
                            FullscreenKey = bits.Length > 8 && !string.IsNullOrEmpty(bits[8]) ? bits[8] : "F11",
                            PlayBehaviour = bits.Length > 9 && int.TryParse(bits[9], out var pb)
                                ? (PlayBehaviour)pb : PlayBehaviour.RemainOnWidget,
                            CloseBehaviour = bits.Length > 10 && int.TryParse(bits[10], out var cb)
                                ? (CloseBehaviour)cb : CloseBehaviour.RemainOnWidget,
                            IsManualOverride = bits.Length > 12 && bits[12] == "1",
                            CloseExePath = bits.Length > 13 && !string.IsNullOrEmpty(bits[13]) ? bits[13] : null,
                            FocusExePath = bits.Length > 14 && !string.IsNullOrEmpty(bits[14]) ? bits[14] : null,
                            Description = bits.Length > 15 ? bits[15] : "",
                            InfoUrl = bits.Length > 16 ? bits[16] : "",
                            UpdateUrl = bits.Length > 17 ? bits[17] : ""
                            // IsLaunched intentionally NOT restored — app state is unknown after restart
                        });
                    }
                }
            }
            catch { }
        }
    }
}
// QuickTimers.cs — single-file .NET 8 WPF app
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Path = System.IO.Path;
using File = System.IO.File;

namespace QuickTimers
{
    // --- Data ---
    public class TimerEntry
    {
        public string Text { get; set; } = "";
        public bool Starred { get; set; } = false;
        public long StarOrder { get; set; } = 0;
        public int Order { get; set; } = 0;
        public DateTime TriggerAt { get; set; } = DateTime.MinValue;
    }

    public class AppSettings
    {
        public string Theme { get; set; } = "Dark";
        public string GlobalHotkey { get; set; } = "Ctrl+Alt+/";
        public string NewNoteHotkey { get; set; } = "Ctrl+N";
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
        public double EditorLeft { get; set; } = double.NaN;
        public double EditorTop { get; set; } = double.NaN;
    }

    public class ThemeColors
    {
        public string Background { get; set; } = "";
        public string Foreground { get; set; } = "";
        public string Border { get; set; } = "";
        public string SearchBg { get; set; } = "";
        public string HighlightBg { get; set; } = "";
        public string AccentColor { get; set; } = "";
        public string AccentBorder { get; set; } = "";
    }

    public static class Themes
    {
        public static Dictionary<string, ThemeColors> GetThemes() => new()
        {
            ["Catppuccin Latte"] = new() { Background = "#EFF1F5", Foreground = "#4C4F69", Border = "#CCD0DA", SearchBg = "#E6E9EF", HighlightBg = "#DCE0E8", AccentColor = "#1E66F5", AccentBorder = "#1E55D0" },
            ["Catppuccin Frappe"] = new() { Background = "#303446", Foreground = "#C6D0F5", Border = "#626880", SearchBg = "#292C3C", HighlightBg = "#414559", AccentColor = "#8CAAEE", AccentBorder = "#7A96E0" },
            ["Catppuccin Macchiato"] = new() { Background = "#24273A", Foreground = "#CAD3F5", Border = "#5B6078", SearchBg = "#1E2030", HighlightBg = "#363A4F", AccentColor = "#8AADF4", AccentBorder = "#7A9AE6" },
            ["Catppuccin Mocha"] = new() { Background = "#1E1E2E", Foreground = "#CDD6F4", Border = "#585B70", SearchBg = "#181825", HighlightBg = "#313244", AccentColor = "#89B4FA", AccentBorder = "#7AA2F7" },
            ["Dark"] = new() { Background = "#222222", Foreground = "#FFFFFF", Border = "#555555", SearchBg = "#333333", HighlightBg = "#444444", AccentColor = "#00FFFF", AccentBorder = "#00CCCC" },
            ["Dracula"] = new() { Background = "#282A36", Foreground = "#F8F8F2", Border = "#6272A4", SearchBg = "#44475A", HighlightBg = "#44475A", AccentColor = "#8BE9FD", AccentBorder = "#6DCFE0" },
            ["Gruvbox Dark"] = new() { Background = "#282828", Foreground = "#EBDBB2", Border = "#504945", SearchBg = "#3C3836", HighlightBg = "#504945", AccentColor = "#83A598", AccentBorder = "#6F8A88" },
            ["Gruvbox Light"] = new() { Background = "#FBF1C7", Foreground = "#3C3836", Border = "#D5C4A1", SearchBg = "#F2E5BC", HighlightBg = "#EBDBB2", AccentColor = "#458588", AccentBorder = "#3A7178" },
            ["Light"] = new() { Background = "#FFFFFF", Foreground = "#000000", Border = "#CCCCCC", SearchBg = "#F0F0F0", HighlightBg = "#E0E0E0", AccentColor = "#40E0D0", AccentBorder = "#20B2AA" },
        };

        public static Color DimAccentColor(Color c)
        {
            float f = 0.78f;
            return Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
        }
    }

    // --- Entrypoint ---
    public partial class App : Application
    {
        [STAThread]
        public static void Main() => new App().Run(new MainWindow());
    }

    // ===================================================================
    // Main window
    // ===================================================================
    public partial class MainWindow : Window
    {
        const int HOTKEY_ID = 9100;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        string dataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickTimers");
        string timersFile => Path.Combine(dataDir, "timers.json");
        string settingsFile => Path.Combine(dataDir, "settings.json");

        List<TimerEntry> timers = new();
        AppSettings settings = new();

        Border mainBorder = null!;
        StackPanel contentPanel = null!;
        Border addButton = null!;
        Border settingsButton = null!;
        Border statusBar = null!;
        Dictionary<TimerEntry, Border> timerToBorder = new();

        bool childWindowOpen = false;
        long starCounter = 0;

        // Ticker to refresh "time remaining" labels every minute
        DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(30) };

        public MainWindow()
        {
            Title = "QuickTimers";
            Width = 450;
            MinHeight = 80;
            MaxHeight = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;

            LoadSettings();
            LoadTimers();
            ApplyWindowPosition();

            SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                src.AddHook(WndProc);
                RegisterGlobalHotkey(hwnd);
            };

            Deactivated += (_, _) => { if (!childWindowOpen) Hide(); };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Hide(); e.Handled = true; return; }
                // New note hotkey (when window active)
                var newHk = settings.NewNoteHotkey ?? "Ctrl+N";
                if (MatchesHotkey(e, newHk)) { OpenNoteEditor(null); e.Handled = true; }
            };

            ticker.Tick += (_, _) => RefreshTimerLabels();
            ticker.Start();

            Content = BuildUI();
            ApplyTheme();
            BuildTimerList();
        }

        void ApplyWindowPosition()
        {
            if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            LocationChanged += (_, _) => { settings.WindowLeft = Left; settings.WindowTop = Top; SaveSettings(); };
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == HOTKEY_ID)
            {
                if (IsVisible) Hide(); else { Show(); Activate(); Focus(); }
                handled = true;
            }
            return IntPtr.Zero;
        }

        void RegisterGlobalHotkey(IntPtr hwnd)
        {
            try { UnregisterHotKey(hwnd, HOTKEY_ID); } catch { }
            var (mod, key) = ParseHotkey(settings.GlobalHotkey);
            if (key == 0) { (mod, key) = ParseHotkey("Ctrl+Alt+/"); settings.GlobalHotkey = "Ctrl+Alt+/"; }
            RegisterHotKey(hwnd, HOTKEY_ID, mod, key);
        }

        (uint mod, uint key) ParseHotkey(string s)
        {
            uint mod = 0, key = 0;
            foreach (var p in s.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl":  mod |= 0x0002; break;
                    case "alt":   mod |= 0x0001; break;
                    case "shift": mod |= 0x0004; break;
                    default:
                        var t = p.ToUpperInvariant();
                        if (t == "/") key = 0xBF;
                        else if (t == ",") key = 0xBC;
                        else if (t == ".") key = 0xBE;
                        else if (Enum.TryParse<Key>(t, true, out var parsed)) key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
                        break;
                }
            }
            return (mod, key);
        }

        bool MatchesHotkey(KeyEventArgs e, string hotkey)
        {
            var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
            bool ctrl  = parts.Contains("ctrl");
            bool alt   = parts.Contains("alt");
            bool shift = parts.Contains("shift");
            if (ctrl  != ((Keyboard.Modifiers & ModifierKeys.Control) != 0)) return false;
            if (alt   != ((Keyboard.Modifiers & ModifierKeys.Alt)     != 0)) return false;
            if (shift != ((Keyboard.Modifiers & ModifierKeys.Shift)   != 0)) return false;
            var keyPart = parts.FirstOrDefault(p => p != "ctrl" && p != "alt" && p != "shift");
            if (keyPart == null) return false;
            if (keyPart == "n" && e.Key == Key.N) return true;
            if (Enum.TryParse<Key>(keyPart, true, out var k) && e.Key == k) return true;
            return false;
        }

        // ---------------------------------------------------------------
        // UI
        // ---------------------------------------------------------------
        FrameworkElement BuildUI()
        {
            var outer = new Border { Background = Brushes.Transparent };

            mainBorder = new Border
            {
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2)
            };

            var dock = new DockPanel();

            // Drag handle
            var dragHandle = new Border
            {
                Height = 20,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Margin = new Thickness(0, 0, 0, 6)
            };
            dragHandle.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) DragMove(); };
            var dragIcon = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dragLines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dragLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            dragLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            dragIcon.Child = dragLines;
            dragHandle.Child = dragIcon;
            DockPanel.SetDock(dragHandle, Dock.Top);
            dock.Children.Add(dragHandle);

            // ── Bottom status bar ──────────────────────────────────────
            statusBar = new Border
            {
                Height = 44,
                Margin = new Thickness(0, 6, 0, 0),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            DockPanel.SetDock(statusBar, Dock.Bottom);

            var statusGrid = new Grid();
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: Settings + Add buttons
            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            settingsButton = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Focusable = true,
                FocusVisualStyle = null,
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock { Text = "🛠️", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            settingsButton.MouseLeftButtonUp += (_, _) => OpenSettings();
            settingsButton.GotFocus  += (_, _) => { var th = GetTheme(); settingsButton.BorderThickness = new Thickness(2); settingsButton.BorderBrush = Br(th.AccentBorder); };
            settingsButton.LostFocus += (_, _) => { settingsButton.BorderThickness = new Thickness(0); settingsButton.BorderBrush = Brushes.Transparent; };
            leftPanel.Children.Add(settingsButton);

            addButton = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Focusable = true,
                FocusVisualStyle = null,
                Child = new TextBlock { Text = "+", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            addButton.MouseLeftButtonUp += (_, _) => OpenNoteEditor(null);
            addButton.GotFocus  += (_, _) => { var th = GetTheme(); addButton.BorderThickness = new Thickness(2); addButton.BorderBrush = Br(th.AccentBorder); };
            addButton.LostFocus += (_, _) => { addButton.BorderThickness = new Thickness(0); addButton.BorderBrush = Brushes.Transparent; };
            leftPanel.Children.Add(addButton);

            Grid.SetColumn(leftPanel, 0);
            statusGrid.Children.Add(leftPanel);

            // Right: empty for now (future notification controls go here)
            Grid.SetColumn(new StackPanel(), 2);

            statusBar.Child = statusGrid;
            dock.Children.Add(statusBar);

            // ── Scroll area ────────────────────────────────────────────
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FocusVisualStyle = null,
                Focusable = false,
                MaxHeight = 510
            };
            contentPanel = new StackPanel { FocusVisualStyle = null };
            scroll.Content = contentPanel;
            dock.Children.Add(scroll);

            mainBorder.Child = dock;
            outer.Child = mainBorder;
            return outer;
        }

        ThemeColors GetTheme()
        {
            var themes = Themes.GetThemes();
            return themes.ContainsKey(settings.Theme) ? themes[settings.Theme] : themes["Dark"];
        }

        void ApplyTheme()
        {
            var theme = GetTheme();
            var bgBrush = Br(theme.Background);
            var fgBrush = Br(theme.Foreground);
            Foreground = fgBrush;

            if (mainBorder != null)
            {
                mainBorder.Background = bgBrush;
                mainBorder.BorderBrush = Br(theme.Border);
            }

            if (addButton != null)
                addButton.Background = Br(theme.AccentColor);

            if (settingsButton != null)
            {
                var ac = (Color)ColorConverter.ConvertFromString(theme.AccentColor);
                settingsButton.Background = new SolidColorBrush(Themes.DimAccentColor(ac));
            }

            if (statusBar != null)
                statusBar.BorderBrush = Br(theme.Border);
        }

        static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

        // ---------------------------------------------------------------
        // Timer list
        // ---------------------------------------------------------------
        void BuildTimerList()
        {
            contentPanel.Children.Clear();
            timerToBorder.Clear();

            var theme = GetTheme();

            var sorted = timers
                .OrderByDescending(t => t.Starred)
                .ThenBy(t => t.Starred ? t.StarOrder : t.Order)
                .ThenBy(t => t.TriggerAt)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0) contentPanel.Children.Add(MakeSeparator(theme));
                var b = CreateTimerBorder(sorted[i]);
                contentPanel.Children.Add(b);
                timerToBorder[sorted[i]] = b;
            }

            if (timers.Count == 0)
            {
                contentPanel.Children.Add(new TextBlock
                {
                    Text = "Add a timed note with + or Ctrl+N.",
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(4, 12, 4, 8)
                });
            }
        }

        Border MakeSeparator(ThemeColors theme)
        {
            var c = (Color)ColorConverter.ConvertFromString(theme.Border);
            return new Border { Height = 1, Margin = new Thickness(8, 0, 8, 0), Opacity = 0.3, Background = new SolidColorBrush(c) };
        }

        Border CreateTimerBorder(TimerEntry entry)
        {
            var theme = GetTheme();

            var container = new Border
            {
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                Background = Br(theme.Background),
                Focusable = false,
                FocusVisualStyle = null
            };

            var row = new DockPanel();

            // Star indicator
            if (entry.Starred)
            {
                var star = new TextBlock
                {
                    Text = "❗",
                    FontSize = 11,
                    Foreground = Brushes.Red,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                DockPanel.SetDock(star, Dock.Left);
                row.Children.Add(star);
            }

            // Clock icon
            var clockIcon = new TextBlock
            {
                Text = "🕐",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            DockPanel.SetDock(clockIcon, Dock.Left);
            row.Children.Add(clockIcon);

            // Time label (right side)
            var timeLabel = new TextBlock
            {
                Text = FormatTimeLabel(entry.TriggerAt),
                Foreground = TimeColor(entry.TriggerAt, theme),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Tag = entry  // used for refresh
            };
            DockPanel.SetDock(timeLabel, Dock.Right);
            row.Children.Add(timeLabel);

            // Note text
            var tb = new TextBlock
            {
                Text = entry.Text,
                Foreground = Br(theme.Foreground),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(tb);

            container.Child = row;

            // Hover
            container.MouseEnter += (_, _) => container.Background = Br(theme.HighlightBg);
            container.MouseLeave += (_, _) => container.Background = Br(theme.Background);

            // Double-click to edit
            container.MouseLeftButtonUp += (_, e) =>
            {
                if (e.ClickCount == 2)
                    OpenNoteEditor(entry);
            };

            // Right-click menu
            container.MouseRightButtonUp += (_, _) =>
            {
                var menu = new ContextMenu();

                var star = new MenuItem { Header = entry.Starred ? "Unstar" : "⭐ Star" };
                star.Click += (_, _) =>
                {
                    entry.Starred = !entry.Starred;
                    if (entry.Starred) entry.StarOrder = ++starCounter;
                    SaveTimers(); BuildTimerList();
                };

                var edit = new MenuItem { Header = "✏ Edit" };
                edit.Click += (_, _) => OpenNoteEditor(entry);

                var del = new MenuItem { Header = "🗑 Delete" };
                del.Click += (_, _) =>
                {
                    if (MessageBox.Show("Delete this timer note?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        timers.Remove(entry);
                        SaveTimers();
                        BuildTimerList();
                    }
                };

                menu.Items.Add(star);
                menu.Items.Add(edit);
                menu.Items.Add(del);
                menu.IsOpen = true;
            };

            return container;
        }

        static string FormatTimeLabel(DateTime dt)
        {
            if (dt == DateTime.MinValue) return "(no time)";
            var diff = dt - DateTime.Now;
            if (diff.TotalSeconds < 0) return $"⚠ {dt:MMM d, HH:mm} (past)";
            if (diff.TotalMinutes < 60) return $"in {(int)diff.TotalMinutes}m — {dt:HH:mm}";
            if (diff.TotalHours < 24) return $"in {(int)diff.TotalHours}h {diff.Minutes}m — {dt:HH:mm}";
            if (diff.TotalDays < 7) return $"{dt:ddd, MMM d} at {dt:HH:mm}";
            return $"{dt:MMM d, yyyy} at {dt:HH:mm}";
        }

        Brush TimeColor(DateTime dt, ThemeColors theme)
        {
            if (dt == DateTime.MinValue) return Brushes.Gray;
            var diff = dt - DateTime.Now;
            if (diff.TotalSeconds < 0) return Brushes.OrangeRed;
            if (diff.TotalHours < 1) return Brushes.Orange;
            return Br(theme.AccentColor);
        }

        void RefreshTimerLabels()
        {
            var theme = GetTheme();
            foreach (var child in contentPanel.Children)
            {
                if (child is Border container && container.Child is DockPanel row)
                {
                    foreach (var rowChild in row.Children)
                    {
                        if (rowChild is TextBlock tl && tl.Tag is TimerEntry entry)
                        {
                            tl.Text = FormatTimeLabel(entry.TriggerAt);
                            tl.Foreground = TimeColor(entry.TriggerAt, theme);
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------
        // Note editor — step 1: text
        // ---------------------------------------------------------------
        void OpenNoteEditor(TimerEntry? editing)
        {
            childWindowOpen = true;
            var theme = GetTheme();

            var win = new Window
            {
                Title = editing == null ? "New Timer Note" : "Edit Timer Note",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Owner = this,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            if (!double.IsNaN(settings.EditorLeft) && !double.IsNaN(settings.EditorTop))
            {
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = settings.EditorLeft;
                win.Top = settings.EditorTop;
            }
            win.LocationChanged += (_, _) => { settings.EditorLeft = win.Left; settings.EditorTop = win.Top; SaveSettings(); };

            var outerBorder = new Border { Background = Brushes.Transparent };
            var mainBrd = new Border
            {
                Background = Br(theme.Background),
                BorderBrush = Br(theme.Border),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };

            var panel = new StackPanel();
            panel.Children.Add(BuildDragHandle(win));

            panel.Children.Add(new TextBlock
            {
                Text = "Note",
                Foreground = Br(theme.Foreground),
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var tb = new TextBox
            {
                Text = editing?.Text ?? "",
                Background = Br(theme.SearchBg),
                Foreground = Br(theme.Foreground),
                CaretBrush = Br(theme.Foreground),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MinWidth = 340
            };
            panel.Children.Add(tb);

            var btnGrid = new Grid { Height = 36, Margin = new Thickness(0, 10, 0, 0) };
            var nextBtn = new Border
            {
                Width = 26, Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = Br(theme.AccentColor),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "→", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            nextBtn.MouseLeftButtonUp += (_, _) =>
            {
                var text = tb.Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) return;
                win.DialogResult = true;
                win.Close();
            };
            btnGrid.Children.Add(nextBtn);
            panel.Children.Add(btnGrid);

            win.KeyDown += (_, k) =>
            {
                if (k.Key == Key.Escape) { win.DialogResult = false; win.Close(); }
                else if (k.Key == Key.Enter) { if (!string.IsNullOrWhiteSpace(tb.Text)) { win.DialogResult = true; win.Close(); } }
            };

            mainBrd.Child = panel;
            outerBorder.Child = mainBrd;
            win.Content = outerBorder;
            win.Closed += (_, _) => { childWindowOpen = false; };

            Dispatcher.BeginInvoke(() => tb.Focus(), DispatcherPriority.Input);

            if (win.ShowDialog() == true)
            {
                var noteText = tb.Text.Trim();
                if (string.IsNullOrWhiteSpace(noteText)) return;

                // Open date/time picker at the same position as the note window
                childWindowOpen = true;
                DateTime? picked = editing?.TriggerAt;
                if (editing?.TriggerAt == DateTime.MinValue) picked = null;

                var dtWin = new DateTimePickerWindow(this, settings.Theme, picked, win.Left, win.Top);
                dtWin.Owner = this;
                dtWin.Closed += (_, _) => { childWindowOpen = false; };

                if (dtWin.ShowDialog() == true && dtWin.SelectedDateTime.HasValue)
                {
                    if (editing == null)
                    {
                        var entry = new TimerEntry
                        {
                            Text = noteText,
                            TriggerAt = dtWin.SelectedDateTime.Value,
                            Order = timers.Count
                        };
                        timers.Add(entry);
                    }
                    else
                    {
                        editing.Text = noteText;
                        editing.TriggerAt = dtWin.SelectedDateTime.Value;
                    }
                    SaveTimers();
                    BuildTimerList();
                }
            }
        }

        // ---------------------------------------------------------------
        // Settings
        // ---------------------------------------------------------------
        void OpenSettings()
        {
            childWindowOpen = true;
            var win = new SettingsWindow(this, settings);
            win.Owner = this;
            win.Closed += (_, _) => { childWindowOpen = false; ApplyTheme(); BuildTimerList(); };
            win.ShowDialog();
        }

        // ---------------------------------------------------------------
        // Storage
        // ---------------------------------------------------------------
        void LoadSettings()
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                var opts = JsonOpts();
                settings = File.Exists(settingsFile)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile), opts) ?? new AppSettings()
                    : new AppSettings();
            }
            catch { settings = new AppSettings(); }
        }

        public void SaveSettings()
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings, JsonOpts()));
            ApplyTheme();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            RegisterGlobalHotkey(hwnd);
        }

        void LoadTimers()
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                timers = File.Exists(timersFile)
                    ? JsonSerializer.Deserialize<List<TimerEntry>>(File.ReadAllText(timersFile)) ?? new()
                    : new();
                starCounter = timers.Any() ? timers.Max(t => t.StarOrder) : 0;
            }
            catch { timers = new(); }
        }

        void SaveTimers()
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(timersFile, JsonSerializer.Serialize(timers, new JsonSerializerOptions { WriteIndented = true }));
        }

        static JsonSerializerOptions JsonOpts() => new()
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        static Border BuildDragHandle(Window owner)
        {
            var dh = new Border { Height = 20, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 8) };
            dh.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) owner.DragMove(); };
            var icon = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var lines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            lines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            icon.Child = lines;
            dh.Child = icon;
            return dh;
        }
    }

    // ===================================================================
    // Date + Time picker window
    // ===================================================================
    public class DateTimePickerWindow : Window
    {
        public DateTime? SelectedDateTime { get; private set; }

        int selectedYear, selectedMonth, selectedDay;
        int selectedHour = 9, selectedMinute = 0;

        TextBlock monthLabel = null!;
        Grid calGrid = null!;
        TextBlock hourDisplay = null!;
        TextBlock minuteDisplay = null!;
        ThemeColors theme;

        public DateTimePickerWindow(Window owner, string themeName, DateTime? existing = null, double spawnLeft = double.NaN, double spawnTop = double.NaN)
        {
            Title = "Set Date & Time";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;

            // Always open at the same spot the note editor was at
            if (!double.IsNaN(spawnLeft) && !double.IsNaN(spawnTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = spawnLeft;
                Top = spawnTop;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            var themes = Themes.GetThemes();
            theme = themes.ContainsKey(themeName) ? themes[themeName] : themes["Dark"];

            var seed = existing ?? DateTime.Now.AddHours(1);
            selectedYear = seed.Year;
            selectedMonth = seed.Month;
            selectedDay = seed.Day;
            selectedHour = seed.Hour;
            selectedMinute = (seed.Minute / 5) * 5;

            Content = BuildContent();
            KeyDown += (_, k) => { if (k.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        FrameworkElement BuildContent()
        {
            var outer = new Border { Background = Brushes.Transparent };
            var mainBrd = new Border
            {
                Background = Br(theme.Background),
                BorderBrush = Br(theme.Border),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14)
            };

            var panel = new StackPanel { Width = 310 };

            // Drag handle
            var dh = new Border { Height = 20, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 8) };
            dh.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) DragMove(); };
            var dhIcon = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dhLines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            dhIcon.Child = dhLines;
            dh.Child = dhIcon;
            panel.Children.Add(dh);

            // --- Calendar ---
            panel.Children.Add(SectionLabel("Date"));

            // Month navigation
            var monthNav = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var prevMonth = NavArrow("◀");
            prevMonth.MouseLeftButtonUp += (_, _) =>
            {
                selectedMonth--;
                if (selectedMonth < 1) { selectedMonth = 12; selectedYear--; }
                ClampDay();
                RebuildCalendar();
            };
            var nextMonth = NavArrow("▶");
            nextMonth.MouseLeftButtonUp += (_, _) =>
            {
                selectedMonth++;
                if (selectedMonth > 12) { selectedMonth = 1; selectedYear++; }
                ClampDay();
                RebuildCalendar();
            };
            monthLabel = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = Br(theme.Foreground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            DockPanel.SetDock(prevMonth, Dock.Left);
            DockPanel.SetDock(nextMonth, Dock.Right);
            monthNav.Children.Add(prevMonth);
            monthNav.Children.Add(nextMonth);
            monthNav.Children.Add(monthLabel);
            panel.Children.Add(monthNav);

            // Day headers
            var dayHeaders = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
            foreach (var d in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
            {
                dayHeaders.Children.Add(new TextBlock
                {
                    Text = d,
                    FontSize = 11,
                    Foreground = Br(theme.Foreground),
                    Opacity = 0.5,
                    TextAlignment = TextAlignment.Center
                });
            }
            panel.Children.Add(dayHeaders);

            calGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(calGrid);
            RebuildCalendar();

            // --- Time ---
            panel.Children.Add(SectionLabel("Time"));

            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };

            // Hour
            var hourCol = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, Width = 64 };
            var hourUp = NavArrow("▲");
            hourUp.HorizontalAlignment = HorizontalAlignment.Center;
            hourUp.MouseLeftButtonUp += (_, _) => { selectedHour = (selectedHour + 1) % 24; UpdateTimeDisplay(); };
            hourDisplay = new TextBlock
            {
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = Br(theme.Foreground),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            };
            var hourDown = NavArrow("▼");
            hourDown.HorizontalAlignment = HorizontalAlignment.Center;
            hourDown.MouseLeftButtonUp += (_, _) => { selectedHour = (selectedHour + 23) % 24; UpdateTimeDisplay(); };
            hourCol.Children.Add(hourUp);
            hourCol.Children.Add(hourDisplay);
            hourCol.Children.Add(hourDown);

            var colon = new TextBlock
            {
                Text = ":",
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = Br(theme.Foreground),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 8)
            };

            // Minute
            var minCol = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, Width = 64 };
            var minUp = NavArrow("▲");
            minUp.HorizontalAlignment = HorizontalAlignment.Center;
            minUp.MouseLeftButtonUp += (_, _) => { selectedMinute = (selectedMinute + 5) % 60; UpdateTimeDisplay(); };
            minuteDisplay = new TextBlock
            {
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = Br(theme.Foreground),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            };
            var minDown = NavArrow("▼");
            minDown.HorizontalAlignment = HorizontalAlignment.Center;
            minDown.MouseLeftButtonUp += (_, _) => { selectedMinute = (selectedMinute + 55) % 60; UpdateTimeDisplay(); };
            minCol.Children.Add(minUp);
            minCol.Children.Add(minuteDisplay);
            minCol.Children.Add(minDown);

            timeRow.Children.Add(hourCol);
            timeRow.Children.Add(colon);
            timeRow.Children.Add(minCol);
            panel.Children.Add(timeRow);

            UpdateTimeDisplay();

            // Confirm button
            var confirmGrid = new Grid { Height = 36, Margin = new Thickness(0, 0, 0, 2) };
            var confirmBtn = new Border
            {
                Width = 26, Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = Br(theme.AccentColor),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "✓", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            confirmBtn.MouseLeftButtonUp += (_, _) => Confirm();
            confirmGrid.Children.Add(confirmBtn);
            panel.Children.Add(confirmGrid);

            mainBrd.Child = panel;
            outer.Child = mainBrd;
            return outer;
        }

        void RebuildCalendar()
        {
            monthLabel.Text = new DateTime(selectedYear, selectedMonth, 1).ToString("MMMM yyyy");

            calGrid.Children.Clear();
            calGrid.RowDefinitions.Clear();
            for (int i = 0; i < 6; i++) calGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            calGrid.ColumnDefinitions.Clear();
            for (int i = 0; i < 7; i++) calGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var first = new DateTime(selectedYear, selectedMonth, 1);
            int startCol = ((int)first.DayOfWeek + 6) % 7; // Monday = 0
            int daysInMonth = DateTime.DaysInMonth(selectedYear, selectedMonth);

            for (int day = 1; day <= daysInMonth; day++)
            {
                int idx = startCol + day - 1;
                int row = idx / 7, col = idx % 7;
                var dt = new DateTime(selectedYear, selectedMonth, day);
                bool isToday = dt.Date == DateTime.Today;
                bool isSelected = day == selectedDay;
                bool isPast = dt.Date < DateTime.Today;

                var dayBorder = new Border
                {
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Background = isSelected ? Br(theme.AccentColor) :
                                 isToday ? new SolidColorBrush(Color.FromArgb(60,
                                     ((SolidColorBrush)Br(theme.AccentColor)).Color.R,
                                     ((SolidColorBrush)Br(theme.AccentColor)).Color.G,
                                     ((SolidColorBrush)Br(theme.AccentColor)).Color.B)) :
                                 Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = day.ToString(),
                        FontSize = 12,
                        Foreground = isSelected ? Brushes.Black :
                                     isPast ? new SolidColorBrush(Color.FromArgb(100, 150, 150, 150)) :
                                     Br(theme.Foreground),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };

                var capturedDay = day;
                dayBorder.MouseLeftButtonUp += (_, _) => { selectedDay = capturedDay; RebuildCalendar(); };
                dayBorder.MouseEnter += (_, _) => { if (!isSelected) dayBorder.Background = Br(theme.HighlightBg); };
                dayBorder.MouseLeave += (_, _) => { dayBorder.Background = isSelected ? Br(theme.AccentColor) : isToday ? new SolidColorBrush(Color.FromArgb(60, ((SolidColorBrush)Br(theme.AccentColor)).Color.R, ((SolidColorBrush)Br(theme.AccentColor)).Color.G, ((SolidColorBrush)Br(theme.AccentColor)).Color.B)) : Brushes.Transparent; };

                Grid.SetRow(dayBorder, row);
                Grid.SetColumn(dayBorder, col);
                calGrid.Children.Add(dayBorder);
            }
        }

        void UpdateTimeDisplay()
        {
            if (hourDisplay != null) hourDisplay.Text = selectedHour.ToString("D2");
            if (minuteDisplay != null) minuteDisplay.Text = selectedMinute.ToString("D2");
        }

        void ClampDay()
        {
            int max = DateTime.DaysInMonth(selectedYear, selectedMonth);
            if (selectedDay > max) selectedDay = max;
        }

        void Confirm()
        {
            SelectedDateTime = new DateTime(selectedYear, selectedMonth, selectedDay, selectedHour, selectedMinute, 0);
            DialogResult = true;
            Close();
        }

        TextBlock SectionLabel(string text) => new()
        {
            Text = text.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Br(theme.Foreground),
            Opacity = 0.5,
            Margin = new Thickness(0, 6, 0, 6)
        };

        Border NavArrow(string symbol)
        {
            var btn = new Border
            {
                Width = 24, Height = 24,
                CornerRadius = new CornerRadius(4),
                Background = Br(theme.SearchBg),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = symbol, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Br(theme.Foreground) }
            };
            btn.MouseEnter += (_, _) => btn.Background = Br(theme.HighlightBg);
            btn.MouseLeave += (_, _) => btn.Background = Br(theme.SearchBg);
            return btn;
        }

        static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    }

    // ===================================================================
    // Settings window
    // ===================================================================
    public class SettingsWindow : Window
    {
        MainWindow parent;
        AppSettings settings;
        ComboBox themeBox = null!;
        TextBlock globalHotkeyDisplay = null!;
        TextBlock newNoteHotkeyDisplay = null!;

        public SettingsWindow(MainWindow parent, AppSettings settings)
        {
            this.parent = parent;
            this.settings = settings;

            Title = "Settings";
            Width = 320;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Content = BuildContent();
            KeyDown += (_, k) => { if (k.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        FrameworkElement BuildContent()
        {
            var themes = Themes.GetThemes();
            var theme = themes.ContainsKey(settings.Theme) ? themes[settings.Theme] : themes["Dark"];

            var outer = new Border { Background = Brushes.Transparent, Padding = new Thickness(0) };
            var mainBrd = new Border
            {
                Background = Br(theme.Background),
                BorderBrush = Br(theme.Border),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8)
            };

            var panel = new StackPanel { Margin = new Thickness(8) };

            // Drag handle
            var dh = new Border { Height = 20, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 8) };
            dh.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) DragMove(); };
            var dhLines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            var dhIcon = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), Background = Br(theme.Background), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = dhLines };
            dh.Child = dhIcon;
            panel.Children.Add(dh);

            // Global hotkey
            panel.Children.Add(new TextBlock { Text = "Global hotkey (show/hide):", Foreground = Br(theme.Foreground), Margin = new Thickness(0, 0, 0, 4) });
            globalHotkeyDisplay = new TextBlock
            {
                Text = settings.GlobalHotkey,
                Padding = new Thickness(6),
                Background = Brushes.Gray,
                Foreground = Br(theme.Foreground),
                Cursor = Cursors.Hand
            };
            globalHotkeyDisplay.MouseLeftButtonUp += (_, _) => CaptureHotkey(isGlobal: true);
            panel.Children.Add(globalHotkeyDisplay);

            var resetGlobal = new Border
            {
                Background = Brushes.DimGray,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 4, 0, 10),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = "Reset Global Hotkey", Foreground = Brushes.White }
            };
            resetGlobal.MouseLeftButtonUp += (_, _) =>
            {
                settings.GlobalHotkey = "Ctrl+Alt+/";
                globalHotkeyDisplay.Text = settings.GlobalHotkey;
                parent.SaveSettings();
            };
            panel.Children.Add(resetGlobal);

            // New note hotkey
            panel.Children.Add(new TextBlock { Text = "New note hotkey (when app active):", Foreground = Br(theme.Foreground), Margin = new Thickness(0, 0, 0, 4) });
            newNoteHotkeyDisplay = new TextBlock
            {
                Text = settings.NewNoteHotkey,
                Padding = new Thickness(6),
                Background = Brushes.Gray,
                Foreground = Br(theme.Foreground),
                Cursor = Cursors.Hand
            };
            newNoteHotkeyDisplay.MouseLeftButtonUp += (_, _) => CaptureHotkey(isGlobal: false);
            panel.Children.Add(newNoteHotkeyDisplay);

            var resetNew = new Border
            {
                Background = Brushes.DimGray,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 4, 0, 10),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = "Reset New Note Hotkey", Foreground = Brushes.White }
            };
            resetNew.MouseLeftButtonUp += (_, _) =>
            {
                settings.NewNoteHotkey = "Ctrl+N";
                newNoteHotkeyDisplay.Text = settings.NewNoteHotkey;
                parent.SaveSettings();
            };
            panel.Children.Add(resetNew);

            // Theme picker (unthemed ComboBox matching original)
            panel.Children.Add(new TextBlock { Text = "Theme", Foreground = Br(theme.Foreground) });
            themeBox = new ComboBox
            {
                ItemsSource = themes.Keys.OrderBy(k => k).ToList(),
                SelectedItem = settings.Theme,
                Margin = new Thickness(0, 4, 0, 10)
            };
            panel.Children.Add(themeBox);

            // Clear all
            var resetAll = new Border
            {
                Background = Brushes.DimGray,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 12),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = "Clear all timer notes", Foreground = Brushes.White }
            };
            resetAll.MouseLeftButtonUp += (_, _) =>
            {
                if (MessageBox.Show("This will delete all saved timer notes. Continue?", "Clear all", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickTimers", "timers.json");
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    Close();
                }
            };
            panel.Children.Add(resetAll);

            // Confirm (✓) button
            var btnContainer = new Grid { Height = 32, Margin = new Thickness(0, 4, 0, 4) };
            var checkBtn = new Border
            {
                Width = 24, Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = Br(theme.AccentColor),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "✓", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            checkBtn.MouseLeftButtonUp += (_, _) => SaveAndClose();
            btnContainer.Children.Add(checkBtn);
            panel.Children.Add(btnContainer);

            mainBrd.Child = panel;
            outer.Child = mainBrd;
            return outer;
        }

        void CaptureHotkey(bool isGlobal)
        {
            var cap = new HotkeyCaptureWindow(settings.Theme);
            cap.Owner = this;
            if (cap.ShowDialog() == true && cap.CapturedHotkey != null)
            {
                if (isGlobal)
                {
                    settings.GlobalHotkey = cap.CapturedHotkey;
                    globalHotkeyDisplay.Text = settings.GlobalHotkey;
                }
                else
                {
                    settings.NewNoteHotkey = cap.CapturedHotkey;
                    newNoteHotkeyDisplay.Text = settings.NewNoteHotkey;
                }
                parent.SaveSettings();
            }
        }

        void SaveAndClose()
        {
            settings.Theme = themeBox.SelectedValue?.ToString() ?? "Dark";
            parent.SaveSettings();
            Close();
        }

        static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    }

    // ===================================================================
    // Hotkey capture window
    // ===================================================================
    public class HotkeyCaptureWindow : Window
    {
        public string? CapturedHotkey { get; private set; }
        TextBlock preview = null!;
        HashSet<Key> pressed = new();
        TextBlock instruction = null!;
        DispatcherTimer? cancelTimer;

        public HotkeyCaptureWindow(string themeName)
        {
            Title = "Capture Hotkey";
            Width = 280;
            Height = 180;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;

            var themes = Themes.GetThemes();
            var theme = themes.ContainsKey(themeName) ? themes[themeName] : themes["Dark"];

            var outer = new Border { Background = Brushes.Transparent };
            var mainBrd = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString(theme.Background)!,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString(theme.Border)!,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };

            var panel = new StackPanel();

            var dh = new Border { Height = 20, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 8) };
            dh.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) DragMove(); };
            var dhLines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            var dhIcon = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = dhLines };
            dh.Child = dhIcon;
            panel.Children.Add(dh);

            instruction = new TextBlock
            {
                Text = "Press key combination.\nRelease to confirm. ESC to cancel.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)new BrushConverter().ConvertFromString(theme.Foreground)!,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(instruction);

            preview = new TextBlock
            {
                Text = "",
                Foreground = (Brush)new BrushConverter().ConvertFromString(theme.Foreground)!,
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                TextAlignment = TextAlignment.Center,
                MinHeight = 30
            };
            panel.Children.Add(preview);

            mainBrd.Child = panel;
            outer.Child = mainBrd;
            Content = outer;

            PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                if (e.Key == Key.Escape) { CancelCapture(); return; }
                if (e.Key == Key.System) return;
                pressed.Add(e.Key);
                if (pressed.Count > 5) { CancelCapture(); return; }
                preview.Text = FormatHotkey();
            };
            PreviewKeyUp += (_, e) =>
            {
                e.Handled = true;
                if (pressed.Count > 0)
                {
                    CapturedHotkey = FormatHotkey();
                    DialogResult = true;
                    Close();
                }
            };
            Loaded += (_, _) => Focus();
        }

        string FormatHotkey()
        {
            var parts = new List<string>();
            if (pressed.Contains(Key.LeftCtrl) || pressed.Contains(Key.RightCtrl)) parts.Add("Ctrl");
            if (pressed.Contains(Key.LeftAlt) || pressed.Contains(Key.RightAlt)) parts.Add("Alt");
            if (pressed.Contains(Key.LeftShift) || pressed.Contains(Key.RightShift)) parts.Add("Shift");
            foreach (var k in pressed)
            {
                if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) continue;
                var ks = k.ToString();
                if (ks.StartsWith("D") && ks.Length == 2 && char.IsDigit(ks[1])) ks = ks[1..];
                if (k == Key.OemComma) ks = ",";
                if (k == Key.OemPeriod) ks = ".";
                if (k == Key.OemQuestion) ks = "/";
                parts.Add(ks);
            }
            return string.Join("+", parts);
        }

        void CancelCapture()
        {
            instruction.Text = "Cancelling...";
            preview.Text = "";
            pressed.Clear();
            cancelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            cancelTimer.Tick += (_, _) => { cancelTimer!.Stop(); DialogResult = false; Close(); };
            cancelTimer.Start();
        }
    }
}
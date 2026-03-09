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
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Path = System.IO.Path;
using File = System.IO.File;

namespace QuickTimers
{
    // ===================================================================
    // Data
    // ===================================================================

    // NotifState is persisted inside TimerEntry so behaviour survives restarts.
    // None      → no notification sent yet
    // Fired     → first notification shown (not yet acted on, app running)
    // Ignored   → user let first notif auto-dismiss; second fires after 5 min
    // Ignored2  → user let second notif auto-dismiss too; no more notifs ever
    // Dismissed → user explicitly clicked Dismiss; no more notifs ever
    // Done      → user completed via notification
    public enum NotifState { None, Fired, Ignored, Ignored2, Dismissed, Done }

    public class TimerEntry
    {
        public string Text { get; set; } = "";
        public bool Starred { get; set; } = false;
        public long StarOrder { get; set; } = 0;
        public int Order { get; set; } = 0;
        public DateTime TriggerAt { get; set; } = DateTime.MinValue;
        public string? Group { get; set; } = null;
        public NotifState NotifState { get; set; } = NotifState.None;
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
        public Dictionary<string, bool> CollapsedGroups { get; set; } = new();
        public HashSet<string> StarredGroups { get; set; } = new();
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
            ["Catppuccin Latte"]     = new() { Background = "#EFF1F5", Foreground = "#4C4F69", Border = "#CCD0DA", SearchBg = "#E6E9EF", HighlightBg = "#DCE0E8", AccentColor = "#1E66F5", AccentBorder = "#1E55D0" },
            ["Catppuccin Frappe"]    = new() { Background = "#303446", Foreground = "#C6D0F5", Border = "#626880", SearchBg = "#292C3C", HighlightBg = "#414559", AccentColor = "#8CAAEE", AccentBorder = "#7A96E0" },
            ["Catppuccin Macchiato"] = new() { Background = "#24273A", Foreground = "#CAD3F5", Border = "#5B6078", SearchBg = "#1E2030", HighlightBg = "#363A4F", AccentColor = "#8AADF4", AccentBorder = "#7A9AE6" },
            ["Catppuccin Mocha"]     = new() { Background = "#1E1E2E", Foreground = "#CDD6F4", Border = "#585B70", SearchBg = "#181825", HighlightBg = "#313244", AccentColor = "#89B4FA", AccentBorder = "#7AA2F7" },
            ["Dark"]                 = new() { Background = "#222222", Foreground = "#FFFFFF", Border = "#555555", SearchBg = "#333333", HighlightBg = "#444444", AccentColor = "#00FFFF", AccentBorder = "#00CCCC" },
            ["Dracula"]              = new() { Background = "#282A36", Foreground = "#F8F8F2", Border = "#6272A4", SearchBg = "#44475A", HighlightBg = "#44475A", AccentColor = "#8BE9FD", AccentBorder = "#6DCFE0" },
            ["Gruvbox Dark"]         = new() { Background = "#282828", Foreground = "#EBDBB2", Border = "#504945", SearchBg = "#3C3836", HighlightBg = "#504945", AccentColor = "#83A598", AccentBorder = "#6F8A88" },
            ["Gruvbox Light"]        = new() { Background = "#FBF1C7", Foreground = "#3C3836", Border = "#D5C4A1", SearchBg = "#F2E5BC", HighlightBg = "#EBDBB2", AccentColor = "#458588", AccentBorder = "#3A7178" },
            ["Light"]                = new() { Background = "#FFFFFF", Foreground = "#000000", Border = "#CCCCCC", SearchBg = "#F0F0F0", HighlightBg = "#E0E0E0", AccentColor = "#40E0D0", AccentBorder = "#20B2AA" },
        };

        public static Color DimAccentColor(Color c)
        {
            float f = 0.78f;
            return Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
        }
    }

    // ===================================================================
    // Entrypoint
    // ===================================================================
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

        string dataDir     => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickTimers");
        string timersFile  => Path.Combine(dataDir, "timers.json");
        string settingsFile => Path.Combine(dataDir, "settings.json");

        List<TimerEntry> timers  = new();
        AppSettings      settings = new();

        Border     mainBorder     = null!;
        StackPanel contentPanel   = null!;
        Border     addButton      = null!;
        Border     settingsButton = null!;
        Border     statusBar      = null!;
        Dictionary<TimerEntry, Border> timerToBorder = new();

        bool childWindowOpen = false;
        long starCounter     = 0;

        // Dismiss / 5-second undo state
        Dictionary<TimerEntry, DispatcherTimer> _pendingDismiss = new();

        // Drag-drop state
        TimerEntry? _draggedEntry        = null;
        Border?     _dragSourceContainer = null;

        // Ticker to refresh time-remaining labels
        DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(10) };

        // Notification tracking
        HashSet<TimerEntry>  _activeNotifs   = new();   // notif window currently on screen
        List<ToastNotificationWindow> _toastStack = new(); // stacked toasts

        public MainWindow()
        {
            Title           = "QuickTimers";
            Width           = 450;
            MinHeight       = 80;
            MaxHeight       = 620;
            SizeToContent   = SizeToContent.Height;
            ResizeMode      = ResizeMode.NoResize;
            WindowStyle     = WindowStyle.None;
            AllowsTransparency = true;
            Background      = Brushes.Transparent;
            Topmost         = true;

            LoadSettings();
            LoadTimers();
            ApplyWindowPosition();

            SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var src  = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                src.AddHook(WndProc);
                RegisterGlobalHotkey(hwnd);
            };

            Deactivated += (_, _) => { if (!childWindowOpen) Hide(); };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Hide(); e.Handled = true; return; }
                var newHk = settings.NewNoteHotkey ?? "Ctrl+N";
                if (MatchesHotkey(e, newHk)) { OpenNoteEditor(null); e.Handled = true; }
            };

            ticker.Tick += (_, _) => { RefreshTimerLabels(); CheckTimerNotifications(); };
            ticker.Start();

            Content = BuildUI();
            ApplyTheme();
            BuildTimerList();
        }

        // ---------------------------------------------------------------
        // Window position
        // ---------------------------------------------------------------
        void ApplyWindowPosition()
        {
            if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.WindowLeft;
                Top  = settings.WindowTop;
            }
            else
                WindowStartupLocation = WindowStartupLocation.CenterScreen;

            LocationChanged += (_, _) => { settings.WindowLeft = Left; settings.WindowTop = Top; SaveSettings(); };
        }

        // ---------------------------------------------------------------
        // Global hotkey plumbing
        // ---------------------------------------------------------------
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
                        if      (t == "/") key = 0xBF;
                        else if (t == ",") key = 0xBC;
                        else if (t == ".") key = 0xBE;
                        else if (Enum.TryParse<Key>(t, true, out var parsed))
                            key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
                        break;
                }
            }
            return (mod, key);
        }

        bool MatchesHotkey(KeyEventArgs e, string hotkey)
        {
            var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries)
                              .Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
            bool ctrl  = parts.Contains("ctrl");
            bool alt   = parts.Contains("alt");
            bool shift = parts.Contains("shift");
            if (ctrl  != ((Keyboard.Modifiers & ModifierKeys.Control) != 0)) return false;
            if (alt   != ((Keyboard.Modifiers & ModifierKeys.Alt)     != 0)) return false;
            if (shift != ((Keyboard.Modifiers & ModifierKeys.Shift)   != 0)) return false;
            var keyPart = parts.FirstOrDefault(p => p != "ctrl" && p != "alt" && p != "shift");
            if (keyPart == null) return false;
            if (Enum.TryParse<Key>(keyPart, true, out var k) && e.Key == k) return true;
            return false;
        }

        // ---------------------------------------------------------------
        // UI shell
        // ---------------------------------------------------------------
        FrameworkElement BuildUI()
        {
            var outer = new Border { Background = Brushes.Transparent };

            mainBorder = new Border
            {
                Padding         = new Thickness(8),
                CornerRadius    = new CornerRadius(8),
                BorderThickness = new Thickness(2)
            };

            var dock = new DockPanel();

            // Drag handle (top)
            var dragHandle = new Border
            {
                Height     = 20,
                Background = Brushes.Transparent,
                Cursor     = Cursors.SizeAll,
                Margin     = new Thickness(0, 0, 0, 6)
            };
            dragHandle.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) DragMove(); };
            var dragIcon  = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dragLines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dragLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            dragLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            dragIcon.Child   = dragLines;
            dragHandle.Child = dragIcon;
            DockPanel.SetDock(dragHandle, Dock.Top);
            dock.Children.Add(dragHandle);

            // Bottom status bar
            statusBar = new Border
            {
                Height          = 44,
                Margin          = new Thickness(0, 6, 0, 0),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            DockPanel.SetDock(statusBar, Dock.Bottom);

            var statusGrid = new Grid();
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            settingsButton = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0), BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
                Margin = new Thickness(0, 0, 6, 0),
                Child  = new TextBlock { Text = "🛠️", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            settingsButton.MouseLeftButtonUp += (_, _) => OpenSettings();
            settingsButton.GotFocus  += (_, _) => { var th = GetTheme(); settingsButton.BorderThickness = new Thickness(2); settingsButton.BorderBrush = Br(th.AccentBorder); };
            settingsButton.LostFocus += (_, _) => { settingsButton.BorderThickness = new Thickness(0); settingsButton.BorderBrush = Brushes.Transparent; };
            leftPanel.Children.Add(settingsButton);

            addButton = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0), BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
                Child  = new TextBlock { Text = "+", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            addButton.MouseLeftButtonUp += (_, _) => OpenNoteEditor(null);
            addButton.GotFocus  += (_, _) => { var th = GetTheme(); addButton.BorderThickness = new Thickness(2); addButton.BorderBrush = Br(th.AccentBorder); };
            addButton.LostFocus += (_, _) => { addButton.BorderThickness = new Thickness(0); addButton.BorderBrush = Brushes.Transparent; };
            leftPanel.Children.Add(addButton);

            Grid.SetColumn(leftPanel, 0);
            statusGrid.Children.Add(leftPanel);

            statusBar.Child = statusGrid;
            dock.Children.Add(statusBar);

            // Scroll area
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FocusVisualStyle = null,
                Focusable        = false,
                MaxHeight        = 510
            };
            contentPanel = new StackPanel { FocusVisualStyle = null };
            scroll.Content = contentPanel;
            dock.Children.Add(scroll);

            mainBorder.Child = dock;
            outer.Child      = mainBorder;
            return outer;
        }

        // ---------------------------------------------------------------
        // Theme
        // ---------------------------------------------------------------
        ThemeColors GetTheme()
        {
            var themes = Themes.GetThemes();
            return themes.ContainsKey(settings.Theme) ? themes[settings.Theme] : themes["Dark"];
        }

        void ApplyTheme()
        {
            var theme = GetTheme();
            Foreground = Br(theme.Foreground);
            if (mainBorder != null)
            {
                mainBorder.Background  = Br(theme.Background);
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
        // Build list  (groups + ungrouped)
        // ---------------------------------------------------------------
        void BuildTimerList()
        {
            contentPanel.Children.Clear();
            timerToBorder.Clear();

            var theme = GetTheme();

            // ── Grouped entries ──────────────────────────────────────────
            var grouped = timers
                .Where(t => !string.IsNullOrWhiteSpace(t.Group))
                .GroupBy(t => t.Group!)
                .OrderByDescending(g => settings.StarredGroups != null && settings.StarredGroups.Contains(g.Key))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var g in grouped)
            {
                var groupName     = g.Key;
                bool isGrpStarred = settings.StarredGroups != null && settings.StarredGroups.Contains(groupName);

                var header = new Border
                {
                    Padding    = new Thickness(6),
                    Margin     = new Thickness(0, 2, 0, 2),
                    Cursor     = Cursors.Hand,
                    Background = Br(theme.HighlightBg),
                    Tag        = groupName    // drag-drop target marker
                };
                var headerPanel = new DockPanel();
                var arrow = new TextBlock
                {
                    Text       = settings.CollapsedGroups.ContainsKey(groupName) && settings.CollapsedGroups[groupName] ? "▶" : "▼",
                    Width      = 20,
                    Foreground = Br(theme.Foreground)
                };

                if (isGrpStarred)
                {
                    var starMark = new TextBlock { Text = "⭐", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                    DockPanel.SetDock(starMark, Dock.Right);
                    headerPanel.Children.Add(starMark);
                }

                var nameText = new TextBlock
                {
                    Text       = groupName,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Br(theme.Foreground)
                };
                headerPanel.Children.Add(arrow);
                headerPanel.Children.Add(nameText);
                header.Child = headerPanel;

                header.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.ClickCount == 2) { e.Handled = true; StartInlineGroupEdit(groupName, header, headerPanel, nameText, theme); return; }
                    var cur = settings.CollapsedGroups.ContainsKey(groupName) && settings.CollapsedGroups[groupName];
                    settings.CollapsedGroups[groupName] = !cur;
                    SaveSettings();
                    BuildTimerList();
                };

                header.MouseRightButtonUp += (_, _) =>
                {
                    var menu      = new ContextMenu();
                    var starItem  = new MenuItem { Header = isGrpStarred ? "Unstar group" : "⭐ Star group" };
                    starItem.Click += (_, _) =>
                    {
                        settings.StarredGroups ??= new HashSet<string>();
                        if (isGrpStarred) settings.StarredGroups.Remove(groupName);
                        else              settings.StarredGroups.Add(groupName);
                        SaveSettings(); BuildTimerList();
                    };
                    var renameItem = new MenuItem { Header = "Rename group" };
                    renameItem.Click += (_, _) => StartInlineGroupEdit(groupName, header, headerPanel, nameText, theme);
                    menu.Items.Add(starItem);
                    menu.Items.Add(renameItem);
                    menu.IsOpen = true;
                };

                contentPanel.Children.Add(header);

                bool collapsed = settings.CollapsedGroups.ContainsKey(groupName) && settings.CollapsedGroups[groupName];
                if (!collapsed)
                {
                    var sortedGroup = g
                        .OrderByDescending(t => t.Starred)
                        .ThenBy(t => t.Starred ? t.StarOrder : t.Order)
                        .ThenBy(t => t.TriggerAt)
                        .ToList();

                    for (int i = 0; i < sortedGroup.Count; i++)
                    {
                        if (i > 0) contentPanel.Children.Add(MakeSeparator(theme));
                        var b = CreateTimerBorder(sortedGroup[i], indent: 16);
                        contentPanel.Children.Add(b);
                        timerToBorder[sortedGroup[i]] = b;
                    }
                }
            }

            // ── Ungrouped entries ────────────────────────────────────────
            var ungrouped = timers
                .Where(t => string.IsNullOrWhiteSpace(t.Group))
                .OrderByDescending(t => t.Starred)
                .ThenBy(t => t.Starred ? t.StarOrder : t.Order)
                .ThenBy(t => t.TriggerAt)
                .ToList();

            for (int i = 0; i < ungrouped.Count; i++)
            {
                if (i > 0) contentPanel.Children.Add(MakeSeparator(theme));
                var b = CreateTimerBorder(ungrouped[i], indent: 0);
                contentPanel.Children.Add(b);
                timerToBorder[ungrouped[i]] = b;
            }

            if (timers.Count == 0)
                contentPanel.Children.Add(new TextBlock
                {
                    Text          = "Add a timed note with + or Ctrl+N.",
                    Foreground    = Brushes.Gray,
                    FontStyle     = FontStyles.Italic,
                    TextAlignment = TextAlignment.Center,
                    Margin        = new Thickness(4, 12, 4, 8)
                });
        }

        Border MakeSeparator(ThemeColors theme)
        {
            var c = (Color)ColorConverter.ConvertFromString(theme.Border);
            return new Border { Height = 1, Margin = new Thickness(8, 0, 8, 0), Opacity = 0.3, Background = new SolidColorBrush(c) };
        }

        // ---------------------------------------------------------------
        // Create one timer row
        // ---------------------------------------------------------------
        Border CreateTimerBorder(TimerEntry entry, double indent = 0)
        {
            var theme = GetTheme();

            var container = new Border
            {
                BorderThickness  = new Thickness(0),
                Padding          = new Thickness(6),
                Background       = Br(theme.Background),
                Focusable        = false,
                FocusVisualStyle = null,
                Tag              = entry    // drag-drop target marker (TimerEntry, not string)
            };

            var row = new DockPanel { Margin = new Thickness(indent, 0, 0, 0) };

            // ── ⠿ Drag handle (right-docked, appears on hover) ─────────
            var dragHandle = new TextBlock
            {
                Text      = "⠿",
                FontSize  = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                Margin    = new Thickness(6, 0, 0, 0),
                Cursor    = Cursors.SizeAll,
                Opacity   = 0,
                ToolTip   = "Drag to group"
            };
            DockPanel.SetDock(dragHandle, Dock.Right);
            row.Children.Add(dragHandle);

            // ── Completion circle (left-docked, replaces clock icon) ───
            // Empty ring at rest; on row-hover: border + interior text adopt time-label colour
            var circle = new Border
            {
                Width            = 20,
                Height           = 20,
                CornerRadius     = new CornerRadius(10),
                BorderBrush      = Br(theme.Border),
                BorderThickness  = new Thickness(1.5),
                Background       = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin           = new Thickness(0, 0, 6, 0),
                Cursor           = Cursors.Hand
            };
            var circleText = new TextBlock
            {
                Text      = "",
                FontSize  = 9,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.Transparent
            };
            circle.Child = circleText;
            DockPanel.SetDock(circle, Dock.Left);
            row.Children.Add(circle);

            // ── Star indicator (left) ──────────────────────────────────
            if (entry.Starred)
            {
                var excl = new TextBlock { Text = "❗", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.Red, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                DockPanel.SetDock(excl, Dock.Left);
                row.Children.Add(excl);
            }

            // ── Time label (right) ─────────────────────────────────────
            var timeLabel = new TextBlock
            {
                Text      = FormatTimeLabel(entry.TriggerAt),
                Foreground = TimeColor(entry.TriggerAt, theme),
                FontSize  = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin    = new Thickness(6, 0, 0, 0),
                Tag       = entry    // used by RefreshTimerLabels
            };
            DockPanel.SetDock(timeLabel, Dock.Right);
            row.Children.Add(timeLabel);

            // ── Note text ──────────────────────────────────────────────
            var tb = new TextBlock
            {
                Text         = entry.Text,
                Foreground   = Br(theme.Foreground),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(tb);

            container.Child = row;

            // ── Row hover: highlight + drag handle + circle colour hint ──
            container.MouseEnter += (_, _) =>
            {
                if (_pendingDismiss.ContainsKey(entry)) return;
                container.Background = Br(theme.HighlightBg);
                dragHandle.Opacity   = 1;
                var tc = TimeColor(entry.TriggerAt, GetTheme());
                circle.BorderBrush = tc;
                // Only overdue entries show content inside the circle (!)
                var diff = entry.TriggerAt - DateTime.Now;
                if (entry.TriggerAt != DateTime.MinValue && diff.TotalSeconds < 0)
                {
                    circleText.Text       = "!";
                    circleText.Foreground = tc;
                }
            };
            container.MouseLeave += (_, _) =>
            {
                if (_pendingDismiss.ContainsKey(entry)) return;
                container.Background  = Br(theme.Background);
                dragHandle.Opacity    = 0;
                circleText.Text       = "";
                circleText.Foreground = Brushes.Transparent;
                circle.BorderBrush    = Br(theme.Border);
            };

            // ── Circle click = complete ────────────────────────────────
            circle.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!_pendingDismiss.ContainsKey(entry))
                    BeginDismiss(entry, container, tb, timeLabel, row, circle, circleText, theme);
            };

            // ── Double-click text = open note/time editor ──────────────
            tb.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    if (!_pendingDismiss.ContainsKey(entry))
                        OpenNoteEditor(entry);
                }
            };

            // ── Drag-drop from ⠿ handle ────────────────────────────────
            bool  dragging  = false;
            Point dragStart = new();

            dragHandle.MouseLeftButtonDown += (_, e) =>
            {
                dragging  = false;
                dragStart = e.GetPosition(null);
                dragHandle.CaptureMouse();
                e.Handled = true;
            };
            dragHandle.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) { dragHandle.ReleaseMouseCapture(); return; }
                var pos = e.GetPosition(null);
                if (!dragging && (Math.Abs(pos.X - dragStart.X) > 4 || Math.Abs(pos.Y - dragStart.Y) > 4))
                {
                    dragging             = true;
                    _draggedEntry        = entry;
                    _dragSourceContainer = container;
                    HighlightGroupDropZones(true);
                }
            };
            dragHandle.MouseLeftButtonUp += (_, e) =>
            {
                dragHandle.ReleaseMouseCapture();
                if (dragging && _draggedEntry != null)
                {
                    var pos     = e.GetPosition(contentPanel);
                    var dropped = HitTestGroupHeader(pos);
                    if (dropped != null)
                    {
                        _draggedEntry.Group = dropped;
                        SaveTimers();
                        BuildTimerList();
                    }
                }
                dragging             = false;
                _draggedEntry        = null;
                _dragSourceContainer = null;
                HighlightGroupDropZones(false);
                e.Handled = true;
            };

            // ── Right-click menu ───────────────────────────────────────
            container.MouseRightButtonUp += (_, _) =>
            {
                if (_pendingDismiss.ContainsKey(entry)) return;

                var menu = new ContextMenu();

                var star = new MenuItem { Header = entry.Starred ? "Unstar" : "⭐ Star" };
                star.Click += (_, _) =>
                {
                    entry.Starred = !entry.Starred;
                    entry.StarOrder = entry.Starred ? ++starCounter : 0;
                    SaveTimers(); BuildTimerList();
                };

                var setGroup = new MenuItem { Header = "Set group" };
                setGroup.Click += (_, _) =>
                {
                    var input = PromptForText("Set group", "Enter group name (leave blank to remove from group):");
                    if (input != null)
                    {
                        entry.Group = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
                        SaveTimers(); BuildTimerList();
                    }
                };

                var edit = new MenuItem { Header = "✏ Edit" };
                edit.Click += (_, _) => OpenNoteEditor(entry);

                var del = new MenuItem { Header = "🗑 Delete" };
                del.Click += (_, _) =>
                {
                    if (MessageBox.Show("Delete this timer note?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        timers.Remove(entry);
                        SaveTimers(); BuildTimerList();
                    }
                };

                menu.Items.Add(star);
                menu.Items.Add(setGroup);
                menu.Items.Add(edit);
                menu.Items.Add(del);
                menu.IsOpen = true;
            };

            return container;
        }

        // ---------------------------------------------------------------
        // ---------------------------------------------------------------
        // Complete: fill circle ✓, hide timeLabel, show undo badge in its place
        // ---------------------------------------------------------------
        void BeginDismiss(TimerEntry entry, Border container, TextBlock tb, TextBlock timeLabel,
                          DockPanel row, Border circle, TextBlock circleText, ThemeColors theme)
        {
            // Fill the circle with ✓
            circleText.Text       = "✓";
            circleText.FontSize   = 13;
            circleText.Foreground = Brushes.Black;
            circle.Background     = Br(theme.AccentColor);
            circle.BorderBrush    = Br(theme.AccentColor);

            // Hide the time label — undo badge takes its slot
            timeLabel.Visibility = Visibility.Collapsed;

            // Build undo badge and dock it right, just like timeLabel was
            var undoBadge = new Border
            {
                Background   = new SolidColorBrush(Color.FromArgb(210, 40, 40, 40)),
                CornerRadius = new CornerRadius(4),
                Padding      = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin       = new Thickness(6, 0, 0, 0),
                Cursor       = Cursors.Hand,
                IsHitTestVisible = true
            };
            var undoText = new TextBlock { Text = "Undo (5s)", Foreground = Brushes.White, FontSize = 11 };
            undoBadge.Child = undoText;
            DockPanel.SetDock(undoBadge, Dock.Right);
            // Insert right after timeLabel's position (it's the first right-docked child)
            int timeLabelIdx = row.Children.IndexOf(timeLabel);
            row.Children.Insert(timeLabelIdx + 1, undoBadge);

            bool undone    = false;
            int  remaining = 5;

            var countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            countdown.Tick += (_, _) =>
            {
                remaining--;
                if (remaining > 0)
                    undoText.Text = $"Undo ({remaining}s)";
                else
                {
                    countdown.Stop();
                    if (!undone) DoRemove(entry, container);
                }
            };
            countdown.Start();
            _pendingDismiss[entry] = countdown;

            undoBadge.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                undone = true;
                countdown.Stop();
                _pendingDismiss.Remove(entry);

                // Restore circle to empty ring
                circleText.Text       = "";
                circleText.FontSize   = 9;
                circleText.Foreground = Brushes.Transparent;
                circle.Background     = Brushes.Transparent;
                circle.BorderBrush    = Br(theme.Border);

                // Restore time label
                timeLabel.Visibility = Visibility.Visible;
                if (row.Children.Contains(undoBadge))
                    row.Children.Remove(undoBadge);

                container.Background = Br(theme.Background);
            };
        }

        void DoRemove(TimerEntry entry, Border visual)
        {
            _pendingDismiss.Remove(entry);
            visual.IsHitTestVisible = false;

            var fade = new DoubleAnimation
            {
                From = 1.0, To = 0.0,
                Duration       = TimeSpan.FromSeconds(0.5),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) =>
            {
                timers.Remove(entry);
                SaveTimers();
                BuildTimerList();
            };
            visual.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // ---------------------------------------------------------------
        // Notification: check which timers have elapsed and fire toasts
        // ---------------------------------------------------------------
        void CheckTimerNotifications()
        {
            var now = DateTime.Now;
            foreach (var entry in timers.ToList())
            {
                if (entry.TriggerAt == DateTime.MinValue) continue;
                if (_pendingDismiss.ContainsKey(entry))  continue;
                if (entry.TriggerAt > now)               continue;

                // States that mean "never show again"
                var s = entry.NotifState;
                if (s == NotifState.Dismissed || s == NotifState.Done ||
                    s == NotifState.Ignored2)             continue;

                // Already showing a window for this entry
                if (_activeNotifs.Contains(entry))        continue;

                bool isReminder = (s == NotifState.Ignored);

                if (s == NotifState.None || s == NotifState.Fired)
                {
                    // First notification
                    entry.NotifState = NotifState.Fired;
                    SaveTimers();
                    _activeNotifs.Add(entry);
                    TriggerToast(entry, isReminder: false);
                }
                else if (s == NotifState.Ignored)
                {
                    // Second (reminder) notification — scheduled via DispatcherTimer in TriggerToast,
                    // but if the app was restarted while in Ignored state we also catch it here.
                    _activeNotifs.Add(entry);
                    TriggerToast(entry, isReminder: true);
                }
            }
        }

        void TriggerToast(TimerEntry entry, bool isReminder)
        {
            var theme = GetTheme();
            var toast = new ToastNotificationWindow(theme, entry, isReminder);

            // Position: stack above any existing toasts
            double baseBottom = SystemParameters.WorkArea.Bottom;
            double baseRight  = SystemParameters.WorkArea.Right;
            const double margin = 12;
            double yOffset = margin;
            foreach (var existing in _toastStack)
                yOffset += existing.ActualHeight > 0 ? existing.ActualHeight + 6 : 90;

            toast.Left = baseRight - toast.Width - margin;
            toast.Top  = baseBottom + 60;
            toast.Show();

            Dispatcher.BeginInvoke(() =>
            {
                toast.Left = baseRight - toast.Width - margin;
                double finalTop = baseBottom - yOffset - toast.ActualHeight;
                toast.AnimateIn(finalTop);
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            _toastStack.Add(toast);

            // ── Complete ─────────────────────────────────────────────────
            toast.OnComplete += () =>
            {
                entry.NotifState = NotifState.Done;
                SaveTimers();
                _activeNotifs.Remove(entry);
                _toastStack.Remove(toast);
                Dispatcher.BeginInvoke(() => CompleteEntryFromNotification(entry));
            };

            // ── User explicitly dismissed ────────────────────────────────
            toast.OnDismiss += () =>
            {
                entry.NotifState = NotifState.Dismissed;
                SaveTimers();
                _activeNotifs.Remove(entry);
                _toastStack.Remove(toast);
            };

            // ── Auto-ignored (timed out without interaction) ─────────────
            toast.OnIgnored += () =>
            {
                _activeNotifs.Remove(entry);
                _toastStack.Remove(toast);

                if (!isReminder)
                {
                    // First ignore → schedule reminder in 5 min
                    entry.NotifState = NotifState.Ignored;
                    SaveTimers();
                    var reTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
                    reTimer.Tick += (_, _) =>
                    {
                        reTimer.Stop();
                        if (timers.Contains(entry) && !_pendingDismiss.ContainsKey(entry) &&
                            entry.NotifState == NotifState.Ignored && !_activeNotifs.Contains(entry))
                        {
                            _activeNotifs.Add(entry);
                            TriggerToast(entry, isReminder: true);
                        }
                    };
                    reTimer.Start();
                }
                else
                {
                    // Second ignore → mark permanently silent
                    entry.NotifState = NotifState.Ignored2;
                    SaveTimers();
                }
            };
        }

        void CompleteEntryFromNotification(TimerEntry entry)
        {
            if (!timers.Contains(entry)) return;
            if (_pendingDismiss.ContainsKey(entry)) return;

            // Find the visual row for this entry (may not be visible if window is hidden)
            if (timerToBorder.TryGetValue(entry, out var container) && container.Child is DockPanel row)
            {
                var theme     = GetTheme();
                TextBlock? tb        = null;
                TextBlock? timeLabel = null;
                Border?    circle    = null;
                TextBlock? circleText = null;

                foreach (var child in row.Children)
                {
                    if (child is Border b && b.CornerRadius.TopLeft >= 8 && b.Child is TextBlock ct)
                    { circle = b; circleText = ct; }
                    else if (child is TextBlock tbl)
                    {
                        if (tbl.Tag is TimerEntry) timeLabel = tbl;
                        else if (tbl.Tag == null && tb == null) tb = tbl;
                    }
                }

                if (circle != null && circleText != null && tb != null && timeLabel != null)
                { BeginDismiss(entry, container, tb, timeLabel, row, circle, circleText, theme); return; }
            }

            // Fallback: just remove directly if no visual
            timers.Remove(entry);
            SaveTimers();
            BuildTimerList();
        }

        // ---------------------------------------------------------------
        // Drag-drop helpers
        // ---------------------------------------------------------------
        void HighlightGroupDropZones(bool on)
        {
            var theme       = GetTheme();
            var accentBrush = Br(theme.AccentColor);
            var normalBrush = Br(theme.HighlightBg);

            foreach (var child in contentPanel.Children)
            {
                if (child is Border b && b.Tag is string)   // group headers have a string Tag
                {
                    b.BorderBrush     = on ? accentBrush : Br(theme.Border);
                    b.BorderThickness = on ? new Thickness(2) : new Thickness(0);
                    if (on)
                    {
                        var ac = ((SolidColorBrush)accentBrush).Color;
                        b.Background = new SolidColorBrush(Color.FromArgb(40, ac.R, ac.G, ac.B));
                    }
                    else
                        b.Background = normalBrush;
                }
            }
        }

        string? HitTestGroupHeader(Point posInContentPanel)
        {
            foreach (var child in contentPanel.Children)
            {
                if (child is Border b && b.Tag is string groupName)
                {
                    var topLeft = b.TranslatePoint(new Point(0, 0), contentPanel);
                    var rect    = new Rect(topLeft, new Size(b.ActualWidth, b.ActualHeight));
                    if (rect.Contains(posInContentPanel)) return groupName;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------
        // Inline group name editing (double-click on group header)
        // ---------------------------------------------------------------
        void StartInlineGroupEdit(string groupName, Border header, DockPanel headerPanel, TextBlock nameText, ThemeColors theme)
        {
            var editBox = new TextBox
            {
                Text              = groupName,
                Background        = Br(theme.SearchBg),
                Foreground        = Br(theme.Foreground),
                CaretBrush        = Br(theme.Foreground),
                BorderThickness   = new Thickness(0),
                Padding           = new Thickness(2),
                MinWidth          = 100,
                FontWeight        = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var idx = headerPanel.Children.IndexOf(nameText);
            headerPanel.Children.RemoveAt(idx);
            headerPanel.Children.Insert(idx, editBox);
            editBox.Focus();
            editBox.SelectAll();

            Action commit = () =>
            {
                var newName = editBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(newName) && newName != groupName)
                {
                    foreach (var t in timers.Where(t => t.Group == groupName))
                        t.Group = newName;
                    if (settings.CollapsedGroups.ContainsKey(groupName))
                    {
                        var val = settings.CollapsedGroups[groupName];
                        settings.CollapsedGroups.Remove(groupName);
                        settings.CollapsedGroups[newName] = val;
                    }
                    settings.StarredGroups ??= new HashSet<string>();
                    if (settings.StarredGroups.Contains(groupName))
                    {
                        settings.StarredGroups.Remove(groupName);
                        settings.StarredGroups.Add(newName);
                    }
                    SaveSettings();
                    SaveTimers();
                }
                BuildTimerList();
            };

            editBox.LostFocus += (_, _) => commit();
            editBox.KeyDown   += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; commit(); }
                else if (e.Key == Key.Escape) BuildTimerList();
            };
        }

        // ---------------------------------------------------------------
        // Time label helpers
        // ---------------------------------------------------------------
        static string FormatTimeLabel(DateTime dt)
        {
            if (dt == DateTime.MinValue) return "(no time)";
            var diff = dt - DateTime.Now;
            if (diff.TotalSeconds < 0)  return $"⚠ {dt:MMM d, HH:mm} (past)";
            if (diff.TotalMinutes < 60) return $"in {(int)diff.TotalMinutes}m — {dt:HH:mm}";
            if (diff.TotalHours   < 24) return $"in {(int)diff.TotalHours}h {diff.Minutes}m — {dt:HH:mm}";
            if (diff.TotalDays    < 7)  return $"{dt:ddd, MMM d} at {dt:HH:mm}";
            return $"{dt:MMM d, yyyy} at {dt:HH:mm}";
        }


        Brush TimeColor(DateTime dt, ThemeColors theme)
        {
            if (dt == DateTime.MinValue) return Brushes.Gray;
            var diff = dt - DateTime.Now;
            if (diff.TotalSeconds < 0)   return Brushes.OrangeRed;
            if (diff.TotalHours   < 1)   return Brushes.Orange;
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
                            tl.Text       = FormatTimeLabel(entry.TriggerAt);
                            tl.Foreground = TimeColor(entry.TriggerAt, theme);
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------
        // Generic text prompt (used for "Set group")
        // ---------------------------------------------------------------
        string? PromptForText(string title, string message)
        {
            childWindowOpen = true;
            var theme = GetTheme();

            var win = new Window
            {
                Title       = title,
                Width       = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                ResizeMode  = ResizeMode.NoResize,
                Owner       = this,
                AllowsTransparency = true,
                Background  = Brushes.Transparent
            };

            var outer   = new Border { Background = Brushes.Transparent };
            var mainBrd = new Border
            {
                Background      = Br(theme.Background),
                BorderBrush     = Br(theme.Border),
                BorderThickness = new Thickness(2),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(8)
            };

            var panel = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(BuildDragHandle(win));
            panel.Children.Add(new TextBlock
            {
                Text = message, Foreground = Br(theme.Foreground),
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });

            var tb = new TextBox
            {
                MinWidth        = 320,
                Padding         = new Thickness(4),
                Background      = Br(theme.SearchBg),
                Foreground      = Br(theme.Foreground),
                CaretBrush      = Br(theme.Foreground),
                BorderThickness = new Thickness(0)
            };
            panel.Children.Add(tb);

            var btnGrid = new Grid { Height = 32, Margin = new Thickness(0, 8, 0, 0) };
            var okBtn   = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Background = Br(theme.AccentColor), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child  = new TextBlock { Text = "✓", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            okBtn.MouseLeftButtonUp += (_, _) => win.DialogResult = true;
            btnGrid.Children.Add(okBtn);
            panel.Children.Add(btnGrid);

            win.KeyDown += (_, k) =>
            {
                if (k.Key == Key.Escape) { win.DialogResult = false; win.Close(); }
                else if (k.Key == Key.Enter) win.DialogResult = true;
            };

            mainBrd.Child = panel;
            outer.Child   = mainBrd;
            win.Content   = outer;
            win.Closed   += (_, _) => { childWindowOpen = false; };

            var result = win.ShowDialog();
            childWindowOpen = false;
            return result == true ? tb.Text.Trim() : null;
        }

        // ---------------------------------------------------------------
        // Note editor  (step 1: text  →  step 2: date/time picker)
        // ---------------------------------------------------------------
        void OpenNoteEditor(TimerEntry? editing)
        {
            childWindowOpen = true;
            var theme = GetTheme();

            var win = new Window
            {
                Title       = editing == null ? "New Timer Note" : "Edit Timer Note",
                Width       = 380,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode  = ResizeMode.NoResize,
                Owner       = this,
                AllowsTransparency = true,
                Background  = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            if (!double.IsNaN(settings.EditorLeft) && !double.IsNaN(settings.EditorTop))
            {
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = settings.EditorLeft;
                win.Top  = settings.EditorTop;
            }
            win.LocationChanged += (_, _) => { settings.EditorLeft = win.Left; settings.EditorTop = win.Top; SaveSettings(); };

            var outer   = new Border { Background = Brushes.Transparent };
            var mainBrd = new Border
            {
                Background      = Br(theme.Background),
                BorderBrush     = Br(theme.Border),
                BorderThickness = new Thickness(2),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(12)
            };

            var panel = new StackPanel();
            panel.Children.Add(BuildDragHandle(win));
            panel.Children.Add(new TextBlock { Text = "Note", Foreground = Br(theme.Foreground), FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 4) });

            var tb = new TextBox
            {
                Text            = editing?.Text ?? "",
                Background      = Br(theme.SearchBg),
                Foreground      = Br(theme.Foreground),
                CaretBrush      = Br(theme.Foreground),
                BorderThickness = new Thickness(0),
                Padding         = new Thickness(6),
                TextWrapping    = TextWrapping.Wrap,
                AcceptsReturn   = false,
                MinWidth        = 340
            };
            panel.Children.Add(tb);

            var btnGrid = new Grid { Height = 36, Margin = new Thickness(0, 10, 0, 0) };
            var nextBtn = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                Background = Br(theme.AccentColor), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "→", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            nextBtn.MouseLeftButtonUp += (_, _) => { if (!string.IsNullOrWhiteSpace(tb.Text)) { win.DialogResult = true; win.Close(); } };
            btnGrid.Children.Add(nextBtn);
            panel.Children.Add(btnGrid);

            win.KeyDown += (_, k) =>
            {
                if (k.Key == Key.Escape) { win.DialogResult = false; win.Close(); }
                else if (k.Key == Key.Enter) { if (!string.IsNullOrWhiteSpace(tb.Text)) { win.DialogResult = true; win.Close(); } }
            };

            mainBrd.Child = panel;
            outer.Child   = mainBrd;
            win.Content   = outer;
            win.Closed   += (_, _) => { childWindowOpen = false; };

            Dispatcher.BeginInvoke(() => tb.Focus(), DispatcherPriority.Input);

            if (win.ShowDialog() == true)
            {
                var noteText = tb.Text.Trim();
                if (string.IsNullOrWhiteSpace(noteText)) return;

                childWindowOpen = true;
                DateTime? picked = (editing == null || editing.TriggerAt == DateTime.MinValue) ? null : editing.TriggerAt;

                var dtWin = new DateTimePickerWindow(this, settings.Theme, picked, win.Left, win.Top);
                dtWin.Owner  = this;
                dtWin.Closed += (_, _) => { childWindowOpen = false; };

                if (dtWin.ShowDialog() == true && dtWin.SelectedDateTime.HasValue)
                {
                    if (editing == null)
                        timers.Add(new TimerEntry { Text = noteText, TriggerAt = dtWin.SelectedDateTime.Value, Order = timers.Count });
                    else
                    {
                        editing.Text      = noteText;
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
            win.Owner  = this;
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
                settings = File.Exists(settingsFile)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile), JsonOpts()) ?? new AppSettings()
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
            var lines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            lines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            var icon = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = lines };
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

        TextBlock monthLabel  = null!;
        Grid      calGrid     = null!;
        TextBlock hourDisplay = null!;
        TextBlock minDisplay  = null!;
        ThemeColors theme;

        public DateTimePickerWindow(Window owner, string themeName, DateTime? existing = null,
                                    double spawnLeft = double.NaN, double spawnTop = double.NaN)
        {
            Title = "Set Date & Time";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            WindowStyle   = WindowStyle.None;
            ResizeMode    = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background    = Brushes.Transparent;

            if (!double.IsNaN(spawnLeft) && !double.IsNaN(spawnTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = spawnLeft;
                Top  = spawnTop;
            }
            else
                WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var themes = Themes.GetThemes();
            theme = themes.ContainsKey(themeName) ? themes[themeName] : themes["Dark"];

            var seed    = existing ?? DateTime.Now.AddHours(1);
            selectedYear   = seed.Year;
            selectedMonth  = seed.Month;
            selectedDay    = seed.Day;
            selectedHour   = seed.Hour;
            selectedMinute = (seed.Minute / 5) * 5;

            Content  = BuildContent();
            KeyDown += (_, k) => { if (k.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        FrameworkElement BuildContent()
        {
            var outer   = new Border { Background = Brushes.Transparent };
            var mainBrd = new Border
            {
                Background = Br(theme.Background), BorderBrush = Br(theme.Border),
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), Padding = new Thickness(14)
            };

            var panel = new StackPanel { Width = 310 };

            // Drag handle
            var dh = new Border { Height = 20, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 8) };
            dh.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) DragMove(); };
            var dhLines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            dh.Child = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = dhLines };
            panel.Children.Add(dh);

            // ── Calendar ─────────────────────────────────────────────────
            panel.Children.Add(SectionLabel("Date"));

            var monthNav = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var prevM = NavArrow("◀");
            prevM.MouseLeftButtonUp += (_, _) => { selectedMonth--; if (selectedMonth < 1) { selectedMonth = 12; selectedYear--; } ClampDay(); RebuildCalendar(); };
            var nextM = NavArrow("▶");
            nextM.MouseLeftButtonUp += (_, _) => { selectedMonth++; if (selectedMonth > 12) { selectedMonth = 1; selectedYear++; } ClampDay(); RebuildCalendar(); };
            monthLabel = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Br(theme.Foreground), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            DockPanel.SetDock(prevM, Dock.Left);
            DockPanel.SetDock(nextM, Dock.Right);
            monthNav.Children.Add(prevM);
            monthNav.Children.Add(nextM);
            monthNav.Children.Add(monthLabel);
            panel.Children.Add(monthNav);

            var dayHdrs = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
            foreach (var d in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
                dayHdrs.Children.Add(new TextBlock { Text = d, FontSize = 11, Foreground = Br(theme.Foreground), Opacity = 0.5, TextAlignment = TextAlignment.Center });
            panel.Children.Add(dayHdrs);

            calGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(calGrid);
            RebuildCalendar();

            // ── Time ──────────────────────────────────────────────────────
            panel.Children.Add(SectionLabel("Time"));

            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
            var hourCol = MakeSpinner(ref hourDisplay,
                () => { selectedHour = (selectedHour + 1) % 24; UpdateTimeDisplay(); },
                () => { selectedHour = (selectedHour + 23) % 24; UpdateTimeDisplay(); });
            var colon = new TextBlock { Text = ":", FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Br(theme.Foreground), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 8) };
            var minCol  = MakeSpinner(ref minDisplay,
                () => { selectedMinute = (selectedMinute + 5) % 60; UpdateTimeDisplay(); },
                () => { selectedMinute = (selectedMinute + 55) % 60; UpdateTimeDisplay(); });
            timeRow.Children.Add(hourCol);
            timeRow.Children.Add(colon);
            timeRow.Children.Add(minCol);
            panel.Children.Add(timeRow);
            UpdateTimeDisplay();

            // Confirm button
            var confirmGrid = new Grid { Height = 36, Margin = new Thickness(0, 0, 0, 2) };
            var confirmBtn  = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                Background = Br(theme.AccentColor), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "✓", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            confirmBtn.MouseLeftButtonUp += (_, _) => Confirm();
            confirmGrid.Children.Add(confirmBtn);
            panel.Children.Add(confirmGrid);

            mainBrd.Child = panel;
            outer.Child   = mainBrd;
            return outer;
        }

        StackPanel MakeSpinner(ref TextBlock display, Action up, Action down)
        {
            var col   = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, Width = 64 };
            var upBtn = NavArrow("▲"); upBtn.HorizontalAlignment = HorizontalAlignment.Center;
            upBtn.MouseLeftButtonUp += (_, _) => up();
            display = new TextBlock { FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Br(theme.Foreground), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            var dnBtn = NavArrow("▼"); dnBtn.HorizontalAlignment = HorizontalAlignment.Center;
            dnBtn.MouseLeftButtonUp += (_, _) => down();
            col.Children.Add(upBtn);
            col.Children.Add(display);
            col.Children.Add(dnBtn);
            return col;
        }

        void RebuildCalendar()
        {
            monthLabel.Text = new DateTime(selectedYear, selectedMonth, 1).ToString("MMMM yyyy");

            calGrid.Children.Clear();
            calGrid.RowDefinitions.Clear();
            for (int i = 0; i < 6; i++) calGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            calGrid.ColumnDefinitions.Clear();
            for (int i = 0; i < 7; i++) calGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var first       = new DateTime(selectedYear, selectedMonth, 1);
            int startCol    = ((int)first.DayOfWeek + 6) % 7;
            int daysInMonth = DateTime.DaysInMonth(selectedYear, selectedMonth);

            for (int day = 1; day <= daysInMonth; day++)
            {
                int  idx        = startCol + day - 1;
                int  r          = idx / 7, c = idx % 7;
                var  dt         = new DateTime(selectedYear, selectedMonth, day);
                bool isToday    = dt.Date == DateTime.Today;
                bool isSelected = day == selectedDay;
                bool isPast     = dt.Date < DateTime.Today;

                var acBrush  = Br(theme.AccentColor);
                var acColor  = ((SolidColorBrush)acBrush).Color;
                Brush normalBg = isSelected ? acBrush
                               : isToday    ? new SolidColorBrush(Color.FromArgb(60, acColor.R, acColor.G, acColor.B))
                               : Brushes.Transparent;

                var dayBorder = new Border
                {
                    Margin       = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Background   = normalBg,
                    Cursor       = Cursors.Hand,
                    Child        = new TextBlock
                    {
                        Text      = day.ToString(),
                        FontSize  = 12,
                        Foreground = isSelected ? Brushes.Black
                                   : isPast     ? new SolidColorBrush(Color.FromArgb(100, 150, 150, 150))
                                   : Br(theme.Foreground),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center
                    }
                };

                var capturedDay = day;
                dayBorder.MouseLeftButtonUp += (_, _) => { selectedDay = capturedDay; RebuildCalendar(); };
                dayBorder.MouseEnter += (_, _) => { if (!isSelected) dayBorder.Background = Br(theme.HighlightBg); };
                dayBorder.MouseLeave += (_, _) => { dayBorder.Background = normalBg; };

                Grid.SetRow(dayBorder, r);
                Grid.SetColumn(dayBorder, c);
                calGrid.Children.Add(dayBorder);
            }
        }

        void UpdateTimeDisplay()
        {
            if (hourDisplay != null) hourDisplay.Text = selectedHour.ToString("D2");
            if (minDisplay  != null) minDisplay.Text  = selectedMinute.ToString("D2");
        }

        void ClampDay()
        {
            int max = DateTime.DaysInMonth(selectedYear, selectedMonth);
            if (selectedDay > max) selectedDay = max;
        }

        void Confirm()
        {
            SelectedDateTime = new DateTime(selectedYear, selectedMonth, selectedDay, selectedHour, selectedMinute, 0);
            DialogResult     = true;
            Close();
        }

        TextBlock SectionLabel(string text) => new()
        {
            Text       = text.ToUpperInvariant(),
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Br(theme.Foreground),
            Opacity    = 0.5,
            Margin     = new Thickness(0, 6, 0, 6)
        };

        Border NavArrow(string symbol)
        {
            var btn = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(4),
                Background = Br(theme.SearchBg), Cursor = Cursors.Hand,
                Child      = new TextBlock { Text = symbol, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Br(theme.Foreground) }
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
        MainWindow  parent;
        AppSettings settings;
        ComboBox    themeBox             = null!;
        TextBlock   globalHotkeyDisplay  = null!;
        TextBlock   newNoteHotkeyDisplay = null!;

        public SettingsWindow(MainWindow parent, AppSettings settings)
        {
            this.parent   = parent;
            this.settings = settings;

            Title = "Settings";
            Width = 320;
            SizeToContent = SizeToContent.Height;
            WindowStyle   = WindowStyle.None;
            ResizeMode    = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background    = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Content  = BuildContent();
            KeyDown += (_, k) => { if (k.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        FrameworkElement BuildContent()
        {
            var themes = Themes.GetThemes();
            var theme  = themes.ContainsKey(settings.Theme) ? themes[settings.Theme] : themes["Dark"];

            var outer   = new Border { Background = Brushes.Transparent, Padding = new Thickness(0) };
            var mainBrd = new Border
            {
                Background = Br(theme.Background), BorderBrush = Br(theme.Border),
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), Padding = new Thickness(8)
            };

            var panel = new StackPanel { Margin = new Thickness(8) };

            // Drag handle
            var dh = new Border { Height = 20, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 8) };
            dh.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) DragMove(); };
            var dhLines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            dh.Child = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), Background = Br(theme.Background), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = dhLines };
            panel.Children.Add(dh);

            // Global hotkey
            panel.Children.Add(new TextBlock { Text = "Global hotkey (show/hide):", Foreground = Br(theme.Foreground), Margin = new Thickness(0, 0, 0, 4) });
            globalHotkeyDisplay = new TextBlock { Text = settings.GlobalHotkey, Padding = new Thickness(6), Background = Brushes.Gray, Foreground = Br(theme.Foreground), Cursor = Cursors.Hand };
            globalHotkeyDisplay.MouseLeftButtonUp += (_, _) => CaptureHotkey(true);
            panel.Children.Add(globalHotkeyDisplay);
            panel.Children.Add(MakeResetBtn("Reset Global Hotkey", () => { settings.GlobalHotkey = "Ctrl+Alt+/"; globalHotkeyDisplay.Text = settings.GlobalHotkey; parent.SaveSettings(); }));

            // New note hotkey
            panel.Children.Add(new TextBlock { Text = "New note hotkey (when app active):", Foreground = Br(theme.Foreground), Margin = new Thickness(0, 4, 0, 4) });
            newNoteHotkeyDisplay = new TextBlock { Text = settings.NewNoteHotkey, Padding = new Thickness(6), Background = Brushes.Gray, Foreground = Br(theme.Foreground), Cursor = Cursors.Hand };
            newNoteHotkeyDisplay.MouseLeftButtonUp += (_, _) => CaptureHotkey(false);
            panel.Children.Add(newNoteHotkeyDisplay);
            panel.Children.Add(MakeResetBtn("Reset New Note Hotkey", () => { settings.NewNoteHotkey = "Ctrl+N"; newNoteHotkeyDisplay.Text = settings.NewNoteHotkey; parent.SaveSettings(); }));

            // Theme
            panel.Children.Add(new TextBlock { Text = "Theme", Foreground = Br(theme.Foreground), Margin = new Thickness(0, 4, 0, 0) });
            themeBox = new ComboBox { ItemsSource = themes.Keys.OrderBy(k => k).ToList(), SelectedItem = settings.Theme, Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(themeBox);

            // Clear all
            var resetAll = MakeResetBtn("Clear all timer notes", () =>
            {
                if (MessageBox.Show("This will delete all saved timer notes. Continue?", "Clear all", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickTimers", "timers.json");
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    Close();
                }
            });
            resetAll.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(resetAll);

            // ✓ Confirm
            var btnGrid  = new Grid { Height = 32, Margin = new Thickness(0, 4, 0, 4) };
            var checkBtn = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Background = Br(theme.AccentColor), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child  = new TextBlock { Text = "✓", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
            };
            checkBtn.MouseLeftButtonUp += (_, _) => SaveAndClose();
            btnGrid.Children.Add(checkBtn);
            panel.Children.Add(btnGrid);

            mainBrd.Child = panel;
            outer.Child   = mainBrd;
            return outer;
        }

        Border MakeResetBtn(string label, Action onClick)
        {
            var b = new Border
            {
                Background = Brushes.DimGray, Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 4, 0, 10), CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
                Child  = new TextBlock { Text = label, Foreground = Brushes.White }
            };
            b.MouseLeftButtonUp += (_, _) => onClick();
            return b;
        }

        void CaptureHotkey(bool isGlobal)
        {
            var cap = new HotkeyCaptureWindow(settings.Theme) { Owner = this };
            if (cap.ShowDialog() == true && cap.CapturedHotkey != null)
            {
                if (isGlobal) { settings.GlobalHotkey  = cap.CapturedHotkey; globalHotkeyDisplay.Text  = settings.GlobalHotkey; }
                else          { settings.NewNoteHotkey = cap.CapturedHotkey; newNoteHotkeyDisplay.Text = settings.NewNoteHotkey; }
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
        TextBlock preview     = null!;
        TextBlock instruction = null!;
        HashSet<Key> pressed  = new();
        DispatcherTimer? cancelTimer;

        public HotkeyCaptureWindow(string themeName)
        {
            Title = "Capture Hotkey";
            Width = 280; Height = 180;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode  = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background  = Brushes.Transparent;

            var themes = Themes.GetThemes();
            var theme  = themes.ContainsKey(themeName) ? themes[themeName] : themes["Dark"];

            var outer   = new Border { Background = Brushes.Transparent };
            var mainBrd = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString(theme.Background)!,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString(theme.Border)!,
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), Padding = new Thickness(12)
            };

            var panel = new StackPanel();
            var dh = new Border { Height = 20, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 8) };
            dh.MouseLeftButtonDown += (_, ev) => { if (ev.ChangedButton == MouseButton.Left) DragMove(); };
            var dhLines = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 3, 0, 1) });
            dhLines.Children.Add(new Border { Height = 2, Width = 20, Background = Brushes.DarkGray, Margin = new Thickness(0, 1, 0, 3) });
            dh.Child = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = dhLines };
            panel.Children.Add(dh);

            instruction = new TextBlock { Text = "Press key combination.\nRelease to confirm. ESC to cancel.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)new BrushConverter().ConvertFromString(theme.Foreground)!, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(instruction);
            preview = new TextBlock { Text = "", Foreground = (Brush)new BrushConverter().ConvertFromString(theme.Foreground)!, FontWeight = FontWeights.Bold, FontSize = 16, TextAlignment = TextAlignment.Center, MinHeight = 30 };
            panel.Children.Add(preview);

            mainBrd.Child = panel;
            outer.Child   = mainBrd;
            Content       = outer;

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
                if (pressed.Count > 0) { CapturedHotkey = FormatHotkey(); DialogResult = true; Close(); }
            };
            Loaded += (_, _) => Focus();
        }

        string FormatHotkey()
        {
            var parts = new List<string>();
            if (pressed.Contains(Key.LeftCtrl)  || pressed.Contains(Key.RightCtrl))  parts.Add("Ctrl");
            if (pressed.Contains(Key.LeftAlt)   || pressed.Contains(Key.RightAlt))   parts.Add("Alt");
            if (pressed.Contains(Key.LeftShift) || pressed.Contains(Key.RightShift)) parts.Add("Shift");
            foreach (var k in pressed)
            {
                if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) continue;
                var ks = k.ToString();
                if (ks.StartsWith("D") && ks.Length == 2 && char.IsDigit(ks[1])) ks = ks[1..];
                if (k == Key.OemComma)    ks = ",";
                if (k == Key.OemPeriod)   ks = ".";
                if (k == Key.OemQuestion) ks = "/";
                parts.Add(ks);
            }
            return string.Join("+", parts);
        }

        void CancelCapture()
        {
            instruction.Text = "Cancelling...";
            preview.Text     = "";
            pressed.Clear();
            cancelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            cancelTimer.Tick += (_, _) => { cancelTimer!.Stop(); DialogResult = false; Close(); };
            cancelTimer.Start();
        }
    }

    // ===================================================================
    // Toast Notification Window  (Steam-style, bottom-right)
    // ===================================================================
    public class ToastNotificationWindow : Window
    {
        public event Action? OnComplete;
        public event Action? OnDismiss;   // user explicitly clicked Dismiss
        public event Action? OnIgnored;   // auto-dismissed after 10s with no interaction

        const double ToastWidth = 300;

        DispatcherTimer _autoTimer;
        bool _dismissed = false;

        // Hover-pause state for the 10s auto-dismiss
        double _elapsedMs   = 0;       // ms consumed before any pause
        bool   _timerPaused = false;
        System.Diagnostics.Stopwatch _timerSw = new();

        // ── Glow overlay – easy to remove: delete the field + the 4 lines in BuildContent
        //    that reference _glowOverlay, and the MouseEnter/MouseLeave on clipGrid.
        Border _glowOverlay = null!;

        public ToastNotificationWindow(ThemeColors theme, TimerEntry entry, bool isReminder)
        {
            Width               = ToastWidth;
            SizeToContent       = SizeToContent.Height;
            WindowStyle         = WindowStyle.None;
            ResizeMode          = ResizeMode.NoResize;
            AllowsTransparency  = true;
            Background          = Brushes.Transparent;
            Topmost             = true;
            ShowInTaskbar       = false;
            WindowStartupLocation = WindowStartupLocation.Manual;

            Content = BuildContent(theme, entry, isReminder);

            // Tick every 200ms; checks remaining budget against elapsed stopwatch time
            _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _autoTimer.Tick += (_, _) =>
            {
                if (_dismissed || _timerPaused) return;
                if (_elapsedMs + _timerSw.ElapsedMilliseconds >= 10_000)
                {
                    _autoTimer.Stop();
                    if (!_dismissed) AnimateOut(reason: DismissReason.Ignored);
                }
            };

            Loaded += (_, _) =>
            {
                _timerSw.Restart();
                _autoTimer.Start();
            };
        }

        enum DismissReason { Complete, Dismiss, Ignored }

        public void AnimateIn(double finalTop)
        {
            double start = SystemParameters.WorkArea.Bottom + 20;
            double dur   = 280;
            var sw       = System.Diagnostics.Stopwatch.StartNew();
            Top          = start;

            var moveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            moveTimer.Tick += (_, _) =>
            {
                double t     = Math.Min(sw.ElapsedMilliseconds / dur, 1.0);
                double eased = 1 - Math.Pow(1 - t, 3); // cubic ease-out
                Top = start + (finalTop - start) * eased;
                if (t >= 1.0) { moveTimer.Stop(); Top = finalTop; }
            };
            moveTimer.Start();
        }

        void AnimateOut(DismissReason reason)
        {
            if (_dismissed) return;
            _dismissed = true;
            _autoTimer.Stop();

            double start  = Top;
            double target = SystemParameters.WorkArea.Bottom + 20;
            double dur    = 240;
            var sw        = System.Diagnostics.Stopwatch.StartNew();

            var moveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            moveTimer.Tick += (_, _) =>
            {
                double t     = Math.Min(sw.ElapsedMilliseconds / dur, 1.0);
                double eased = t * t; // ease-in
                Top = start + (target - start) * eased;
                if (t >= 1.0)
                {
                    moveTimer.Stop();
                    Close();
                    if      (reason == DismissReason.Complete) OnComplete?.Invoke();
                    else if (reason == DismissReason.Dismiss)  OnDismiss?.Invoke();
                    else                                        OnIgnored?.Invoke();
                }
            };
            moveTimer.Start();
        }

        FrameworkElement BuildContent(ThemeColors theme, TimerEntry entry, bool isReminder)
        {
            var bg          = (Color)ColorConverter.ConvertFromString(theme.Background);
            var border      = (Color)ColorConverter.ConvertFromString(theme.Border);
            var fg          = (Color)ColorConverter.ConvertFromString(theme.Foreground);
            var accentBorder = (Color)ColorConverter.ConvertFromString(theme.AccentBorder); // dimmer accent for glow

            // Reminder pass → subtle red border tint
            var borderColor = isReminder
                ? Color.FromRgb(170, 55, 55)
                : border;

            // Outer wrapper — transparent padding for drop-shadow room
            var outer = new Border { Background = Brushes.Transparent, Padding = new Thickness(3) };

            // Clipping grid so the glow stays inside the rounded border
            var clipGrid = new Grid();

            var mainBrd = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(248, bg.R, bg.G, bg.B)),
                BorderBrush     = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1.5),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 10, 10, 10),
                ClipToBounds    = true,
            };

            var stack = new StackPanel();

            // ── Timer text row: optional ❗ + text ───────────────────────
            var textRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

            if (entry.Starred)
            {
                var excl = new TextBlock
                {
                    Text      = "❗",
                    FontSize  = 12,
                    Foreground = Brushes.OrangeRed,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin    = new Thickness(0, 1, 5, 0)
                };
                DockPanel.SetDock(excl, Dock.Left);
                textRow.Children.Add(excl);
            }

            var textBlock = new TextBlock
            {
                Text         = entry.Text,
                Foreground   = new SolidColorBrush(fg),
                FontSize     = 13,
                FontWeight   = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            textRow.Children.Add(textBlock);
            stack.Children.Add(textRow);

            // ── Scheduled time ───────────────────────────────────────────
            if (entry.TriggerAt != DateTime.MinValue)
            {
                stack.Children.Add(new TextBlock
                {
                    Text       = entry.TriggerAt.ToString("ddd, MMM d · HH:mm"),
                    Foreground = new SolidColorBrush(Color.FromArgb(150, fg.R, fg.G, fg.B)),
                    FontSize   = 10,
                    Margin     = new Thickness(0, 0, 0, 10)
                });
            }

            // ── Action buttons ───────────────────────────────────────────
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var dismissBtn  = MakeToastBtn("Dismiss",  theme.Border,      theme.Foreground, isDismiss: true);
            var completeBtn = MakeToastBtn("Complete", theme.AccentColor, "#000000",        isDismiss: false);

            dismissBtn.MouseLeftButtonUp += (_, _) => AnimateOut(DismissReason.Dismiss);
            completeBtn.MouseLeftButtonUp += (_, _) => AnimateOut(DismissReason.Complete);

            btnRow.Children.Add(dismissBtn);
            btnRow.Children.Add(new Border { Width = 6 });
            btnRow.Children.Add(completeBtn);
            stack.Children.Add(btnRow);

            mainBrd.Child = stack;

            // ── Glow overlay ─────────────────────────────────────────────
            // To disable the glow effect entirely, delete from here...
            _glowOverlay = new Border
            {
                CornerRadius     = new CornerRadius(6),
                IsHitTestVisible = false,
                Opacity          = 0,
                Background       = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 1),
                    EndPoint   = new Point(0.5, 0),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(40, accentBorder.R, accentBorder.G, accentBorder.B), 0.0),
                        new GradientStop(Color.FromArgb(12, accentBorder.R, accentBorder.G, accentBorder.B), 0.5),
                        new GradientStop(Color.FromArgb(0,  accentBorder.R, accentBorder.G, accentBorder.B), 1.0),
                    }
                }
            };

            clipGrid.Children.Add(mainBrd);
            clipGrid.Children.Add(_glowOverlay);

            // Hover: fade glow in/out + pause/resume the 10s auto-dismiss timer
            double glowDur = 180;
            clipGrid.MouseEnter += (_, _) =>
            {
                // Pause: bank elapsed time, stop stopwatch
                _elapsedMs  += _timerSw.ElapsedMilliseconds;
                _timerSw.Reset();
                _timerPaused = true;
                AnimateGlow(from: _glowOverlay.Opacity, to: 1.0, durationMs: glowDur);
            };
            clipGrid.MouseLeave += (_, _) =>
            {
                // Resume: restart stopwatch from current banked position
                _timerPaused = false;
                _timerSw.Restart();
                AnimateGlow(from: _glowOverlay.Opacity, to: 0.0, durationMs: glowDur);
            };
            // ...to here (and also remove the _glowOverlay field declaration above)

            outer.Child = clipGrid;
            return outer;
        }

        void AnimateGlow(double from, double to, double durationMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var t  = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
            t.Tick += (_, _) =>
            {
                double progress = Math.Min(sw.ElapsedMilliseconds / durationMs, 1.0);
                double eased    = progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2;
                _glowOverlay.Opacity = from + (to - from) * eased;
                if (progress >= 1.0) { t.Stop(); _glowOverlay.Opacity = to; }
            };
            t.Start();
        }

        static Border MakeToastBtn(string label, string borderHex, string fgHex, bool isDismiss)
        {
            var fgColor = (Color)ColorConverter.ConvertFromString(fgHex);
            var bdColor = (Color)ColorConverter.ConvertFromString(borderHex);

            var bg = isDismiss
                ? new SolidColorBrush(Colors.Transparent)
                : new SolidColorBrush(Color.FromArgb(210, bdColor.R, bdColor.G, bdColor.B));

            var borderBrush = isDismiss
                ? new SolidColorBrush(Color.FromArgb(70,  bdColor.R, bdColor.G, bdColor.B))
                : new SolidColorBrush(Color.FromArgb(220, bdColor.R, bdColor.G, bdColor.B));

            var textFg = isDismiss
                ? new SolidColorBrush(Color.FromArgb(150, fgColor.R, fgColor.G, fgColor.B))
                : new SolidColorBrush(fgColor);

            var btn = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(10, 4, 10, 4),
                Background      = bg,
                Cursor          = Cursors.Hand,
                Child           = new TextBlock
                {
                    Text       = label,
                    Foreground = textFg,
                    FontSize   = 11,
                    FontWeight = isDismiss ? FontWeights.Normal : FontWeights.SemiBold
                }
            };

            btn.MouseEnter += (_, _) => btn.Opacity = 0.72;
            btn.MouseLeave += (_, _) => btn.Opacity = 1.0;
            return btn;
        }
    }
}
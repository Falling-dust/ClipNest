using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClipNest
{
    public sealed class ClipRow : INotifyPropertyChanged
    {
        private ClipEntry entry;
        public ClipRow(ClipEntry value) { entry = value; }
        public ClipEntry Entry { get { return entry; } }
        public string Preview { get { return entry.Preview; } }
        public string Detail { get { return entry.Detail; } }
        public string Label { get { return entry.Kind + "  ·  " + entry.CapturedAt.ToString("HH:mm"); } }
        public string PinLabel { get { return entry.IsPinned ? "PINNED" : ""; } }
        public string Icon { get { return entry.Kind == ClipKind.Text ? "\uE8A5" : entry.Kind == ClipKind.Image ? "\uEB9F" : "\uE8B7"; } }
        public Brush BadgeBrush { get { return MainWindow.Brush(entry.Kind == ClipKind.Text ? "#E7EFF8" : entry.Kind == ClipKind.Image ? "#F0E9F8" : "#FBF0DD"); } }
        public Brush IconBrush { get { return MainWindow.Brush(entry.Kind == ClipKind.Text ? "#5077A6" : entry.Kind == ClipKind.Image ? "#8B69A8" : "#AD843D"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        public void Update(ClipEntry value)
        {
            entry = value;
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(null));
        }
    }

    public sealed class MainWindow : Window
    {
        private readonly HistoryStore store = new HistoryStore();
        private readonly bool demo;
        private readonly FrameworkElement root;
        private readonly ListBox list;
        private readonly TextBox search;
        private readonly TextBox editor;
        private readonly Image image;
        private readonly DispatcherTimer statusTimer;
        private ClipboardBridge bridge;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem trayPause;
        private System.Drawing.Icon trayIcon;
        private HwndSource source;
        private ClipEntry selected;
        private string filter = "All";
        private bool refreshing;
        private bool loadingEditor;
        private bool exiting;
        private bool paused;
        private bool initialized;
        private bool draftRejected;

        public MainWindow(bool demoMode)
        {
            demo = demoMode;
            Title = demo ? "ClipNest — Preview" : "ClipNest";
            Width = 490; Height = 820; MinWidth = 440; MinHeight = 730;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.CanResize;
            Background = Brush("#F5F7FA");
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Rect work = SystemParameters.WorkArea;
            Height = Math.Min(Height, work.Height - 28);
            MinHeight = Math.Min(MinHeight, Height);
            Left = Math.Max(work.Left, work.Right - Width - 24);
            Top = Math.Max(work.Top, work.Bottom - Height - 20);
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(10), UseAeroCaptionButtons = false });
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClipNest.MainWindow.xaml"))
                root = (FrameworkElement)XamlReader.Load(stream);
            Content = root;
            list = Find<ListBox>("ClipList");
            search = Find<TextBox>("SearchBox");
            editor = Find<TextBox>("Editor");
            image = Find<Image>("ImagePreview");
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            statusTimer.Tick += delegate { statusTimer.Stop(); ShowDefaultStatus(); };

            Find<Border>("DragArea").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                DependencyObject current = e.OriginalSource as DependencyObject;
                while (current != null && current != sender)
                {
                    if (current is Button) return;
                    current = VisualTreeHelper.GetParent(current);
                }
                if (e.ClickCount == 1) DragMove();
            };
            Click("HideButton", delegate { Hide(); });
            Click("FloatButton", delegate { Topmost = !Topmost; UpdateFloat(); });
            Click("PauseButton", delegate { SetPaused(!paused); });
            Click("CopyButton", delegate { CopySelected(false); });
            Click("PinButton", delegate { PinSelected(); });
            Click("DeleteButton", delegate { DeleteSelected(); });
            Click("ClearButton", delegate { ClearHistory(); });
            Click("MenuButton", delegate { OpenMenu(); });
            Find<Slider>("TransparencySlider").ValueChanged += delegate { UpdateTransparency(); };
            Click("ResetTransparencyButton", delegate { Find<Slider>("TransparencySlider").Value = 0; });
            foreach (string name in new[] { "AllFilter", "TextFilter", "ImageFilter", "FilesFilter" })
            {
                Button button = Find<Button>(name);
                button.Click += delegate(object sender, RoutedEventArgs e)
                { filter = (string)((Button)sender).Tag; RefreshList(selected == null ? (Guid?)null : selected.Id); UpdateFilters(); };
            }
            search.TextChanged += delegate
            {
                Find<TextBlock>("SearchHint").Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                RefreshList(selected == null ? (Guid?)null : selected.Id);
            };
            list.SelectionChanged += delegate { if (!refreshing) SelectEntry(list.SelectedItem as ClipRow); };
            list.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e)
            {
                if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem) CopySelected(false);
            };
            list.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                ListBoxItem item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
                if (item != null) item.IsSelected = true;
            };
            list.ContextMenuOpening += delegate(object sender, ContextMenuEventArgs e)
            {
                if (selected == null) { e.Handled = true; return; }
                list.ContextMenu = CreateClipMenu();
            };
            list.ContextMenu = new ContextMenu();
            editor.TextChanged += delegate { SaveEditor(); };
            PreviewKeyDown += HandleKeys;
            SourceInitialized += InitializeNative;
            Closing += delegate(object sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } };
            Closed += delegate { Cleanup(); };
            UpdateFloat(); UpdateFilters(); SelectEntry(null); RefreshList(null);
            if (demo) SeedDemo();
        }

        internal static Brush Brush(string hex)
        {
            SolidColorBrush brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            brush.Freeze(); return brush;
        }

        private T Find<T>(string name) where T : FrameworkElement { return (T)root.FindName(name); }
        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }

        private void InitializeNative(object sender, EventArgs e)
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WindowMessages);
            UpdateTransparency();
            using (System.Drawing.Icon extracted = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
            {
                trayIcon = extracted == null ? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone() : (System.Drawing.Icon)extracted.Clone();
                Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            if (!demo)
            {
                CreateTray();
                bridge = new ClipboardBridge(this);
                bridge.Captured += OnCaptured;
                bridge.Status += SetStatus;
                bridge.ToggleRequested += ToggleWindow;
                bridge.Start();
                if (tray != null) tray.Text = "ClipNest · " + (bridge.HotkeyRegistered ? bridge.HotkeyText : "Click to open");
            }
            initialized = true;
            ShowDefaultStatus();
        }

        private IntPtr WindowMessages(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if ((uint)message == App.ShowMessage && (wparam.ToInt64() == 1) == demo) { ShowWindow(); handled = true; }
            return IntPtr.Zero;
        }

        private void CreateTray()
        {
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show / hide ClipNest", null, delegate { ToggleWindow(); });
            trayPause = new Forms.ToolStripMenuItem("Pause capture");
            trayPause.Click += delegate { SetPaused(!paused); };
            menu.Items.Add(trayPause);
            menu.Items.Add("Clear history…", null, delegate { ShowWindow(); ClearHistory(); });
            menu.Items.Add("Reset transparency", null, delegate { Find<Slider>("TransparencySlider").Value = 0; ShowWindow(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit and forget history", null, delegate { ExitApp(); });
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "ClipNest · Ctrl+Alt+V", Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) ToggleWindow(); };
        }

        private void ToggleWindow() { if (IsVisible && IsActive) Hide(); else ShowWindow(); }
        private void ShowWindow()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            // Restore a window left on a monitor that has since been disconnected.
            Rect bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            Matrix toDevice = source == null || source.CompositionTarget == null ? Matrix.Identity : source.CompositionTarget.TransformToDevice;
            Point physical = toDevice.Transform(bounds.TopLeft);
            System.Drawing.Rectangle screenBounds = new System.Drawing.Rectangle((int)physical.X, (int)physical.Y, (int)(bounds.Width * toDevice.M11), (int)(bounds.Height * toDevice.M22));
            if (!Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(screenBounds)))
            { Left = SystemParameters.WorkArea.Right - Width - 20; Top = SystemParameters.WorkArea.Top + 20; }
            Activate();
        }

        private void OnCaptured(ClipEntry entry)
        {
            Guid? id = selected == null ? (Guid?)null : selected.Id;
            ClipEntry added = store.Add(entry);
            if (added == null) { SetStatus("History is full. Unpin or remove a clip."); return; }
            RefreshList(id);
        }

        private bool Matches(ClipEntry entry)
        {
            if (filter != "All" && entry.Kind.ToString() != filter) return false;
            string query = search.Text.Trim();
            if (query.Length == 0) return true;
            string value = entry.Kind == ClipKind.Text ? entry.Text : entry.Kind == ClipKind.Files ? String.Join("\n", entry.FilePaths) : entry.Preview;
            return value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefreshList(Guid? preferred)
        {
            List<ClipRow> rows = store.Entries.Where(Matches).Select(entry => new ClipRow(entry)).ToList();
            refreshing = true;
            list.ItemsSource = rows;
            ClipRow selection = preferred.HasValue ? rows.FirstOrDefault(r => r.Entry.Id == preferred.Value) : null;
            if (selection == null) selection = rows.FirstOrDefault();
            list.SelectedItem = selection;
            refreshing = false;
            SelectEntry(selection);
            Find<TextBlock>("CountText").Text = store.Entries.Count + " / 20";
            Find<StackPanel>("EmptyPanel").Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool filtered = store.Entries.Count > 0;
            Find<TextBlock>("EmptyTitle").Text = filtered ? "No clips match" : "Ready for your next idea";
            Find<TextBlock>("EmptyMessage").Text = filtered ? "Try another search or choose All clips." : "Copy text, an image or files in any app.\nYour latest 20 clips will appear here.";
            Find<Button>("ClearButton").IsEnabled = store.Entries.Count > 0;
        }

        private void SelectEntry(ClipRow row)
        {
            ClipEntry next = row == null ? null : row.Entry;
            bool unchanged = selected != null && next != null && selected.Id == next.Id;
            selected = next;
            Find<Button>("PinButton").IsEnabled = selected != null;
            Find<Button>("DeleteButton").IsEnabled = selected != null;
            Find<Button>("CopyButton").IsEnabled = selected != null && !(selected.Kind == ClipKind.Text && selected.Text.Length == 0);
            Find<Button>("PinButton").Content = selected != null && selected.IsPinned ? "Unpin" : "Pin";
            if (unchanged) return;
            draftRejected = false;
            loadingEditor = true;
            editor.IsUndoEnabled = false; editor.Clear();
            editor.Visibility = Visibility.Collapsed;
            image.Source = null; image.Visibility = Visibility.Collapsed;
            Find<TextBlock>("EditorPlaceholder").Visibility = selected == null ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("EditorTitle").Text = selected == null ? "PREVIEW & EDIT" : selected.Kind == ClipKind.Text ? "EDIT TEXT" : selected.Kind == ClipKind.Image ? "IMAGE PREVIEW" : "FILES & FOLDERS";
            Find<TextBlock>("EditorHint").Text = selected == null ? "Edits stay in this session" : selected.Kind == ClipKind.Text ? "Edits save automatically" : selected.Kind == ClipKind.Image ? selected.Detail : "References to the original files";
            if (selected != null && selected.Kind != ClipKind.Image)
            {
                editor.IsReadOnly = selected.Kind == ClipKind.Files;
                editor.Text = selected.Kind == ClipKind.Text ? selected.Text : String.Join(Environment.NewLine, selected.FilePaths);
                editor.Visibility = Visibility.Visible;
                editor.ScrollToHome();
            }
            else if (selected != null)
            {
                try
                {
                    using (MemoryStream stream = new MemoryStream(selected.PngData, false))
                    {
                        BitmapImage bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 800; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); image.Source = bitmap;
                    }
                    image.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    if (!(ex is NotSupportedException || ex is FileFormatException || ex is ArgumentException || ex is IOException)) throw;
                    SetStatus("This image cannot be previewed.");
                }
            }
            editor.IsUndoEnabled = true;
            loadingEditor = false;
        }

        private void SaveEditor()
        {
            if (loadingEditor || selected == null || selected.Kind != ClipKind.Text) return;
            ClipEntry saved = store.EditText(selected.Id, editor.Text);
            draftRejected = saved == null;
            Find<TextBlock>("EditorHint").Text = draftRejected ? "Memory limit: edit not saved" : "Edits saved in memory";
            if (saved != null)
            {
                selected = saved;
                ClipRow row = list.SelectedItem as ClipRow;
                if (row != null) row.Update(saved);
            }
            Find<Button>("CopyButton").IsEnabled = editor.Text.Length > 0;
        }

        private void CopySelected(bool pathsOnly)
        {
            if (selected == null) return;
            if (demo) { SetStatus("Preview mode — clipboard is untouched."); return; }
            ClipEntry payload = selected;
            if (selected.Kind == ClipKind.Text && editor.Text.Length == 0) { SetStatus("Type some text before copying."); return; }
            if (draftRejected) payload = ClipEntry.FromText(editor.Text);
            if (pathsOnly && selected.Kind == ClipKind.Files) payload = ClipEntry.FromText(String.Join(Environment.NewLine, selected.FilePaths));
            bridge.Copy(payload, delegate(bool ok) { if (ok) SetStatus("Copied. Paste into any app with Ctrl+V."); });
        }

        private void PinSelected()
        {
            if (selected == null) return;
            store.TogglePin(selected.Id);
            ClipRow row = list.SelectedItem as ClipRow;
            if (row != null) row.Update(selected);
            Find<Button>("PinButton").Content = selected.IsPinned ? "Unpin" : "Pin";
            SetStatus(selected.IsPinned ? "Pinned for this session. Still counts toward 20." : "Unpinned.");
        }

        private void DeleteSelected()
        {
            if (selected == null) return;
            store.Remove(selected.Id);
            RefreshList(null);
            SetStatus("Clip removed from history.");
        }

        private void ClearHistory()
        {
            if (store.Entries.Count == 0) return;
            if (MessageBox.Show(this, "Remove all " + store.Entries.Count + " clips, including pinned clips?\n\nThe current Windows clipboard will stay unchanged.", "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            store.Clear(); RefreshList(null); SetStatus("History cleared.");
        }

        private void SetPaused(bool value)
        {
            paused = value;
            if (bridge != null) bridge.IsPaused = value;
            Find<Button>("PauseButton").Content = paused ? "Resume" : "Pause";
            Find<Button>("PauseButton").ToolTip = paused ? "Resume capturing new copies" : "Pause clipboard capture";
            if (trayPause != null) { trayPause.Text = paused ? "Resume capture" : "Pause capture"; trayPause.Checked = paused; }
            if (tray != null) tray.Text = paused ? "ClipNest · Capture paused" : "ClipNest · " + (bridge != null && bridge.HotkeyRegistered ? bridge.HotkeyText : "Click to open");
            statusTimer.Stop(); ShowDefaultStatus();
        }

        private void UpdateFloat()
        {
            Button button = Find<Button>("FloatButton");
            button.Content = Topmost ? "Float on" : "Float off";
            button.Background = Brush(Topmost ? "#E4F1EC" : "#EEF1F4");
            button.Foreground = Brush(Topmost ? "#137665" : "#667983");
        }

        private void UpdateTransparency()
        {
            Slider slider = Find<Slider>("TransparencySlider");
            int requested = (int)Math.Round(slider.Value);
            Opacity = 1.0 - requested / 100.0;
            Find<TextBlock>("TransparencyValue").Text = requested + "%";
            Find<Button>("ResetTransparencyButton").IsEnabled = requested != 0;
        }

        private void UpdateFilters()
        {
            foreach (string name in new[] { "AllFilter", "TextFilter", "ImageFilter", "FilesFilter" })
            {
                Button button = Find<Button>(name); bool active = (string)button.Tag == filter;
                button.Background = Brush(active ? "#DCECE6" : "#F5F7FA");
                button.Foreground = Brush(active ? "#146652" : "#74858F");
                button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private void SetStatus(string message)
        {
            Find<TextBlock>("StatusText").Text = message;
            Find<TextBlock>("StatusText").ToolTip = message;
            statusTimer.Stop(); statusTimer.Start();
        }

        private void ShowDefaultStatus()
        {
            string text = demo ? "Preview mode · clipboard untouched" : paused ? "Paused · new copies are not captured" : initialized && bridge != null && !bridge.IsListening ? "Capture unavailable · restart ClipNest" : initialized && bridge != null && !bridge.HotkeyRegistered ? "Listening · shortcut unavailable; use tray" : "Listening · " + (bridge != null && bridge.HotkeyRegistered ? bridge.HotkeyText : "Ctrl+Alt+V") + " to open";
            Find<TextBlock>("StatusText").Text = text; Find<TextBlock>("StatusText").ToolTip = text;
            Find<System.Windows.Shapes.Ellipse>("StatusDot").Fill = Brush(paused || demo || (initialized && bridge != null && !bridge.IsListening) ? "#C3943A" : "#24997A");
        }

        private MenuItem MenuAction(string label, Action action)
        { MenuItem item = new MenuItem { Header = label }; item.Click += delegate { action(); }; return item; }

        private ContextMenu CreateClipMenu()
        {
            ContextMenu menu = new ContextMenu();
            menu.Items.Add(MenuAction("Copy clip", delegate { CopySelected(false); }));
            if (selected != null && selected.Kind == ClipKind.Files) menu.Items.Add(MenuAction("Copy file paths as text", delegate { CopySelected(true); }));
            menu.Items.Add(MenuAction(selected != null && selected.IsPinned ? "Unpin" : "Pin for this session", PinSelected));
            menu.Items.Add(MenuAction("Delete clip", DeleteSelected));
            return menu;
        }

        private void OpenMenu()
        {
            ContextMenu menu = new ContextMenu();
            menu.Items.Add(MenuAction(paused ? "Resume capture" : "Pause capture", delegate { SetPaused(!paused); }));
            menu.Items.Add(MenuAction("Clear history…", ClearHistory));
            menu.Items.Add(MenuAction("Clear Windows clipboard…", delegate
            {
                if (demo) { SetStatus("Preview mode — clipboard is untouched."); return; }
                if (MessageBox.Show(this, "Empty the current Windows clipboard?\n\nYour ClipNest history will remain available.", "Clear Windows clipboard", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
                    bridge.ClearClipboard(delegate(bool ok) { if (ok) SetStatus("Windows clipboard cleared."); });
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Help & shortcuts", delegate
            {
                string shortcut = bridge != null && bridge.HotkeyRegistered ? bridge.HotkeyText : "Tray icon";
                MessageBox.Show(this, "Copy in any app to capture a new clip. Select a clip to edit or preview it. Copy clip puts it back on the Windows clipboard; use Ctrl+V in your destination app.\n\n" + shortcut + "    Show / hide ClipNest\nCtrl+F    Search\nCtrl+Enter    Copy selected clip\nCtrl+Shift+P    Pin / unpin selected clip\nEsc    Hide to tray\n\nThe Transparency slider fades the whole window from 0% (solid) to 70% transparent. Reset restores a solid window; the tray menu also has Reset transparency. Your choice lasts until you exit.\n\nText edits save automatically. Pins prevent eviction but still count toward the 20-clip limit. Files are references to their original locations.\n\nThe close button hides the window and capture continues. Choose Exit to clear this session. The current Windows clipboard is not cleared on exit.\n\nHistory stays in memory: no files, sync or network. Sensitive text can still be captured; use Pause when needed. Rich text is stored as plain text.\n\nClipNest 1.1", "About ClipNest", MessageBoxButton.OK, MessageBoxImage.Information);
            }));
            menu.Items.Add(MenuAction("Exit and forget history", ExitApp));
            menu.PlacementTarget = Find<Button>("MenuButton"); menu.IsOpen = true;
        }

        private void HandleKeys(object sender, KeyEventArgs e)
        {
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            else if (modifiers == ModifierKeys.Control && e.Key == Key.F) { search.Focus(); search.SelectAll(); e.Handled = true; }
            else if (modifiers == ModifierKeys.Control && e.Key == Key.Enter) { CopySelected(false); e.Handled = true; }
            else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.P) { PinSelected(); e.Handled = true; }
            else if (modifiers == ModifierKeys.None && e.Key == Key.Delete && list.IsKeyboardFocusWithin) { DeleteSelected(); e.Handled = true; }
        }

        public void ExitApp() { exiting = true; Close(); }

        private void Cleanup()
        {
            statusTimer.Stop();
            if (bridge != null) bridge.Dispose();
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowMessages);
            if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            if (trayIcon != null) trayIcon.Dispose();
            store.Clear(); selected = null; list.ItemsSource = null;
            loadingEditor = true; editor.IsUndoEnabled = false; editor.Clear(); image.Source = null;
        }

        private void SeedDemo()
        {
            store.Add(ClipEntry.FromText("Meeting notes\n\nMake the useful things easy to find.\nKeep the small details close at hand."));
            store.Add(ClipEntry.FromFiles(new[] { @"C:\Projects\Brand refresh\Moodboard.pdf", @"C:\Projects\Brand refresh\Palette.png" }));
            DrawingVisual drawing = new DrawingVisual();
            using (DrawingContext dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(Brush("#E2EFE7"), null, new Rect(0, 0, 640, 360));
                dc.DrawEllipse(Brush("#ADC9B2"), null, new Point(180, 240), 180, 180);
                dc.DrawEllipse(Brush("#537A68"), null, new Point(420, 315), 220, 180);
                dc.DrawEllipse(Brush("#F8D986"), null, new Point(465, 85), 42, 42);
            }
            RenderTargetBitmap bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (MemoryStream stream = new MemoryStream()) { encoder.Save(stream); store.Add(ClipEntry.FromImage(stream.ToArray(), 640, 360)); }
            ClipEntry note = store.Add(ClipEntry.FromText("A little space for your next big idea.\n\nCollect the useful bits. Edit them here.\nCopy them back whenever you need them."));
            store.TogglePin(note.Id);
            RefreshList(note.Id);
        }
    }
}

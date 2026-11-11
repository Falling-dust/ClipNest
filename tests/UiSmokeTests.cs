using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipNest;

internal static class UiSmokeTests
{
    private static MainWindow window;
    private static FrameworkElement root;
    private static ListBox list;
    private static TextBox editor;
    private static TextBox search;
    private static int passed;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);

    [STAThread]
    public static int Main(string[] args)
    {
        Application application = null;
        try
        {
            // Demo mode never reads or writes the Windows clipboard and creates no tray icon.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = new MainWindow(true);
            window.Left = 60;
            window.Top = 40;
            window.Show();
            Pump();
            root = (FrameworkElement)window.Content;
            list = Find<ListBox>("ClipList");
            editor = Find<TextBox>("Editor");
            search = Find<TextBox>("SearchBox");

            Test("demo opens with four sample clips and editable selection", delegate
            {
                Assert(window.IsVisible && list.Items.Count == 4, "Initial window or sample history missing");
                Assert(Find<TextBlock>("CountText").Text == "4 / 20", "Count label incorrect");
                Assert(Selected().Kind == ClipKind.Text && Selected().IsPinned, "Initial sample selection missing");
                Assert(!editor.IsReadOnly && editor.Text.Contains("next big idea"), "Text editor not initialized");
            });

            string screenshot = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClipNest-preview.png");
            Capture(screenshot);
            Console.WriteLine("SCREENSHOT " + Path.GetFullPath(screenshot));

            Test("transparency fades rendered window, preserves resizing and history, and resets", delegate
            {
                Slider slider = Find<Slider>("TransparencySlider");
                Assert(slider.Value == 0 && !Find<Button>("ResetTransparencyButton").IsEnabled, "Window did not start fully opaque");
                Guid selectedId = Selected().Id;
                slider.Value = 50;
                Pump();
                IntPtr handle = new WindowInteropHelper(window).Handle;
                Assert(window.AllowsTransparency && window.Opacity == 0.5 && RenderedAlpha() == 128,
                    "The window content did not render with 50% alpha");
                Assert(Find<TextBlock>("TransparencyValue").Text == "50%", "Transparency percentage is incorrect");
                Point edge = window.PointToScreen(new Point(2, window.ActualHeight / 2));
                int position = ((int)edge.Y << 16) | ((int)edge.X & 0xffff);
                int hit = SendMessage(handle, 0x0084, IntPtr.Zero, new IntPtr(position)).ToInt32();
                Assert(hit == 10, "The transparent window lost its left resize handle: hit=" + hit);
                window.Hide(); window.Show(); Pump();
                Assert(window.Opacity == 0.5 && RenderedAlpha() == 128,
                    "Transparency was lost when reopening the window");
                slider.Value = 100;
                Pump();
                Assert(slider.Value == 70 && window.Opacity >= 0.29 && RenderedAlpha() > 0,
                    "Transparency allowed an invisible window");
                Click("ResetTransparencyButton");
                Assert(slider.Value == 0 && window.Opacity == 1 && RenderedAlpha() == 255,
                    "Reset did not restore a fully opaque window");
                Assert(Selected().Id == selectedId && list.Items.Count == 4, "Changing transparency disturbed clipboard history");
            });

            Test("image filter renders the selected image", delegate
            {
                Click("ImageFilter");
                Assert(list.Items.Count == 1 && Selected().Kind == ClipKind.Image, "Image filtering failed");
                Image image = Find<Image>("ImagePreview");
                Assert(image.Visibility == Visibility.Visible && image.Source != null, "Image preview missing");
                Assert(editor.Visibility == Visibility.Collapsed, "Text editor remained visible for image");
            });

            Test("file filter shows readonly original paths", delegate
            {
                Click("FilesFilter");
                Assert(list.Items.Count == 1 && Selected().Kind == ClipKind.Files, "File filtering failed");
                Assert(editor.IsReadOnly && editor.Text.Contains(@"C:\Projects\Brand refresh\Moodboard.pdf") && editor.Text.Contains("Palette.png"), "File references not displayed");
            });

            Test("text filter and empty search results update controls", delegate
            {
                Click("TextFilter");
                Assert(list.Items.Count == 2, "Text filtering failed");
                search.Text = "no-clip-matches-this-phrase";
                Pump();
                Assert(list.Items.Count == 0 && Find<StackPanel>("EmptyPanel").Visibility == Visibility.Visible, "No-results state missing");
                Assert(!Find<Button>("CopyButton").IsEnabled && !Find<Button>("DeleteButton").IsEnabled, "Selection actions remained enabled");
                search.Clear();
                Click("AllFilter");
                Assert(list.Items.Count == 4, "Search clear did not restore history");
            });

            Test("Unicode text edits save without changing identity or order", delegate
            {
                Guid id = Selected().Id;
                editor.Text = "Edited text \u4e2d\u6587 \ud83d\udc4b\nA second line.";
                Pump();
                Assert(Selected().Text == editor.Text && Selected().Id == id && list.SelectedIndex == 0, "Edit did not save in place");
                Assert(((ClipRow)list.SelectedItem).Preview.Contains("Edited text"), "Card preview did not update");
            });

            Test("empty edits stay in history and cannot be copied", delegate
            {
                Guid id = Selected().Id;
                editor.Clear();
                Pump();
                Assert(Selected().Id == id && Selected().Text.Length == 0 && list.Items.Count == 4, "Empty edit was not saved");
                Assert(!Find<Button>("CopyButton").IsEnabled, "Empty-text copy remained enabled");
                editor.Text = "Restored sample text";
                Assert(Find<Button>("CopyButton").IsEnabled, "Copy did not re-enable after typing");
            });

            Test("pin toggles without adding or removing clips", delegate
            {
                bool before = Selected().IsPinned;
                Click("PinButton");
                Assert(Selected().IsPinned != before && list.Items.Count == 4, "Pin did not toggle");
                Assert((string)Find<Button>("PinButton").Content == (Selected().IsPinned ? "Unpin" : "Pin"), "Pin action label incorrect");
                Click("PinButton");
                Assert(Selected().IsPinned == before, "Pin restore failed");
            });

            Test("switching clips clears text undo from the previous clip", delegate
            {
                editor.AppendText(" changed");
                list.SelectedIndex = 3;
                Pump();
                Assert(Selected().Kind == ClipKind.Text && editor.Text.StartsWith("Meeting notes"), "Other text clip was not selected");
                Assert(!editor.CanUndo, "Undo stack leaked between clips");
                editor.Undo();
                Assert(editor.Text.StartsWith("Meeting notes"), "Undo restored another clip's text");
            });

            Test("incoming clips preserve an active edit and caret", delegate
            {
                editor.Text = "Keep my current selection";
                editor.CaretIndex = 7;
                Guid id = Selected().Id;
                MethodInfo capture = typeof(MainWindow).GetMethod("OnCaptured", BindingFlags.Instance | BindingFlags.NonPublic);
                capture.Invoke(window, new object[] { ClipEntry.FromText("Synthetic incoming clip") });
                Pump();
                Assert(list.Items.Count == 5 && Selected().Id == id, "Incoming clip stole selection");
                Assert(editor.Text == "Keep my current selection" && editor.CaretIndex == 7, "Incoming clip reset editor");
            });

            Test("copy in demo mode does not create a clipboard bridge", delegate
            {
                Click("CopyButton");
                Assert(Find<TextBlock>("StatusText").Text.Contains("untouched"), "Demo copy was not suppressed");
                Assert(typeof(MainWindow).GetField("bridge", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) == null, "Demo created a clipboard bridge");
                Assert(typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) == null, "Demo created a tray icon");
            });

            Test("delete updates selection and total count", delegate
            {
                Guid removed = Selected().Id;
                Click("DeleteButton");
                Assert(list.Items.Count == 4 && Selected().Id != removed, "Delete did not remove selected clip");
                Assert(Find<TextBlock>("CountText").Text == "4 / 20", "Delete count label incorrect");
            });

            Test("always-on-top and pause controls toggle", delegate
            {
                bool original = window.Topmost;
                Click("FloatButton");
                Assert(window.Topmost != original, "Float control did not toggle");
                Click("FloatButton");
                Click("PauseButton");
                Assert((string)Find<Button>("PauseButton").Content == "Resume", "Pause action label incorrect");
                Click("PauseButton");
                Assert((string)Find<Button>("PauseButton").Content == "Pause", "Resume action label incorrect");
            });

            Test("minimum size keeps primary controls inside the window", delegate
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Pump();
                foreach (string name in new[] { "HideButton", "SearchBox", "ClipList", "CopyButton", "ClearButton", "Editor", "TransparencySlider", "ResetTransparencyButton" })
                    AssertWithin(Find<FrameworkElement>(name), name);
                Assert(list.ActualHeight >= 70, "History list collapsed at minimum size");
            });

            Test("close control hides the window and it can reopen", delegate
            {
                Click("HideButton");
                Assert(!window.IsVisible, "Close control did not hide window");
                window.Show();
                Pump();
                Assert(window.IsVisible && list.Items.Count == 4, "Hidden window could not reopen with history");
            });

            Test("exit clears visible history and image references", delegate
            {
                window.ExitApp();
                Pump();
                Assert(!window.IsVisible && list.Items.Count == 0 && editor.Text.Length == 0, "Exit retained visible history");
                Assert(Find<Image>("ImagePreview").Source == null, "Exit retained image preview");
                window = null;
            });

            Test("production startup registers native listener and creates tray without importing clipboard", delegate
            {
                window = new MainWindow(false);
                window.Show();
                ClipboardBridge bridge = (ClipboardBridge)typeof(MainWindow).GetField("bridge", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                // Pause before pumping any clipboard events; never read or change clipboard content.
                bridge.IsPaused = true;
                Assert(bridge.IsListening, "Production native listener failed to register");
                Assert(typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) != null, "Production tray was not created");
                root = (FrameworkElement)window.Content;
                Assert(Find<ListBox>("ClipList").Items.Count == 0, "Startup imported pre-existing clipboard");
                Console.WriteLine("Native hotkey registration: " + bridge.HotkeyRegistered + " " + bridge.HotkeyText);
                window.ExitApp();
                Assert(!bridge.IsListening && !bridge.HotkeyRegistered, "Native resources not released on exit");
                window = null;
            });

            Console.WriteLine("PASS: " + passed + " UI smoke tests.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
        finally
        {
            if (window != null) window.ExitApp();
            if (application != null) application.Shutdown();
        }
    }

    private static T Find<T>(string name) where T : FrameworkElement
    {
        T control = root.FindName(name) as T;
        if (control == null) throw new Exception("Control not found: " + name);
        return control;
    }

    private static ClipEntry Selected()
    {
        ClipRow row = list.SelectedItem as ClipRow;
        if (row == null) throw new Exception("No selected clip");
        return row.Entry;
    }

    private static void Click(string name)
    {
        Button button = Find<Button>(name);
        Assert(button.IsEnabled, name + " was disabled");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
        if (window != null && window.IsVisible) window.UpdateLayout();
    }

    private static void AssertWithin(FrameworkElement element, string name)
    {
        Point origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
        Assert(element.ActualWidth > 0 && element.ActualHeight > 0, name + " has no area");
        Assert(origin.X >= -1 && origin.Y >= -1 && origin.X + element.ActualWidth <= root.ActualWidth + 1 && origin.Y + element.ActualHeight <= root.ActualHeight + 1, name + " extends outside the window");
    }

    private static void Capture(string path)
    {
        Pump();
        RenderTargetBitmap screenshot = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 2), (int)Math.Ceiling(root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        screenshot.Render(root);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(screenshot));
        using (FileStream output = File.Create(path)) encoder.Save(output);
    }

    private static byte RenderedAlpha()
    {
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(20, 20, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static void Test(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

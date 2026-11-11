using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipNest.Tests
{
    /// <summary>Uses a private Windows window station: never touches the interactive clipboard.</summary>
    internal static class ClipboardBridgeTests
    {
        private static int passed;
        private static IntPtr isolatedDesktop;

        [STAThread]
        public static int Main()
        {
            PrivateDesktop isolation;
            try
            {
                // MUST happen before WPF, OLE clipboard access, or creation of any window.
                isolation = PrivateDesktop.Create();
                isolatedDesktop = isolation.Desktop;
                Console.WriteLine("Private window station and desktop initialized; interactive clipboard untouched.");
            }
            catch (Exception error)
            {
                Win32Exception native = error as Win32Exception;
                Console.Error.WriteLine("ISOLATION FAILED. No clipboard access attempted. " + error.Message
                    + (native == null ? "" : " Win32 error " + native.NativeErrorCode + ": "
                        + new Win32Exception(native.NativeErrorCode).Message));
                return 2;
            }

            try
            {
                RunTests();
                Console.WriteLine("PASS: " + passed + " isolated native clipboard checks.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error.GetType().Name + ": " + error.Message);
                Console.Error.WriteLine(error.StackTrace);
                return 1;
            }
            finally
            {
                // WPF/OLE may keep hidden HWNDs for this STA until process exit. Do not switch
                // back to the user's desktop. The OS releases these private handles on exit.
                GC.KeepAlive(isolation);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunTests()
        {
            Clipboard.SetText("ClipNest startup sentinel", TextDataFormat.UnicodeText);
            Window window = new Window();
            window.Title = "ClipNest isolated clipboard tests";
            window.ShowInTaskbar = false;
            window.Width = 200;
            window.Height = 100;
            new WindowInteropHelper(window).EnsureHandle();
            ClipboardBridge bridge = new ClipboardBridge(window);
            List<ClipEntry> captures = new List<ClipEntry>();
            bridge.Captured += delegate(ClipEntry item) { captures.Add(item); };
            bridge.Start();
            try
            {
                Check(bridge.IsListening, "native listener registered");
                Pump(160);
                Check(captures.Count == 0, "startup clipboard is not captured");

                string unicode = "First line\r\n\u4e2d\u6587 / \u03b1\u03b2 / \ud83d\ude80\tlast line";
                SetText(unicode);
                Await(delegate { return captures.Count == 1; }, "Unicode capture");
                Check(captures[0].Kind == ClipKind.Text && captures[0].Text == unicode,
                    "Unicode, multiline, tab, and surrogate pair preserved");

                SetText(unicode);
                Await(delegate { return captures.Count == 2; }, "duplicate capture notification");
                Check(captures[1].Text == unicode, "duplicate clipboard changes reach the history layer");

                bridge.IsPaused = true;
                SetText("paused value");
                Pump(160);
                Check(captures.Count == 2, "pause suppresses captures");
                bridge.IsPaused = false;
                Pump(160);
                Check(captures.Count == 2, "resume does not capture paused content");
                SetText("resumed value");
                Await(delegate { return captures.Count == 3; }, "resumed capture");
                Check(captures[2].Text == "resumed value", "next change is captured after resume");

                BitmapSource bitmap = SampleBitmap();
                byte[] png = EncodePng(bitmap);
                DataObject pngData = new DataObject();
                pngData.SetData("PNG", new MemoryStream(png, false));
                pngData.SetImage(bitmap);
                Clipboard.SetDataObject(pngData, true);
                Await(delegate { return captures.Count == 4; }, "PNG image capture");
                Check(captures[3].Kind == ClipKind.Image && SameBytes(captures[3].PngData, png),
                    "PNG image bytes preserved");

                Clipboard.SetImage(bitmap);
                Await(delegate { return captures.Count == 5; }, "Windows bitmap capture");
                Check(captures[4].Kind == ClipKind.Image && captures[4].PngData.Length > 20,
                    "Windows bitmap captured as PNG");

                DataObject mixed = new DataObject();
                mixed.SetText("editable Office text", TextDataFormat.UnicodeText);
                mixed.SetImage(bitmap);
                Clipboard.SetDataObject(mixed, true);
                Await(delegate { return captures.Count == 6; }, "text plus bitmap capture");
                Check(captures[5].Kind == ClipKind.Text, "editable text takes precedence over Office bitmap");

                StringCollection files = new StringCollection();
                files.Add(@"C:\ClipNest-test\report.txt");
                files.Add(@"C:\ClipNest-test\folder");
                Clipboard.SetFileDropList(files);
                Await(delegate { return captures.Count == 7; }, "file drop capture");
                Check(captures[6].Kind == ClipKind.Files && captures[6].FilePaths.Length == 2
                    && captures[6].FilePaths[0] == files[0], "file and folder paths preserved");

                SetMarkedText("excluded", "ExcludeClipboardContentFromMonitorProcessing", 1);
                Pump(200);
                Check(captures.Count == 7, "monitor exclusion marker respected");
                SetMarkedText("private", "CanIncludeInClipboardHistory", 0);
                Pump(200);
                Check(captures.Count == 7, "clipboard history opt-out respected");
                SetMarkedText("allowed", "CanIncludeInClipboardHistory", 1);
                Await(delegate { return captures.Count == 8; }, "history opt-in capture");
                Check(captures[7].Text == "allowed", "clipboard history opt-in allowed");

                bool? copied = null;
                bridge.Copy(ClipEntry.FromText(unicode), delegate(bool ok) { copied = ok; });
                Await(delegate { return copied.HasValue; }, "copy callback");
                Pump(120);
                Check(copied == true && Clipboard.GetText(TextDataFormat.UnicodeText) == unicode,
                    "copy writes exact Unicode text");
                Check(captures.Count == 8, "self copy does not capture or duplicate an item");

                bool? firstCopy = null;
                bool? latestCopy = null;
                bridge.Copy(ClipEntry.FromText("older copy"), delegate(bool ok) { firstCopy = ok; });
                bridge.Copy(ClipEntry.FromText("latest copy"), delegate(bool ok) { latestCopy = ok; });
                Await(delegate { return latestCopy.HasValue; }, "latest copy callback");
                Pump(120);
                Check(firstCopy == false && latestCopy == true && Clipboard.GetText() == "latest copy",
                    "latest queued copy wins and cancels older callback");
                Check(captures.Count == 8, "rapid own copies stay suppressed");

                bool? imageCopied = null;
                bridge.Copy(captures[3], delegate(bool ok) { imageCopied = ok; });
                Await(delegate { return imageCopied.HasValue; }, "image copy callback");
                Pump(120);
                Check(imageCopied == true && Clipboard.ContainsImage() && captures.Count == 8,
                    "image copy supplies image formats without self capture");

                bool? filesCopied = null;
                bridge.Copy(captures[6], delegate(bool ok) { filesCopied = ok; });
                Await(delegate { return filesCopied.HasValue; }, "files copy callback");
                Pump(120);
                Check(filesCopied == true && Clipboard.GetFileDropList().Count == 2 && captures.Count == 8,
                    "file copy supplies file drop list without self capture");

                TestReadContention(captures);

                bool? cleared = null;
                bridge.ClearClipboard(delegate(bool ok) { cleared = ok; });
                Await(delegate { return cleared.HasValue; }, "clear callback");
                Pump(120);
                Check(cleared == true && !Clipboard.ContainsText() && !Clipboard.ContainsImage()
                    && !Clipboard.ContainsFileDropList() && captures.Count == 9,
                    "clear empties clipboard without new capture");

                SetText(new string('x', 1000001));
                Pump(200);
                Check(captures.Count == 9, "oversized text rejected");

                bridge.Dispose();
                SetText("after disposal");
                Pump(160);
                Check(!bridge.IsListening && !bridge.HotkeyRegistered && captures.Count == 9,
                    "dispose removes listener, hotkey, and pending timers");
                bool? disposedCopy = null;
                bridge.Copy(ClipEntry.FromText("must not write"), delegate(bool ok) { disposedCopy = ok; });
                Check(disposedCopy == false && Clipboard.GetText() == "after disposal",
                    "copy after disposal fails without writing");
            }
            finally
            {
                bridge.Dispose();
                window.Close();
            }
        }

        private static void TestReadContention(List<ClipEntry> captures)
        {
            SetText("captured after contention");
            using (ManualResetEvent locked = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            {
                Exception threadFailure = null;
                Thread holder = new Thread(delegate()
                {
                    bool opened = false;
                    try
                    {
                        if (!Native.SetThreadDesktop(isolatedDesktop))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Contention thread desktop setup failed.");
                        opened = Native.OpenClipboard(IntPtr.Zero);
                        if (!opened) throw new Win32Exception(Marshal.GetLastWin32Error(), "Contention lock failed.");
                        locked.Set();
                        release.WaitOne(3000);
                    }
                    catch (Exception error) { threadFailure = error; locked.Set(); }
                    finally { if (opened) Native.CloseClipboard(); }
                });
                holder.IsBackground = true;
                holder.SetApartmentState(ApartmentState.STA);
                holder.Start();
                try
                {
                    if (!locked.WaitOne(2000)) throw new Exception("Contention setup timed out.");
                    if (threadFailure != null) throw threadFailure;
                    Pump(180);
                    Check(captures.Count == 8, "locked clipboard defers capture");
                }
                finally
                {
                    release.Set();
                    if (!holder.Join(2000)) throw new Exception("Contention thread did not exit.");
                }
            }
            Await(delegate { return captures.Count == 9; }, "capture retry after contention");
            Check(captures[8].Text == "captured after contention", "bounded capture retry succeeds after unlock");
        }

        private static void SetText(string text) { Clipboard.SetText(text, TextDataFormat.UnicodeText); }

        private static void SetMarkedText(string text, string format, int value)
        {
            DataObject data = new DataObject();
            data.SetText(text, TextDataFormat.UnicodeText);
            data.SetData(format, new MemoryStream(BitConverter.GetBytes(value), false), false);
            Clipboard.SetDataObject(data, true);
        }

        private static BitmapSource SampleBitmap()
        {
            byte[] pixels = { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 80, 80, 80, 128 };
            BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            bitmap.Freeze();
            return bitmap;
        }

        private static byte[] EncodePng(BitmapSource bitmap)
        {
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (MemoryStream stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
        }

        private static bool SameBytes(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
            return true;
        }

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }

        private static void Await(Func<bool> ready, string label)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!ready() && DateTime.UtcNow < deadline) Pump(25);
            if (!ready()) throw new Exception("Timed out: " + label);
        }

        private static void Pump(int milliseconds)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        private sealed class PrivateDesktop
        {
            public IntPtr Station;
            public IntPtr Desktop;

            public static PrivateDesktop Create()
            {
                IntPtr originalStation = Native.GetProcessWindowStation();
                IntPtr station = Native.CreateWindowStation("ClipNestTests_" + Guid.NewGuid().ToString("N"),
                    0, 0x0000037F, IntPtr.Zero);
                if (station == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowStation failed.");
                if (!Native.SetProcessWindowStation(station))
                {
                    int error = Marshal.GetLastWin32Error();
                    Native.CloseWindowStation(station);
                    throw new Win32Exception(error, "SetProcessWindowStation failed.");
                }
                IntPtr desktop = Native.CreateDesktop("ClipNestTestDesktop", null, IntPtr.Zero,
                    0, 0x000001FF, IntPtr.Zero);
                if (desktop == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Native.SetProcessWindowStation(originalStation);
                    Native.CloseWindowStation(station);
                    throw new Win32Exception(error, "CreateDesktop failed.");
                }
                if (!Native.SetThreadDesktop(desktop))
                {
                    int error = Marshal.GetLastWin32Error();
                    Native.CloseDesktop(desktop);
                    Native.SetProcessWindowStation(originalStation);
                    Native.CloseWindowStation(station);
                    throw new Win32Exception(error, "SetThreadDesktop failed; isolation cannot be guaranteed.");
                }
                if (Native.GetProcessWindowStation() != station
                    || Native.GetThreadDesktop(Native.GetCurrentThreadId()) != desktop)
                    throw new Exception("Private window station / desktop verification failed.");
                return new PrivateDesktop { Station = station, Desktop = desktop };
            }
        }

        private static class Native
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr attributes);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetProcessWindowStation(IntPtr station);
            [DllImport("user32.dll")]
            internal static extern IntPtr GetProcessWindowStation();
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseWindowStation(IntPtr station);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateDesktop(string name, string device, IntPtr devmode,
                uint flags, uint access, IntPtr attributes);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetThreadDesktop(IntPtr desktop);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseDesktop(IntPtr desktop);
            [DllImport("user32.dll")]
            internal static extern IntPtr GetThreadDesktop(uint thread);
            [DllImport("kernel32.dll")]
            internal static extern uint GetCurrentThreadId();
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool OpenClipboard(IntPtr hwnd);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseClipboard();
        }
    }
}

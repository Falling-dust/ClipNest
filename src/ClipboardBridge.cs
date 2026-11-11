using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipNest
{
    /// <summary>STA, event-driven clipboard access. No clipboard payload is written to disk.</summary>
    public sealed class ClipboardBridge : IDisposable
    {
        private const int ClipboardUpdate = 0x031D;
        private const int HotkeyMessage = 0x0312;
        private const int HotkeyId = 0x4E43;
        private const uint UnicodeTextFormat = 13;
        private const uint BitmapFormat = 2;
        private const uint FileDropFormat = 15;
        private const int MaxTextChars = 1000000;
        private const int MaxPngBytes = 16 * 1024 * 1024;
        private const long MaxPixels = 16000000;
        private const int MaxAttempts = 8;

        private readonly Window window;
        private readonly DispatcherTimer readTimer;
        private readonly DispatcherTimer writeTimer;
        private readonly byte[] sourceToken = Guid.NewGuid().ToByteArray();
        private HwndSource source;
        private IntPtr windowHandle;
        private bool started;
        private bool disposed;
        private bool listening;
        private bool paused;
        private uint observedSequence;
        private uint pendingSequence;
        private uint ownSequence;
        private bool haveOwnSequence;
        private bool writing;
        private int readAttempts;
        private int writeAttempts;
        private long writeGeneration;
        private uint pngFormat;
        private uint excludeFormat;
        private uint historyFormat;
        private uint sourceFormat;
        private Action pendingWrite;
        private Action<bool> writeCompleted;

        public event Action<ClipEntry> Captured;
        public event Action<string> Status;
        public event Action ToggleRequested;

        public ClipboardBridge(Window window)
        {
            if (window == null) throw new ArgumentNullException("window");
            this.window = window;
            readTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher);
            writeTimer = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher);
            readTimer.Tick += ReadTick;
            writeTimer.Tick += WriteTick;
        }

        public bool IsPaused
        {
            get { return paused; }
            set
            {
                window.Dispatcher.VerifyAccess();
                if (paused == value) return;
                paused = value;
                readTimer.Stop();
                // Resuming starts with the next clipboard change, never the paused content.
                observedSequence = Native.GetClipboardSequenceNumber();
                pendingSequence = observedSequence;
                readAttempts = 0;
            }
        }

        public bool HotkeyRegistered { get; private set; }
        public string HotkeyText { get; private set; }
        public bool IsListening { get { return listening && !disposed; } }

        public void Start()
        {
            window.Dispatcher.VerifyAccess();
            if (disposed || started) return;
            windowHandle = new WindowInteropHelper(window).Handle;
            if (windowHandle == IntPtr.Zero)
            {
                Report("Clipboard monitoring needs an initialized window.");
                return;
            }

            source = HwndSource.FromHwnd(windowHandle);
            if (source == null)
            {
                Report("Clipboard monitoring could not initialize. Restart ClipNest to retry.");
                return;
            }

            pngFormat = Native.RegisterClipboardFormat("PNG");
            excludeFormat = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
            historyFormat = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
            sourceFormat = Native.RegisterClipboardFormat("ClipNest.SourceInstance");
            observedSequence = Native.GetClipboardSequenceNumber();
            source.AddHook(WindowMessage);
            listening = Native.AddClipboardFormatListener(windowHandle);
            // MOD_NOREPEAT | MOD_CONTROL | MOD_ALT; virtual key V.
            HotkeyRegistered = Native.RegisterHotKey(windowHandle, HotkeyId, 0x4003, 0x56);
            if (HotkeyRegistered) HotkeyText = "Ctrl+Alt+V";
            else
            {
                HotkeyRegistered = Native.RegisterHotKey(windowHandle, HotkeyId, 0x4003, 0x48);
                if (HotkeyRegistered) HotkeyText = "Ctrl+Alt+H";
            }
            window.Closed += WindowClosed;
            started = true;
            if (!listening)
                Report("Clipboard monitoring could not start. Restart ClipNest to retry.");
            else if (!HotkeyRegistered)
                Report("Keyboard shortcuts are in use. Open ClipNest from its tray icon.");
            else if (HotkeyText != "Ctrl+Alt+V")
                Report("Ctrl+Alt+V is in use. Use Ctrl+Alt+H instead.");
        }

        public void Copy(ClipEntry entry, Action<bool> completed)
        {
            window.Dispatcher.VerifyAccess();
            if (entry == null || disposed)
            {
                if (completed != null) completed(false);
                return;
            }

            try
            {
                DataObject data = new DataObject();
                if (entry.Kind == ClipKind.Text)
                {
                    data.SetText(entry.Text, TextDataFormat.UnicodeText);
                }
                else if (entry.Kind == ClipKind.Files)
                {
                    StringCollection paths = new StringCollection();
                    paths.AddRange(entry.FilePaths);
                    data.SetFileDropList(paths);
                }
                else
                {
                    byte[] bytes = entry.PngData;
                    using (MemoryStream input = new MemoryStream(bytes, false))
                    {
                        BitmapFrame frame = BitmapFrame.Create(input,
                            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                            BitmapCacheOption.OnLoad);
                        frame.Freeze();
                        data.SetImage(frame);
                    }
                    // Keep PNG in addition to the Windows bitmap for applications that preserve alpha.
                    data.SetData("PNG", new MemoryStream(bytes, false));
                }
                // A private, random instance marker also suppresses later native format
                // conversions, whose clipboard sequence may differ from the initial write.
                data.SetData("ClipNest.SourceInstance", new MemoryStream(sourceToken, false), false);
                BeginWrite(delegate { Clipboard.SetDataObject(data, true); }, completed);
            }
            catch (Exception error)
            {
                if (IsFatal(error)) throw;
                Report("This item could not be copied to the clipboard.");
                if (completed != null) completed(false);
            }
        }

        public void ClearClipboard(Action<bool> completed)
        {
            window.Dispatcher.VerifyAccess();
            if (disposed)
            {
                if (completed != null) completed(false);
                return;
            }
            BeginWrite(delegate { Clipboard.Clear(); }, completed);
        }

        private void BeginWrite(Action operation, Action<bool> completed)
        {
            // There is only one pending command: the user's latest copy/clear wins.
            long generation = ++writeGeneration;
            Action<bool> cancelled = writeCompleted;
            writeTimer.Stop();
            pendingWrite = operation;
            writeCompleted = completed;
            writeAttempts = 0;
            if (cancelled != null) cancelled(false);
            if (disposed || generation != writeGeneration) return;
            writeTimer.Interval = TimeSpan.FromMilliseconds(1);
            writeTimer.Start();
        }

        private void WriteTick(object sender, EventArgs args)
        {
            writeTimer.Stop();
            if (disposed || pendingWrite == null) return;
            // OLE can pump window messages. Do not enter a second write inside the first.
            if (writing)
            {
                writeTimer.Interval = TimeSpan.FromMilliseconds(30);
                writeTimer.Start();
                return;
            }
            long generation = writeGeneration;
            Action operation = pendingWrite;
            bool succeeded = false;
            try
            {
                writing = true;
                operation();
                ownSequence = Native.GetClipboardSequenceNumber();
                haveOwnSequence = true;
                observedSequence = ownSequence;
                pendingSequence = ownSequence;
                readTimer.Stop();
                succeeded = true;
            }
            catch (Exception error)
            {
                if (IsFatal(error)) throw;
                if (generation != writeGeneration || disposed) return;
                if (IsClipboardBusy(error) && ++writeAttempts < MaxAttempts)
                {
                    writeTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(50 * writeAttempts, 250));
                    writeTimer.Start();
                    return;
                }
                Report("The clipboard is busy or unavailable. Please try copying again.");
            }
            finally
            {
                writing = false;
            }

            if (generation != writeGeneration || disposed) return;
            Action<bool> callback = writeCompleted;
            pendingWrite = null;
            writeCompleted = null;
            if (callback != null) callback(succeeded);
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (disposed) return IntPtr.Zero;
            if (message == ClipboardUpdate)
            {
                uint sequence = Native.GetClipboardSequenceNumber();
                if (sequence == observedSequence) return IntPtr.Zero;
                observedSequence = sequence;
                readTimer.Stop();
                if (paused || writing || (haveOwnSequence && sequence == ownSequence)) return IntPtr.Zero;
                pendingSequence = sequence;
                readAttempts = 0;
                readTimer.Interval = TimeSpan.FromMilliseconds(35);
                readTimer.Start();
            }
            else if (message == HotkeyMessage && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Action toggle = ToggleRequested;
                if (toggle != null) toggle();
            }
            return IntPtr.Zero;
        }

        private void ReadTick(object sender, EventArgs args)
        {
            readTimer.Stop();
            if (disposed || paused || writing) return;
            uint sequence = Native.GetClipboardSequenceNumber();
            if (haveOwnSequence && sequence == ownSequence) return;
            if (sequence != pendingSequence)
            {
                observedSequence = sequence;
                pendingSequence = sequence;
                readAttempts = 0;
            }

            ClipEntry entry = null;
            string notice = null;
            bool opened = Native.OpenClipboard(windowHandle);
            if (!opened)
            {
                if (++readAttempts < MaxAttempts)
                {
                    readTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(40 * readAttempts, 250));
                    readTimer.Start();
                }
                else Report("Clipboard busy. The next item you copy will be captured.");
                return;
            }

            try
            {
                if (!IsOwnData() && !IsExcluded()) entry = ReadEntry(out notice);
            }
            catch (Exception error)
            {
                if (IsFatal(error)) throw;
                notice = "This clipboard format could not be captured.";
            }
            finally
            {
                Native.CloseClipboard();
            }

            // Discard mixed snapshots if another application changed data during capture.
            uint after = Native.GetClipboardSequenceNumber();
            if (after != sequence)
            {
                observedSequence = after;
                pendingSequence = after;
                readAttempts = 0;
                readTimer.Interval = TimeSpan.FromMilliseconds(35);
                readTimer.Start();
                return;
            }
            if (notice != null) Report(notice);
            Action<ClipEntry> capture = Captured;
            if (entry != null && capture != null) capture(entry);
        }

        private bool IsExcluded()
        {
            if (excludeFormat != 0 && Native.IsClipboardFormatAvailable(excludeFormat)) return true;
            if (historyFormat == 0 || !Native.IsClipboardFormatAvailable(historyFormat)) return false;
            IntPtr memory = Native.GetClipboardData(historyFormat);
            if (memory == IntPtr.Zero) return true;
            ulong length = Native.GlobalSize(memory).ToUInt64();
            IntPtr pointer = Native.GlobalLock(memory);
            if (pointer == IntPtr.Zero) return true;
            try
            {
                if (length >= 4) return Marshal.ReadInt32(pointer) == 0;
                if (length >= 1) return Marshal.ReadByte(pointer) == 0;
                return true;
            }
            finally { Native.GlobalUnlock(memory); }
        }

        private bool IsOwnData()
        {
            if (sourceFormat == 0 || !Native.IsClipboardFormatAvailable(sourceFormat)) return false;
            IntPtr memory = Native.GetClipboardData(sourceFormat);
            if (memory == IntPtr.Zero || Native.GlobalSize(memory).ToUInt64() < (ulong)sourceToken.Length)
                return false;
            IntPtr pointer = Native.GlobalLock(memory);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                for (int i = 0; i < sourceToken.Length; i++)
                    if (Marshal.ReadByte(pointer, i) != sourceToken[i]) return false;
                return true;
            }
            finally { Native.GlobalUnlock(memory); }
        }

        private ClipEntry ReadEntry(out string notice)
        {
            notice = null;
            if (Native.IsClipboardFormatAvailable(FileDropFormat))
            {
                IntPtr drop = Native.GetClipboardData(FileDropFormat);
                uint count = Native.DragQueryFile(drop, UInt32.MaxValue, null, 0);
                if (count == 0) return null;
                if (count > 200)
                {
                    notice = "Skipped a file selection with more than 200 paths.";
                    return null;
                }
                string[] paths = new string[(int)count];
                long totalChars = 0;
                for (uint i = 0; i < count; i++)
                {
                    uint length = Native.DragQueryFile(drop, i, null, 0);
                    totalChars += length;
                    if (length == 0 || length > 32767 || totalChars > MaxTextChars)
                    {
                        notice = "Skipped an oversized file selection.";
                        return null;
                    }
                    StringBuilder path = new StringBuilder((int)length + 1);
                    Native.DragQueryFile(drop, i, path, length + 1);
                    paths[i] = path.ToString();
                }
                return ClipEntry.FromFiles(paths);
            }

            // Office apps often offer a bitmap alongside a text selection. Preserve editable
            // Unicode text in that case; real screenshots have no Unicode text payload.
            if (Native.IsClipboardFormatAvailable(UnicodeTextFormat))
                return ReadUnicodeText(out notice);

            if (pngFormat != 0 && Native.IsClipboardFormatAvailable(pngFormat))
            {
                byte[] png = ReadBytes(pngFormat, MaxPngBytes);
                if (png == null)
                {
                    notice = "Skipped an image larger than 16 MB.";
                    return null;
                }
                int width;
                int height;
                if (!PngSize(png, out width, out height))
                {
                    notice = "Skipped an invalid or oversized image (limit: 16 megapixels).";
                    return null;
                }
                // Verify the PNG before admitting it to the history.
                using (MemoryStream stream = new MemoryStream(png, false))
                {
                    BitmapFrame frame = BitmapFrame.Create(stream,
                        BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.OnLoad);
                    if (!SafeDimensions(frame.PixelWidth, frame.PixelHeight)) return null;
                    frame.Freeze();
                }
                return ClipEntry.FromImage(png, width, height);
            }

            if (Native.IsClipboardFormatAvailable(BitmapFormat))
            {
                // Windows may synthesize HBITMAP from a DIB. Check its header before
                // requesting that conversion, so oversized images never reach a decoder.
                if (!SafeDibBacking())
                {
                    notice = "Skipped an invalid or oversized image (limit: 16 megapixels).";
                    return null;
                }
                IntPtr bitmapHandle = Native.GetClipboardData(BitmapFormat);
                Native.BitmapInfo info;
                if (bitmapHandle == IntPtr.Zero || Native.GetObject(bitmapHandle,
                    Marshal.SizeOf(typeof(Native.BitmapInfo)), out info) == 0) return null;
                if (!SafeDimensions(info.Width, info.Height))
                {
                    notice = "Skipped an image larger than 16 megapixels.";
                    return null;
                }
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle,
                    IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (MemoryStream output = new MemoryStream())
                {
                    encoder.Save(output);
                    if (output.Length > MaxPngBytes)
                    {
                        notice = "Skipped an image larger than 16 MB.";
                        return null;
                    }
                    return ClipEntry.FromImage(output.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
                }
            }

            return null;
        }

        private static ClipEntry ReadUnicodeText(out string notice)
        {
            notice = null;
            IntPtr memory = Native.GetClipboardData(UnicodeTextFormat);
            if (memory == IntPtr.Zero) return null;
            ulong length = Native.GlobalSize(memory).ToUInt64();
            // HGLOBAL allocations may include a few padding bytes after their terminator.
            if (length > ((ulong)MaxTextChars + 1) * 2 + 64)
            {
                notice = "Skipped text longer than 1,000,000 characters.";
                return null;
            }
            if (length < 2) return null;
            IntPtr pointer = Native.GlobalLock(memory);
            if (pointer == IntPtr.Zero) return null;
            string text;
            try { text = Marshal.PtrToStringUni(pointer, (int)Math.Min(length / 2, (ulong)MaxTextChars + 1)); }
            finally { Native.GlobalUnlock(memory); }
            int terminator = text.IndexOf('\0');
            if (terminator >= 0) text = text.Substring(0, terminator);
            if (text.Length == 0) return null;
            if (text.Length > MaxTextChars)
            {
                notice = "Skipped text longer than 1,000,000 characters.";
                return null;
            }
            return ClipEntry.FromText(text);
        }

        private static byte[] ReadBytes(uint format, int maximum)
        {
            IntPtr memory = Native.GetClipboardData(format);
            if (memory == IntPtr.Zero) return null;
            ulong length = Native.GlobalSize(memory).ToUInt64();
            if (length == 0 || length > (ulong)maximum) return null;
            IntPtr pointer = Native.GlobalLock(memory);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                byte[] bytes = new byte[(int)length];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { Native.GlobalUnlock(memory); }
        }

        private static bool PngSize(byte[] png, out int width, out int height)
        {
            width = 0;
            height = 0;
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (png.Length < 33) return false;
            for (int i = 0; i < signature.Length; i++) if (png[i] != signature[i]) return false;
            if (png[12] != 73 || png[13] != 72 || png[14] != 68 || png[15] != 82) return false;
            width = BigEndianInt(png, 16);
            height = BigEndianInt(png, 20);
            return SafeDimensions(width, height);
        }

        private static int BigEndianInt(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16)
                | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static bool SafeDimensions(int width, int height)
        {
            return width > 0 && height > 0 && width <= 32768 && height <= 32768
                && (long)width * height <= MaxPixels;
        }

        private static bool SafeDibBacking()
        {
            uint format = 0;
            uint candidate = 0;
            // Synthesized formats follow the source format in Windows' enumeration.
            // If HBITMAP is first, inspect it directly instead of synthesizing a DIB.
            while ((candidate = Native.EnumClipboardFormats(candidate)) != 0)
            {
                if (candidate == BitmapFormat) return true;
                if (candidate == 8 || candidate == 17) { format = candidate; break; }
            }
            if (format == 0) return true;
            IntPtr memory = Native.GetClipboardData(format);
            if (memory == IntPtr.Zero) return false;
            ulong length = Native.GlobalSize(memory).ToUInt64();
            if (length < 12 || length > (ulong)(MaxPixels * 4 + 1024 * 1024)) return false;
            IntPtr pointer = Native.GlobalLock(memory);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                int headerSize = Marshal.ReadInt32(pointer);
                if (headerSize == 12)
                {
                    int width = (ushort)Marshal.ReadInt16(pointer, 4);
                    int height = (ushort)Marshal.ReadInt16(pointer, 6);
                    return SafeDimensions(width, height);
                }
                if (headerSize < 40 || (ulong)headerSize > length) return false;
                int bitmapWidth = Marshal.ReadInt32(pointer, 4);
                int bitmapHeight = Marshal.ReadInt32(pointer, 8);
                if (bitmapHeight == Int32.MinValue) return false;
                return SafeDimensions(bitmapWidth, Math.Abs(bitmapHeight));
            }
            finally { Native.GlobalUnlock(memory); }
        }

        private static bool IsClipboardBusy(Exception error)
        {
            return error is ExternalException || error is InvalidOperationException;
        }

        private static bool IsFatal(Exception error)
        {
            return error is OutOfMemoryException || error is StackOverflowException
                || error is AccessViolationException || error is System.Threading.ThreadAbortException;
        }

        private void Report(string text)
        {
            Action<string> status = Status;
            if (!disposed && status != null) status(text);
        }

        private void WindowClosed(object sender, EventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            window.Dispatcher.VerifyAccess();
            disposed = true;
            writeGeneration++;
            readTimer.Stop();
            writeTimer.Stop();
            readTimer.Tick -= ReadTick;
            writeTimer.Tick -= WriteTick;
            window.Closed -= WindowClosed;
            if (HotkeyRegistered)
            {
                Native.UnregisterHotKey(windowHandle, HotkeyId);
                HotkeyRegistered = false;
            }
            if (listening) Native.RemoveClipboardFormatListener(windowHandle);
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowMessage);
            pendingWrite = null;
            Action<bool> callback = writeCompleted;
            writeCompleted = null;
            if (callback != null) callback(false);
            Captured = null;
            Status = null;
            ToggleRequested = null;
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct BitmapInfo
            {
                internal int Type;
                internal int Width;
                internal int Height;
                internal int WidthBytes;
                internal ushort Planes;
                internal ushort BitsPixel;
                internal IntPtr Bits;
            }

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
            [DllImport("user32.dll")]
            internal static extern uint GetClipboardSequenceNumber();
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool OpenClipboard(IntPtr hwnd);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseClipboard();
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsClipboardFormatAvailable(uint format);
            [DllImport("user32.dll", SetLastError = true)]
            internal static extern uint EnumClipboardFormats(uint format);
            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr GetClipboardData(uint format);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern uint RegisterClipboardFormat(string format);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr GlobalLock(IntPtr handle);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GlobalUnlock(IntPtr handle);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern UIntPtr GlobalSize(IntPtr handle);
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            internal static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder fileName, uint characters);
            [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
            internal static extern int GetObject(IntPtr handle, int size, out BitmapInfo bitmap);
        }
    }
}

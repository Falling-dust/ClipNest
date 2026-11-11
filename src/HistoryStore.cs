using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClipNest
{
    public enum ClipKind { Text, Image, Files }

    /// <summary>A clipboard payload. Contents are kept in memory only.</summary>
    public sealed class ClipEntry
    {
        public const int MaxTextLength = 1000000;
        public const int MaxImageBytes = 16 * 1024 * 1024;
        public const int MaxFileCount = 200;
        public const int MaxFilePathLength = 32767;

        private readonly byte[] pngData;
        private readonly string[] filePaths;
        private readonly int imageWidth;
        private readonly int imageHeight;

        public Guid Id { get; private set; }
        public ClipKind Kind { get; private set; }
        public string Text { get; private set; }
        public byte[] PngData { get { return pngData == null ? null : (byte[])pngData.Clone(); } }
        public string[] FilePaths { get { return filePaths == null ? null : (string[])filePaths.Clone(); } }
        public DateTime CapturedAt { get; private set; }
        public bool IsPinned { get; internal set; }
        public long PayloadBytes { get; private set; }

        private ClipEntry(ClipKind kind, string text, byte[] png, string[] paths,
            int width, int height, long size)
        {
            Id = Guid.NewGuid();
            Kind = kind;
            Text = text;
            pngData = png;
            filePaths = paths;
            imageWidth = width;
            imageHeight = height;
            CapturedAt = DateTime.Now;
            PayloadBytes = size;
        }

        public static ClipEntry FromText(string text)
        {
            ValidateText(text);
            return new ClipEntry(ClipKind.Text, text, null, null, 0, 0, (long)text.Length * 2);
        }

        public static ClipEntry FromImage(byte[] png, int width, int height)
        {
            if (png == null || png.Length == 0 || png.Length > MaxImageBytes)
                throw new ArgumentException("An image must contain between 1 byte and 16 MiB of PNG data.", "png");
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Image dimensions must be positive.");
            return new ClipEntry(ClipKind.Image, null, (byte[])png.Clone(), null,
                width, height, png.Length);
        }

        public static ClipEntry FromFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0 || paths.Length > MaxFileCount)
                throw new ArgumentException("Choose between 1 and 200 file paths.", "paths");
            long size = 0;
            foreach (string path in paths)
            {
                if (String.IsNullOrWhiteSpace(path) || path.Length > MaxFilePathLength || path.IndexOf('\0') >= 0)
                    throw new ArgumentException("File paths must be nonempty and at most 32,767 characters.", "paths");
                size += (long)path.Length * 2;
            }
            return new ClipEntry(ClipKind.Files, null, null, (string[])paths.Clone(), 0, 0, size);
        }

        private static void ValidateText(string text)
        {
            if (String.IsNullOrEmpty(text) || text.Length > MaxTextLength)
                throw new ArgumentException("Text must contain between 1 and 1,000,000 characters.", "text");
        }

        internal ClipEntry WithText(string text)
        {
            if (text == null || text.Length > MaxTextLength)
                throw new ArgumentException("Edited text must be non-null and at most 1,000,000 characters.", "text");
            ClipEntry edited = new ClipEntry(ClipKind.Text, text, null, null, 0, 0, (long)text.Length * 2);
            edited.Id = Id;
            edited.CapturedAt = CapturedAt;
            edited.IsPinned = IsPinned;
            return edited;
        }

        public string Preview
        {
            get
            {
                if (Kind == ClipKind.Image)
                    return "Image  " + imageWidth.ToString(CultureInfo.CurrentCulture) + " \u00d7 " + imageHeight.ToString(CultureInfo.CurrentCulture);
                if (Kind == ClipKind.Files)
                {
                    string path = filePaths[0].TrimEnd('\\', '/');
                    int separator = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
                    string name = separator < 0 ? path : path.Substring(separator + 1);
                    if (name.Length == 0) name = filePaths[0];
                    return Collapse(name + (filePaths.Length > 1 ? "  +" + (filePaths.Length - 1).ToString(CultureInfo.CurrentCulture) + " more" : ""));
                }
                return Text.Length == 0 ? "(Empty text)" : Collapse(Text);
            }
        }

        public string Detail
        {
            get
            {
                if (Kind == ClipKind.Text)
                    return Text.Length.ToString("N0", CultureInfo.CurrentCulture) + (Text.Length == 1 ? " character" : " characters");
                if (Kind == ClipKind.Files)
                    return filePaths.Length.ToString(CultureInfo.CurrentCulture) + (filePaths.Length == 1 ? " file / folder" : " files / folders");
                return imageWidth.ToString(CultureInfo.CurrentCulture) + " \u00d7 " + imageHeight.ToString(CultureInfo.CurrentCulture) + " px";
            }
        }

        private static string Collapse(string text)
        {
            StringBuilder result = new StringBuilder(112);
            bool space = false;
            int position = 0;
            for (; position < text.Length && result.Length < 110; position++)
            {
                char ch = text[position];
                if (Char.IsWhiteSpace(ch))
                {
                    if (result.Length > 0) space = true;
                    continue;
                }
                if (space)
                {
                    result.Append(' ');
                    space = false;
                    if (result.Length == 110) break;
                }
                result.Append(ch);
            }
            if (result.Length > 0 && Char.IsHighSurrogate(result[result.Length - 1]))
                result.Length--;
            if (position < text.Length) result.Append('\u2026');
            string preview = result.ToString().TrimEnd();
            return preview.Length == 0 ? "(Whitespace)" : preview;
        }

        internal bool SamePayload(ClipEntry other)
        {
            if (other == null || Kind != other.Kind || PayloadBytes != other.PayloadBytes) return false;
            if (Kind == ClipKind.Text) return String.Equals(Text, other.Text, StringComparison.Ordinal);
            if (Kind == ClipKind.Image)
            {
                if (imageWidth != other.imageWidth || imageHeight != other.imageHeight || pngData.Length != other.pngData.Length) return false;
                for (int i = 0; i < pngData.Length; i++)
                    if (pngData[i] != other.pngData[i]) return false;
                return true;
            }
            if (filePaths.Length != other.filePaths.Length) return false;
            for (int i = 0; i < filePaths.Length; i++)
                if (!String.Equals(filePaths[i], other.filePaths[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    /// <summary>Use on the UI thread. Pinned items count toward the same 20-entry limit.</summary>
    public sealed class HistoryStore
    {
        public const int Capacity = 20;
        public const long MaxTotalBytes = 64L * 1024 * 1024;
        private readonly List<ClipEntry> entries = new List<ClipEntry>();

        public IList<ClipEntry> Entries { get { return new List<ClipEntry>(entries).AsReadOnly(); } }
        public long TotalBytes { get; private set; }

        public ClipEntry Add(ClipEntry entry)
        {
            if (entry == null || entry.PayloadBytes <= 0 || entry.PayloadBytes > MaxTotalBytes) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].SamePayload(entry))
                {
                    ClipEntry existing = entries[i];
                    entries.RemoveAt(i);
                    entries.Insert(0, existing);
                    return existing;
                }
            }

            // Plan every eviction first. Rejection must leave the history intact.
            long prospectiveBytes = TotalBytes + entry.PayloadBytes;
            int prospectiveCount = entries.Count + 1;
            List<int> evictions = new List<int>();
            for (int i = entries.Count - 1;
                i >= 0 && (prospectiveBytes > MaxTotalBytes || prospectiveCount > Capacity); i--)
            {
                if (entries[i].IsPinned) continue;
                evictions.Add(i);
                prospectiveBytes -= entries[i].PayloadBytes;
                prospectiveCount--;
            }
            if (prospectiveBytes > MaxTotalBytes || prospectiveCount > Capacity) return null;
            foreach (int index in evictions) entries.RemoveAt(index);
            entries.Insert(0, entry);
            TotalBytes = prospectiveBytes;
            return entry;
        }

        public ClipEntry EditText(Guid id, string text)
        {
            int index = FindIndex(id);
            if (index < 0 || entries[index].Kind != ClipKind.Text || text == null || text.Length > ClipEntry.MaxTextLength)
                return null;
            ClipEntry oldEntry = entries[index];
            ClipEntry edited = oldEntry.WithText(text);
            long prospectiveBytes = TotalBytes - oldEntry.PayloadBytes + edited.PayloadBytes;
            if (prospectiveBytes > MaxTotalBytes) return null;
            entries[index] = edited;
            TotalBytes = prospectiveBytes;
            return edited;
        }

        /// <returns>True when the entry exists and its pin was toggled.</returns>
        public bool TogglePin(Guid id)
        {
            int index = FindIndex(id);
            if (index < 0) return false;
            entries[index].IsPinned = !entries[index].IsPinned;
            return true;
        }

        public bool Remove(Guid id)
        {
            int index = FindIndex(id);
            if (index < 0) return false;
            TotalBytes -= entries[index].PayloadBytes;
            entries.RemoveAt(index);
            return true;
        }

        public void Clear()
        {
            entries.Clear();
            TotalBytes = 0;
        }

        private int FindIndex(Guid id)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Id == id) return i;
            return -1;
        }
    }
}

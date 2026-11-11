using System;
using System.Collections.Generic;
using ClipNest;

internal static class HistoryStoreTests
{
    private static int passed;

    public static int Main()
    {
        try
        {
            Test("newest first, strict cap, oldest eviction", Capacity);
            Test("pinned entries survive eviction", PinEviction);
            Test("all pinned rejects safely; duplicate still works", AllPinned);
            Test("exact deduplication and immutable payloads", DuplicatesAndOwnership);
            Test("edit preserves identity, pin, time, and position", Edit);
            Test("empty edits retain identity, pin, and position", EmptyEdit);
            Test("remove, clear, and readonly snapshots", RemoveAndClear);
            Test("Unicode, whitespace, and bounded preview", Unicode);
            Test("factory input limits", Validation);
            Test("total memory eviction and atomic rejection", MemoryBudget);
            Test("edit budget rejection is nondestructive", EditBudget);
            Console.WriteLine("PASS: " + passed + " history tests.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void Test(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected ArgumentException");
    }

    private static void Capacity()
    {
        HistoryStore store = new HistoryStore();
        for (int i = 0; i < 25; i++) store.Add(ClipEntry.FromText("item " + i));
        Assert(store.Entries.Count == 20, "History exceeded 20");
        Assert(store.Entries[0].Text == "item 24" && store.Entries[19].Text == "item 5", "Wrong eviction order");
        long bytes = 0;
        foreach (ClipEntry entry in store.Entries) bytes += entry.PayloadBytes;
        Assert(store.TotalBytes == bytes, "Byte accounting after eviction");
    }

    private static void PinEviction()
    {
        HistoryStore store = new HistoryStore();
        ClipEntry pinned = store.Add(ClipEntry.FromText("keep"));
        Assert(store.TogglePin(pinned.Id), "Could not pin");
        for (int i = 0; i < 30; i++) store.Add(ClipEntry.FromText("item " + i));
        Assert(store.Entries.Count == 20 && store.Entries[19].Id == pinned.Id, "Pin lost on eviction");
        Assert(store.Entries[18].Text == "item 11", "Wrong unpinned eviction");
        Assert(!store.TogglePin(Guid.NewGuid()), "Unknown pin should fail");
    }

    private static void AllPinned()
    {
        HistoryStore store = new HistoryStore();
        for (int i = 0; i < 20; i++)
        {
            ClipEntry entry = store.Add(ClipEntry.FromText("item " + i));
            store.TogglePin(entry.Id);
        }
        ClipEntry first = store.Entries[0];
        long size = store.TotalBytes;
        Assert(store.Add(ClipEntry.FromText("overflow")) == null, "All-pin insert should reject");
        Assert(store.Entries.Count == 20 && store.Entries[0] == first && store.TotalBytes == size, "Rejected insert mutated history");
        ClipEntry duplicate = store.Add(ClipEntry.FromText("item 0"));
        Assert(duplicate != null && duplicate.IsPinned && store.Entries[0] == duplicate, "Pinned duplicate rejected");
        store.TogglePin(duplicate.Id);
        Assert(store.Add(ClipEntry.FromText("new")) != null && store.Entries.Count == 20, "Unpin should allow replacement");
    }

    private static void DuplicatesAndOwnership()
    {
        HistoryStore store = new HistoryStore();
        ClipEntry first = store.Add(ClipEntry.FromText("Case\n"));
        store.TogglePin(first.Id);
        store.Add(ClipEntry.FromText("case\n"));
        Assert(store.Entries.Count == 2, "Case was incorrectly normalized");
        Assert(store.Add(ClipEntry.FromText("Case\n")) == first && store.Entries.Count == 2 && first.IsPinned, "Duplicate identity or pin lost");
        byte[] bytes = new byte[] { 1, 2, 3, 4 };
        ClipEntry image = ClipEntry.FromImage(bytes, 2, 2);
        bytes[0] = 99;
        image.PngData[1] = 99;
        Assert(image.PngData[0] == 1 && image.PngData[1] == 2, "Image array escaped");
        store.Add(image);
        Assert(store.Add(ClipEntry.FromImage(new byte[] { 1, 2, 3, 4 }, 2, 2)) == image, "Images did not deduplicate");
        string[] paths = new string[] { "C:\\a.txt", "C:\\b.txt" };
        ClipEntry files = ClipEntry.FromFiles(paths);
        paths[0] = "changed";
        files.FilePaths[1] = "changed";
        Assert(files.FilePaths[0] == "C:\\a.txt" && files.FilePaths[1] == "C:\\b.txt", "Path array escaped");
        store.Add(files);
        Assert(store.Add(ClipEntry.FromFiles(new string[] { "C:\\a.txt", "C:\\b.txt" })) == files, "Files did not deduplicate");
        Assert(store.Add(null) == null, "Null add should reject");
    }

    private static void Edit()
    {
        HistoryStore store = new HistoryStore();
        ClipEntry original = store.Add(ClipEntry.FromText("original"));
        store.TogglePin(original.Id);
        ClipEntry newer = store.Add(ClipEntry.FromText("newer"));
        ClipEntry edited = store.EditText(original.Id, "edited \u4e2d\u6587 \ud83d\udc4b");
        Assert(edited != null && edited.Id == original.Id && edited.IsPinned && edited.CapturedAt == original.CapturedAt, "Edit lost metadata");
        Assert(store.Entries[0] == newer && store.Entries[1] == edited, "Edit reordered history");
        Assert(original.Text == "original" && edited.Text != original.Text, "Original entry payload mutated");
        Assert(store.TotalBytes == newer.PayloadBytes + edited.PayloadBytes, "Edit byte accounting");
        Assert(store.EditText(original.Id, null) == null && store.EditText(Guid.NewGuid(), "x") == null, "Invalid edit accepted");
        ClipEntry image = store.Add(ClipEntry.FromImage(new byte[] { 1 }, 1, 1));
        Assert(store.EditText(image.Id, "text") == null, "Image converted into text");
    }

    private static void EmptyEdit()
    {
        HistoryStore store = new HistoryStore();
        ClipEntry original = store.Add(ClipEntry.FromText("clear this"));
        store.TogglePin(original.Id);
        ClipEntry newer = store.Add(ClipEntry.FromText("newer"));
        ClipEntry empty = store.EditText(original.Id, "");
        Assert(empty != null && empty.Id == original.Id && empty.CapturedAt == original.CapturedAt && empty.IsPinned, "Empty edit lost metadata");
        Assert(empty.Text == "" && empty.PayloadBytes == 0 && empty.Preview == "(Empty text)", "Empty edit payload wrong");
        Assert(store.Entries.Count == 2 && store.Entries[0] == newer && store.Entries[1] == empty, "Empty edit changed position or count");
        Assert(store.TotalBytes == newer.PayloadBytes, "Empty edit byte accounting");
        Assert(store.EditText(empty.Id, null) == null && store.Entries[1] == empty, "Null edit mutated empty item");
        Assert(store.EditText(empty.Id, "restored").Id == original.Id, "Could not restore cleared text");
        Throws(delegate { ClipEntry.FromText(""); });
    }

    private static void RemoveAndClear()
    {
        HistoryStore store = new HistoryStore();
        ClipEntry entry = store.Add(ClipEntry.FromText("hello"));
        IList<ClipEntry> snapshot = store.Entries;
        Assert(snapshot.IsReadOnly, "Snapshot is mutable");
        Assert(store.Remove(entry.Id) && !store.Remove(entry.Id), "Remove result wrong");
        Assert(store.TotalBytes == 0 && store.Entries.Count == 0 && snapshot.Count == 1, "Remove or snapshot wrong");
        entry = store.Add(ClipEntry.FromText("pinned"));
        store.TogglePin(entry.Id);
        store.Clear();
        Assert(store.Entries.Count == 0 && store.TotalBytes == 0, "Clear retained pinned content");
    }

    private static void Unicode()
    {
        ClipEntry entry = ClipEntry.FromText("  Hello\r\n\t\u4e16\u754c  \ud83d\udc4b  ");
        Assert(entry.Text == "  Hello\r\n\t\u4e16\u754c  \ud83d\udc4b  ", "Unicode text changed");
        Assert(entry.Preview == "Hello \u4e16\u754c \ud83d\udc4b", "Whitespace preview incorrect");
        Assert(ClipEntry.FromText(" \r\n\t").Preview == "(Whitespace)", "Whitespace preview blank");
        string preview = ClipEntry.FromText(new string('a', 109) + "\ud83d\udc4b" + "tail").Preview;
        Assert(preview.Length <= 111 && !Char.IsHighSurrogate(preview[preview.Length - 2]), "Preview split a surrogate");
    }

    private static void Validation()
    {
        Throws(delegate { ClipEntry.FromText(null); });
        Throws(delegate { ClipEntry.FromText(""); });
        Throws(delegate { ClipEntry.FromText(new string('x', ClipEntry.MaxTextLength + 1)); });
        Assert(ClipEntry.FromText(new string('x', ClipEntry.MaxTextLength)).PayloadBytes == 2L * ClipEntry.MaxTextLength, "Text limit excluded valid boundary");
        Throws(delegate { ClipEntry.FromImage(null, 1, 1); });
        Throws(delegate { ClipEntry.FromImage(new byte[0], 1, 1); });
        Throws(delegate { ClipEntry.FromImage(new byte[] { 1 }, 0, 1); });
        Throws(delegate { ClipEntry.FromImage(new byte[ClipEntry.MaxImageBytes + 1], 1, 1); });
        Throws(delegate { ClipEntry.FromFiles(new string[0]); });
        Throws(delegate { ClipEntry.FromFiles(new string[ClipEntry.MaxFileCount + 1]); });
        Throws(delegate { ClipEntry.FromFiles(new string[] { " " }); });
        Throws(delegate { ClipEntry.FromFiles(new string[] { "x\0y" }); });
    }

    private static ClipEntry LargeImage(byte seed)
    {
        byte[] data = new byte[ClipEntry.MaxImageBytes];
        data[0] = seed;
        return ClipEntry.FromImage(data, 2048, 2048);
    }

    private static void MemoryBudget()
    {
        HistoryStore store = new HistoryStore();
        for (byte i = 1; i <= 4; i++) store.Add(LargeImage(i));
        Assert(store.TotalBytes == HistoryStore.MaxTotalBytes && store.Entries.Count == 4, "Boundary budget incorrect");
        ClipEntry oldest = store.Entries[3];
        store.Add(LargeImage(5));
        Assert(store.Entries.Count == 4 && store.TotalBytes == HistoryStore.MaxTotalBytes && !store.Remove(oldest.Id), "Memory eviction failed");
        foreach (ClipEntry item in store.Entries) store.TogglePin(item.Id);
        Assert(store.Add(ClipEntry.FromText("x")) == null && store.Entries.Count == 4, "Pinned memory limit ignored");

        // Candidate cannot fit even after the unpinned small entry is removed.
        store.Clear();
        for (byte i = 1; i <= 3; i++)
        {
            ClipEntry entry = store.Add(LargeImage(i));
            store.TogglePin(entry.Id);
        }
        byte[] almostLarge = new byte[ClipEntry.MaxImageBytes - 4];
        ClipEntry pinned = store.Add(ClipEntry.FromImage(almostLarge, 1024, 1024));
        store.TogglePin(pinned.Id);
        ClipEntry small = store.Add(ClipEntry.FromText("xx"));
        long before = store.TotalBytes;
        Assert(store.Add(LargeImage(9)) == null, "Impossible candidate accepted");
        Assert(store.TotalBytes == before && store.Entries.Count == 5 && store.Entries[0] == small, "Rejected insert partially evicted entries");
    }

    private static void EditBudget()
    {
        HistoryStore store = new HistoryStore();
        for (byte i = 1; i <= 3; i++) store.Add(LargeImage(i));
        store.Add(ClipEntry.FromImage(new byte[ClipEntry.MaxImageBytes - 4], 2048, 2048));
        ClipEntry text = store.Add(ClipEntry.FromText("xx"));
        Assert(store.TotalBytes == HistoryStore.MaxTotalBytes, "Fixture budget wrong");
        Assert(store.EditText(text.Id, "xxx") == null, "Oversize edit accepted");
        Assert(store.Entries[0] == text && store.Entries.Count == 5 && store.TotalBytes == HistoryStore.MaxTotalBytes, "Failed edit changed history");
        Assert(store.EditText(text.Id, "x") != null && store.TotalBytes == HistoryStore.MaxTotalBytes - 2, "Shrinking edit failed");
    }
}

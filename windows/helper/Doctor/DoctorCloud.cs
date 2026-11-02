using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AIStack.Doctor;

/// Cloud Files API (cldapi.dll): the placeholders OneDrive, Google Drive, Dropbox and iCloud for Windows keep
/// under their sync roots. Dehydrating an in-sync placeholder frees its local bytes; the provider's copy stays,
/// and opening or hydrating the file downloads it again.
public static class DoctorCloud
{
    const uint FILE_ATTRIBUTE_PINNED = 0x80000, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    /// IO_REPARSE_TAG_CLOUD and its CLOUD_1..F variants (0x9000x01A).
    public static bool IsCloudTag(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;

    /// Reparse tag of a file or folder (0 if it is not a reparse point), read without opening it.
    public static uint ReparseTag(string path)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var h = FindFirstFileW(path, out var fd);
        if (h == new IntPtr(-1)) return 0;
        FindClose(h);
        return (fd.dwFileAttributes & 0x400) != 0 ? fd.dwReserved0 : 0;
    }

    public record State(bool InSync, bool Pinned, bool Dehydrated);

    /// True if the folder is a sync root or inside one. Placeholders are disguised as plain files to most processes
    /// (no reparse attribute), so the sync root, not the attributes, says where to look.
    public static bool InSyncRoot(string dir)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var buf = new byte[512];
        try { return CfGetSyncRootInfoByPath(dir, 0 /* CF_SYNC_ROOT_INFO_BASIC */, buf, buf.Length, out _) >= 0; }
        catch (DllNotFoundException) { return false; }
    }

    /// null = not a cloud placeholder. Reads placeholder metadata only, so it never triggers a download.
    public static State? PlaceholderState(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var h = Open(path, FILE_READ_ATTRIBUTES, share: 7);
        if (h.IsInvalid) return null;
        var buf = new byte[4096];   // CF_PLACEHOLDER_STANDARD_INFO plus the provider's file identity
        if (CfGetPlaceholderInfo(h, 1 /* CF_PLACEHOLDER_INFO_STANDARD */, buf, buf.Length, out _) < 0) return null;
        long onDisk = BitConverter.ToInt64(buf, 0);
        int pin = BitConverter.ToInt32(buf, 32), inSync = BitConverter.ToInt32(buf, 36);
        var attrs = (uint)File.GetAttributes(path);
        var dehydrated = onDisk < new FileInfo(path).Length || (attrs & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0;
        return new State(inSync == 1, pin == 1 /* CF_PIN_STATE_PINNED */ || (attrs & FILE_ATTRIBUTE_PINNED) != 0, dehydrated);
    }

    /// Frees the local bytes. The handle comes from CfOpenFileWithOplock (exclusive, write access): it fails if the
    /// file is open elsewhere, and the oplock lets the cloud filter purge the data. A plain share-0 CreateFile handle
    /// made CfDehydratePlaceholder block forever on Windows CI. Bounded anyway: false = still running after
    /// `timeoutSec`; the file is then either local or evicted (both lossless) and the journal's recover settles it.
    public static bool Dehydrate(string path, int timeoutSec = 60)
    {
        Check(CfOpenFileWithOplock(path, 1 | 2 /* CF_OPEN_FILE_FLAG_EXCLUSIVE | WRITE_ACCESS */, out var h), "CfOpenFileWithOplock");
        var work = Task.Run(() => CfDehydratePlaceholder(h, 0, -1, 0, IntPtr.Zero));
        if (!work.Wait(timeoutSec * 1000)) return false;   // the handle leaks with the stuck call; the process exits soon
        CfCloseHandle(h);
        Check(work.Result, "CfDehydratePlaceholder");
        return true;
    }

    static void Check(int hr, string what) { if (hr < 0) throw new IOException($"{what}: 0x{hr:X8}"); }

    /// Downloads the bytes again through the provider (OneDrive etc. must be running).
    public static void Hydrate(string path)
    {
        using var h = Open(path, FILE_READ_DATA | FILE_READ_ATTRIBUTES, share: 7);
        if (h.IsInvalid) throw new IOException($"open: {Marshal.GetLastWin32Error()}");
        var hr = CfHydratePlaceholder(h, 0, -1, 0, IntPtr.Zero);
        if (hr < 0) throw new IOException($"CfHydratePlaceholder: 0x{hr:X8}");
    }

    const uint FILE_READ_DATA = 1, FILE_WRITE_DATA = 2, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;

    static SafeFileHandle Open(string path, uint access, uint share) =>
        CreateFileW(path, access, share, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02000000 /* BACKUP_SEMANTICS */, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileW(string name, out WIN32_FIND_DATAW data);
    [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    static extern int CfGetSyncRootInfoByPath(string path, int infoClass, byte[] info, int length, out int returned);
    [DllImport("cldapi.dll")]
    static extern int CfGetPlaceholderInfo(SafeFileHandle h, int infoClass, byte[] info, int length, out int returned);
    [DllImport("cldapi.dll")]
    static extern int CfDehydratePlaceholder(IntPtr h, long offset, long length, int flags, IntPtr overlapped);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    static extern int CfOpenFileWithOplock(string path, int flags, out IntPtr protectedHandle);
    [DllImport("cldapi.dll")]
    static extern void CfCloseHandle(IntPtr protectedHandle);
    [DllImport("cldapi.dll")]
    static extern int CfHydratePlaceholder(SafeFileHandle h, long offset, long length, int flags, IntPtr overlapped);
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AIStack.Doctor;

/// Cloud Files API (cldapi.dll): the placeholders OneDrive, Google Drive, Dropbox and iCloud for Windows keep
/// under their sync roots. Dehydrating an in-sync placeholder frees its local bytes; the provider's copy stays,
/// and opening or hydrating the file downloads it again.
public static class DoctorCloud
{
    const uint FILE_ATTRIBUTE_PINNED = 0x80000, FILE_ATTRIBUTE_UNPINNED = 0x100000, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    /// By default the cloud filter disguises placeholders as plain files (no reparse attribute, no tag); the doctor
    /// needs to see them to tell evictable files from links. Process-wide; call once at startup.
    public static void ExposePlaceholders()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { RtlSetProcessPlaceholderCompatibilityMode(2 /* PHCM_EXPOSE_PLACEHOLDERS */); } catch { }
        try { RtlSetThreadPlaceholderCompatibilityMode(2); } catch { }
    }

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

    /// null = not a cloud placeholder. Reads attributes only, so it never triggers a download.
    public static State? PlaceholderState(string path)
    {
        if (!OperatingSystem.IsWindows() || !IsCloudTag(ReparseTag(path))) return null;
        using var h = Open(path, FILE_READ_ATTRIBUTES, share: 7);
        if (h.IsInvalid) return null;
        var buf = new byte[64];
        if (CfGetPlaceholderInfo(h, 0 /* CF_PLACEHOLDER_INFO_BASIC */, buf, buf.Length, out _) < 0) return null;
        int pin = BitConverter.ToInt32(buf, 0), inSync = BitConverter.ToInt32(buf, 4);
        var attrs = (uint)File.GetAttributes(path);
        var dehydrated = (attrs & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | (uint)FileAttributes.Offline)) != 0 || DoctorFS.Allocated(path) == 0 && new FileInfo(path).Length > 0;
        return new State(inSync == 1, pin == 1 /* CF_PIN_STATE_PINNED */ || (attrs & FILE_ATTRIBUTE_PINNED) != 0, dehydrated);
    }

    /// Frees the local bytes. Needs the file in sync and not open elsewhere (the exclusive handle checks that).
    public static void Dehydrate(string path)
    {
        using var h = Open(path, FILE_WRITE_ATTRIBUTES | FILE_WRITE_DATA, share: 0);
        if (h.IsInvalid) throw new IOException($"open: {Marshal.GetLastWin32Error()}");
        var hr = CfDehydratePlaceholder(h, 0, -1, 0, IntPtr.Zero);
        if (hr < 0) throw new IOException($"CfDehydratePlaceholder: 0x{hr:X8}");
    }

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
    [DllImport("ntdll.dll")] static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);
    [DllImport("ntdll.dll")] static extern sbyte RtlSetThreadPlaceholderCompatibilityMode(sbyte mode);
    [DllImport("cldapi.dll")]
    static extern int CfGetPlaceholderInfo(SafeFileHandle h, int infoClass, byte[] info, int length, out int returned);
    [DllImport("cldapi.dll")]
    static extern int CfDehydratePlaceholder(SafeFileHandle h, long offset, long length, int flags, IntPtr overlapped);
    [DllImport("cldapi.dll")]
    static extern int CfHydratePlaceholder(SafeFileHandle h, long offset, long length, int flags, IntPtr overlapped);
}

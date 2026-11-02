// Test-only Cloud Files sync provider for the Laptop Doctor safety suite (Windows CI).
//
//     CloudTestProvider <syncRoot> <serverDir>
//
// Copies every file under <syncRoot> to <serverDir> (the "cloud"), registers <syncRoot> as a sync root, converts
// the files into in-sync placeholders, then serves downloads (FETCH_DATA) from <serverDir> until it is killed.
// Prints "ready" once serving, or "unsupported: <why>" and exits 3 if the machine can't host a sync root.
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

static class Program
{
    static string root = "", server = "";
    static CfCallback? keep;   // the delegate must outlive the connection

    static int Main(string[] args)
    {
        root = Path.GetFullPath(args[0]); server = Path.GetFullPath(args[1]);
        try
        {
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                var dst = Path.Combine(server, Path.GetRelativePath(root, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, overwrite: true);
            }
            Register();
            foreach (var f in files) Convert(f, Path.GetRelativePath(root, f));
            Connect();
        }
        catch (Exception e)
        {
            Console.WriteLine("unsupported: " + e.Message);
            return 3;
        }
        Console.WriteLine("ready");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static void Check(int hr, string what) { if (hr < 0) throw new IOException($"{what}: 0x{hr:X8}"); }

    [StructLayout(LayoutKind.Sequential)]
    struct Registration
    {
        public uint StructSize; public IntPtr ProviderName, ProviderVersion, SyncRootIdentity; public uint SyncRootIdentityLength;
        public IntPtr FileIdentity; public uint FileIdentityLength; public Guid ProviderId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Policies
    {
        public uint StructSize; public ushort HydrationPrimary, HydrationModifier, PopulationPrimary, PopulationModifier;
        public uint InSync, HardLink, PlaceholderManagement;
    }

    static void Register()
    {
        var reg = new Registration
        {
            StructSize = (uint)Marshal.SizeOf<Registration>(),
            ProviderName = Marshal.StringToHGlobalUni("LaptopDoctorTestCloud"),
            ProviderVersion = Marshal.StringToHGlobalUni("1.0"),
            ProviderId = new Guid("6f0b5d1e-3c1a-4c55-9a43-2d8f0e1b7a11"),
        };
        var pol = new Policies
        {
            StructSize = (uint)Marshal.SizeOf<Policies>(),
            HydrationPrimary = 2,     // CF_HYDRATION_POLICY_FULL
            PopulationPrimary = 3,    // CF_POPULATION_POLICY_ALWAYS_FULL: no placeholder-listing callbacks
        };
        Check(CfRegisterSyncRoot(root, ref reg, ref pol, 0), "CfRegisterSyncRoot");
    }

    static void Convert(string path, string rel)
    {
        using var h = CreateFileW(path, 0x1 | 0x2 | 0x80 | 0x100, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"open {path}: {Marshal.GetLastWin32Error()}");
        var id = Encoding.Unicode.GetBytes(rel);
        Check(CfConvertToPlaceholder(h, id, id.Length, 1 /* CF_CONVERT_FLAG_MARK_IN_SYNC */, IntPtr.Zero, IntPtr.Zero), "CfConvertToPlaceholder");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CallbackRegistration { public int Type; public IntPtr Callback; }

    delegate void CfCallback(IntPtr info, IntPtr parameters);

    static void Connect()
    {
        keep = FetchData;
        var table = new[]
        {
            new CallbackRegistration { Type = 0 /* FETCH_DATA */, Callback = Marshal.GetFunctionPointerForDelegate(keep) },
            new CallbackRegistration { Type = -1 /* NONE: end of table */, Callback = IntPtr.Zero },
        };
        Check(CfConnectSyncRoot(root, table, IntPtr.Zero, 0, out _), "CfConnectSyncRoot");
    }

    /// Transfers the whole file from offset 0, which covers any requested range (FULL hydration policy).
    static void FetchData(IntPtr info, IntPtr p)
    {
        var size = Marshal.ReadInt32(info, 0);
        var idPtr = Marshal.ReadIntPtr(info, 88);
        var idLen = Marshal.ReadInt32(info, 96);
        var rel = Marshal.PtrToStringUni(idPtr, idLen / 2);
        byte[] data;
        int status = 0;
        try { data = File.ReadAllBytes(Path.Combine(server, rel)); }
        catch { data = Array.Empty<byte>(); status = unchecked((int)0xC000CF0D); /* STATUS_CLOUD_FILE_UNSUCCESSFUL */ }
        var op = Marshal.AllocHGlobal(48);
        var par = Marshal.AllocHGlobal(128);
        var buf = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            for (int i = 0; i < 48; i += 8) Marshal.WriteInt64(op, i, 0);
            for (int i = 0; i < 128; i += 8) Marshal.WriteInt64(par, i, 0);
            Marshal.WriteInt32(op, 0, size >= 152 ? 48 : 40);
            Marshal.WriteInt32(op, 4, 0);                                  // CF_OPERATION_TYPE_TRANSFER_DATA
            Marshal.WriteInt64(op, 8, Marshal.ReadInt64(info, 8));         // ConnectionKey
            Marshal.WriteInt64(op, 16, Marshal.ReadInt64(info, 112));      // TransferKey
            Marshal.WriteIntPtr(op, 24, Marshal.ReadIntPtr(info, 128));    // CorrelationVector
            if (size >= 152) Marshal.WriteInt64(op, 40, Marshal.ReadInt64(info, 144));   // RequestKey
            Marshal.WriteInt32(par, 0, 40);                                // ParamSize = offset + sizeof(TransferData)
            Marshal.WriteInt32(par, 12, status);
            Marshal.WriteIntPtr(par, 16, buf.AddrOfPinnedObject());
            Marshal.WriteInt64(par, 24, 0);
            Marshal.WriteInt64(par, 32, status == 0 ? data.Length : Marshal.ReadInt64(p, 24));
            var hr = CfExecute(op, par);
            if (hr < 0) Console.Error.WriteLine($"CfExecute {rel}: 0x{hr:X8}");
        }
        finally { buf.Free(); Marshal.FreeHGlobal(op); Marshal.FreeHGlobal(par); }
    }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    static extern int CfRegisterSyncRoot(string path, ref Registration reg, ref Policies pol, int flags);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    static extern int CfConnectSyncRoot(string path, CallbackRegistration[] table, IntPtr ctx, int flags, out long key);
    [DllImport("cldapi.dll")]
    static extern int CfConvertToPlaceholder(SafeFileHandle h, byte[] identity, int length, int flags, IntPtr usn, IntPtr overlapped);
    [DllImport("cldapi.dll")]
    static extern int CfExecute(IntPtr opInfo, IntPtr opParams);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
}

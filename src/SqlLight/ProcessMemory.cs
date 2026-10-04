using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SqlLight;

public static class ProcessMemory
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder image, ref int length);
    public static long OtherApps(string expectedImage)
    {
        long bytes = 0;
        foreach (var proc in Process.GetProcessesByName("AppUtilDev"))
        {
            using (proc)
            {
                if (proc.Id == Environment.ProcessId) continue;
                var handle = OpenProcess(0x1000, false, proc.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    var image = new System.Text.StringBuilder(32768); var length = image.Capacity;
                    if (QueryFullProcessImageName(handle, 0, image, ref length) && string.Equals(image.ToString(), expectedImage, StringComparison.OrdinalIgnoreCase)) bytes += proc.WorkingSet64;
                } catch { }
                finally { CloseHandle(handle); }
            }
        }
        return bytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus { public uint Type, State, Controls, ExitCode, ServiceExitCode, Checkpoint, WaitHint, Pid, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll")] static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
    public static long? SqlServer()
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) return null;
        IntPtr service = IntPtr.Zero;
        try
        {
            service = OpenService(manager, "MSSQL$SQLLIGHT", 4);
            if (service == IntPtr.Zero || !QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) || status.State != 4 || status.Pid == 0) return null;
            using var process = Process.GetProcessById((int)status.Pid);
            var bytes = process.WorkingSet64;
            // Check that the service still owns this PID after sampling.
            return QueryServiceStatusEx(service, 0, out var after, Marshal.SizeOf<ServiceStatus>(), out _) && after.Pid == status.Pid && after.State == 4 ? bytes : null;
        }
        catch { return null; }
        finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Entry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static long Tree(Process root)
    {
        var rows = new List<(int Id, int Parent)>();
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) return root.WorkingSet64;
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Exe = "" };
            if (Process32FirstW(handle, ref entry)) do { rows.Add(((int)entry.Id, (int)entry.Parent)); } while (Process32NextW(handle, ref entry));
        }
        finally { CloseHandle(handle); }
        var members = new HashSet<int> { root.Id }; var changed = true;
        while (changed) { changed = false; foreach (var row in rows) if (members.Contains(row.Parent) && members.Add(row.Id)) changed = true; }
        var directory = Path.GetDirectoryName(root.MainModule!.FileName); var start = root.StartTime;
        long sum = 0;
        foreach (var id in members)
        {
            try
            {
                using var proc = Process.GetProcessById(id);
                if (proc.StartTime >= start && string.Equals(Path.GetDirectoryName(proc.MainModule!.FileName), directory, StringComparison.OrdinalIgnoreCase)) sum += proc.WorkingSet64;
            } catch { }
        }
        return sum;
    }
}

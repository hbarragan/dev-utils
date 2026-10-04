using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
namespace AppUtilDev;

public static class Native
{
 [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
 [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);
 [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessCritical(IntPtr handle, out bool critical);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageNameW(IntPtr handle,uint flags,StringBuilder path,ref uint size);
 [DllImport("psapi.dll",SetLastError=true)] static extern bool GetProcessMemoryInfo(IntPtr handle,out MemoryCounters counters,uint size);
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 [StructLayout(LayoutKind.Sequential)] struct MemoryCounters
 {
  public uint Size,PageFaults;
  public UIntPtr PeakWorkingSet,WorkingSet,PeakPagedPool,PagedPool,PeakNonPagedPool,NonPagedPool,Pagefile,PeakPagefile;
 }
 public static long WorkingSet(Process process)
 {
  var handle=OpenProcess(0x1000,false,process.Id);
  if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
  try { return GetProcessMemoryInfo(handle,out var counters,(uint)Marshal.SizeOf<MemoryCounters>()) ? checked((long)counters.WorkingSet.ToUInt64()) : throw new Win32Exception(Marshal.GetLastWin32Error()); }
  finally { CloseHandle(handle); }
 }
 public static string ImagePath(Process process)
 {
  var handle=OpenProcess(0x1000,false,process.Id);
  if(handle==IntPtr.Zero) return "";
  try
  {
  uint size=512; var path=new StringBuilder((int)size);
  if(QueryFullProcessImageNameW(handle,0,path,ref size)) return path.ToString();
  if(Marshal.GetLastWin32Error()!=122) return "";
  size=32768; path=new StringBuilder((int)size);
  return QueryFullProcessImageNameW(handle,0,path,ref size)?path.ToString():"";
  } finally { CloseHandle(handle); }
 }
 [StructLayout(LayoutKind.Sequential)] public struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
 [StructLayout(LayoutKind.Sequential)] struct Tcp4 { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
 [StructLayout(LayoutKind.Sequential)] struct Tcp6
 {
  [MarshalAs(UnmanagedType.ByValArray, SizeConst=16)] public byte[] LocalAddress;
  public uint LocalScope, LocalPort;
  [MarshalAs(UnmanagedType.ByValArray, SizeConst=16)] public byte[] RemoteAddress;
  public uint RemoteScope, RemotePort, State, Pid;
 }
 public record Tcp(int Pid, int Port, bool Listening, bool Connected);
 static int Port(uint value) => (int)(((value & 255) << 8) | ((value >> 8) & 255));
 public static List<Tcp> Connections()
 {
  var result = new List<Tcp>();
  foreach (var family in new[] { 2, 23 })
  {
   int size = 0; uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
   if (status != 122 && status != 0) throw new Win32Exception((int)status);
   IntPtr buffer = Marshal.AllocHGlobal(size);
   try
   {
    status = GetExtendedTcpTable(buffer, ref size, false, family, 5, 0);
    if (status != 0) throw new Win32Exception((int)status);
    int count = Marshal.ReadInt32(buffer); IntPtr row = IntPtr.Add(buffer, 4);
    for (int i=0; i<count; i++)
    {
     if (family == 2) { var r=Marshal.PtrToStructure<Tcp4>(row); result.Add(new((int)r.Pid,Port(r.LocalPort),r.State==2,r.State==5)); row=IntPtr.Add(row,Marshal.SizeOf<Tcp4>()); }
     else { var r=Marshal.PtrToStructure<Tcp6>(row); result.Add(new((int)r.Pid,Port(r.LocalPort),r.State==2,r.State==5)); row=IntPtr.Add(row,Marshal.SizeOf<Tcp6>()); }
    }
   }
   finally { Marshal.FreeHGlobal(buffer); }
  }
  return result;
 }
 public static ulong Io(Process p) => GetProcessIoCounters(p.Handle, out var io) ? io.ReadBytes + io.WriteBytes + io.OtherBytes : throw new Win32Exception();
 public static bool Protected(Process p, string path)
 {
  if (p.Id <= 4 || p.Id == Environment.ProcessId || string.IsNullOrEmpty(path)) return true;
  if(path.StartsWith(System.IO.Path.Combine(SqlPortal.Root,"engines")+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) return true;
  if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows)+"\\", StringComparison.OrdinalIgnoreCase)) return true;
  return !IsProcessCritical(p.Handle, out bool critical) || critical;
 }
 public static void Close(ProcessRow row, bool force)
 {
  if(!row.CanClose) throw new InvalidOperationException("El cierre de este grupo está bloqueado.");
  using var p = Process.GetProcessById(row.Pid);
  if (p.StartTime.ToUniversalTime().Ticks != row.Started) throw new InvalidOperationException("El PID ahora pertenece a otro proceso. Actualiza la lista.");
  string path = ImagePath(p);
  if(row.Family!="" && RuntimeOwnership.Owner(p.Id,RuntimeOwnership.Read(),row.PortLinks.Length>0)!="") throw new InvalidOperationException("Este proceso pertenece a un grupo protegido.");
  if (Protected(p,path)) throw new InvalidOperationException("Este proceso está protegido o no es accesible.");
  if (force) p.Kill();
  else if (!p.CloseMainWindow()) throw new InvalidOperationException("No tiene ventana o no acepta cierre normal. Puedes usar Finalizar.");
 }
 public static void CloseInactive(ProcessRow row)
 {
  if(!row.CanOptimize) throw new InvalidOperationException("El proceso no es candidato a optimización.");
  using var p=Process.GetProcessById(row.Pid);
  if(p.StartTime.ToUniversalTime().Ticks!=row.Started) throw new InvalidOperationException("El PID ha cambiado.");
  string name=p.ProcessName;
  if(!name.Equals("node",StringComparison.OrdinalIgnoreCase) && !name.Equals("java",StringComparison.OrdinalIgnoreCase) && !name.Equals("javaw",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("No es Java o Node.");
  var identities=RuntimeOwnership.Read();
  if(!identities.TryGetValue(p.Id,out var identity) || !RuntimeOwnership.MatchesStart(identity,row.Started) || string.IsNullOrEmpty(identity.Command)) throw new InvalidOperationException("No se puede comprobar la identidad del proceso.");
  var sockets=Connections().Where(x=>x.Pid==p.Id).ToList();
  if(RuntimeOwnership.Owner(p.Id,identities,sockets.Any(x=>x.Listening))!="" || Protected(p,ImagePath(p))) throw new InvalidOperationException("Proceso protegido.");
  ulong io=Io(p);
  if(ProcessMonitor.IsActive(p.TotalProcessorTime-row.Cpu,io>=row.IoBytes?io-row.IoBytes:ulong.MaxValue,sockets.Any(x=>x.Connected))) throw new InvalidOperationException("El proceso ha vuelto a tener actividad.");
  p.Kill(); // Retain this process handle: never chase a replacement PID or kill its children.
 }
}
public sealed class ProcessMonitor
{
 record Sample(long Started, TimeSpan Cpu, ulong Io, DateTime LastActivity);
 readonly Dictionary<int, Sample> samples = new();
 public List<ProcessRow> Scan(int idleMinutes)
 {
  var now = DateTime.UtcNow;
  var identities=RuntimeOwnership.Read();
  var tcp = Native.Connections().GroupBy(x=>x.Pid).ToDictionary(g=>g.Key,g=>g.ToList());
  var rows = new List<ProcessRow>(); var seen = new HashSet<int>();
  foreach(var identityRow in identities.Values)
  {
   try
   {
    using var p=Process.GetProcessById(identityRow.Pid);
    int pid=p.Id; seen.Add(pid); string name=System.IO.Path.GetFileNameWithoutExtension(identityRow.Name);
    string family=name.Equals("node",StringComparison.OrdinalIgnoreCase)?"Node.js": name.Equals("java",StringComparison.OrdinalIgnoreCase)||name.Equals("javaw",StringComparison.OrdinalIgnoreCase)?"Java / JDK":"";
    long bytes=Native.WorkingSet(p);
    long started; try { started=p.StartTime.ToUniversalTime().Ticks; } catch { started=0; }
    string path; try { path=Native.ImagePath(p); } catch { path=""; }
    var sockets=tcp.GetValueOrDefault(pid) ?? new();
    var owner=family!="" ? RuntimeOwnership.Owner(pid,identities,sockets.Any(x=>x.Listening)) : "";
    var last=now; bool measurable=false; TimeSpan cpu=default; ulong io=0;
    if(family!="")
    {
     try
     {
      cpu=p.TotalProcessorTime; io=Native.Io(p); measurable=true;
      if(samples.TryGetValue(pid,out var old) && old.Started==started)
       last=IsActive(cpu-old.Cpu, io>=old.Io ? io-old.Io : ulong.MaxValue, sockets.Any(x=>x.Connected))?now:old.LastActivity;
      samples[pid]=new(started,cpu,io,last);
     } catch { samples.Remove(pid); }
    }
    bool protect; try { protect=owner!="" || started==0 || Native.Protected(p,path) || (family!="" && (!identities.TryGetValue(pid,out var identity) || !RuntimeOwnership.MatchesStart(identity,started) || string.IsNullOrEmpty(identity.Command))); } catch { protect=true; }
    var ports=sockets.Where(x=>x.Listening).Select(x=>x.Port).Distinct().Order().ToArray();
    rows.Add(new(pid,started,name,bytes,path,protect,family,ports.Length>0?"TCP :"+string.Join(" · :",ports):"Sin puerto TCP en escucha",now-last,measurable && now-last>=TimeSpan.FromMinutes(idleMinutes),measurable,owner,cpu,io));
   } catch { /* Process ended between enumeration and sampling. */ }
  }
  foreach(var pid in samples.Keys.Where(x=>!seen.Contains(x)).ToArray()) samples.Remove(pid);
  return rows.OrderByDescending(x=>x.Bytes).ToList();
 }
 public static bool IsActive(TimeSpan cpuDelta, ulong ioDelta, bool connected) => cpuDelta.TotalMilliseconds>50 || ioDelta>1024 || connected;
 public static List<ProcessRow> Page(IEnumerable<ProcessRow> rows,int page,int size=7) => rows.Where(x=>x.Bytes>100*1048576L).OrderByDescending(x=>x.Bytes).Skip(page*size).Take(size).ToList();
}

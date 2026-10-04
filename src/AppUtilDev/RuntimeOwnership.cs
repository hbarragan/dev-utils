using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace AppUtilDev;

public record ProcessIdentity(int Pid,int Parent,string Name,string Path,string Command,DateTime Created);
public static class RuntimeOwnership
{
 // WMI CreationDate has microsecond precision; Process.StartTime has 100 ns precision.
 public static bool MatchesStart(ProcessIdentity identity,long started) => identity.Created!=DateTime.MinValue && Math.Abs(identity.Created.Ticks-started)<10;
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
 struct ProcessEntry
 {
  public uint Size,Usage,Pid; public UIntPtr Heap; public uint Module,Threads,Parent;
  public int Priority; public uint Flags;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Name;
 }
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool Process32FirstW(IntPtr snapshot,ref ProcessEntry entry);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool Process32NextW(IntPtr snapshot,ref ProcessEntry entry);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 public static Dictionary<int,ProcessIdentity> Read()
 {
  var map=new Dictionary<int,ProcessIdentity>();
  var snapshot=CreateToolhelp32Snapshot(2,0);
  if(snapshot==new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
  try
  {
   var entry=new ProcessEntry { Size=(uint)Marshal.SizeOf<ProcessEntry>(),Name="" };
   if(!Process32FirstW(snapshot,ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
   do { map[(int)entry.Pid]=new((int)entry.Pid,(int)entry.Parent,entry.Name,"","",DateTime.MinValue); }
   while(Process32NextW(snapshot,ref entry));
   if(Marshal.GetLastWin32Error()!=18) throw new Win32Exception(Marshal.GetLastWin32Error());
  } finally { CloseHandle(snapshot); }
  // Only runtimes need command lines. Avoid materializing WMI objects for every application.
  using var search=new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine,CreationDate FROM Win32_Process WHERE Name='node.exe' OR Name='java.exe' OR Name='javaw.exe'");
  using var results=search.Get();
  foreach(ManagementObject p in results)
  {
   using(p) try
   {
    int id=Convert.ToInt32(p["ProcessId"]); DateTime created=DateTime.MinValue;
    if(p["CreationDate"] is string date) created=ManagementDateTimeConverter.ToDateTime(date).ToUniversalTime();
    map[id]=new(id,Convert.ToInt32(p["ParentProcessId"]),p["Name"]?.ToString()??"",p["ExecutablePath"]?.ToString()??"",p["CommandLine"]?.ToString()??"",created);
   } catch { }
  }
  foreach(var runtime in map.Values.Where(x=>x.Name.Equals("node.exe",StringComparison.OrdinalIgnoreCase) || x.Name.StartsWith("java",StringComparison.OrdinalIgnoreCase)).ToArray())
  {
   var item=runtime; var visited=new HashSet<int>();
   for(int depth=0;depth<16 && visited.Add(item.Pid);depth++)
   {
    if(item.Created==DateTime.MinValue)
    {
     try { using var process=Process.GetProcessById(item.Pid); item=item with { Created=process.StartTime.ToUniversalTime() }; map[item.Pid]=item; }
     catch { break; }
    }
    if(!map.TryGetValue(item.Parent,out item!)) break;
   }
  }
  return map;
 }
 public static string Owner(int pid,Dictionary<int,ProcessIdentity> map,bool listening)
 {
  if(!map.TryGetValue(pid,out var item)) return "";
  string command=item.Command.ToLowerInvariant(),path=item.Path.ToLowerInvariant();
  if(path.Contains("streamdeck")) return "Stream Deck";
  if(path.Contains("\\openai\\codex\\")) return "Codex";
  // Listening user apps take precedence over their IDE/assistant ancestor.
  bool support=command.Contains("mcp-remote") || command.Contains("jetbrains") || command.Contains("intellij") || command.Contains("cua-repl");
  bool javaApp=item.Name.StartsWith("java",StringComparison.OrdinalIgnoreCase) &&
   (command.Contains(" -jar ") || command.Contains(" -jar\"") || (!command.Contains("org.gradle") && !command.Contains("org.jetbrains") && !command.Contains("org.intellij") && !path.Contains("jetbrains")));
  if(listening && (javaApp || (!support && item.Name.Equals("node.exe",StringComparison.OrdinalIgnoreCase)))) return "";
  var visited=new HashSet<int>();
  for(int depth=0;depth<16 && visited.Add(item.Pid);depth++)
  {
   string name=item.Name.ToLowerInvariant();
   if(name=="codex.exe") return "Codex";
   if(name=="streamdeck.exe") return "Stream Deck";
   if(name is "idea64.exe" or "idea.exe") return "IntelliJ";
   if(!map.TryGetValue(item.Parent,out var parent) || (item.Created!=DateTime.MinValue && parent.Created>item.Created)) break;
   item=parent;
  }
  return "";
 }
 public static List<RuntimeGroup> Groups(IEnumerable<ProcessRow> rows)
 {
  var runtime=rows.Where(x=>x.Family!="").ToList(); var groups=new List<RuntimeGroup>();
  void Add(string name,List<ProcessRow> items,bool locked=false)
  {
   if(items.Count==0 && !locked) return;
   groups.Add(new(name,$"{items.Count} procesos · {items.Sum(x=>x.Bytes)/1048576d:N0} MB"+(locked?" · cierre bloqueado":$" · {items.Count(x=>x.Inactive)} sin actividad"),items,locked));
  }
  Add("Aplicaciones con puerto",runtime.Where(x=>x.Owner=="" && x.PortLinks.Length>0).OrderBy(x=>x.Family).ThenByDescending(x=>x.Bytes).ToList());
  Add("Otros Node / Java",runtime.Where(x=>x.Owner=="" && x.PortLinks.Length==0).OrderByDescending(x=>x.Bytes).ToList());
  foreach(string owner in new[]{"Codex","Stream Deck","IntelliJ"}) Add("Node · "+owner,runtime.Where(x=>x.Family=="Node.js" && x.Owner==owner).ToList(),true);
  var protectedJava=runtime.Where(x=>x.Family=="Java / JDK" && x.Owner!="").ToList();
  if(protectedJava.Count>0) Add("Java de herramientas · protegido",protectedJava,true);
  return groups;
 }
}

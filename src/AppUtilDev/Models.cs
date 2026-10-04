using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace AppUtilDev;

public record ProcessRow(int Pid, long Started, string Name, long Bytes, string Path, bool Protected, string Family, string Ports, TimeSpan Idle, bool Inactive, bool ActivityKnown=true, string Owner="", TimeSpan Cpu=default, ulong IoBytes=0)
{
 public string Memory => $"{Bytes / 1048576d:N0} MB";
 public string Detail => $"PID {Pid} · {Ports}";
 public string PidLabel => $"PID {Pid}";
 public PortLink[] PortLinks => Ports.StartsWith("TCP :",StringComparison.Ordinal)
  ? Ports[5..].Split(" · :",StringSplitOptions.RemoveEmptyEntries).Select(x=>int.TryParse(x,out var port)?port:0).Where(x=>x is >0 and <=65535).Distinct().Select(x=>new PortLink(x)).ToArray()
  : Array.Empty<PortLink>();
 public string PortHint => PortLinks.Length==0 ? Ports : "";
 public string Activity => Family=="" ? Protected ? "Protegido" : "" : !ActivityKnown ? "Actividad no disponible" : Inactive ? $"Sin actividad detectada · {Idle.TotalMinutes:N0}m" : Idle.TotalSeconds < 15 ? "Actividad detectada" : $"Observando · {Idle.TotalMinutes:N0}m";
 public string StatusColor => Inactive ? "#65DDB0" : "#F5C56B";
 public bool CanClose => !Protected && Owner=="";
 public bool CanOptimize => CanClose && Family is "Node.js" or "Java / JDK" && ActivityKnown && Inactive && Started>0;
}
public record PortLink(int Port)
{
 public string Label => $":{Port} ↗";
 public string Url => $"http://localhost:{Port}/";
}
public record UsageRow(string Name, double Remaining, string Reset)
{
 public string Label => $"{Remaining:0.#}% disponible";
 public string Color => Remaining > 25 ? "#65DDB0" : Remaining > 10 ? "#F5C56B" : "#FF8585";
}
public record RuntimeGroup(string Name, string Summary, List<ProcessRow> Processes, bool Locked=false)
{
 public bool? ExpandedOverride { get; init; }
 public bool Expanded => ExpandedOverride ?? !Locked;
}
public sealed class Settings
{
 public string MenuPosition { get; set; } = "Left";
 internal static string? DataDirOverride;
 public int IdleMinutes { get; set; } = 4;
 public string CodexPath { get; set; } = "";
 public bool ClaudeConnected { get; set; }
 public string VpnHost { get; set; } = "";
 public int VpnPort { get; set; } = 443;
 public static string DataDir => DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppUtilDev");
 public static Settings Load()
 {
  try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(DataDir, "settings.json"))) ?? new(); }
  catch { return new(); }
 }
 public void Save() { Directory.CreateDirectory(DataDir); File.WriteAllText(Path.Combine(DataDir, "settings.json"), JsonSerializer.Serialize(this)); }
}

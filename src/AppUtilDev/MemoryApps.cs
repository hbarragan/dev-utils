using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace AppUtilDev;

public record MemoryApp(string Key,string Name,List<ProcessRow> Processes)
{
 public long Bytes => Processes.Sum(x=>x.Bytes);
 public string Memory => $"{Bytes/1048576d:N0} MB";
 public string Summary => $"{Processes.Count} procesos"+(CanClose?"":" · cierre bloqueado");
 public bool CanClose => Processes.Count>0 && Processes.All(x=>x.CanClose);
 public bool Expanded { get; init; }
}
public static class MemoryApps
{
 public static List<MemoryApp> Group(IEnumerable<ProcessRow> rows)
 {
  return rows.GroupBy(Key,StringComparer.OrdinalIgnoreCase).Select(g=>new MemoryApp(g.Key,Name(g.First()),g.OrderByDescending(x=>x.Bytes).ToList()))
   .Where(x=>x.Bytes>100*1048576L).OrderByDescending(x=>x.Bytes).ThenBy(x=>x.Name).ToList();
 }
 static string Key(ProcessRow row) => row.Owner!=""?"owner:"+row.Owner:row.Path!=""?"exe:"+row.Path:"name:"+row.Name;
 static string Name(ProcessRow row)
 {
  if(row.Owner!="") return "Node / Java · "+row.Owner;
  return row.Name.ToLowerInvariant() switch { "chrome"=>"Google Chrome","msedge"=>"Microsoft Edge","firefox"=>"Firefox","idea64"=>"IntelliJ IDEA","app util windows" or "apputilwindows"=>"App Util Windows",_=>row.Name };
 }
 public static List<MemoryApp> Page(List<MemoryApp> apps,int page,int size=7) => apps.Skip(page*size).Take(size).ToList();
}

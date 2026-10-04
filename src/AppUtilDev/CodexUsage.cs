using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace AppUtilDev;

public static class CodexUsage
{
 public static string FindExecutable(string configured)
 {
  if(!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
  foreach(var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
  { var candidate=Path.Combine(dir,"codex.exe"); if(File.Exists(candidate)) return candidate; }
  var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
  if(Directory.Exists(root))
  {
   var file=Directory.EnumerateFiles(root,"codex.exe",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
   if(file!=null) return file;
  }
  throw new FileNotFoundException("Instala Codex e inicia sesión con ChatGPT, o selecciona codex.exe en Ajustes.");
 }
 public static async Task<List<UsageRow>> ReadAsync(string configured)
 {
  using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
  using var p=new Process { StartInfo=new ProcessStartInfo(FindExecutable(configured),"app-server --listen stdio://")
   { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true } };
  p.Start();
  EventHandler onExit=(_,_)=> { try { if(!p.HasExited) p.Kill(entireProcessTree:true); } catch { } };
  AppDomain.CurrentDomain.ProcessExit+=onExit;
  // Drain stderr without persisting account information.
  var stderr=DrainAsync(p.StandardError);
  try
  {
   await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id=1,method="initialize",@params=new { clientInfo=new { name="app_util_windows",version="1.0.0" } } }));
   await Response(p,1,timeout.Token);
   await p.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
   await p.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
   var result=await Response(p,2,timeout.Token);
   return Parse(result);
  }
  finally
  {
   AppDomain.CurrentDomain.ProcessExit-=onExit;
   if(!p.HasExited) p.Kill(entireProcessTree:true);
   await p.WaitForExitAsync(); await stderr;
  }
 }
 static async Task DrainAsync(StreamReader reader)
 {
  var buffer=new char[4096];
  while(await reader.ReadAsync(buffer.AsMemory())>0) { }
 }
 static async Task<JsonElement> Response(Process p,int id,CancellationToken ct)
 {
  while(true)
  {
   var line=await p.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidOperationException("Codex cerró la conexión.");
   using var doc=JsonDocument.Parse(line); var root=doc.RootElement;
   if(!root.TryGetProperty("id",out var responseId) || !responseId.TryGetInt32(out int value) || value!=id) continue;
   if(root.TryGetProperty("error",out _)) throw new InvalidOperationException("Codex no pudo consultar los límites. Comprueba tu sesión de ChatGPT en Codex.");
   return root.GetProperty("result").Clone();
  }
 }
 public static List<UsageRow> Parse(JsonElement result)
 {
  var rows=new List<UsageRow>();
  if(result.TryGetProperty("rateLimitsByLimitId",out var buckets) && buckets.ValueKind==JsonValueKind.Object && buckets.EnumerateObject().Any())
   foreach(var bucket in buckets.EnumerateObject()) Add(bucket.Value,bucket.Name,rows);
  else if(result.TryGetProperty("rateLimits",out var legacy) && legacy.ValueKind==JsonValueKind.Object) Add(legacy,"Codex",rows);
  if(rows.Count==0) throw new InvalidOperationException("La cuenta no devuelve cuotas disponibles.");
  return rows;
 }
 static void Add(JsonElement bucket,string key,List<UsageRow> rows)
 {
  string name=bucket.TryGetProperty("limitName",out var n) && n.ValueKind==JsonValueKind.String?n.GetString()!:key;
  foreach(var field in new[]{"primary","secondary"})
  {
   if(!bucket.TryGetProperty(field,out var w) || w.ValueKind!=JsonValueKind.Object || !w.TryGetProperty("usedPercent",out var used) || !used.TryGetDouble(out double percent)) continue;
   int mins=w.TryGetProperty("windowDurationMins",out var d) && d.TryGetInt32(out int duration)?duration:0;
   string durationName=mins>=1440?$"{mins/1440d:0.#} días":mins>=60?$"{mins/60d:0.#} h":mins>0?$"{mins} min":field;
   string reset="Reinicio no informado";
   if(w.TryGetProperty("resetsAt",out var r) && r.TryGetInt64(out long unix))
    reset="Reinicio "+DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd/MM HH:mm");
   rows.Add(new($"{name} · {durationName}",Math.Clamp(100-percent,0,100),reset));
  }
 }
}

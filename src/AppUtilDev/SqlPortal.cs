using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SqlLight;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppUtilDev;

internal static class SqlPortal
{
 internal static string Root = ResolveRoot();
 static readonly SemaphoreSlim gate = new(1,1);
 static PanelServer? server;
 static string? url;
 static readonly HttpClient client=new() {Timeout=TimeSpan.FromSeconds(8)};
 static string? tokenUrl,token;
 internal record Summary(int Count,int Running,double MemoryMb,bool Busy,bool Partial,string Details);
 internal static async Task<Summary> ReadSummaryAsync()
 {
  var address=await StartAsync();
  if(tokenUrl!=address)
  {
   var html=await client.GetStringAsync(address);
   token=Regex.Match(html,"const token='([A-F0-9]+)'").Groups[1].Value;
   if(token.Length==0) throw new InvalidOperationException("El gestor no ha proporcionado acceso al panel.");
   tokenUrl=address;
  }
  using var request=new HttpRequestMessage(HttpMethod.Get,address+"api/state");
  request.Headers.Add("X-SqlLight-Token",token);
  using var response=await client.SendAsync(request); response.EnsureSuccessStatusCode();
  using var state=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  var root=state.RootElement; var profiles=root.GetProperty("profiles").EnumerateArray().ToArray();
  var details=string.Join("\n",profiles.OrderByDescending(p=>p.GetProperty("running").GetBoolean()).Take(3).Select(p=>$"{p.GetProperty("database").GetString()} · :{p.GetProperty("port").GetInt32()} · {(p.GetProperty("running").GetBoolean()?"activo":"detenido")}{(p.TryGetProperty("error",out var error) && error.ValueKind==JsonValueKind.String && !string.IsNullOrEmpty(error.GetString())?" · revisar error":"")}"));
  if(profiles.Length>3) details+=$"\n+{profiles.Length-3} entornos más";
  return new(profiles.Length,profiles.Count(p=>p.GetProperty("running").GetBoolean()),root.GetProperty("totalMemoryMb").GetDouble(),root.GetProperty("busy").GetBoolean(),root.GetProperty("memoryUnavailable").GetInt32()>0,details);
 }
 internal static string ResolveRoot()
 {
  var folder=new DirectoryInfo(AppContext.BaseDirectory);
  var executableFolder=Path.GetDirectoryName(Environment.ProcessPath);
  // Single-file bundles extract runtime assets to a cache; keep user data by the real executable.
  if(executableFolder!=null && !string.Equals(folder.FullName,executableFolder,StringComparison.OrdinalIgnoreCase)) return executableFolder;
  if(folder.Name.Equals("dist",StringComparison.OrdinalIgnoreCase)) return folder.Parent!.FullName;
  return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AppUtilDev","SqlLight");
 }
 internal static async Task<string> StartAsync()
 {
  await gate.WaitAsync();
  try
  {
   if(url!=null) return url;
   if(ServiceInstallation.Installed(Root)) return url=ServiceInstallation.ReadUrl(Root);
   var store=new Store(Root);
   var candidate=new PanelServer(store,new Engines(store));
   try { await candidate.Start(); server=candidate; return url=candidate.Url; }
   catch { await candidate.Shutdown(); throw; }
  }
  finally { gate.Release(); }
 }
 internal static async Task StopAsync()
 {
  await gate.WaitAsync();
  try { if(server!=null) { await server.Shutdown(); server=null; } url=null; tokenUrl=null; }
  finally { gate.Release(); }
 }
 internal static void ResetServiceUrl() { if(server==null) { url=null; tokenUrl=null; } }
}

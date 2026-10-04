using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppUtilDev;

internal record InternetSample(double? DownloadMbps,double? UploadMbps,DateTime At,string? Error);
internal sealed class InternetMonitor : IDisposable
{
 readonly HttpClient client;
 readonly CancellationTokenSource lifetime=new();
 int testing;
 internal InternetSample? Last {get;private set;}
 internal InternetMonitor(HttpMessageHandler? handler=null) { client=handler==null?new HttpClient():new HttpClient(handler); client.Timeout=TimeSpan.FromSeconds(20); }
 internal async Task<InternetSample?> TestAsync()
 {
  if(Interlocked.Exchange(ref testing,1)!=0) return Last;
  double? down=null,up=null;var failures=new List<string>();
  try
  {
   try
   {
    using var phase=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);phase.CancelAfter(TimeSpan.FromSeconds(20));
    using var request=new HttpRequestMessage(HttpMethod.Get,"https://speed.cloudflare.com/__down?bytes=1048576");
    request.Headers.CacheControl=new System.Net.Http.Headers.CacheControlHeaderValue {NoCache=true};
    var clock=Stopwatch.StartNew();
    using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,phase.Token); response.EnsureSuccessStatusCode();
    using var stream=await response.Content.ReadAsStreamAsync(phase.Token); var buffer=new byte[65536];long count=0;int read;
    while((read=await stream.ReadAsync(buffer,phase.Token))>0) { count+=read; if(count>1048576) throw new InvalidOperationException("Respuesta demasiado grande"); }
    if(count!=1048576) throw new InvalidOperationException("Transferencia incompleta");
    down=count*8/Math.Max(clock.Elapsed.TotalSeconds,0.001)/1_000_000;
   }
   catch(Exception) when(!lifetime.IsCancellationRequested) {failures.Add("Descarga no disponible");}
   try
   {
    using var phase=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);phase.CancelAfter(TimeSpan.FromSeconds(20));
    var bytes=RandomNumberGenerator.GetBytes(262144);using var content=new ByteArrayContent(bytes);content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
    var clock=Stopwatch.StartNew(); using var response=await client.PostAsync("https://speed.cloudflare.com/__up",content,phase.Token);response.EnsureSuccessStatusCode();
    up=bytes.Length*8/Math.Max(clock.Elapsed.TotalSeconds,0.001)/1_000_000;
   }
   catch(Exception) when(!lifetime.IsCancellationRequested) {failures.Add("Subida no disponible");}
   return Last=new(down,up,DateTime.Now,failures.Count>0?string.Join(" · ",failures):null);
  }
  catch(OperationCanceledException) {return Last;}
  finally {Interlocked.Exchange(ref testing,0);}
 }
 public void Dispose() {lifetime.Cancel();client.Dispose();lifetime.Dispose();}
}

internal sealed class DailyTraffic
{
 internal sealed record Counter(long Received,long Sent);
 internal sealed class State
 {
  public DateTime Day {get;set;}
  public long Download {get;set;}
  public long Upload {get;set;}
  public Dictionary<string,Counter> Adapters {get;set;}=new();
 }
 readonly string file;
 State state;
 internal long Download=>state.Download;
 internal long Upload=>state.Upload;
 internal DailyTraffic(string? path=null)
 {
  file=path??Path.Combine(Settings.DataDir,"network-totals.json");
  try {state=JsonSerializer.Deserialize<State>(File.ReadAllText(file))??new();} catch {state=new();}
 }
 internal void Sample(IEnumerable<AdapterState> adapters,DateTime today)
 {
  var selected=adapters.Where(a=>a.Physical&&!a.Vpn).ToArray();
  if(state.Day.Date!=today.Date) state=new State {Day=today.Date};
  foreach(var adapter in selected)
  {
   if(state.Adapters.TryGetValue(adapter.Id,out var last))
   {
    state.Download+=adapter.Received>=last.Received?adapter.Received-last.Received:Math.Max(0,adapter.Received);
    state.Upload+=adapter.Sent>=last.Sent?adapter.Sent-last.Sent:Math.Max(0,adapter.Sent);
   }
   state.Adapters[adapter.Id]=new(adapter.Received,adapter.Sent);
  }
  try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file+".tmp",JsonSerializer.Serialize(state));File.Move(file+".tmp",file,true); } catch { }
 }
}

internal record DiskSpace(string Name,long Free,long Total)
{
 public double FreePercent=>Total>0?Free*100d/Total:0;
 public string Summary=>$"{Name}  {Free/1_000_000_000d:N0} / {Total/1_000_000_000d:N0} GB · {FreePercent:N0}% libre";
}
internal static class DiskMonitor
{
 internal static DiskSpace[] Read()=>DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed).Select(d=>
 {
  try { return d.IsReady?new DiskSpace(d.Name,d.AvailableFreeSpace,d.TotalSize):null; } catch {return null;}
 }).Where(d=>d!=null).Cast<DiskSpace>().OrderBy(d=>d.Name).ToArray();
}

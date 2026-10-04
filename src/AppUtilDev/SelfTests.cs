using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace AppUtilDev;

public static class SelfTests
{
 public static void Worker()
 {
  var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
  var memory=new byte[135*1048576]; for(int i=0;i<memory.Length;i+=4096) memory[i]=1;
  Thread.Sleep(60000); GC.KeepAlive(memory); listener.Stop();
 }
 public static async Task RunAsync()
 {
  Directory.CreateDirectory("artifacts");
  var checks=new List<object>(); bool failed=false;
  void Check(string name,bool ok) { checks.Add(new { name, passed=ok }); if(!ok) failed=true; }
  try
  {
   Check("Idle CPU and IO are inactive",!ProcessMonitor.IsActive(TimeSpan.Zero,0,false));
   Check("CPU resets inactivity",ProcessMonitor.IsActive(TimeSpan.FromMilliseconds(100),0,false));
   Check("IO resets inactivity",ProcessMonitor.IsActive(TimeSpan.Zero,2048,false));
   Check("Established TCP is conservatively active",ProcessMonitor.IsActive(TimeSpan.Zero,0,true));
   var idleRuntime=new ProcessRow(999,1,"node",1024,"C:\\node.exe",false,"Node.js","TCP :8080",TimeSpan.FromMinutes(4),true);
   Check("Optimizer accepts idle runtimes and rejects active, unknown, protected and ordinary processes",idleRuntime.CanOptimize && !(idleRuntime with { Inactive=false }).CanOptimize && !(idleRuntime with { ActivityKnown=false }).CanOptimize && !(idleRuntime with { Protected=true }).CanOptimize && !(idleRuntime with { Owner="Codex" }).CanOptimize && !(idleRuntime with { Family="" }).CanOptimize && !(idleRuntime with { Started=0 }).CanOptimize);
   bool optimizerBlocked=false; try { Native.CloseInactive(idleRuntime with { Protected=true }); } catch(InvalidOperationException) { optimizerBlocked=true; }
   Check("Optimizer enforces protection in backend",optimizerBlocked);
   var preciseStart=DateTime.UtcNow;
   var roundedIdentity=new ProcessIdentity(999,0,"node.exe","","node app.js",preciseStart.AddTicks(-8));
   Check("WMI start comparison tolerates only microsecond rounding",RuntimeOwnership.MatchesStart(roundedIdentity,preciseStart.Ticks) && !RuntimeOwnership.MatchesStart(roundedIdentity,preciseStart.AddMilliseconds(1).Ticks));
   var tree=new Dictionary<int,ProcessIdentity> {
    [1]=new(1,0,"codex.exe","C:\\Codex\\codex.exe","",DateTime.MinValue),
    [2]=new(2,1,"cmd.exe","","",DateTime.MinValue),
    [3]=new(3,2,"node.exe","C:\\Program Files\\nodejs\\node.exe","node mcp-remote",DateTime.MinValue),
    [4]=new(4,1,"node.exe","C:\\Program Files\\nodejs\\node.exe","node server.js",DateTime.MinValue),
    [5]=new(5,0,"idea64.exe","","",DateTime.MinValue),
    [6]=new(6,5,"node.exe","C:\\Program Files\\nodejs\\node.exe","node mcp-remote",DateTime.MinValue),
    [7]=new(7,0,"node.exe","C:\\StreamDeck\\NodeJS\\node.exe","plugin.js",DateTime.MinValue),
    [8]=new(8,5,"java.exe","C:\\jdk\\java.exe","java -jar application.jar",DateTime.MinValue) };
   Check("Codex, Stream Deck and IntelliJ Node ownership",RuntimeOwnership.Owner(3,tree,false)=="Codex" && RuntimeOwnership.Owner(6,tree,false)=="IntelliJ" && RuntimeOwnership.Owner(7,tree,true)=="Stream Deck");
   Check("Listening Node and Java apps take precedence over ancestors",RuntimeOwnership.Owner(4,tree,true)=="" && RuntimeOwnership.Owner(8,tree,true)=="");
   tree[8]=tree[8] with { Command="java -javaagent:C:\\JetBrains\\idea_rt.jar -jar application.jar" };
   Check("Java application with IntelliJ agent remains a port app",RuntimeOwnership.Owner(8,tree,true)=="");
   Check("SonicWall NetExtender is recognized as VPN",NetworkMonitor.LooksVpn("SonicWall_NetExtender_SSL"));
   var protectedNode=new ProcessRow(3,1,"node",1000,"",true,"Node.js","",TimeSpan.Zero,false,true,"Codex");
   var app=protectedNode with { Pid=4,Protected=false,Owner="",Ports="TCP :8080" };
   var groups=RuntimeOwnership.Groups(new[]{protectedNode,app});
   Check("Port apps first and all three protected groups present",groups[0].Name=="Aplicaciones con puerto" && groups.Count(x=>x.Locked && x.Name.StartsWith("Node"))==3 && !protectedNode.CanClose && app.CanClose);
   bool closeBlocked=false; try { Native.Close(protectedNode,true); } catch(InvalidOperationException) { closeBlocked=true; }
   Check("Protected runtime closure blocked in backend",closeBlocked);
   Check("Network distinguishes local link, DNS and VPN evidence",NetworkMonitor.Diagnose(false,false,false,null,false).Contains("red local") && NetworkMonitor.Diagnose(true,true,false,null,false).Contains("DNS") && NetworkMonitor.Diagnose(true,true,true,false,false).Contains("VPN") && NetworkMonitor.Diagnose(true,false,false,null,false).Contains("no confirmada"));
   var daily=new DailyNetworkLog(Path.GetFullPath("artifacts/network-rotation-test.txt"));
   daily.Append(new DateTime(2026,10,1,23,59,0),"OLD_DAY"); daily.Append(new DateTime(2026,10,2,0,0,0),"TODAY");
   Check("Daily TXT clears previous day including across restarts",!File.ReadAllText(daily.Path).Contains("OLD_DAY") && File.ReadAllText(daily.Path).Contains("TODAY"));
   var liveNetwork=await new NetworkMonitor(Path.GetFullPath("artifacts/network-live.txt")).SampleAsync("",443);
   Check("Live network sample saves adapters and probes",liveNetwork.Adapters.Count>0 && File.ReadAllText(liveNetwork.LogPath).Contains("Internet TCP"));
   var mock=Enumerable.Range(1,17).Select(i=>new ProcessRow(i,1,"test",i*20*1048576L,"",false,"","",TimeSpan.Zero,false)).Reverse().ToList();
   var first=ProcessMonitor.Page(mock,0); var second=ProcessMonitor.Page(mock,1);
   Check("Strict 100 MB filter, descending sort, pagination", first.Count==7 && first[0].Pid==17 && second.Count==5 && second[0].Pid==10 && first.All(x=>x.Bytes>100*1048576L));
   var chrome=mock[0] with { Name="chrome",Path="C:\\Chrome\\chrome.exe",Bytes=60*1048576L };
   var appMemory=MemoryApps.Group(new[]{chrome,chrome with {Pid=98},chrome with {Pid=99,Name="other",Path="C:\\other.exe",Bytes=110*1048576L},chrome with {Pid=100,Path="C:\\small.exe",Bytes=100*1048576L}});
   Check("App grouping includes small child processes and filters group totals",appMemory.Count==2 && appMemory[0].Name=="Google Chrome" && appMemory[0].Bytes==120*1048576L && appMemory[0].Processes.Count==2);
   Check("Protected member blocks whole app closure",!MemoryApps.Group(new[]{chrome,chrome with { Protected=true }})[0].CanClose);
   Check("GPU uses busiest engine and sums processes only within same engine",GpuMonitor.Aggregate(new[]{("pid_1_phys_0_eng_0",30d),("pid_2_phys_0_eng_0",40d),("pid_1_phys_0_eng_1",55d)})==70);
   Check("GPU usage is bounded at 100 percent",GpuMonitor.Aggregate(new[]{("pid_1_phys_0_eng_0",80d),("pid_2_phys_0_eng_0",80d)})==100);
   var liveGpu=new GpuMonitor().Read();
   checks.Add(new {name="Live GPU counters",passed=liveGpu.Name.Length>0 && liveGpu.Usage.HasValue && liveGpu.Capacity>0,adapter=liveGpu.Name,usage=liveGpu.Usage,capacity=liveGpu.Capacity});
   if(liveGpu.Name.Length==0 || !liveGpu.Usage.HasValue || liveGpu.Capacity==0) failed=true;
   using var fixture=JsonDocument.Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":24,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":100,"windowDurationMins":10080}}}}""");
   var limits=CodexUsage.Parse(fixture.RootElement);
   Check("Quota windows and remaining percentage",limits.Count==2 && limits[0].Remaining==76 && limits[1].Remaining==0);
   using var unknown=JsonDocument.Parse("""{"rateLimits":{"primary":null}}""");
   bool rejected=false; try { CodexUsage.Parse(unknown.RootElement); } catch(InvalidOperationException) { rejected=true; }
   Check("Unknown quotas are never zero",rejected);
   var claude=ClaudeWindow.ParseVisibleText("Current session\nResets in 3 hr\n24% used\nWeekly limits\nAll models\nResets Fri 5:00 PM\n81% used");
   Check("Claude rendered session and weekly percentages",claude.Count==2 && claude.Any(x=>x.Name=="Sesión" && x.Remaining==76) && claude.Any(x=>x.Name=="Todos los modelos" && x.Remaining==19));
   Check("Claude login or unrelated page shows no quota",ClaudeWindow.ParseVisibleText("Sign in\nDiscount 20%").Count==0);
   var three=ClaudeWindow.ParseVisibleText("Current session\nResets in 2 hr\n10% used\nWeekly limits\nAll models\nResets Fri\n20% used\nSonnet only\nResets Sat\n30% used");
   Check("Claude windows keep their own headings and reset dates",three.Count==3 && three[1].Name=="Todos los modelos" && three[1].Reset=="Resets Fri" && three[2].Name=="Sonnet");
   Check("WebView2 runtime is installed",!string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()));
   using var current=Process.GetCurrentProcess();
   Check("App process is protected",Native.Protected(current,current.MainModule!.FileName));
   using var worker=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--test-worker") { UseShellExecute=false,CreateNoWindow=true })!;
   try
   {
    await Task.Delay(2000);
    var tcp=Native.Connections();
    Check("Real TCP listener found by PID",tcp.Any(x=>x.Pid==worker.Id && x.Listening && x.Port>0));
    var scan=new ProcessMonitor().Scan(4);
    var row=scan.Single(x=>x.Pid==worker.Id);
    Check("Real process above 100 MB is listed",row.Bytes>100*1048576L && !row.Protected);
    bool identityGuard=false;
    try { Native.Close(row with { Started=row.Started+1 },true); } catch(InvalidOperationException) { identityGuard=true; }
    Check("PID reuse protection rejects changed identity",identityGuard && !worker.HasExited);
    Native.Close(row,true); await worker.WaitForExitAsync();
    Check("Only the spawned test process can be finalized",worker.HasExited);
   }
   finally { if(!worker.HasExited) worker.Kill(); }
   var nodePath=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Select(dir=>Path.Combine(dir,"node.exe")).FirstOrDefault(File.Exists);
   if(nodePath!=null)
   {
    string workerScript=Path.GetFullPath("artifacts/optimizer-worker.cjs");
    File.WriteAllText(workerScript,"require('net').createServer(s => {}).listen(0, '127.0.0.1');");
    var start=new ProcessStartInfo(nodePath) { UseShellExecute=false,CreateNoWindow=true }; start.ArgumentList.Add(workerScript);
    using var node=Process.Start(start)!;
    try
    {
     await Task.Delay(1000);
     var nodeRow=new ProcessMonitor().Scan(4).Single(x=>x.Pid==node.Id);
     Check("Own listening Node test worker is accessible and initially active",nodeRow.CanClose && !nodeRow.Inactive);
     var idleNode=nodeRow with { Inactive=true,Idle=TimeSpan.FromMinutes(4) };
     using(var client=new TcpClient())
     {
      await client.ConnectAsync(IPAddress.Loopback,nodeRow.PortLinks.Single().Port);
      bool activeBlocked=false;
      try { Native.CloseInactive(idleNode); } catch(InvalidOperationException) { activeBlocked=true; }
      Check("Optimizer rechecks live TCP before terminating",activeBlocked && !node.HasExited);
     }
     // This fixture server retains the accepted connection until killed; use a second worker for idle closure.
     node.Kill(); await node.WaitForExitAsync();
     using var idleWorker=Process.Start(start)!;
     try
     {
      await Task.Delay(1000);
      var idleRow=new ProcessMonitor().Scan(4).Single(x=>x.Pid==idleWorker.Id) with { Inactive=true,Idle=TimeSpan.FromMinutes(4) };
      bool reusedBlocked=false;
      try { Native.CloseInactive(idleRow with { Started=idleRow.Started+1 }); } catch(InvalidOperationException) { reusedBlocked=true; }
      Check("Optimizer rejects a changed exact process identity",reusedBlocked && !idleWorker.HasExited);
      Native.CloseInactive(idleRow); await idleWorker.WaitForExitAsync();
      Check("Optimizer can terminate only its idle Node fixture",idleWorker.HasExited);
     } finally { if(!idleWorker.HasExited) idleWorker.Kill(); }
    } finally { if(!node.HasExited) node.Kill(); }
   }
   List<UsageRow>? live=null;
   try { live=await CodexUsage.ReadAsync(""); checks.Add(new { name="Live Codex integration",passed=true,windows=live.Count }); }
   catch(Exception e) { checks.Add(new { name="Live Codex integration",passed=false,reason=e.Message }); failed=true; }
   var window=new MainWindow(); await window.ScanAsync(renderHidden:true);
   if(live!=null) { window.CodexQuotas.ItemsSource=live; window.CodexStatus.Text="Cuenta de Codex · conexión verificada"; }
   window.Measure(new Size(1180,840)); window.Arrange(new Rect(0,0,1180,840)); window.UpdateLayout();
   window.Tabs.SelectedItem=window.UsageTab; window.UpdateLayout(); Render(window,"artifacts/popup-usage.png");
   window.Tabs.SelectedItem=window.MemoryTab; window.UpdateLayout(); Render(window,"artifacts/popup-memory.png");
   window.Tabs.SelectedItem=window.RuntimeTab; window.UpdateLayout(); Render(window,"artifacts/popup-runtimes.png");
   await window.RefreshNetworkAsync(); window.Tabs.SelectedItem=window.NetworkTab; window.UpdateLayout(); Render(window,"artifacts/popup-network.png");
   Check("Native WPF views rendered",File.Exists("artifacts/popup-usage.png") && File.Exists("artifacts/popup-memory.png"));
  }
  catch(Exception e) { checks.Add(new { name="Unexpected error",passed=false,reason=e.ToString() }); failed=true; }
  File.WriteAllText("artifacts/self-test.json",JsonSerializer.Serialize(new { passed=!failed,checks },new JsonSerializerOptions { WriteIndented=true }));
  Environment.ExitCode=failed?1:0;
 }
 static void Render(MainWindow window,string path)
 {
  var content=(FrameworkElement)window.Content;
  window.Content=null;
  content.Measure(new Size(1180,840)); content.Arrange(new Rect(0,0,1180,840)); content.UpdateLayout();
  var bitmap=new RenderTargetBitmap(1180,840,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
  var pixels=new byte[1180*840*4]; bitmap.CopyPixels(pixels,1180*4,0);
  if(!pixels.Where((_,i)=>i%4==3).Any(x=>x!=0)) throw new InvalidOperationException("WPF rendered an empty view.");
  var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file=File.Create(path); encoder.Save(file);
  window.Content=content;
 }
}

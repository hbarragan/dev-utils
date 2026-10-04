using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
namespace AppUtilDev;

public record AdapterState(string Id,string Name,string Description,bool Up,bool Vpn,bool Physical,long Received,long Sent,long Errors,long Discards,string Addresses,string Gateways,string Dns)
{
 public string Detail => $"{(Up?"Conectado":"Desconectado")}{(Vpn?" · posible VPN":"")} · IP {Addresses}\nGateway {Gateways} · DNS {Dns}";
}
public record NetworkView(string Diagnosis,string Probes,List<string> Adapters,string Traffic,string Changes,string LogPath);
public sealed class DailyNetworkLog
{
 public string Path { get; }
 readonly object sync=new();
 public DailyNetworkLog(string path) { Path=path; }
 public void Append(DateTime localTime,string text)
 {
  lock(sync)
  {
   Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
   string header="# App Util RED · "+localTime.ToString("yyyy-MM-dd");
   string? existing=null;
   if(File.Exists(Path)) { using var reader=File.OpenText(Path); existing=reader.ReadLine(); }
   if(existing!=header || (File.Exists(Path) && new FileInfo(Path).Length>20*1024*1024))
    File.WriteAllText(Path,header+"\n",Encoding.UTF8);
   File.AppendAllText(Path,$"[{localTime:yyyy-MM-dd HH:mm:ss zzz}] {text}\n",Encoding.UTF8);
  }
 }
}
public sealed class NetworkMonitor
{
 readonly DailyNetworkLog log;
 List<AdapterState> previous=new();
 DateTime lastSample=DateTime.MinValue,lastEvents=DateTime.UtcNow.AddMinutes(-1);
 bool previouslySampled;
 string lastDiagnosis="";
 bool previousInternet;
 readonly Queue<string> changes=new();
 public NetworkMonitor(string? path=null) { log=new(path ?? System.IO.Path.Combine(Settings.DataDir,"network-today.txt")); }
 public static bool LooksVpn(string name) => new[]{"vpn","wireguard","wintun","tap-windows","anyconnect","fortinet","forticlient","globalprotect","tailscale","zerotier","pulse secure","juniper","sonicwall","netextender","tunnel"}.Any(x=>name.Contains(x,StringComparison.OrdinalIgnoreCase));
 public static List<AdapterState> ReadAdapters()
 {
  var rows=new List<AdapterState>();
  foreach(var nic in NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.NetworkInterfaceType!=NetworkInterfaceType.Loopback))
  {
   try
   {
    var props=nic.GetIPProperties(); var stat=nic.GetIPStatistics();
    bool vpn=nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel || LooksVpn(nic.Name+" "+nic.Description);
    bool physical=!vpn && nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 && !new[]{"virtual","vmware","hyper-v","vethernet","docker"}.Any(x=>nic.Description.Contains(x,StringComparison.OrdinalIgnoreCase)||nic.Name.Contains(x,StringComparison.OrdinalIgnoreCase));
    rows.Add(new(nic.Id,nic.Name,nic.Description,nic.OperationalStatus==OperationalStatus.Up,vpn,physical,stat.BytesReceived,stat.BytesSent,
     stat.IncomingPacketsWithErrors+stat.OutgoingPacketsWithErrors,stat.IncomingPacketsDiscarded+stat.OutgoingPacketsDiscarded,
     string.Join(", ",props.UnicastAddresses.Select(x=>x.Address).Where(x=>x.AddressFamily==AddressFamily.InterNetwork)),
     string.Join(", ",props.GatewayAddresses.Select(x=>x.Address)),string.Join(", ",props.DnsAddresses)));
   } catch { }
  }
  return rows;
 }
 public async Task<NetworkView> SampleAsync(string vpnHost,int vpnPort)
 {
  var now=DateTime.UtcNow; var adapters=ReadAdapters();
  var internetA=TcpProbe("1.1.1.1",443); var internetB=TcpProbe("8.8.8.8",443); var dnsTask=DnsProbe();
  var vpnTask=string.IsNullOrWhiteSpace(vpnHost)?Task.FromResult<bool?>(null):VpnProbe(vpnHost,vpnPort);
  var gateway=adapters.Where(x=>x.Up && x.Physical).SelectMany(x=>x.Gateways.Split(", ",StringSplitOptions.RemoveEmptyEntries)).FirstOrDefault();
  var gatewayTask=GatewayProbe(gateway);
  await Task.WhenAll(internetA,internetB,dnsTask,vpnTask,gatewayTask);
  bool internet=internetA.Result||internetB.Result;
  bool vpnDropped=previouslySampled && previous.Any(x=>x.Up && x.Vpn && !adapters.Any(y=>y.Id==x.Id && y.Up));
  string diagnosis=Diagnose(adapters.Any(x=>x.Up && x.Physical),internet,dnsTask.Result,vpnTask.Result,vpnDropped);
  if(previouslySampled && diagnosis!=lastDiagnosis) Change(diagnosis);
  string vpnAdapters=string.Join(", ",adapters.Where(x=>x.Vpn && x.Up).Select(x=>x.Name));
  string probes=$"Internet TCP: Cloudflare {(internetA.Result?"OK":"fallo")} · Google {(internetB.Result?"OK":"fallo")}\nDNS: {(dnsTask.Result?"OK":"fallo en ambas pruebas")} · Gateway {gateway??"sin gateway"}: {gatewayTask.Result}\nVPN adaptadores: {(vpnAdapters.Length>0?vpnAdapters:"ninguno detectado activo")}\nVPN destino {(vpnTask.Result.HasValue?$"{vpnHost}:{vpnPort} {(vpnTask.Result.Value?"OK":"sin respuesta")}":"interno sin configurar")}";
  var deltas=new List<string>(); var interfaceLines=new List<string>();
  foreach(var adapter in adapters)
  {
   var old=previous.FirstOrDefault(x=>x.Id==adapter.Id);
   double seconds=lastSample==DateTime.MinValue?0:(now-lastSample).TotalSeconds;
   string speed=old!=null && seconds>0 && seconds<60 ? $"↓ {Math.Max(0,adapter.Received-old.Received)/seconds/1024:N1} KB/s · ↑ {Math.Max(0,adapter.Sent-old.Sent)/seconds/1024:N1} KB/s":"Esperando segunda muestra";
   long errorDelta=old==null?0:Math.Max(0,adapter.Errors-old.Errors),discardDelta=old==null?0:Math.Max(0,adapter.Discards-old.Discards);
   string traffic=$"{adapter.Name}: {speed} · errores +{errorDelta}, descartes +{discardDelta}";
   if(adapter.Up) deltas.Add(traffic);
   interfaceLines.Add($"{adapter.Name} · {adapter.Description}\n{adapter.Detail}\n{speed}");
   if(previouslySampled && old==null) Change($"{adapter.Name}: nuevo adaptador detectado");
   if(old!=null && (adapter.Up!=old.Up || adapter.Addresses!=old.Addresses || adapter.Gateways!=old.Gateways || adapter.Dns!=old.Dns))
    Change($"{adapter.Name}: cambió estado/IP/gateway/DNS");
   if(errorDelta>0 || discardDelta>0) Change(traffic);
  }
  foreach(var missing in previous.Where(x=>!adapters.Any(y=>y.Id==x.Id))) Change($"{missing.Name}: adaptador desapareció");
  int tcpCount=0; try { tcpCount=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Count(x=>x.State==TcpState.Established); } catch { }
  string trafficText=deltas.Count==0?"Sin tráfico medible en adaptadores activos":string.Join("\n",deltas);
  trafficText+=$"\n{tcpCount} conexiones TCP establecidas";
  var eventLines=now-lastEvents>=TimeSpan.FromMinutes(1)?ReadEvents(lastEvents):"";
  if(now-lastEvents>=TimeSpan.FromMinutes(1)) lastEvents=now;
  string route=!previouslySampled || vpnDropped || internet!=previousInternet || adapters.Any(a=>!previous.Any(p=>p.Id==a.Id && p.Up==a.Up && p.Gateways==a.Gateways))?await RoutesAsync():"";
  log.Append(DateTime.Now,$"{diagnosis}\n{probes}\n{trafficText}\n"+string.Join("\n",interfaceLines)+"\n"+eventLines+route);
  previous=adapters; lastSample=now; lastDiagnosis=diagnosis; previousInternet=internet; previouslySampled=true;
  return new(diagnosis,probes,interfaceLines,trafficText,string.Join("\n",changes),log.Path);
 }
 void Change(string text) { changes.Enqueue($"{DateTime.Now:HH:mm:ss} · {text}"); while(changes.Count>8) changes.Dequeue(); }
 public static string Diagnose(bool physical,bool internet,bool dns,bool? vpn,bool vpnDropped)
 {
  if(!physical && !internet) return "Posible pérdida de red local: sin enlace físico activo ni Internet.";
  if(internet && (vpnDropped || vpn==false)) return "Posible problema de VPN o destino interno: Internet funciona.";
  if(internet && !dns) return "Posible problema DNS: Internet por IP funciona, pero DNS falla.";
  if(!internet) return "Internet no responde: revisar router, proveedor, VPN y rutas; causa no confirmada.";
  return "Conectividad disponible"+(vpn==true?" · destino VPN accesible":vpn==null?" · VPN sin prueba interna":"")+".";
 }
 public static async Task<bool> TcpProbe(string host,int port)
 {
  try { using var tcp=new TcpClient(); using var timeout=new System.Threading.CancellationTokenSource(2500); await tcp.ConnectAsync(host,port,timeout.Token); return true; } catch { return false; }
 }
 static async Task<bool?> VpnProbe(string host,int port) => await TcpProbe(host,port);
 static async Task<bool> DnsProbe()
 {
  async Task<bool> Resolve(string host)
  {
   try { using var timeout=new System.Threading.CancellationTokenSource(5000); return (await Dns.GetHostAddressesAsync(host,timeout.Token)).Length>0; } catch { return false; }
  }
  var probes=await Task.WhenAll(Resolve("example.com"),Resolve("www.microsoft.com")); return probes.Any(x=>x);
 }
 static async Task<string> GatewayProbe(string? ip)
 {
  if(ip==null) return "no disponible";
  try { using var ping=new Ping(); var result=await ping.SendPingAsync(ip,1500); return result.Status==IPStatus.Success?$"{result.RoundtripTime} ms":"no responde a ICMP (no concluyente)"; } catch { return "ICMP no disponible"; }
 }
 static string ReadEvents(DateTime since)
 {
  var lines=new List<string>();
  foreach(string channel in new[]{"System","Application"})
  {
   try
   {
    using var events=new EventLog(channel);
    for(int i=events.Entries.Count-1;i>=Math.Max(0,events.Entries.Count-150);i--)
    {
     var entry=events.Entries[i]; if(entry.TimeGenerated.ToUniversalTime()<=since) break;
     if(!new[]{"rasclient","rasman","tcpip","dns-client","ndis","wlan","nlasvc","netwtw","e1d","dhcp"}.Any(x=>entry.Source.Contains(x,StringComparison.OrdinalIgnoreCase))) continue;
     string message=entry.Message.Replace("\r"," ").Replace("\n"," "); if(message.Length>500) message=message[..500];
     lines.Add($"EVENTO {entry.TimeGenerated:HH:mm:ss} {channel}/{entry.Source} {entry.InstanceId}: {message}");
    }
   } catch { lines.Add($"EVENTOS {channel}: no disponibles (permisos o canal)."); }
  }
  return string.Join("\n",lines)+"\n";
 }
 static async Task<string> RoutesAsync()
 {
  try
  {
   using var p=new Process { StartInfo=new ProcessStartInfo("route.exe","print") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true } };
   p.Start(); string output=await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); return "RUTAS\n"+output;
  } catch { return "RUTAS: no disponibles\n"; }
 }
}

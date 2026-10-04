using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace AppUtilDev;
internal static class HardwareTests
{
 sealed class ProbeHandler(bool failUpload=false) : HttpMessageHandler
 {
  internal long Uploaded; internal int Downloads;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)
  {
   if(request.Method==HttpMethod.Get) {Downloads++;return new(HttpStatusCode.OK) {Content=new ByteArrayContent(new byte[1048576])};}
   Uploaded=(await request.Content!.ReadAsByteArrayAsync(cancel)).Length;
   return new(failUpload?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK) {Content=new StringContent("OK")};
  }
 }
 internal static async Task RunAsync(Action<string,bool> check,string root)
 {
  var handler=new ProbeHandler();using(var monitor=new InternetMonitor(handler))
  {
   var result=await monitor.TestAsync();check("Descarga y subida con cargas acotadas",result?.DownloadMbps>0 && result.UploadMbps>0 && handler.Downloads==1 && handler.Uploaded==262144);
  }
  using(var monitor=new InternetMonitor(new ProbeHandler(true)))
  {
   var result=await monitor.TestAsync();check("Fallo de subida no se representa como velocidad cero",result?.DownloadMbps>0 && result.UploadMbps==null && result.Error!=null);
  }
  AdapterState Adapter(long received,long sent,bool vpn=false)=>new(vpn?"vpn":"eth","Test","Test",true,vpn,!vpn,received,sent,0,0,"","","");
  var file=Path.Combine(root,"traffic-test.json");var traffic=new DailyTraffic(file);var day=new DateTime(2026,10,4);
  traffic.Sample(new[]{Adapter(1000,500),Adapter(7000,4000,true)},day);
  traffic.Sample(new[]{Adapter(3000,1300),Adapter(9000,6000,true)},day);
  check("Tráfico acumulado sin duplicar el adaptador VPN",traffic.Download==2000 && traffic.Upload==800);
  traffic=new DailyTraffic(file);traffic.Sample(new[]{Adapter(4000,1600)},day);
  check("Los GB acumulados sobreviven al reinicio",traffic.Download==3000 && traffic.Upload==1100);
  traffic.Sample(new[]{Adapter(200,100)},day);
  check("Reinicio de contadores conserva el total diario",traffic.Download==3200 && traffic.Upload==1200);
  traffic.Sample(new[]{Adapter(700,300)},day.AddDays(1));
  check("El acumulado comienza de nuevo al cambiar de día",traffic.Download==0 && traffic.Upload==0);
  var disk=new DiskSpace("C:\\",25_000_000_000,100_000_000_000);check("Porcentaje libre y GB coherentes",disk.FreePercent==25 && disk.Summary.Contains("25 / 100 GB"));
  check("Discos reales con espacio válido",DiskMonitor.Read().All(d=>d.Total>0 && d.Free>=0 && d.Free<=d.Total));
 }
}

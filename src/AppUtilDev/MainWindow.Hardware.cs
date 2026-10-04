using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace AppUtilDev;
public partial class MainWindow
{
 readonly InternetMonitor internet=new();
 internal static string? TrafficPathOverride;
 readonly DailyTraffic dailyTraffic=new(TrafficPathOverride);
 internal static readonly TimeSpan InternetInterval=TimeSpan.FromMinutes(1);
 DateTime nextInternet=DateTime.MinValue,nextDisk=DateTime.MinValue;
 DiskSpace[] disks=Array.Empty<DiskSpace>();
 int diskPage,runtimePage;
 internal async Task RefreshInternetAsync()
 {
  var sample=await internet.TestAsync();
  if(sample==null) return;
  string Format(double? value)=>value.HasValue?$"{value.Value:N1}":"—";
  NetworkSpeed.Text=$"↓ {Format(sample.DownloadMbps)} / ↑ {Format(sample.UploadMbps)} Mb/s";
  NetworkSpeedStatus.Text=sample.Error??$"Última prueba {sample.At:HH:mm:ss} · cada minuto";
  HomeInternetSpeed.Text=$"↓ {Format(sample.DownloadMbps)} / ↑ {Format(sample.UploadMbps)} Mb/s";
  HomeInternetSpeed.ToolTip=sample.Error??$"Prueba ligera de transferencia · {sample.At:HH:mm:ss}; no mide la capacidad máxima de la línea.";
  RenderHomeIndicators();
  try {System.IO.Directory.CreateDirectory(Settings.DataDir);System.IO.File.WriteAllText(System.IO.Path.Combine(Settings.DataDir,"internet-last.json"),System.Text.Json.JsonSerializer.Serialize(sample));} catch { }
 }
 internal void RefreshTraffic()
 {
  dailyTraffic.Sample(NetworkMonitor.ReadAdapters(),DateTime.Now);
  HomeTraffic.Text=$"{dailyTraffic.Download/1_000_000_000d:N2} GB / {dailyTraffic.Upload/1_000_000_000d:N2} GB";
  HomeTraffic.ToolTip="Descarga / subida de hoy en adaptadores físicos; incluye tráfico local y las pruebas. Se conserva al reiniciar el portal.";
 }
 internal async Task RefreshDisksAsync()
 {
  disks=await Task.Run(DiskMonitor.Read);RenderDisks();
 }
 void RenderDisks()
 {
  int pages=Math.Max(1,(disks.Length+2)/3);diskPage=Math.Clamp(diskPage,0,pages-1);
  DiskList.ItemsSource=disks.Skip(diskPage*3).Take(3).ToArray();DiskPage.Text=$"{diskPage+1} / {pages}";
  DiskPrev.IsEnabled=diskPage>0;DiskNext.IsEnabled=diskPage<pages-1;
  HomeDisks.Text=disks.Length==0?"Sin discos disponibles":string.Join("\n",disks.Take(2).Select(d=>d.Summary));
  HomeDisks.ToolTip=string.Join("\n",disks.Select(d=>d.Summary));
  RenderHomeIndicators();
 }
 void DiskPrev_Click(object sender,RoutedEventArgs e) {diskPage--;RenderDisks();}
 void DiskNext_Click(object sender,RoutedEventArgs e) {diskPage++;RenderDisks();}
 void RuntimePrev_Click(object sender,RoutedEventArgs e) {runtimePage--;RenderRows();}
 void RuntimeNext_Click(object sender,RoutedEventArgs e) {runtimePage++;RenderRows();}
 void RuntimeFilter_Changed(object sender,System.Windows.Controls.SelectionChangedEventArgs e) {runtimePage=0;if(RuntimeList!=null && RuntimePage!=null) RenderRows();}
 async void TestInternet_Click(object sender,RoutedEventArgs e)=>await RefreshInternetAsync();
}

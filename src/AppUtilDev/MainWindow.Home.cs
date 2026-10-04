using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AppUtilDev;

public partial class MainWindow
{
 bool hasProcessSample;
 string homeSqlIndicator="—";
 Task? homeSqlTask;
 internal void RenderHome()
 {
  if(HomeRuntimeSummary==null) return;
  EquipmentSummary.Visibility=HomeTab.IsSelected?Visibility.Hidden:Visibility.Visible;
  HomeIndicators.Visibility=HomeTab.IsSelected?Visibility.Visible:Visibility.Hidden;
  if(hasProcessSample)
  {
   var runtime=rows.Where(r=>r.Family!="").ToList();
   var servers=runtime.Where(r=>r.PortLinks.Length>0).OrderByDescending(r=>r.Owner=="").ThenByDescending(r=>r.Bytes).ToList();
   HomeRuntimeSummary.Text=$"{servers.Count} con puerto · {runtime.Count} procesos Node / Java";
   var idle=runtime.Where(r=>r.CanOptimize).ToList();
   HomeRuntimeHint.Text=idle.Count>0?$"{idle.Count} inactivos para revisar · {idle.Sum(r=>r.Bytes)/1048576d:N0} MB":"";
   HomeRuntimeHint.Visibility=idle.Count>0?Visibility.Visible:Visibility.Collapsed;
   HomePorts.ItemsSource=servers.Take(2).ToArray();
   var apps=MemoryApps.Group(rows);
   HomeMemorySummary.Text=$"{apps.Sum(a=>a.Bytes)/1073741824d:N1} GB";
   HomeMemorySummary.ToolTip=$"RAM de {apps.Count} aplicaciones que superan 100 MB; no es toda la RAM del equipo.";
   HomeMemoryTop.Text=string.Join("\n",apps.Take(2).Select(a=>$"{a.Name} · {a.Memory}"));
   HomeUpdated.Text=DateTime.Now.ToString("HH:mm:ss");
  }
  var codex=CodexQuotas.ItemsSource?.Cast<UsageRow>().Take(2).Select(q=>q with {Name="Codex · "+q.Name}) ?? Enumerable.Empty<UsageRow>();
  var claudeUsage=ClaudeQuotas.ItemsSource?.Cast<UsageRow>().Take(2).Select(q=>q with {Name="Claude · "+q.Name}) ?? Enumerable.Empty<UsageRow>();
  var quotas=codex.OrderBy(q=>q.Remaining).Take(1).Concat(claudeUsage.OrderBy(q=>q.Remaining).Take(1)).ToArray(); HomeQuotas.ItemsSource=quotas;
  HomeAiHint.Text=quotas.Any(q=>q.Remaining<=10)?"Cuota baja · 10% o menos":quotas.Length>0?"":"Conecta tus cuentas en Consumo IA.";
  HomeAiHint.Visibility=HomeAiHint.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
  HomeNetworkSummary.Text=NetworkDiagnosis.Text;
  var vpn=NetworkProbes.Text.Split('\n').Where(line=>line.StartsWith("VPN",StringComparison.Ordinal)).ToArray();
  HomeVpn.Text=settings.VpnHost.Length>0?$"Destino VPN · {settings.VpnHost}:{settings.VpnPort}":vpn.FirstOrDefault(line=>line.StartsWith("VPN adaptadores:") && !line.Contains("ninguno"))??"";
  HomeVpn.Visibility=HomeVpn.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
  RenderHomeIndicators();
 }
 void RenderHomeIndicators()
 {
  if(HomeIndicatorRuntime==null) return;
  HomeIndicatorRuntime.Text=hasProcessSample?rows.Count(r=>r.Family!="").ToString():"—";
  HomeIndicatorMemory.Text=hasProcessSample?$"{MemoryApps.Group(rows).Sum(a=>a.Bytes)/1073741824d:N1} GB":"—";
  var quotas=HomeQuotas.ItemsSource?.Cast<UsageRow>().ToArray()??Array.Empty<UsageRow>();
  HomeIndicatorAi.Text=quotas.Length>0?$"{quotas.Min(q=>q.Remaining):0.#}%":"—";
  HomeIndicatorSql.Text=homeSqlIndicator;
  HomeIndicatorSql.ToolTip=HomeSqlSummary.Text;
  HomeIndicatorDisk.Text=disks.Length>0?$"{disks[0].FreePercent:N0}% libre":"—";
  HomeIndicatorDisk.ToolTip=string.Join("\n",disks.Select(d=>d.Summary));
  var sample=internet.Last;
  HomeIndicatorInternet.Text=sample?.DownloadMbps is double down && sample.UploadMbps is double up?$"{down:N0}/{up:N0}":"—";
  HomeIndicatorInternet.ToolTip=sample==null?"Prueba pendiente":HomeInternetSpeed.Text+" · descarga/subida";
 }
 internal Task RefreshHomeSqlAsync()
 {
  if(homeSqlTask==null || homeSqlTask.IsCompleted) homeSqlTask=ReadHomeSqlAsync();
  return homeSqlTask;
 }
 async Task ReadHomeSqlAsync()
 {
  try
  {
   var summary=await SqlPortal.ReadSummaryAsync();
   homeSqlIndicator=$"{summary.Running}/{summary.Count}";
   HomeSqlSummary.Text=summary.Count==0?"Sin entornos todavía":$"{summary.Count} {(summary.Count==1?"guardado":"guardados")} · {summary.Running} {(summary.Running==1?"activo":"activos")}";
   HomeSqlDetails.Text=summary.Count==0?"Crea tu primera base para desarrollo.":$"{summary.MemoryMb:N0} MB de motores{(summary.Partial?" · parcial":"")}\n"+summary.Details;
   if(summary.Busy) HomeSqlDetails.Text+="\nOperación en curso…";
  }
  catch { homeSqlIndicator="—"; HomeSqlSummary.Text="Gestor no disponible"; HomeSqlDetails.Text="Abre Bases de datos y pulsa Reintentar."; }
  finally { RenderHomeIndicators(); }
 }
 void HomeNavigate_Click(object sender,RoutedEventArgs e)
 {
  Tabs.SelectedItem=(((Button)sender).Tag as string) switch
  {
   "runtimes"=>RuntimeTab,"usage"=>UsageTab,"memory"=>MemoryTab,"network"=>NetworkTab,"sql"=>DatabaseTab,"disks"=>DiskTab,"settings"=>SettingsTab,_=>HomeTab
  };
 }
}

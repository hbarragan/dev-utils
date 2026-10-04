using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
namespace AppUtilDev;

public partial class MainWindow : Window
{
 readonly Settings settings=Settings.Load();
 ProcessMonitor monitor=new();
 readonly NetworkMonitor network=new();
 readonly GpuMonitor gpu=new();
 DateTime nextNetwork=DateTime.MinValue;
 bool readingNetwork;
 readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromSeconds(5) };
 List<ProcessRow> rows=new();
 readonly Dictionary<string,bool> expandedGroups=new();
 readonly Dictionary<string,bool> expandedMemoryApps=new(StringComparer.OrdinalIgnoreCase);
 ClaudeWindow? claude;
 int page;
 bool scanning, readingUsage, initializing=true, exiting, optimizing, dialog;
 DateTime nextUsage=DateTime.MinValue, lastScan=DateTime.UtcNow;
 public MainWindow()
 {
  InitializeComponent();
  Tabs.SelectedItem=HomeTab;
  RenderQuotaPages();RenderAdapterPage();
  AutoStart.IsChecked=StartupEnabled();
  foreach(ComboBoxItem item in IdleChoice.Items) if(item.Content.ToString()==settings.IdleMinutes.ToString()) IdleChoice.SelectedItem=item;
  if(IdleChoice.SelectedIndex<0) IdleChoice.SelectedIndex=1;
  UpdateCodexLabel(); initializing=false;
  VpnHostBox.Text=settings.VpnHost; VpnPortBox.Text=settings.VpnPort.ToString();
  Deactivated+=(_,_)=> { if(!dialog && !exiting && !portalMode) Hide(); };
  Closing+=(_,e)=> { if(!exiting) { e.Cancel=true; Hide(); } };
  timer.Tick+=async (_,_)=>await TickAsync();
  RenderHome();
 }
 public void StartMonitoring() { timer.Start(); _=TickAsync(); }
 async Task TickAsync()
 {
  if(!optimizing) await ScanAsync();
  RefreshTraffic();
  if(DateTime.UtcNow>=nextInternet) {nextInternet=DateTime.UtcNow+InternetInterval;_=RefreshInternetAsync();}
  if(DateTime.UtcNow>=nextDisk) {nextDisk=DateTime.UtcNow.AddSeconds(30);_=RefreshDisksAsync();}
  if(DateTime.UtcNow>=nextNetwork) { nextNetwork=DateTime.UtcNow.AddSeconds(15); _=RefreshNetworkAsync(); }
  if(IsVisible && DateTime.UtcNow>=nextUsage) { nextUsage=DateTime.UtcNow.AddMinutes(5); _=RefreshUsageAsync(); claude?.RefreshPage(); }
  if(IsVisible && HomeTab.IsSelected) _=RefreshHomeSqlAsync();
 }
 public async Task ScanAsync(bool renderHidden=false)
 {
  if(scanning) return;
  scanning=true;
  try
  {
   if(DateTime.UtcNow-lastScan>TimeSpan.FromSeconds(20)) monitor=new();
   rows=await Task.Run(()=>monitor.Scan(settings.IdleMinutes)); lastScan=DateTime.UtcNow;
   hasProcessSample=true;
   if(!IsVisible && !renderHidden) return;
   RenderRows();
   using(var self=Process.GetCurrentProcess()) SelfMemory.Text=$"RAM app · {self.WorkingSet64/1048576d:N0} MB";
   var gpuSample=await Task.Run(gpu.Read);
   GpuLabel.Text=gpuSample.Label; GpuMemory.Text=gpuSample.Memory; GpuBar.Value=gpuSample.Usage??0;
   GpuBar.Visibility=gpuSample.Usage.HasValue?Visibility.Visible:Visibility.Collapsed;
   RenderHome();
   ScanStatus.Text="Actualizado "+DateTime.Now.ToString("HH:mm:ss");
  }
  catch { ScanStatus.Text="No se pudo leer TCP"; Footer.Text="No se ha actualizado el monitor. Se reintentará en 5 s."; }
  finally { scanning=false; }
 }
 void RenderRows()
 {
  var large=MemoryApps.Group(rows);
  MemorySummary.Text=$"{large.Count} apps > 100 MB · {large.Sum(x=>x.Bytes)/1073741824d:N1} GB";
  int size=4;int pages=Math.Max(1,(large.Count+size-1)/size); page=Math.Clamp(page,0,pages-1);
  MemoryList.ItemsSource=MemoryApps.Page(large,page,size).Select(g=>expandedMemoryApps.TryGetValue(g.Key,out bool expanded)?g with {Expanded=expanded}:g).ToList();
  PageLabel.Text=$"{page+1} / {pages}"; PrevButton.IsEnabled=page>0; NextButton.IsEnabled=page<pages-1;
  MemoryEmpty.Text=large.Count==0?"No hay aplicaciones por encima del umbral.":"";
  var runtime=rows.Where(r=>r.Family!="").Where(r=>RuntimeFilter.SelectedIndex switch {1=>r.PortLinks.Length>0,2=>r.Family=="Node.js",3=>r.Family=="Java / JDK",4=>r.Protected,_=>true}).OrderByDescending(r=>r.PortLinks.Length>0).ThenByDescending(r=>r.Bytes).ToList();
  int runtimeSize=4;int runtimePages=Math.Max(1,(runtime.Count+runtimeSize-1)/runtimeSize);runtimePage=Math.Clamp(runtimePage,0,runtimePages-1);
  RuntimeList.ItemsSource=runtime.Skip(runtimePage*runtimeSize).Take(runtimeSize).ToArray();RuntimePage.Text=$"{runtimePage+1} / {runtimePages} · {runtime.Count} procesos";
  RuntimePrev.IsEnabled=runtimePage>0;RuntimeNext.IsEnabled=runtimePage<runtimePages-1;
  RuntimeEmpty.Text=rows.Any(x=>x.Family!="")?"":"No se están ejecutando procesos Node.js o Java.";
  OptimizeButton.IsEnabled=!optimizing;
  OptimizeButton.Content=$"Optimizar ({rows.Count(x=>x.CanOptimize)})";
  RenderHome();
 }
 async void Optimize_Click(object sender,RoutedEventArgs e)
 {
  if(optimizing || scanning) return;
  optimizing=true; OptimizeButton.IsEnabled=false; dialog=true;
  try
  {
   await ScanAsync();
   var candidates=rows.Where(x=>x.CanOptimize).ToList();
   if(candidates.Count==0) { Footer.Text="No hay Java o Node inactivos que puedan finalizarse. Espera el umbral de Ajustes."; return; }
   string list=string.Join("\n",candidates.Select(x=>$"{x.Name} · PID {x.Pid} · {x.Memory}"));
   if(MessageBox.Show(this,$"Se finalizarán {candidates.Count} procesos sin actividad detectada ({candidates.Sum(x=>x.Bytes)/1048576d:N0} MB residentes).\n\n{list}\n\nSe volverá a comprobar su actividad. Puedes perder trabajo no guardado. ¿Continuar?","Optimizar Node / Java",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
   // Resample after the confirmation; a process may have resumed while the dialog was open.
   await ScanAsync();
   var fresh=rows.ToDictionary(x=>(x.Pid,x.Started));
   var result=await Task.Run(()=>
   {
    int closed=0,skipped=0; long bytes=0;
    foreach(var candidate in candidates)
    {
     if(!fresh.TryGetValue((candidate.Pid,candidate.Started),out var row) || !row.CanOptimize) { skipped++; continue; }
     try { Native.CloseInactive(row); closed++; bytes+=row.Bytes; } catch { skipped++; }
    }
    return (closed,skipped,bytes);
   });
   Footer.Text=$"Optimización: {result.closed} finalizados · {result.skipped} omitidos · {result.bytes/1048576d:N0} MB residentes en procesos finalizados.";
   await ScanAsync();
  }
  catch { Footer.Text="No se pudo comprobar la actividad. Vuelve a intentarlo."; }
  finally { optimizing=false; dialog=false; OptimizeButton.IsEnabled=true; }
 }
 void MemoryApp_ExpansionChanged(object sender,RoutedEventArgs e)
 {
  if(sender is Expander expander && ReferenceEquals(e.OriginalSource,sender) && expander.DataContext is MemoryApp app) expandedMemoryApps[app.Key]=expander.IsExpanded;
 }
 async Task CloseMemoryAppAsync(MemoryApp app,bool force)
 {
  if(!app.CanClose) return;
  dialog=true;
  try
  {
   if(MessageBox.Show(this,$"¿{(force?"Finalizar":"Solicitar el cierre de")} {app.Name}?\nIncluye {app.Processes.Count} procesos. Puedes perder trabajo no guardado.","Cerrar aplicación",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
   var result=await Task.Run(()=>
   {
    int closed=0,ended=0,errors=0;
    foreach(var row in app.Processes)
    {
     try { Native.Close(row,force); closed++; }
     catch(ArgumentException) { ended++; }
     catch { errors++; }
    }
    return (closed,ended,errors);
   });
   Footer.Text=$"{app.Name}: {result.closed} {(force?"finalizados":"solicitudes de cierre")}, {result.ended} ya cerrados, {result.errors} no aceptaron el cierre.";
   await Task.Delay(500); await ScanAsync();
  }
  finally { dialog=false; }
 }
 async void CloseMemoryApp_Click(object sender,RoutedEventArgs e) { if(((Button)sender).Tag is MemoryApp app) await CloseMemoryAppAsync(app,false); }
 async void KillMemoryApp_Click(object sender,RoutedEventArgs e) { if(((Button)sender).Tag is MemoryApp app) await CloseMemoryAppAsync(app,true); }
 void RuntimeGroup_ExpansionChanged(object sender,RoutedEventArgs e)
 {
  if(sender is Expander expander && ReferenceEquals(e.OriginalSource,sender) && expander.DataContext is RuntimeGroup group) expandedGroups[group.Name]=expander.IsExpanded;
 }
 public async Task RefreshNetworkAsync()
 {
  if(readingNetwork) return; readingNetwork=true;
  string host=settings.VpnHost; int port=settings.VpnPort;
  try
  {
   var view=await Task.Run(()=>network.SampleAsync(host,port));
   NetworkDiagnosis.Text=view.Diagnosis; NetworkProbes.Text=view.Probes; NetworkTraffic.Text=view.Traffic;
   allAdapters=view.Adapters.ToArray(); RenderAdapterPage(); NetworkChanges.Text=view.Changes.Length>0?view.Changes:"Sin cambios desde el inicio.";
   NetworkTime.Text="Actualizado "+DateTime.Now.ToString("HH:mm:ss");
  }
  catch(Exception e) { NetworkDiagnosis.Text="No se pudo completar la muestra de red."; NetworkTime.Text=e.Message; }
  finally { readingNetwork=false; RenderHome(); }
 }
 void SaveVpn_Click(object sender,RoutedEventArgs e)
 {
  string host=VpnHostBox.Text.Trim();
  if(!int.TryParse(VpnPortBox.Text,out int port) || port<1 || port>65535 || (host!="" && Uri.CheckHostName(host)==UriHostNameType.Unknown)) { NetworkTime.Text="Indica un host/IP y un puerto entre 1 y 65535."; return; }
  settings.VpnHost=host; settings.VpnPort=port; settings.Save(); _=RefreshNetworkAsync();
 }
 async void RefreshNetwork_Click(object sender,RoutedEventArgs e)=>await RefreshNetworkAsync();
 async void OpenNetworkLog_Click(object sender,RoutedEventArgs e)
 {
  await RefreshNetworkAsync();
  try { Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Settings.DataDir,"network-today.txt")) { UseShellExecute=true }); }
  catch { NetworkTime.Text="No se pudo abrir el TXT. Revisa el resultado de la muestra."; }
 }
 public async Task RefreshUsageAsync()
 {
  if(readingUsage) return; readingUsage=true; CodexStatus.Text="Consultando límites…";
  try { allCodex=(await CodexUsage.ReadAsync(settings.CodexPath)).ToArray(); RenderQuotaPages(); CodexStatus.Text="Cuenta de Codex · leído "+DateTime.Now.ToString("HH:mm"); }
  catch(Exception e) { allCodex=Array.Empty<UsageRow>(); RenderQuotaPages(); CodexStatus.Text=e is OperationCanceledException?"Tiempo de espera agotado. Se reintentará.":e.Message; }
  finally { readingUsage=false; RenderHome(); }
 }
 public void ShowPopup()
 {
  SetPortalMode(false);
  if(WindowState==WindowState.Minimized) WindowState=WindowState.Normal;
  Show();
  PositionPortal(false);
  Activate(); RenderRows(); _=TickAsync();
 }
 async Task CloseAsync(ProcessRow row,bool force)
 {
  dialog=true;
  try
  {
   if(MessageBox.Show(this,force?$"¿Finalizar {row.Name} (PID {row.Pid})?\nPuedes perder el trabajo no guardado. Solo se cerrará este proceso.":$"¿Solicitar el cierre de {row.Name} (PID {row.Pid})?",
    force?"Finalizar proceso":"Cerrar proceso",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
   await Task.Run(()=>Native.Close(row,force));
   Footer.Text=force?$"Se ha finalizado {row.Name}.":$"Se ha solicitado el cierre de {row.Name}.";
   await Task.Delay(500); await ScanAsync();
  }
  catch(Exception e) { MessageBox.Show(this,e.Message,"No se pudo cerrar",MessageBoxButton.OK,MessageBoxImage.Information); }
  finally { dialog=false; }
 }
 async void CloseProcess_Click(object sender,RoutedEventArgs e) { if(((Button)sender).Tag is ProcessRow row) await CloseAsync(row,false); }
 async void KillProcess_Click(object sender,RoutedEventArgs e) { if(((Button)sender).Tag is ProcessRow row) await CloseAsync(row,true); }
 void Prev_Click(object sender,RoutedEventArgs e) { page--; RenderRows(); }
 void Next_Click(object sender,RoutedEventArgs e) { page++; RenderRows(); }
 void Hide_Click(object sender,RoutedEventArgs e)=>Hide();
 void PortLink_RequestNavigate(object sender,System.Windows.Navigation.RequestNavigateEventArgs e)
 {
  e.Handled=true;
  try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute=true }); }
  catch { Footer.Text="No se pudo abrir el navegador predeterminado."; }
 }
 async void RefreshUsage_Click(object sender,RoutedEventArgs e) { nextUsage=DateTime.UtcNow.AddMinutes(5); await RefreshUsageAsync(); claude?.RefreshPage(); }
 void Claude_Click(object sender,RoutedEventArgs e)=>ConnectClaude(true);
 public async void ConnectClaude(bool visible)
 {
  try
  {
   if(claude==null)
   {
    claude=new(); claude.UsageChanged+=(usage,status)=> { allClaude=usage.ToArray(); RenderQuotaPages(); ClaudeStatus.Text=status; RenderHome(); };
    var connection=claude;
    connection.IsVisibleChanged+=(_,_)=>
    {
     if(connection.IsVisible) return;
     Dispatcher.BeginInvoke(()=>
     {
      if(connection.IsVisible || claude!=connection) return;
      connection.DisposeBrowser(); connection.Close(); claude=null;
     });
    };
   }
   if(visible) { settings.ClaudeConnected=true; settings.Save(); claude.Owner=this; claude.ShowInTaskbar=true; claude.Show(); claude.Activate(); }
   else
   {
    var connection=claude;
    connection.ShowActivated=false; connection.ShowInTaskbar=false; connection.Opacity=0;
    connection.WindowStartupLocation=WindowStartupLocation.Manual; connection.Left=-32000; connection.Top=-32000;
    connection.Show(); await connection.InitializeAsync(); connection.Hide();
    connection.Opacity=1; connection.ShowActivated=true; connection.Left=100; connection.Top=100;
   }
  }
  catch { ClaudeStatus.Text="No se ha podido abrir la conexión de Claude."; }
 }
 void ChatGpt_Click(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo("https://chatgpt.com") { UseShellExecute=true });
 void IdleChoice_Changed(object sender,SelectionChangedEventArgs e)
 {
  if(initializing) return;
  settings.IdleMinutes=int.Parse(((ComboBoxItem)IdleChoice.SelectedItem).Content.ToString()!); settings.Save(); _=ScanAsync();
 }
 const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 static bool StartupEnabled() { using var key=Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("AppUtilDev")!=null; }
 void AutoStart_Changed(object sender,RoutedEventArgs e)
 {
  if(initializing) return;
  try
  {
   using var key=Registry.CurrentUser.CreateSubKey(RunKey);
   if(AutoStart.IsChecked==true) key.SetValue("AppUtilDev","\""+Environment.ProcessPath+"\"");
   else key.DeleteValue("AppUtilDev",false);
  } catch { Footer.Text="No se pudo modificar el inicio automático."; }
 }
 void UpdateCodexLabel() { try { CodexPathLabel.Text=CodexUsage.FindExecutable(settings.CodexPath); } catch { CodexPathLabel.Text="Codex no encontrado."; } }
 void PickCodex_Click(object sender,RoutedEventArgs e)
 {
  dialog=true;
  try
  {
   var picker=new OpenFileDialog { Filter="Codex (codex.exe)|codex.exe",Title="Seleccionar Codex" };
   if(picker.ShowDialog(this)==true) { settings.CodexPath=picker.FileName; settings.Save(); UpdateCodexLabel(); _=RefreshUsageAsync(); }
  } finally { dialog=false; }
 }
 async void DisconnectClaude_Click(object sender,RoutedEventArgs e)
 {
  settings.ClaudeConnected=false; settings.Save();
  try
  {
   // The browser may already have been released; reopen its local profile briefly to clear it.
   if(claude==null)
   {
    claude=new() { ShowActivated=false,ShowInTaskbar=false,Opacity=0,WindowStartupLocation=WindowStartupLocation.Manual,Left=-32000,Top=-32000 };
    claude.Show(); await claude.InitializeAsync();
   }
   await claude.DisconnectAsync();
   allClaude=Array.Empty<UsageRow>(); RenderQuotaPages(); ClaudeStatus.Text="Desconectado.";
  }
  catch { ClaudeStatus.Text="No se pudo borrar la sesión local de Claude. Vuelve a intentarlo."; }
  finally { claude?.DisposeBrowser(); claude?.Close(); claude=null; }
 }
 public async void ExitApp() { if(exiting) return; exiting=true; try { await SqlPortal.StopAsync(); internet.Dispose(); timer.Stop(); SqlBrowser.Dispose(); claude?.DisposeBrowser(); ((App)Application.Current).ExitApp(); } catch(Exception e) { exiting=false; MessageBox.Show(this,e.Message,"No se pudo salir"); } }
 void Exit_Click(object sender,RoutedEventArgs e)=>ExitApp();
}




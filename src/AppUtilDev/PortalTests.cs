using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using SqlLight;

namespace AppUtilDev;

internal static class PortalTests
{
 static IEnumerable<System.Windows.Controls.TextBlock> Texts(System.Windows.DependencyObject parent)
 {
  for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
  {
   var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);
   if(child is System.Windows.Controls.TextBlock text) yield return text;
   foreach(var descendant in Texts(child)) yield return descendant;
  }
 }
 internal static async Task RunAsync()
 {
  Directory.CreateDirectory("artifacts");
  var root=Path.GetFullPath(Path.Combine("artifacts","portal-test-"+Guid.NewGuid().ToString("N")));
  SqlPortal.Root=root;
  Settings.DataDirOverride=Path.Combine(root,"settings");
  MainWindow.BrowserProfile=Path.Combine(root,"browser");
  MainWindow.TrafficPathOverride=Path.Combine(root,"traffic-preview.json");
  var checks=new List<object>(); bool passed=true; MainWindow? window=null;
  void Check(string name,bool ok) { checks.Add(new {name,passed=ok}); if(!ok) passed=false; }
  try
  {
   await HardwareTests.RunAsync(Check,root);
   var url=await SqlPortal.StartAsync();
   using var client=new HttpClient {BaseAddress=new Uri(url),Timeout=TimeSpan.FromSeconds(20)};
   var html=await client.GetStringAsync("");
   Check("Panel SQL servido por el host integrado",html.Contains("<h1>Bases de datos</h1>") && html.Contains("#101B2A"));
   Check("API protegida sin token",(await client.GetAsync("api/state")).StatusCode==HttpStatusCode.Forbidden);
   var token=Regex.Match(html,"const token='([A-F0-9]+)'").Groups[1].Value;
   client.DefaultRequestHeaders.Add("X-SqlLight-Token",token);
   var state=JsonDocument.Parse(await client.GetStringAsync("api/state"));
   Check("Entorno de pruebas aislado y vacío",state.RootElement.GetProperty("profiles").GetArrayLength()==0 && state.RootElement.GetProperty("appPid").GetInt32()==Environment.ProcessId);
   using(var badOrigin=new HttpRequestMessage(HttpMethod.Get,"api/state"))
   {
    badOrigin.Headers.Add("Origin","https://example.com");
    Check("Origen ajeno bloqueado",(await client.SendAsync(badOrigin)).StatusCode==HttpStatusCode.Forbidden);
   }
   var body=JsonSerializer.Serialize(new {engine="postgres",database="portal_test",username="devuser",password="test-local",port=55432});
   var response=await client.PostAsync("api/profiles",new StringContent(body,Encoding.UTF8,"application/json"));
   Check("Creación de perfil desde API",response.IsSuccessStatusCode);
   var saved=new Store(root).Snapshot();
   Check("Perfil persistido con credenciales cifradas",saved.Length==1 && saved[0].Database=="portal_test" && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"private","state.dat"))).Contains("test-local"));
   var invalid=await client.PostAsync("api/profiles",new StringContent(body.Replace("55432","80"),Encoding.UTF8,"application/json"));
   Check("Puerto inválido rechazado",!invalid.IsSuccessStatusCode && new Store(root).Snapshot().Length==1);
   Check("Telemetría accesible",(await client.GetAsync("api/telemetry")).IsSuccessStatusCode);
   Check("Almacenamiento accesible",(await client.GetAsync("api/storage")).IsSuccessStatusCode);
   await SqlPortal.StopAsync(); await SqlPortal.StartAsync();
   Check("Perfiles conservados al reiniciar",new Store(root).Snapshot().Length==1);
   window=new MainWindow {Opacity=0,ShowInTaskbar=false}; window.Show();
   Check("Home seleccionado al abrir",window.Tabs.SelectedItem==window.HomeTab);
   await window.RefreshHomeSqlAsync();
   Check("Home refleja los perfiles reales del gestor",window.HomeSqlSummary.Text=="1 guardado · 0 activos" && window.HomeSqlDetails.Text.Contains("portal_test") && window.HomeSqlDetails.Text.Contains(":55432"));
   await window.ScanAsync(renderHidden:true);
   Check("Memoria muestra cuatro aplicaciones por página",window.MemoryList.ItemsSource.Cast<MemoryApp>().Count()==Math.Min(4,int.Parse(window.MemorySummary.Text.Split(' ')[0])));
   window.RefreshTraffic();await window.RefreshDisksAsync();
   Check("Home presenta GB acumulados y espacio de disco",window.HomeTraffic.Text.Contains("GB /") && window.HomeDisks.Text.Contains("% libre"));
   Check("Texto oscuro en el desplegable",window.IdleChoice.Foreground.ToString()=="#FF101B2A" && ((System.Windows.Controls.ComboBoxItem)window.IdleChoice.Items[0]).Foreground.ToString()=="#FF101B2A");
   foreach(var combo in new[]{window.RuntimeFilter,window.IdleChoice})
   {
    window.Tabs.SelectedItem=combo==window.RuntimeFilter?window.RuntimeTab:window.SettingsTab;
    await Task.Delay(80);
    window.UpdateLayout();
    var selected=Texts(combo).Where(t=>!string.IsNullOrWhiteSpace(t.Text)).ToArray();
    combo.IsDropDownOpen=true;await Task.Delay(80);window.UpdateLayout();
    var options=combo.Items.Cast<System.Windows.Controls.ComboBoxItem>().SelectMany(Texts).Where(t=>!string.IsNullOrWhiteSpace(t.Text)).ToArray();
    Check($"Texto renderizado azul oscuro en {combo.Name}",selected.Length>0 && options.Length==combo.Items.Count && selected.Concat(options).All(t=>t.Foreground.ToString()=="#FF101B2A"));
    combo.IsDropDownOpen=false;
   }
   window.Tabs.SelectedItem=window.HomeTab;
   window.PositionPortal(false);
   var screen=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
   var transform=System.Windows.PresentationSource.FromVisual(window)!.CompositionTarget.TransformFromDevice;
   var right=transform.Transform(new System.Windows.Point(screen.WorkingArea.Right,screen.WorkingArea.Bottom));
   Check("Popup sin marco y anclado a la derecha",window.WindowStyle==System.Windows.WindowStyle.None && !window.ShowInTaskbar && window.Topmost && window.Width<=570 && Math.Abs(window.Left+window.Width+12-right.X)<1 && Math.Abs(window.Top+window.Height+12-right.Y)<1);
   window.UpdateLayout();
   Check("Cabecera navegable sin menú lateral",window.HeaderNavigation.Children.Count==8 && window.Tabs.Template.FindName("PART_SelectedContentHost",window.Tabs) is System.Windows.Controls.ContentPresenter);
   var content=(System.Windows.FrameworkElement)window.Content;
   var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
   bitmap.Render(content);
   var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
   using(var image=File.Create("artifacts/portal-home.png")) encoder.Save(image);
   window.Tabs.SelectedItem=window.MemoryTab;window.UpdateLayout();
   bitmap.Clear();bitmap.Render(content);encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
   using(var image=File.Create("artifacts/portal-memory.png")) encoder.Save(image);
   window.Tabs.SelectedItem=window.RuntimeTab;window.UpdateLayout();
   bitmap.Clear();bitmap.Render(content);encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
   using(var image=File.Create("artifacts/portal-runtimes.png"))encoder.Save(image);
   window.Tabs.SelectedItem=window.HomeTab;
   window.SetPortalMode(true); window.PositionPortal(true);
   Check("Portal completo accesible desde el popup",window.ShowInTaskbar && !window.Topmost && window.Tabs.TabStripPlacement==System.Windows.Controls.Dock.Left);
   window.SetPortalMode(false); window.PositionPortal(false);
   Check("Retorno a vista rápida con menú lateral",!window.ShowInTaskbar && window.Topmost && window.Tabs.TabStripPlacement==System.Windows.Controls.Dock.Left && window.Tabs.Items.Count==8);
   bool stable=true;
   foreach(bool expanded in new[]{false,true})
   {
    window.SetPortalMode(expanded);window.Tabs.SelectedItem=window.HomeTab;window.UpdateLayout();
    var anchor=window.HomeIndicators.TranslatePoint(new System.Windows.Point(0,0),window);
    foreach(var section in new[]{window.MemoryTab,window.UsageTab,window.RuntimeTab,window.NetworkTab,window.DiskTab,window.SettingsTab})
    {
     window.Tabs.SelectedItem=section;window.UpdateLayout();
     stable &= window.HomeIndicators.Visibility==System.Windows.Visibility.Visible && (window.HomeIndicators.TranslatePoint(new System.Windows.Point(0,0),window)-anchor).Length<0.1 && window.EquipmentSummary.Visibility==System.Windows.Visibility.Collapsed;
    }
   }
   Check("La cabecera de indicadores permanece en todas las secciones y vistas",stable);
   Check("Procesos y discos utilizan páginas de cuatro",window.RuntimeList.ItemsSource.Cast<ProcessRow>().Count()<=4 && window.DiskList.Items.Count<=4);
   window.SetPortalMode(true); window.PositionPortal(true); window.UpdateLayout();
   window.HomeSqlButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
   Check("Acceso directo del Home abre Bases de datos",window.Tabs.SelectedItem==window.DatabaseTab);
   for(int i=0;i<200 && window.SqlBrowser.CoreWebView2==null;i++) await Task.Delay(100);
   if(window.SqlBrowser.CoreWebView2==null) throw new Exception(window.SqlStatus.Text);
   var web=window.SqlBrowser.CoreWebView2;
   bool ready=false;
   for(int i=0;i<200;i++)
   {
    var result=await web.ExecuteScriptAsync("Boolean(document.getElementById('profiles') && document.getElementById('profiles').innerText.includes('portal_test'))");
    if(result=="true") {ready=true;break;} await Task.Delay(100);
   }
   Check("Panel real cargado dentro del portal WPF",ready);
   Check("Navegación común disponible",window.Tabs.Items.Count==8);
   Check("Motor automático sin descargas manuales",await web.ExecuteScriptAsync("Boolean(document.getElementById('create') && document.querySelectorAll('#engine option').length===3 && document.querySelectorAll('[data-install]').length===0 && document.getElementById('sql-guide'))")=="true");
   await web.ExecuteScriptAsync("document.getElementById('sql-guide').click()");
   Check("Explicación distingue RAM y datos persistentes",await web.ExecuteScriptAsync("Boolean(document.getElementById('sql').open && document.getElementById('sql').innerText.includes('RAM') && document.getElementById('sql').innerText.includes('disco'))")=="true");
   await web.ExecuteScriptAsync("document.getElementById('sql').close();document.querySelector('[data-action=options]').click();render()");
   Check("Opciones accesibles en diálogo y conservadas al actualizar",await web.ExecuteScriptAsync("document.getElementById('options').open")=="true");
   await web.ExecuteScriptAsync("document.getElementById('options').close()");
   await web.ExecuteScriptAsync("metricPending=true;metrics[state.profiles[0].id]={telemetry:{sessions:Array.from({length:11},(_,i)=>({pid:i+1,user:'test',application:'dev',state:'idle',client:'127.0.0.1'}))}};showSessions(state.profiles[0]);");
   Check("Sesiones accesibles por páginas sin scroll",await web.ExecuteScriptAsync("Boolean(document.querySelectorAll('#sessions-content tbody tr').length===4 && document.getElementById('sessions-page').textContent==='1 / 3')")=="true");
   await web.ExecuteScriptAsync("document.getElementById('sessions-next').click()");
   Check("Página siguiente de sesiones disponible",await web.ExecuteScriptAsync("document.getElementById('sessions-page').textContent==='2 / 3'")=="true");
   await web.ExecuteScriptAsync("document.getElementById('sessions').close();metricPending=false;");
   Check("Panel SQL sin barras de scroll",await web.ExecuteScriptAsync("Boolean(getComputedStyle(document.body).overflow==='hidden' && document.getElementById('sql-page-next'))")=="true");
   using(var image=File.Create("artifacts/portal-sql.png")) await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
   await web.ExecuteScriptAsync("document.getElementById('database').value='from_portal';document.getElementById('username').value='devuser';document.getElementById('password').value='test-local';document.getElementById('port').value='55433';document.getElementById('create').requestSubmit();");
   for(int i=0;i<100 && new Store(root).Snapshot().Length<2;i++) await Task.Delay(100);
   Check("Creación completa desde formulario embebido",new Store(root).Snapshot().Length==2);
   using var pageClient=new HttpClient{BaseAddress=new Uri(await SqlPortal.StartAsync())};
   var pageHtml=await pageClient.GetStringAsync("");
   pageClient.DefaultRequestHeaders.Add("X-SqlLight-Token",Regex.Match(pageHtml,"const token='([A-F0-9]+)'").Groups[1].Value);
   for(int i=0;i<4;i++)
   {
    var extra=JsonSerializer.Serialize(new{engine="postgres",database=$"page_test_{i}",username="devuser",password="test-local",port=55434+i});
    (await pageClient.PostAsync("api/profiles",new StringContent(extra,Encoding.UTF8,"application/json"))).EnsureSuccessStatusCode();
   }
   await web.ExecuteScriptAsync("refresh();viewPage=0;");
   for(int i=0;i<100 && await web.ExecuteScriptAsync("state.profiles.length===6")!="true";i++)await Task.Delay(100);
   await web.ExecuteScriptAsync("render()");
   Check("SQL pagina cuatro bases por página",await web.ExecuteScriptAsync("document.querySelectorAll('#profiles .profile').length===4 && document.getElementById('sql-page-label').textContent==='1 / 2'")=="true");
   window.SetPortalMode(false);window.PositionPortal(false);window.UpdateLayout();await Task.Delay(150);
   await web.ExecuteScriptAsync("document.getElementById('sql-page-next').click()");
   Check("Segunda página SQL mantiene acciones visibles en vista rápida",await web.ExecuteScriptAsync("document.querySelectorAll('#profiles .profile').length===2 && [...document.querySelectorAll('#profiles [data-action=delete]')].every(b=>{const r=b.getBoundingClientRect();return r.top>=0&&r.bottom<=innerHeight&&r.right<=innerWidth&&!b.disabled})")=="true");
   await web.ExecuteScriptAsync("document.querySelector('#profiles [data-action=delete]').click()");
   Check("Eliminar accesible desde una página SQL",await web.ExecuteScriptAsync("document.getElementById('delete').open")=="true");
   await web.ExecuteScriptAsync("document.getElementById('delete-name').value=deleting.database;document.getElementById('confirm-delete').click()");
   for(int i=0;i<100 && new Store(root).Snapshot().Length!=5;i++)await Task.Delay(100);
   Check("Borrado confirmado desde la segunda página elimina solo el entorno de prueba",new Store(root).Snapshot().Length==5 && new Store(root).Snapshot().Any(p=>p.Database=="portal_test") && new Store(root).Snapshot().Any(p=>p.Database=="from_portal"));
   await web.ExecuteScriptAsync("viewPage=0;render()");await Task.Delay(100);
   Check("Cuatro tarjetas SQL conservan Eliminar visible sin scroll",await web.ExecuteScriptAsync("document.querySelectorAll('#profiles .profile').length===4 && [...document.querySelectorAll('#profiles [data-action=delete]')].every(b=>{const r=b.getBoundingClientRect();return r.top>=0&&r.bottom<=innerHeight&&r.right<=innerWidth})")=="true");
   await web.ExecuteScriptAsync("document.getElementById('toast').style.display='none'");
   using(var image=File.Create("artifacts/portal-sql-four.png"))await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
  }
  catch(Exception error) {passed=false;checks.Add(new {name="Error de integración",passed=false,reason=error.ToString()});}
  finally
  {
   window?.SqlBrowser.Dispose(); window?.Hide();
   try {await SqlPortal.StopAsync();} catch(Exception error) {passed=false; checks.Add(new {name="Cierre del host",passed=false,reason=error.Message});}
   File.WriteAllText("artifacts/portal-test.json",JsonSerializer.Serialize(new {passed,checks},new JsonSerializerOptions {WriteIndented=true}));
   Environment.ExitCode=passed?0:1;
  }
 }
}






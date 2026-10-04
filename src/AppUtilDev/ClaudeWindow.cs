using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace AppUtilDev;

// Reads only rendered usage text after the user signs in. No cookies or OAuth secrets are extracted.
public sealed class ClaudeWindow : Window
{
 readonly WebView2 browser=new();
 public event Action<List<UsageRow>,string>? UsageChanged;
 bool ready;
 bool disposed;
 Task? initialization;
 public ClaudeWindow()
 {
  Title="Conectar Claude · App Util"; Width=920; Height=760; WindowStartupLocation=WindowStartupLocation.CenterScreen;
  var grid=new DockPanel(); var bar=new StackPanel { Orientation=Orientation.Horizontal,Background=System.Windows.Media.Brushes.SlateGray };
  var help=new TextBlock { Text="Inicia sesión y abre Ajustes → Uso. La app lee los porcentajes visibles.",Margin=new Thickness(12),VerticalAlignment=VerticalAlignment.Center };
  var read=new Button { Content="Leer uso",Margin=new Thickness(6) };
  read.Click+=async (_,_)=>await ReadAsync(); bar.Children.Add(help); bar.Children.Add(read);
  DockPanel.SetDock(bar,Dock.Top); grid.Children.Add(bar); grid.Children.Add(browser); Content=grid;
  Closing+=(_,e)=> { if(!disposed) { e.Cancel=true; Hide(); } };
  Loaded+=async (_,_)=>await InitializeAsync();
 }
 public Task InitializeAsync() => initialization ??= InitializeCoreAsync();
 async Task InitializeCoreAsync()
 {
  if(ready) return;
  try
  {
   var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Settings.DataDir,"ClaudeBrowser"));
   await browser.EnsureCoreWebView2Async(env); ready=true;
   browser.CoreWebView2.NavigationCompleted+=async (_,e)=>
   { if(e.IsSuccess) { for(int attempt=0;attempt<4 && !disposed;attempt++) { await Task.Delay(1500); await ReadAsync(); } } else UsageChanged?.Invoke(new(),"Claude: no se ha podido cargar la página."); };
   browser.CoreWebView2.NewWindowRequested+=(_,e)=>
   { e.Handled=true; if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri) && uri.Scheme=="https") browser.CoreWebView2.Navigate(e.Uri); };
   browser.CoreWebView2.Navigate("https://claude.ai/settings/usage");
  }
  catch { UsageChanged?.Invoke(new(),"Instala Microsoft Edge WebView2 Runtime para conectar Claude."); }
 }
 public async Task ReadAsync()
 {
  if(!ready || disposed) return;
  if(!Uri.TryCreate(browser.CoreWebView2.Source,UriKind.Absolute,out var uri) || uri.Host!="claude.ai" || !uri.AbsolutePath.StartsWith("/settings/usage",StringComparison.Ordinal))
  { UsageChanged?.Invoke(new(),"Claude: inicia sesión y abre Ajustes → Uso."); return; }
  try
  {
   string json=await browser.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
   string text=JsonSerializer.Deserialize<string>(json) ?? "";
   var rows=ParseVisibleText(text);
   UsageChanged?.Invoke(rows,rows.Count>0?$"Página de uso · leído {DateTime.Now:HH:mm}":"Claude no muestra porcentajes reconocibles. Abre su página de uso.");
  }
  catch { UsageChanged?.Invoke(new(),"No se pudo leer el uso de Claude."); }
 }
 public void RefreshPage() { if(ready && !disposed && IsVisible) browser.CoreWebView2.Navigate("https://claude.ai/settings/usage"); }
 public void DisposeBrowser() { disposed=true; browser.Dispose(); }
 public async Task DisconnectAsync() { if(ready) await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(); }
 public static List<UsageRow> ParseVisibleText(string text)
 {
  var lines=text.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
  var rows=new List<UsageRow>();
  string section="",reset="Reinicio: consulta la página de uso";
  for(int i=0;i<lines.Length;i++)
  {
   string? heading=Regex.IsMatch(lines[i],@"^.*(?:current session|sesión actual|sesion actual).*$",RegexOptions.IgnoreCase)?"Sesión":
    Regex.IsMatch(lines[i],@"^.*(?:all models|todos los modelos).*$",RegexOptions.IgnoreCase)?"Todos los modelos":
    Regex.IsMatch(lines[i],@"^(?:sonnet(?: only)?|solo sonnet).*$",RegexOptions.IgnoreCase)?"Sonnet":
    Regex.IsMatch(lines[i],@"^(?:weekly limits|límites semanales|limites semanales).*$",RegexOptions.IgnoreCase)?"Semana":null;
   if(heading!=null) { section=heading; reset="Reinicio: consulta la página de uso"; }
   if(Regex.IsMatch(lines[i],@"resets|reinicia|restablece",RegexOptions.IgnoreCase)) reset=lines[i];
   // Require an explicit usage suffix; never interpret billing percentages as quota.
   var match=Regex.Match(lines[i],@"(?<p>\d{1,3}(?:[.,]\d+)?)\s*%\s*(?:used|utilizado|usado|utilisé|genutzt)",RegexOptions.IgnoreCase);
   if(!match.Success) continue;
   if(section=="") continue;
   if(!double.TryParse(match.Groups["p"].Value.Replace(',','.'),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double used) || used>100) continue;
   rows.Add(new(section,100-used,reset)); section="";
  }
  return rows.GroupBy(x=>x.Name).Select(g=>g.Last()).ToList();
 }
}

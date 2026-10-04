using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace AppUtilDev;

public partial class MainWindow
{
 bool portalMode;
 internal void SetPortalMode(bool expanded)
 {
  portalMode=expanded;
  Width=expanded?1180:570;
  Height=expanded?840:810;
  Topmost=!expanded;
  ShowInTaskbar=expanded;

  PortalModeButton.Content=expanded?"Vista rápida ↘":"Abrir portal ↗";
 }
 internal void PositionPortal(bool expanded)
 {
  var screen=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
  var source=PresentationSource.FromVisual(this);
  var transform=source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
  var top=transform.Transform(new Point(screen.WorkingArea.Left,screen.WorkingArea.Top));
  var bottom=transform.Transform(new Point(screen.WorkingArea.Right,screen.WorkingArea.Bottom));
  Width=Math.Min(expanded?1180:570,bottom.X-top.X-16);
  Height=Math.Min(expanded?840:810,bottom.Y-top.Y-16);
  Left=expanded?top.X+(bottom.X-top.X-Width)/2:bottom.X-Width-12;
  Top=expanded?top.Y+(bottom.Y-top.Y-Height)/2:bottom.Y-Height-12;
 }
 internal void OpenFullPortal()
 {
  SetPortalMode(true); Show(); PositionPortal(true); Activate();
 }
 void PortalMode_Click(object sender,RoutedEventArgs e)
 {
  if(portalMode) ShowPopup(); else OpenFullPortal();
 }
 bool openingSql;
 string sqlOrigin="";
 internal static string BrowserProfile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AppUtilDev","SqlBrowser");
 internal async Task OpenSqlAsync()
 {
  if(openingSql) return;
  openingSql=true;
  try
  {
   SqlStatus.Text="Conectando…"; SqlRetryButton.Visibility=Visibility.Collapsed;
   var url=await SqlPortal.StartAsync();
   sqlOrigin=new Uri(url).GetLeftPart(UriPartial.Authority);
   if(SqlBrowser.CoreWebView2==null)
   {
    var environment=await CoreWebView2Environment.CreateAsync(null,BrowserProfile);
    await SqlBrowser.EnsureCoreWebView2Async(environment);
    var web=SqlBrowser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 no se ha inicializado.");
    web.Settings.AreDevToolsEnabled=false;
    web.Settings.AreDefaultContextMenusEnabled=false;
    web.NewWindowRequested+=(_,e)=>e.Handled=true;
    web.NavigationStarting+=(_,e)=>
    {
     if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var target) || target.GetLeftPart(UriPartial.Authority)!=sqlOrigin) e.Cancel=true;
    };
    SqlBrowser.NavigationCompleted+=(_,e)=> { SqlStatus.Text=e.IsSuccess?"Gestor local":"No se pudo conectar"; SqlRetryButton.Visibility=e.IsSuccess?Visibility.Collapsed:Visibility.Visible; };
   }
   SqlBrowser.Source=new Uri(url);
  }
  catch(Exception e) { SqlStatus.Text="No se pudo abrir el gestor"; SqlStatus.ToolTip=e.Message; SqlRetryButton.Visibility=Visibility.Visible; }
  finally { openingSql=false; }
 }
 async void PortalTab_Changed(object sender,SelectionChangedEventArgs e)
 {
  if(e.Source!=Tabs || DatabaseTab==null) return;
  RenderHome();
  if(HomeTab.IsSelected && IsVisible) _=RefreshHomeSqlAsync();
  if(!DatabaseTab.IsSelected) return;
  if(SqlBrowser.CoreWebView2==null) await OpenSqlAsync();
 }
 async void RetrySql_Click(object sender,RoutedEventArgs e)
 {
  SqlPortal.ResetServiceUrl(); await OpenSqlAsync();
 }
 internal async Task StartSqlAsync()
 {
  try { await SqlPortal.StartAsync(); SqlStatus.Text="Gestor local"; SqlRetryButton.Visibility=Visibility.Collapsed; }
  catch(Exception e) { SqlStatus.Text="No se pudo iniciar el gestor"; SqlStatus.ToolTip=e.Message; SqlRetryButton.Visibility=Visibility.Visible; }
 }
}


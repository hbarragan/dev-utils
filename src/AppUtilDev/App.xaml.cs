using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Forms=System.Windows.Forms;
namespace AppUtilDev;

public partial class App : Application
{
 Mutex? instance;
 EventWaitHandle? activation;
 RegisteredWaitHandle? activationWait;
 EventWaitHandle? quit;
 RegisteredWaitHandle? quitWait;
 Forms.NotifyIcon? tray;
 Icon? icon;
 [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
 protected override async void OnStartup(StartupEventArgs e)
 {
  base.OnStartup(e);
  var rootIndex=Array.IndexOf(e.Args,"--root");
  if(rootIndex>=0 && rootIndex+1<e.Args.Length) SqlPortal.Root=Path.GetFullPath(e.Args[rootIndex+1]);
  if(e.Args.Contains("--internet-test"))
  {
   using var monitor=new InternetMonitor();var sample=await monitor.TestAsync();Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/internet-test.json",System.Text.Json.JsonSerializer.Serialize(sample));Environment.ExitCode=sample?.DownloadMbps.HasValue==true && sample.UploadMbps.HasValue?0:1;Shutdown();return;
  }
  if(e.Args.Contains("--portal-test")) { await PortalTests.RunAsync(); Shutdown(); return; }
  if(e.Args.Any(a=>a.StartsWith("--sql-") || a.StartsWith("--service") || a=="--install" || a=="--uninstall"))
  {
   try
   {
    if(e.Args[0].StartsWith("--sql-")) await SqlLight.SqlSetup.Execute(e.Args[0],int.Parse(e.Args[1]),SqlPortal.Root);
    else if(e.Args[0] is "--service-install" or "--service-uninstall") await SqlLight.ServiceInstallation.Elevated(SqlPortal.Root,e.Args[1],e.Args[0]=="--service-uninstall");
    else if(e.Args.Contains("--install") || e.Args.Contains("--uninstall")) await SqlLight.ServiceInstallation.Begin(SqlPortal.Root,e.Args.Contains("--uninstall"));
    else
    {
     var store=new SqlLight.Store(SqlPortal.Root);
     var host=new SqlLight.PanelServer(store,new SqlLight.Engines(store),serviceMode:true);
     await host.Start(); await host.WaitForShutdown();
    }
   }
   catch(Exception error) { Directory.CreateDirectory(Path.Combine(SqlPortal.Root,"artifacts")); File.WriteAllText(Path.Combine(SqlPortal.Root,"artifacts","last-error.txt"),error.Message); Environment.ExitCode=1; }
   Shutdown(); return;
  }
  if(e.Args.Contains("--test-worker")) { SelfTests.Worker(); Shutdown(); return; }
  if(e.Args.Contains("--self-test")) { await SelfTests.RunAsync(); Shutdown(); return; }
  instance=new Mutex(true,"Local\\AppUtilDev.Tray",out bool created);
  if(!created)
  {
   try { using var existing=EventWaitHandle.OpenExisting("Local\\AppUtilDev.Activate"); existing.Set(); } catch { }
   Shutdown(); return;
  }
  DispatcherUnhandledException+=(_,args)=>
  {
   MessageBox.Show("La operación no se pudo completar. Vuelve a intentarlo.","App Util",MessageBoxButton.OK,MessageBoxImage.Information);
   args.Handled=true;
  };
  var window=new MainWindow(); MainWindow=window;
  activation=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\AppUtilDev.Activate");
  activationWait=ThreadPool.RegisterWaitForSingleObject(activation,(_,_)=>Dispatcher.BeginInvoke(window.ShowPopup),null,Timeout.Infinite,false);
  quit=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\AppUtilDev.Quit");
  quitWait=ThreadPool.RegisterWaitForSingleObject(quit,(_,_)=>Dispatcher.BeginInvoke(window.ExitApp),null,Timeout.Infinite,false);
  icon=CreateIcon();
  tray=new Forms.NotifyIcon { Icon=icon,Text="App Util Dev · equipo y bases de datos",Visible=true };
  tray.MouseClick+=(_,args)=> { if(args.Button==Forms.MouseButtons.Left) Dispatcher.Invoke(()=> { if(window.IsVisible) window.Hide(); else window.ShowPopup(); }); };
  var menu=new Forms.ContextMenuStrip();
  menu.Items.Add("Abrir App Util",null,(_,_)=>Dispatcher.Invoke(window.ShowPopup));
  menu.Items.Add("Abrir portal",null,(_,_)=>Dispatcher.Invoke(window.OpenFullPortal));
  menu.Items.Add("Salir",null,(_,_)=>Dispatcher.Invoke(window.ExitApp));
  tray.ContextMenuStrip=menu;
  window.StartMonitoring();
  _=window.StartSqlAsync();
  if(e.Args.Contains("--show")) window.ShowPopup();
 }
 static Icon CreateIcon()
 {
  using var bitmap=new Bitmap(32,32); using var g=Graphics.FromImage(bitmap);
  g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
  using var bg=new SolidBrush(Color.FromArgb(20,37,52)); g.FillEllipse(bg,0,0,31,31);
  using var pen=new Pen(Color.FromArgb(101,221,176),3);
  g.DrawLines(pen,new[]{new PointF(8,11),new PointF(8,20),new PointF(12,24),new PointF(20,24),new PointF(24,20),new PointF(24,11)});
  var handle=bitmap.GetHicon(); try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
 }
 public void ExitApp() { tray?.Dispose(); tray=null; icon?.Dispose(); Shutdown(); }
 protected override void OnExit(ExitEventArgs e)
 {
  activationWait?.Unregister(null); activation?.Dispose(); quitWait?.Unregister(null); quit?.Dispose(); tray?.Dispose(); icon?.Dispose(); instance?.Dispose(); base.OnExit(e);
 }
}

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SqlLight;

public static class ServiceInstallation
{
    public const string Name = "AppUtilDevSql";
    const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppUtilDevSql";
    public static string? InstalledRoot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        return key?.GetValue("InstallLocation") as string;
    }
    public static bool Installed(string root) => string.Equals(InstalledRoot(), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
    public static string ReadUrl(string root)
    {
        var url = File.ReadAllText(Path.Combine(root, "artifacts", "panel-url.txt")).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Host != "127.0.0.1" || parsed.Scheme != "http") throw new Exception("Dirección del panel inválida.");
        return url;
    }
    public static async Task Begin(string root, bool remove)
    {
        using var operation = new Mutex(true, @"Local\AppUtilDev.Installation", out var created);
        if (!created) throw new Exception("Ya hay una instalación o desinstalación en curso.");
        var owner = WindowsIdentity.GetCurrent().User!.Value;
        if (remove)
        {
            if (!Installed(root)) throw new Exception("SQL Light todavía no está instalado como servicio en esta carpeta.");
            try { using var quit = EventWaitHandle.OpenExisting(@"Local\AppUtilDev.Quit"); quit.Set(); } catch (WaitHandleCannotBeOpenedException) { }
        }
        if (!remove)
        {
            // Run in the desktop user's context to migrate their old DPAPI credentials.
            var store = new Store(root);
            bool prepared = false;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var url = ReadUrl(root); var html = await client.GetStringAsync(url);
                var token = Regex.Match(html, "const token='([A-F0-9]+)'").Groups[1].Value;
                client.DefaultRequestHeaders.Add("X-SqlLight-Token", token);
                var response = await client.PostAsync(url + "api/prepare-install", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                if (response.IsSuccessStatusCode) prepared = true;
                else throw new Exception("No se puede preparar la instalación mientras hay una operación en curso.");
            }
            catch (HttpRequestException) { }
            catch (IOException) { }
            if (!prepared) { store.Save(); store.MigrateArchives(); }
            // Shut down the desktop host gracefully before the service takes ownership.
            try { using var quit = EventWaitHandle.OpenExisting(@"Local\AppUtilDev.Quit"); quit.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            for (var i = 0; i < 180; i++)
            {
                var others = Process.GetProcessesByName("AppUtilDev").Where(p => p.Id != Environment.ProcessId).ToArray();
                bool desktop = false;
                foreach (var p in others) { try { desktop |= p.SessionId == Process.GetCurrentProcess().SessionId && string.Equals(p.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase); } catch { } finally { p.Dispose(); } }
                if (!desktop) break;
                if (i == 179) throw new Exception("El gestor sigue ocupado. Termina la operación antes de instalar.");
                await Task.Delay(500);
            }
        }
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(remove ? "--service-uninstall" : "--service-install");
        info.ArgumentList.Add(owner); info.ArgumentList.Add("--root"); info.ArgumentList.Add(root);
        using var helper = Process.Start(info) ?? throw new Exception("No se pudo iniciar el instalador.");
        await helper.WaitForExitAsync();
        if (helper.ExitCode != 0) throw new Exception("La instalación no se completó. Consulta artifacts/last-error.txt o acepta el aviso de permisos de Windows.");
        if (!remove)
        {
            var launch=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=true }; launch.ArgumentList.Add("--show"); Process.Start(launch);
            
        }
        else MessageBox.Show("Servicio e inicio automático retirados. Los entornos y sus datos se conservan en la carpeta de SQL Light.", "SQL Light");
    }
    public static async Task Elevated(string root, string ownerSid, bool remove)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new Exception("Windows requiere permisos de administrador para instalar un servicio.");
        var owner = new SecurityIdentifier(ownerSid);
        var exe = Path.Combine(root, "dist", "AppUtilDev.exe");
        if (!File.Exists(exe)) throw new Exception("No se encuentra dist/AppUtilDev.exe.");
        if (remove)
        {
            if (!Installed(root)) throw new Exception("Esta carpeta no corresponde al servicio instalado.");
            await StopIfExists();
            await Engines.Run("sc.exe", new[] { "delete", Name });
            using var old = Registry.LocalMachine.OpenSubKey(RegistryPath);
            var sid = old?.GetValue("OwnerSid") as string ?? ownerSid;
            using var run = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue(Name, false);
            Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, false);
            var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "App Util Dev.lnk");
            if (File.Exists(link)) File.Delete(link);
            return;
        }
        if (InstalledRoot() is { } installed && !string.Equals(installed, root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Ya existe SQL Light instalado en otra carpeta. Desinstálalo antes.");
        var query = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        query.ArgumentList.Add("query"); query.ArgumentList.Add(Name);
        using var proc = Process.Start(query)!; await proc.StandardOutput.ReadToEndAsync(); await proc.WaitForExitAsync();
        if (proc.ExitCode == 0 && !Installed(root)) throw new Exception("Ya existe un servicio SqlLight ajeno a esta instalación.");
        if (proc.ExitCode == 0) await StopIfExists();
        var binPath = "\"" + exe + "\" --service --root \"" + root + "\"";
        await Engines.Run("sc.exe", new[] { proc.ExitCode == 0 ? "config" : "create", Name, "binPath=", binPath, "start=", "delayed-auto", "obj=", @"NT AUTHORITY\LocalService", "DisplayName=", "SQL Light · Bases de datos locales" });
        await Engines.Run("sc.exe", new[] { "sidtype", Name, "unrestricted" });
        await Engines.Run("sc.exe", new[] { "description", Name, "Panel local y motores PostgreSQL/MySQL. Datos persistentes y arranque automático configurable por entorno." });
        if (!EventLog.SourceExists(Name)) EventLog.CreateEventSource(Name, "Application");
        await Engines.Run("sc.exe", new[] { "failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000" });
        var serviceSid = (SecurityIdentifier)new NTAccount(@"NT SERVICE\" + Name).Translate(typeof(SecurityIdentifier));
        Grant(root, serviceSid, FileSystemRights.ReadAndExecute);
        foreach (var folder in new[] { "private", "data", "trash", "engines", "downloads", "artifacts" })
        {
            var dir = Path.Combine(root, folder); Directory.CreateDirectory(dir); Grant(dir, serviceSid, FileSystemRights.Modify);
        }
        Grant(Path.Combine(root, "private"), owner, FileSystemRights.FullControl);
        if (Engines.SqlInstalled())
        {
            try { await ConfigureSqlPermissions(root, serviceSid); }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "sql-service-setup.txt"), "PostgreSQL y MySQL pueden funcionar. Falta preparar el acceso del servicio a SQLLIGHT: " + ex.Message); }
        }
        using (var key = Registry.LocalMachine.CreateSubKey(RegistryPath))
        {
            key.SetValue("DisplayName", "SQL Light"); key.SetValue("DisplayVersion", "1.1.0"); key.SetValue("Publisher", "SQL Light · Desarrollo local");
            key.SetValue("InstallLocation", root); key.SetValue("OwnerSid", ownerSid);
            key.SetValue("UninstallString", "\"" + exe + "\" --uninstall"); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        }
        using (var run = Registry.Users.CreateSubKey(ownerSid + @"\Software\Microsoft\Windows\CurrentVersion\Run")) run.SetValue(Name, "\"" + exe + "\"");
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "App Util Dev.lnk");
        var script = "$w=New-Object -ComObject WScript.Shell; $s=$w.CreateShortcut('" + shortcut.Replace("'", "''") + "'); $s.TargetPath='" + exe.Replace("'", "''") + "'; $s.Arguments='--show'; $s.Save()";
        await Engines.Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", script });
        await Engines.Run("sc.exe", new[] { "start", Name });
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var html = await client.GetStringAsync(ReadUrl(root));
                if (html.Contains("SQL Light")) return;
            } catch { }
            await Task.Delay(500);
        }
        throw new Exception("El servicio se registró, pero no respondió. Consulta artifacts/last-error.txt y el Visor de eventos.");
    }
    static void Grant(string dir, SecurityIdentifier sid, FileSystemRights rights)
    {
        var info = new DirectoryInfo(dir); var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
    static async Task ConfigureSqlPermissions(string root, SecurityIdentifier sid)
    {
        var sddl = await Engines.Run("sc.exe", new[] { "sdshow", "MSSQL$SQLLIGHT" });
        var descriptor = new RawSecurityDescriptor(sddl.Trim());
        descriptor.DiscretionaryAcl!.InsertAce(descriptor.DiscretionaryAcl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x34, sid, false, null));
        await Engines.Run("sc.exe", new[] { "sdset", "MSSQL$SQLLIGHT", descriptor.GetSddlForm(AccessControlSections.All) });
        var status = await Engines.Run("sc.exe", new[] { "query", "MSSQL$SQLLIGHT" });
        var started = !Regex.IsMatch(status, @":\s+4\s+");
        if (started) await Engines.Run("sc.exe", new[] { "start", "MSSQL$SQLLIGHT" });
        try
        {
            var engines = new Engines(new Store(root));
            for (var i = 0; ; i++)
            {
                try
                {
                    await using var conn = engines.Connection(new Profile { Engine = "sqlserver", Port = 1433 }); await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "IF SUSER_ID(N'NT SERVICE\\AppUtilDevSql') IS NULL CREATE LOGIN [NT SERVICE\\AppUtilDevSql] FROM WINDOWS; ALTER SERVER ROLE sysadmin ADD MEMBER [NT SERVICE\\AppUtilDevSql];";
                    await cmd.ExecuteNonQueryAsync(); break;
                }
                catch when (i < 15) { await Task.Delay(500); }
            }
        }
        finally { if (started) await Engines.Run("sc.exe", new[] { "stop", "MSSQL$SQLLIGHT" }); }
    }
    public static Task ConfigureInstalledSqlPermissions(string root)
    {
        if (!Installed(root)) return Task.CompletedTask;
        var sid = (SecurityIdentifier)new NTAccount(@"NT SERVICE\" + Name).Translate(typeof(SecurityIdentifier));
        return ConfigureSqlPermissions(root, sid);
    }
    static async Task StopIfExists()
    {
        try { await Engines.Run("sc.exe", new[] { "stop", Name }); } catch { }
        for (var i = 0; i < 180; i++)
        {
            var status = await Engines.Run("sc.exe", new[] { "query", Name });
            if (Regex.IsMatch(status, @":\s+1\s+")) return;
            await Task.Delay(500);
        }
        throw new Exception("El servicio todavía está deteniendo sus bases de datos.");
    }
}

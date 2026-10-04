using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;

namespace SqlLight;

public static class SqlSetup
{
    public static async Task Execute(string action, int port, string root)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new Exception("Requiere permisos de administrador.");
        if (port is < 1024 or > 65535) throw new Exception("Puerto inválido.");
        const string service = "MSSQL$SQLLIGHT";
        if (!Engines.SqlInstalled()) throw new Exception("Instala primero SQL Server Express con instancia SQLLIGHT.");
        if (action == "--sql-configure")
        {
            using var instances = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
            var id = instances?.GetValue("SQLLIGHT") as string ?? throw new Exception("No existe SQLLIGHT.");
            var baseKey = @"SOFTWARE\Microsoft\Microsoft SQL Server\" + id + @"\MSSQLServer";
            using (var server = Registry.LocalMachine.OpenSubKey(baseKey, true)!)
            {
                server.SetValue("LoginMode", 2, RegistryValueKind.DWord);
                var data = Path.Combine(root, "data", "sqlserver"); Directory.CreateDirectory(data);
                await Engines.Run("icacls.exe", new[] { data, "/grant", @"NT SERVICE\MSSQL$SQLLIGHT:(OI)(CI)M" });
                server.SetValue("DefaultData", data); server.SetValue("DefaultLog", data);
            }
            using (var tcp = Registry.LocalMachine.OpenSubKey(baseKey + @"\SuperSocketNetLib\Tcp", true)!)
            {
                tcp.SetValue("Enabled", 1, RegistryValueKind.DWord); tcp.SetValue("ListenOnAllIPs", 0, RegistryValueKind.DWord);
                foreach (var name in tcp.GetSubKeyNames())
                {
                    using var ip = tcp.OpenSubKey(name, true)!;
                    var address = ip.GetValue("IpAddress") as string;
                    var loopback = address is "127.0.0.1" or "::1";
                    if (name != "IPAll") ip.SetValue("Enabled", loopback ? 1 : 0, RegistryValueKind.DWord);
                    ip.SetValue("TcpDynamicPorts", ""); ip.SetValue("TcpPort", port.ToString());
                }
            }
            using (var pipes = Registry.LocalMachine.OpenSubKey(baseKey + @"\SuperSocketNetLib\Np", true)!)
            {
                pipes.SetValue("Enabled", 1, RegistryValueKind.DWord);
                pipes.SetValue("PipeName", @"\\.\pipe\MSSQL$SQLLIGHT\sql\query");
            }
            await Engines.Run("sc.exe", new[] { "config", service, "start=", "demand" });
            try { await Engines.Run("sc.exe", new[] { "stop", service }); } catch { }
            await WaitService(false);
            await Engines.Run("sc.exe", new[] { "start", service });
            await WaitService(true);
            // Restrict engine buffer memory; this is not a total process RAM ceiling.
            var dummy = new Profile { Engine = "sqlserver", Port = port };
            var engines = new Engines(new Store(root));
            await using var conn = engines.Connection(dummy); await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXEC sp_configure 'show advanced options',1; RECONFIGURE; EXEC sp_configure 'max server memory (MB)',256; RECONFIGURE;";
            await cmd.ExecuteNonQueryAsync();
            await ServiceInstallation.ConfigureInstalledSqlPermissions(root);
        }
        else if (action == "--sql-start") { await Engines.Run("sc.exe", new[] { "start", service }); await WaitService(true); }
        else if (action == "--sql-stop") { await Engines.Run("sc.exe", new[] { "stop", service }); await WaitService(false); }
        else throw new Exception("Acción desconocida.");
    }
    static async Task WaitService(bool running)
    {
        for (var i = 0; i < 60; i++)
        {
            var text = await Engines.Run("sc.exe", new[] { "query", "MSSQL$SQLLIGHT" });
            if (System.Text.RegularExpressions.Regex.IsMatch(text, running ? @":\s+4\s+" : @":\s+1\s+")) return;
            await Task.Delay(500);
        }
        throw new Exception("El servicio de SQL Server no respondió a tiempo.");
    }
    public static async Task Install(string root)
    {
        var downloads = Path.Combine(root, "downloads"); Directory.CreateDirectory(downloads);
        var installer = Path.Combine(downloads, "SQL2025-SSEI-Expr.exe");
        using var client = new HttpClient();
        await File.WriteAllBytesAsync(installer, await client.GetByteArrayAsync("https://go.microsoft.com/fwlink/?linkid=2216019"));
        // Validate the downloaded bootstrapper's Authenticode signature before running it.
        var result = await Engines.Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "$s=Get-AuthenticodeSignature -LiteralPath '" + installer.Replace("'", "''") + "'; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){exit 1}" });
        Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
    }
}

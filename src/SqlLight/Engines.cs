using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Data.Common;
using Npgsql;
using MySqlConnector;
using Microsoft.Data.SqlClient;

namespace SqlLight;

public sealed partial class Engines(Store store, string? binariesRoot = null)
{
    string EngineRoot => binariesRoot ?? Path.Combine(store.Root, "engines");
    public string Activity { get; private set; } = "Listo";
    public static int DefaultPort(string engine) => engine switch { "postgres" => 5432, "mysql" => 3306, _ => 1433 };
    public static bool PortFree(int port)
    {
        try { var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); listener.Stop(); return true; } catch (SocketException) { return false; }
    }
    public int SuggestPort(string engine)
    {
        if (engine == "sqlserver") return store.Profiles.FirstOrDefault(p => p.Engine == engine)?.Port ?? 1433;
        var port = DefaultPort(engine);
        while (!PortFree(port) || store.Profiles.Any(p => p.Port == port)) port++;
        return port;
    }
    string Bin(string engine, string exe) => engine == "tds" ? Path.Combine(EngineRoot, "tds-python", "python.exe") : engine == "babelfish" ? Path.Combine(EngineRoot, "wiltondb", "wiltondb_3_lts_13.18.1", "bin", exe + ".exe") : Path.Combine(EngineRoot, engine, "bin", exe + ".exe");
    public bool Installed(string engine) => engine == "sqlserver" ? SqlInstalled() : File.Exists(Bin(engine, engine is "postgres" or "babelfish" ? "postgres" : "mysqld"));
    public static bool SqlInstalled()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MSSQL$SQLLIGHT");
        return key != null;
    }
    public async Task Install(string engine)
    {
        if (engine == "sqlserver") throw new Exception("Usa Preparar SQL Server para instalar la instancia SQLLIGHT.");
        if (Installed(engine)) return;
        if (engine == "babelfish") { await InstallBabelfish(); return; }
        if (engine == "tds")
        {
            Activity = "Descargando Python portable para TDS experimental…";
            var path = Path.Combine(store.Root, "downloads", "python-3.13.7-embed-amd64.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                using var client = new HttpClient();
                await File.WriteAllBytesAsync(path, await client.GetByteArrayAsync("https://www.python.org/ftp/python/3.13.7/python-3.13.7-embed-amd64.zip"));
            }
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
            if (hash != "F6CCA216A359BE84797CABB54149CE5E062AFB16CC7567EB7FC51CACB2D86B65") throw new Exception("El paquete de Python no coincide con el hash esperado.");
            var stagePath = Path.Combine(EngineRoot, "tds-python-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(path, stagePath);
            if (!File.Exists(Path.Combine(stagePath, "python.exe")) || !File.Exists(Path.Combine(stagePath, "_sqlite3.pyd"))) throw new Exception("Paquete Python incompleto.");
            Directory.Move(stagePath, Path.Combine(EngineRoot, "tds-python")); Activity = "Listo"; return;
        }
        Activity = $"Descargando {engine}…";
        var downloads = Path.Combine(store.Root, "downloads"); Directory.CreateDirectory(downloads);
        var zip = Path.Combine(downloads, engine == "postgres" ? "postgresql.zip" : "mysql.zip");
        var url = engine == "postgres" ? "https://get.enterprisedb.com/postgresql/postgresql-17.11-1-windows-x64-binaries.zip" : "https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.10-winx64.zip";
        if (!File.Exists(zip))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync();
            await using (var output = File.Create(zip + ".part"))
            {
                var buffer = new byte[131072]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read)); total += read; Activity = $"Descargando {engine}: {total / 1048576} MB / {response.Content.Headers.ContentLength / 1048576} MB"; }
            }
            File.Move(zip + ".part", zip, true);
        }
        Activity = $"Extrayendo {engine}…";
        var stage = Path.Combine(EngineRoot, engine + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        // Sólo necesitamos bin, lib y share. No se instala pgAdmin, ejemplos o suites de pruebas.
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                var parts = entry.FullName.Replace('\\', '/').Split('/');
                var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (extension is ".pdb" or ".lib" or ".a" || parts.Contains("debug")) continue;
                if (entry.Name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase) || entry.Name.Contains("COPYING", StringComparison.OrdinalIgnoreCase) || entry.Name.Contains("COPYRIGHT", StringComparison.OrdinalIgnoreCase))
                {
                    var licenses = Path.Combine(stage, "licenses"); Directory.CreateDirectory(licenses);
                    var licenseFile = Path.Combine(licenses, entry.Name);
                    if (entry.Name.Length > 0 && !File.Exists(licenseFile)) entry.ExtractToFile(licenseFile);
                    continue;
                }
                if (parts.Length < 3 || parts[1] is not ("bin" or "lib" or "share") || entry.Name.Length == 0) continue;
                var destination = Path.GetFullPath(Path.Combine(stage, Path.Combine(parts.Skip(1).ToArray())));
                if (!destination.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Ruta inválida en el ZIP.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination);
            }
        });
        if (!File.Exists(Path.Combine(stage, "bin", engine == "postgres" ? "postgres.exe" : "mysqld.exe"))) throw new Exception("El ZIP no contiene el motor esperado.");
        Directory.Move(stage, Path.Combine(EngineRoot, engine));
        Activity = "Listo";
    }
    public static async Task<string> Run(string file, IEnumerable<string> args, IDictionary<string, string>? env = null, int timeout = 120)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (env != null) foreach (var pair in env) info.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(info) ?? throw new Exception("No se pudo iniciar el proceso.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new Exception("El motor tardó demasiado en responder."); }
        var message = (await output) + (await error);
        if (process.ExitCode != 0) throw new Exception(message.Length > 3000 ? message[^3000..] : message);
        return message;
    }
    public Process? OwnedProcess(Profile p)
    {
        if (p.Pid == null) return null;
        try
        {
            var proc = Process.GetProcessById(p.Pid.Value);
            var expected = Bin(p.Engine, p.Engine is "postgres" or "babelfish" ? "postgres" : "mysqld");
            if (proc.StartTime.ToUniversalTime().Ticks == p.ProcessStart && string.Equals(proc.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return proc;
            proc.Dispose();
        } catch { }
        return null;
    }
    public bool Running(Profile p)
    {
        if (p.Engine == "sqlserver") return SqlRunning();
        using var proc = OwnedProcess(p); return proc != null;
    }
    static bool SqlRunning()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MSSQL$SQLLIGHT");
        if (key == null) return false;
        // Query service status without requiring administrator permissions.
        var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("query"); info.ArgumentList.Add("MSSQL$SQLLIGHT");
        using var proc = Process.Start(info)!; var text = proc.StandardOutput.ReadToEnd(); proc.WaitForExit();
        return System.Text.RegularExpressions.Regex.IsMatch(text, @":\s+4\s+");
    }
    Process Launch(Profile p, string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Bin(p.Engine, exe)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = store.Folder(p) };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var proc = Process.Start(info) ?? throw new Exception("No se pudo levantar el motor.");
        p.Pid = proc.Id; p.ProcessStart = proc.StartTime.ToUniversalTime().Ticks; store.Save();
        // Drenar stdout/stderr para no bloquear el proceso. Logs propios del motor van a su carpeta.
        proc.OutputDataReceived += (_, _) => { }; proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); return proc;
    }
    public DbConnection Connection(Profile p, bool admin = true, string? database = null)
    {
        if (p.Engine == "postgres") return new NpgsqlConnection(new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = p.Port, Database = database ?? (admin ? "postgres" : p.Database), Username = admin ? "sql_light_admin" : p.Username, Password = admin ? p.AdminPassword : p.Password, Timeout = 3, CommandTimeout = 20, Pooling = false }.ConnectionString);
        if (p.Engine == "mysql") return new MySqlConnection(new MySqlConnectionStringBuilder { Server = "127.0.0.1", Port = (uint)p.Port, Database = database ?? (admin ? "" : p.Database), UserID = admin ? "root" : p.Username, Password = admin ? p.AdminPassword : p.Password, ConnectionTimeout = 3, DefaultCommandTimeout = 20, Pooling = false, SslMode = MySqlSslMode.Disabled, AllowPublicKeyRetrieval = true }.ConnectionString);
        if (p.Engine == "tds") admin = false;
        if (p.Engine == "babelfish") return new SqlConnection(new SqlConnectionStringBuilder { DataSource = $"tcp:127.0.0.1,{p.Port}", InitialCatalog = database ?? (admin ? "master" : p.Database), UserID = admin ? "wilton" : p.Username, Password = admin ? p.AdminPassword : p.Password, Encrypt = false, TrustServerCertificate = true, ConnectTimeout = 3, Pooling = false }.ConnectionString);
        return new SqlConnection(new SqlConnectionStringBuilder { DataSource = admin ? @"np:\\.\pipe\MSSQL$SQLLIGHT\sql\query" : $"tcp:127.0.0.1,{p.Port}", InitialCatalog = database ?? (admin ? "master" : p.Database), IntegratedSecurity = admin, UserID = admin ? "" : p.Username, Password = admin ? "" : p.Password, Encrypt = false, TrustServerCertificate = true, ConnectTimeout = 3, Pooling = false }.ConnectionString);
    }
    static async Task Exec(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 30; await cmd.ExecuteNonQueryAsync();
    }
    static string Literal(string s) => "'" + s.Replace("'", "''").Replace("\\", "\\\\") + "'";
    static string PgLiteral(string s) => "'" + s.Replace("'", "''") + "'";
    public async Task Start(Profile p)
    {
        if (p.Engine == "babelfish") { await StartBabelfish(p); return; }
        if (p.Engine == "tds")
        {
            if (!Running(p))
            {
                if (!PortFree(p.Port)) throw new Exception($"El puerto {p.Port} ya está ocupado.");
                await Install(p.Engine); var folder = store.Folder(p); Directory.CreateDirectory(folder);
                var info = new ProcessStartInfo(Bin(p.Engine, "python")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
                foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "emulator", "tds_server.py"), "--folder", folder, "--port", p.Port.ToString() }) info.ArgumentList.Add(arg);
                using var proc = Process.Start(info) ?? throw new Exception("No se pudo arrancar el emulador.");
                p.Pid = proc.Id; p.ProcessStart = proc.StartTime.ToUniversalTime().Ticks; store.Save();
                proc.OutputDataReceived += (_, _) => { }; proc.ErrorDataReceived += (_, _) => { }; proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
                await proc.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { username = p.Username, password = p.Password, database = p.Database }));
                await proc.StandardInput.FlushAsync();
            }
            await WaitReady(p); await using var check = Connection(p, false); await check.OpenAsync(); await Exec(check, "SELECT 1");
            p.Provisioned = true; p.Error = null; store.Save(); return;
        }
        if (p.Engine == "sqlserver")
        {
            if (!SqlInstalled()) throw new Exception("SQL Server Express no está instalado. Pulsa Preparar SQL Server e instala una instancia llamada SQLLIGHT.");
            if (!SqlRunning()) await Elevated("--sql-start", p.Port);
        }
        else if (!Running(p))
        {
            if (!PortFree(p.Port)) throw new Exception($"El puerto {p.Port} ya está ocupado. No se ha cerrado ningún proceso.");
            await Install(p.Engine);
            var folder = store.Folder(p); Directory.CreateDirectory(folder);
            if (p.Engine == "postgres")
            {
                var data = Path.Combine(folder, "cluster"); var pw = Path.Combine(folder, "init-password.tmp");
                if (!File.Exists(Path.Combine(data, "PG_VERSION")))
                {
                    if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()) throw new Exception("Inicialización incompleta: conserva los datos y crea otro perfil o elimina éste desde el panel.");
                    try { await File.WriteAllTextAsync(pw, p.AdminPassword); await Run(Bin(p.Engine, "initdb"), new[] { "-D", data, "-U", "sql_light_admin", "--pwfile=" + pw, "--auth=scram-sha-256", "--encoding=UTF8", "--locale=C" }); }
                    finally { if (File.Exists(pw)) File.Delete(pw); }
                    await File.AppendAllTextAsync(Path.Combine(data, "postgresql.conf"), $"\nlisten_addresses='127.0.0.1'\nport={p.Port}\nshared_buffers=32MB\nwork_mem=2MB\nmaintenance_work_mem=16MB\nmax_connections=20\nmax_parallel_workers=0\nlogging_collector=on\nlog_directory='../logs'\n");
                }
                using var proc = Launch(p, "postgres", new[] { "-D", data });
            }
            else
            {
                var data = Path.Combine(folder, "cluster");
                if (!Directory.Exists(Path.Combine(data, "mysql"))) await Run(Bin(p.Engine, "mysqld"), new[] { "--no-defaults", "--initialize-insecure", "--basedir=" + Path.Combine(EngineRoot, p.Engine), "--datadir=" + data, "--console" }, timeout: 180);
                var ini = Path.Combine(folder, "my.ini");
                await File.WriteAllTextAsync(ini, $"[mysqld]\nbasedir={Path.Combine(EngineRoot, p.Engine).Replace('\\', '/')}\ndatadir={data.Replace('\\', '/')}\nport={p.Port}\nbind-address=127.0.0.1\nmysqlx=0\ninnodb_buffer_pool_size=32M\ninnodb_log_buffer_size=4M\nmax_connections=20\ntable_open_cache=200\nperformance_schema=OFF\nlog-error={Path.Combine(folder, "mysql.log").Replace('\\', '/')}\n");
                var init = Path.Combine(folder, "bootstrap.sql");
                if (!p.Provisioned) await File.WriteAllTextAsync(init, $"ALTER USER 'root'@'localhost' IDENTIFIED BY {Literal(p.AdminPassword)};\n");
                using var proc = Launch(p, "mysqld", new[] { "--defaults-file=" + ini, "--console" }.Concat(!p.Provisioned ? new[] { "--init-file=" + init } : Array.Empty<string>()));
            }
        }
        try
        {
            await WaitReady(p);
            if (!p.Provisioned) await Provision(p);
            await using var user = Connection(p, false); await user.OpenAsync();
            await Exec(user, "SELECT 1"); p.Error = null; store.Save();
        }
        finally { var init = Path.Combine(store.Folder(p), "bootstrap.sql"); if (File.Exists(init)) File.Delete(init); }
    }
    async Task WaitReady(Profile p)
    {
        for (var i = 0; i < 60; i++)
        {
            if (!Running(p))
            {
                // Windows can briefly make MainModule unavailable just after Process.Start.
                if (i < 10) { await Task.Delay(500); continue; }
                throw new Exception("El motor terminó al arrancar. Consulta sus logs; puede faltar Microsoft Visual C++ Redistributable x64.");
            }
            try { await using var conn = Connection(p); await conn.OpenAsync(); return; } catch when (i < 59) { await Task.Delay(500); }
        }
        throw new Exception("El motor no está disponible.");
    }
    async Task Provision(Profile p)
    {
        await using var conn = Connection(p); await conn.OpenAsync();
        if (p.Engine == "postgres")
        {
            await Exec(conn, $"DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='{p.Username}') THEN CREATE ROLE \"{p.Username}\" LOGIN; END IF; END $$;");
            await Exec(conn, $"ALTER ROLE \"{p.Username}\" PASSWORD {PgLiteral(p.Password)};");
            await using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT count(*) FROM pg_database WHERE datname='{p.Database}'";
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0) await Exec(conn, $"CREATE DATABASE \"{p.Database}\" OWNER \"{p.Username}\";");
        }
        else if (p.Engine == "mysql")
        {
            await Exec(conn, $"CREATE DATABASE IF NOT EXISTS `{p.Database}` CHARACTER SET utf8mb4;");
            await Exec(conn, $"CREATE USER IF NOT EXISTS '{p.Username}'@'localhost' IDENTIFIED BY {Literal(p.Password)};");
            await Exec(conn, $"ALTER USER '{p.Username}'@'localhost' IDENTIFIED BY {Literal(p.Password)};");
            await Exec(conn, $"GRANT ALL PRIVILEGES ON `{p.Database}`.* TO '{p.Username}'@'localhost';");
        }
        else
        {
            await using var login = conn.CreateCommand(); login.CommandText = $"SELECT COUNT(*) FROM sys.server_principals WHERE name=N'{p.Username}'";
            if (Convert.ToInt32(await login.ExecuteScalarAsync()) == 0) await Exec(conn, $"CREATE LOGIN [{p.Username}] WITH PASSWORD=N{PgLiteral(p.Password)}, CHECK_POLICY=OFF;");
            else { await using var check = Connection(p, false, "master"); await check.OpenAsync(); }
            await using var exists = conn.CreateCommand(); exists.CommandText = $"SELECT DB_ID(N'{p.Database}')";
            // Never adopt an existing, unmanaged database.
            var file = Path.Combine(store.Folder(p), "sql-created.marker");
            Directory.CreateDirectory(store.Folder(p));
            var dbExists = await exists.ExecuteScalarAsync() is not DBNull;
            if (dbExists && !File.Exists(file)) throw new Exception("Ya existe esa base en SQLLIGHT. Elige otro nombre para no modificar datos existentes.");
            if (!dbExists)
            {
                await Exec(conn, $"CREATE DATABASE [{p.Database}];");
                await File.WriteAllTextAsync(file, p.Database);
            }
            await Exec(conn, $"USE [{p.Database}]; IF USER_ID(N'{p.Username}') IS NULL CREATE USER [{p.Username}] FOR LOGIN [{p.Username}]; ALTER ROLE db_owner ADD MEMBER [{p.Username}];");
        }
        p.Provisioned = true; store.Save();
    }
    public async Task Stop(Profile p)
    {
        if (p.Engine == "sqlserver") { if (SqlRunning()) await Elevated("--sql-stop", p.Port); return; }
        using var proc = OwnedProcess(p); if (proc == null) return;
        if (p.Engine == "tds") await File.WriteAllTextAsync(Path.Combine(store.Folder(p), "stop.request"), "stop");
        else if (p.Engine is "postgres" or "babelfish") await Run(Bin(p.Engine, "pg_ctl"), new[] { "stop", "-D", Path.Combine(store.Folder(p), "cluster"), "-m", "fast", "-w", "-t", "30" }, timeout: 40);
        else { await using var conn = Connection(p); await conn.OpenAsync(); try { await Exec(conn, "SHUTDOWN;"); } catch (MySqlException) when (proc.HasExited) { } }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40)); await proc.WaitForExitAsync(cts.Token);
        p.Pid = null; p.ProcessStart = 0; store.Save();
    }
    public async Task Delete(Profile p)
    {
        if (p.Engine == "sqlserver" && (p.Provisioned || File.Exists(Path.Combine(store.Folder(p), "sql-created.marker"))))
        {
            if (!SqlRunning()) await Elevated("--sql-start", p.Port);
            await using var conn = Connection(p); await conn.OpenAsync();
            await Exec(conn, $"ALTER DATABASE [{p.Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{p.Database}];");
            // Shared logins are kept: another managed database may use this login.
        }
        else if (p.Engine != "sqlserver") await Stop(p);
        var folder = store.Folder(p);
        if (Directory.Exists(folder))
        {
            var trash = Path.GetFullPath(Path.Combine(store.Root, "trash")); Directory.CreateDirectory(trash);
            var resolved = Path.GetFullPath(folder);
            if (!resolved.StartsWith(Path.Combine(store.Root, "data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Ruta fuera de data.");
            if (p.Engine != "sqlserver") Store.Archive(Path.Combine(resolved, "profile.dat"), p);
            Directory.Move(resolved, Path.Combine(trash, p.Id + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")));
        }
        lock (store.Profiles) store.Profiles.Remove(p); store.Save();
    }
    public void Restore(string key)
    {
        var archive = store.Archives().FirstOrDefault(x => x.Key == key);
        if (archive.Profile == null) throw new Exception("No existe ese entorno en la papelera.");
        var p = archive.Profile; p.Validate();
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^[a-f0-9]{32}$")) throw new Exception("Identificador inválido.");
        if (store.Profiles.Any(x => x.Port == p.Port || x.Id == p.Id)) throw new Exception("Otro entorno ya usa su puerto. Elimina ese perfil antes de recuperar éste.");
        if (Directory.Exists(store.Folder(p))) throw new Exception("La carpeta de datos ya existe.");
        Directory.CreateDirectory(Path.GetDirectoryName(store.Folder(p))!);
        Directory.Move(Path.Combine(store.Root, "trash", key), store.Folder(p));
        p.Pid = null; p.ProcessStart = 0; p.Error = null;
        lock (store.Profiles) store.Profiles.Add(p); store.Save();
    }
    public static async Task Elevated(string action, int port)
    {
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
        {
            if (action is not ("--sql-start" or "--sql-stop")) throw new Exception("Configura SQLLIGHT desde el escritorio con permisos de administrador antes de usarlo en el servicio.");
            try
            {
                using var controller = new System.ServiceProcess.ServiceController("MSSQL$SQLLIGHT");
                if (action == "--sql-start") controller.Start(); else controller.Stop();
                await Task.Run(() => controller.WaitForStatus(action == "--sql-start" ? System.ServiceProcess.ServiceControllerStatus.Running : System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)));
                return;
            }
            catch { throw new Exception("El servicio SQL Light necesita permisos sobre SQLLIGHT. Prepara SQL Server en el escritorio y vuelve a instalar el servicio SQL Light."); }
        }
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(action); info.ArgumentList.Add(port.ToString());
        using var proc = Process.Start(info) ?? throw new Exception("No se pudo abrir la preparación de SQL Server.");
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new Exception("SQL Server no se pudo preparar. Comprueba que instalaste SQLLIGHT y concediste acceso a tu usuario de Windows.");
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;

namespace SqlLight;

public sealed class PanelServer(Store store, Engines engines, bool serviceMode = false, bool resumeOnStart = true, bool consoleLogging = false)
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    readonly EnvironmentTelemetry telemetry = new(store, engines);
    WebApplication? app;
    public string Url { get; private set; } = "";
    public bool Busy { get; private set; }
    public async Task Start()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders(); if (consoleLogging) builder.Logging.AddConsole(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (serviceMode) builder.Services.AddWindowsService(options => options.ServiceName = ServiceInstallation.Name);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(120));
        builder.Services.AddSingleton(this);
        builder.Services.AddHostedService<EngineLifetime>();
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8388608);
        app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Host.Host != "127.0.0.1" || ctx.Request.Headers.ContainsKey("Origin") && ctx.Request.Headers.Origin != Url.TrimEnd('/')) { ctx.Response.StatusCode = 403; return; }
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'nonce-{token}'; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
            if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["X-SqlLight-Token"] != token) { ctx.Response.StatusCode = 403; return; }
            try { await next(); }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 400;
                var message = ex.Message;
                foreach (var p in store.Snapshot()) { message = message.Replace(p.AdminPassword, "[oculto]").Replace(p.Password, "[oculto]"); }
                await ctx.Response.WriteAsJsonAsync(new { error = message });
            }
        });
        app.MapGet("/", async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var html = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "www", "index.html"));
            await ctx.Response.WriteAsync(html.Replace("__TOKEN__", token));
        });
        app.MapGet("/api/state", () =>
        {
            var sqlMemory = ProcessMemory.SqlServer();
            var profiles = store.Snapshot().Select(p =>
            {
                var running = engines.Running(p);
                using var proc = p.Engine == "sqlserver" ? null : engines.OwnedProcess(p);
                double? memory = running ? null : 0;
                try
                {
                    long? bytes = p.Engine == "sqlserver" ? sqlMemory : proc != null ? ProcessMemory.Tree(proc) : null;
                    if (running && bytes.HasValue) memory = Math.Round(bytes.Value / 1048576d, 1);
                } catch { }
                return new { p.Id, p.Engine, p.Database, p.Username, p.Port, p.Provisioned, p.AutoStart, p.Error, running, memoryMb = memory, memoryShared = p.Engine == "sqlserver", jdbc = p.Jdbc, driver = p.Driver, dataPath = telemetry.DataPath(p), profilePath = store.Folder(p) };
            }).ToArray();
            using var manager = Process.GetCurrentProcess();
            var otherApps = ProcessMemory.OtherApps(Environment.ProcessPath!);
            var appMemory = Math.Round(manager.WorkingSet64 / 1048576d, 1);
            var databaseMemory = Math.Round(profiles.Where(p => p.running && p.Engine != "sqlserver").Sum(p => p.memoryMb ?? 0) + (profiles.Any(p => p.running && p.Engine == "sqlserver") ? sqlMemory / 1048576d ?? 0 : 0), 1);
            return Results.Json(new
            {
                busy = Busy, activity = engines.Activity,
                engines = new[] { "postgres", "mysql", "sqlserver", "tds", "babelfish" }.Select(e => new { id = e, installed = engines.Installed(e) }),
                profiles,
                totalMemoryMb = databaseMemory,
                memoryUnavailable = profiles.Count(p => p.running && p.memoryMb == null),
                appMemoryMb = appMemory,
                serviceMemoryMb = serviceMode ? appMemory : 0,
                desktopMemoryMb = Math.Round((serviceMode ? 0 : appMemory) + otherApps / 1048576d, 1),
                overallMemoryMb = Math.Round(databaseMemory + appMemory + otherApps / 1048576d, 1),
                hostingMode = serviceMode ? "service" : "desktop",
                serviceInstalled = ServiceInstallation.Installed(store.Root),
                appPid = Environment.ProcessId,
                saved = true,
                dataRoot = Path.Combine(store.Root, "data"), trashRoot = Path.Combine(store.Root, "trash")
            });
        });
        app.MapGet("/api/telemetry", async () => Results.Json(await telemetry.Read()));
        app.MapGet("/api/storage", async () => Results.Json(await Task.Run(() => store.Snapshot().Select(p => new { p.Id, diskBytes = EnvironmentTelemetry.DiskBytes(store.Folder(p)) }).ToArray())));
        app.MapPost("/api/profiles/{id}/auto-start", async (string id, AutoStartRequest request) => await Locked(() =>
        {
            var p = Find(id); p.AutoStart = request.Enabled; store.Save(); return Task.FromResult(Results.Ok());
        }));
        app.MapPost("/api/prepare-install", async () => await Locked(() =>
        {
            if (serviceMode) throw new Exception("El servicio ya está instalado.");
            foreach (var p in store.Snapshot()) if (engines.Running(p)) p.AutoStart = true;
            store.Save(); store.MigrateArchives(); return Task.FromResult(Results.Ok());
        }));
        app.MapPost("/api/install-service", () =>
        {
            if (serviceMode || Busy) return Results.Json(new { error = "Gestiona la instalación desde el escritorio cuando termine la operación." }, statusCode: 409);
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--install"); info.ArgumentList.Add("--root"); info.ArgumentList.Add(store.Root);
            Process.Start(info); return Results.Ok();
        });
        app.MapGet("/api/port/{engine}", (string engine) => Results.Json(new { port = engines.SuggestPort(engine) }));
        app.MapGet("/api/trash", async () => Results.Json(await Task.Run(() => store.Archives().Select(x => new { x.Key, x.Profile.Database, x.Profile.Engine, x.Profile.Port, path = Path.Combine(store.Root, "trash", x.Key), diskBytes = EnvironmentTelemetry.DiskBytes(Path.Combine(store.Root, "trash", x.Key)) }).ToArray())));
        app.MapPost("/api/trash/{key}/restore", async (string key) => await Locked(() => { engines.Restore(key); return Task.FromResult(Results.Ok()); }));
        app.MapPost("/api/trash/{key}/purge", async (string key, PurgeRequest request) => await Locked(() =>
        {
            var archive = store.Archives().FirstOrDefault(x => x.Key == key);
            if (archive.Profile == null || request.Database != archive.Profile.Database) throw new Exception("Escribe el nombre exacto del entorno de la papelera.");
            var parent = Path.GetFullPath(Path.Combine(store.Root, "trash"));
            var path = Path.GetFullPath(Path.Combine(parent, archive.Key));
            if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new Exception("Ruta de papelera inválida.");
            // Never recursively follow junctions or symbolic links out of the managed folder.
            var pending = new Stack<DirectoryInfo>(); pending.Push(new DirectoryInfo(path));
            while (pending.TryPop(out var directory))
                foreach (var item in directory.EnumerateFileSystemInfos())
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new Exception("La carpeta contiene enlaces. Revísala antes de borrarla.");
                    if (item is DirectoryInfo child) pending.Push(child);
                }
            Directory.Delete(path, true); return Task.FromResult(Results.Ok());
        }));
        app.MapPost("/api/profiles", async (Profile input) => await Locked(async () =>
        {
            // Client cannot supply runtime identity, admin secret or provisioning flags.
            var p = new Profile { Engine = input.Engine, Database = input.Database, Username = input.Username, Password = input.Password, Port = input.Port }; p.Validate();
            if (store.Profiles.Any(other => other.Engine == "sqlserver" && p.Engine == "sqlserver" && other.Database == p.Database)) throw new Exception("Ese nombre ya existe en SQLLIGHT.");
            if (store.Profiles.Any(other => other.Port == p.Port && (other.Engine != "sqlserver" || p.Engine != "sqlserver"))) throw new Exception("Ese puerto ya está asignado a otro perfil.");
            if (p.Engine == "sqlserver" && store.Profiles.Any(other => other.Engine == "sqlserver" && other.Port != p.Port)) throw new Exception("Los perfiles de SQL Server comparten el puerto de la instancia SQLLIGHT.");
            if (p.Engine == "sqlserver" && store.Profiles.Any(other => other.Engine == "sqlserver" && other.Username == p.Username && other.Password != p.Password)) throw new Exception("Ese usuario ya existe con otra contraseña en SQLLIGHT.");
            lock (store.Profiles) store.Profiles.Add(p); store.Save(); await Task.CompletedTask; return Results.Json(new { p.Id });
        }));
        app.MapPost("/api/install/{engine}", async (string engine) => await Locked(async () =>
        {
            if (engine is not ("postgres" or "mysql" or "sqlserver" or "tds" or "babelfish")) throw new Exception("Motor desconocido.");
            if (engine == "sqlserver")
            {
                if (serviceMode) throw new Exception("Abre el menú del icono junto al reloj y elige Instalar SQL Server Express. Su asistente necesita el escritorio de Windows.");
                await SqlSetup.Install(store.Root);
            }
            else await engines.Install(engine);
            return Results.Ok();
        }));
        app.MapPost("/api/sql/configure", async (PortRequest request) => await Locked(async () =>
        {
            if (serviceMode) throw new Exception("Desde el menú del icono junto al reloj elige Configurar SQLLIGHT para el servicio. Windows solicitará permisos de administrador.");
            if (store.Profiles.Any(p => p.Engine == "sqlserver" && p.Port != request.Port)) throw new Exception("Usa el puerto de los perfiles SQL Server existentes.");
            await Engines.Elevated("--sql-configure", request.Port); return Results.Ok();
        }));
        app.MapGet("/api/profiles/{id}/scripts", (string id) =>
        {
            var p = Find(id);
            return Results.Json(new { defaultSql = StartupScripts.DefaultSql(p), provisioned = p.Provisioned, scripts = StartupScripts.Ordered(p.Scripts), history = p.ScriptHistory });
        });
        app.MapPost("/api/profiles/{id}/scripts", async (string id, ScriptsRequest request) => await Locked(() =>
        {
            var p = Find(id); StartupScripts.Save(p, request.Scripts); store.Save(); return Task.FromResult(Results.Ok());
        }));
        app.MapGet("/api/profiles/{id}/connection", (string id) => Results.Json(new { yaml = Find(id).Yaml }));
        app.MapGet("/api/profiles/{id}/logs", async (string id) =>
        {
            var p = Find(id); var folder = store.Folder(p);
            var files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.log", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(2).ToArray() : Array.Empty<string>();
            var lines = new List<string>();
            foreach (var file in files)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 16384) stream.Seek(-16384, SeekOrigin.End);
                using var reader = new StreamReader(stream); lines.Add(await reader.ReadToEndAsync());
            }
            return Results.Json(new { text = (string.Join("\n", lines) is { Length: > 0 } text ? text : "No hay logs todavía. SQL Server registra sus errores en la carpeta de la instancia.").Replace(p.AdminPassword, "[oculto]").Replace(p.Password, "[oculto]") });
        });
        app.MapPost("/api/profiles/{id}/{action}", async (string id, string action) => await Locked(async () =>
        {
            var p = Find(id);
            try
            {
                if (action == "start") await engines.Start(p);
                else if (action == "stop") await engines.Stop(p);
                else if (action == "delete") await engines.Delete(p);
                else if (action == "test") { await using var conn = engines.Connection(p, false); await conn.OpenAsync(); await using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT 1"; await cmd.ExecuteScalarAsync(); }
                else throw new Exception("Acción desconocida.");
                p.Error = null; store.Save(); return Results.Ok();
            }
            catch (Exception ex) { p.Error = ex.Message.Replace(p.AdminPassword, "[oculto]").Replace(p.Password, "[oculto]"); store.Save(); throw; }
        }));
        await app.StartAsync();
        Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
        Directory.CreateDirectory(Path.Combine(store.Root, "artifacts"));
        await File.WriteAllTextAsync(Path.Combine(store.Root, "artifacts", "panel-url.txt"), Url);
    }
    Profile Find(string id) => store.Profiles.FirstOrDefault(p => p.Id == id) ?? throw new Exception("No existe ese perfil.");
    async Task<IResult> Locked(Func<Task<IResult>> work)
    {
        if (!await gate.WaitAsync(0)) return Results.Json(new { error = "Hay una operación en curso. Espera a que termine." }, statusCode: 409);
        Busy = true;
        try { return await work(); } finally { Busy = false; gate.Release(); }
    }
    public async Task Shutdown()
    {
        if (Busy) throw new Exception("Espera a que termine la operación antes de salir.");
        if (app != null) { await app.StopAsync(); await app.DisposeAsync(); app=null; }
    }
    public async Task WaitForShutdown() { if (app != null) await app.WaitForShutdownAsync(); }
    public async Task ResumeEnvironments(CancellationToken cancellationToken)
    {
        if (!resumeOnStart) return;
        foreach (var p in store.Snapshot().Where(p => p.AutoStart))
        {
            if (cancellationToken.IsCancellationRequested) break;
            await gate.WaitAsync(cancellationToken); Busy = true;
            try { await engines.Start(p); }
            catch (Exception ex) { p.Error = ex.Message.Replace(p.AdminPassword, "[oculto]").Replace(p.Password, "[oculto]"); store.Save(); }
            finally { Busy = false; gate.Release(); }
        }
    }
    public async Task StopEnvironments(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken); Busy = true;
        try
        {
            foreach (var p in store.Profiles.Where(p => p.Engine != "sqlserver").ToArray()) await engines.Stop(p);
        }
        finally { Busy = false; gate.Release(); }
    }
    public record PortRequest(int Port);
    public record ScriptsRequest(StartupScript[] Scripts);
    public record AutoStartRequest(bool Enabled);
    public record PurgeRequest(string Database);
}

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlLight;

public static class SelfTests
{
    public static async Task<int> Run(string root)
    {
        var results = new List<object>();
        var testRoot = Path.Combine(root, "artifacts", "test-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var store = new Store(testRoot); var engines = new Engines(store, Path.Combine(root, "engines"));
        PanelServer? panel = null;
        try
        {
            await Cleanup(root);
            foreach (var bad in new[] { "../data", "app;DROP", "app'", "" })
            {
                var rejected = false; try { new Profile { Database = bad, Port = 5432 }.Validate(); } catch { rejected = true; }
                Check(rejected, "Reject unsafe database name");
            }
            var secret = "quote' back\\slash \" €";
            var sample = new Profile { Password = secret, Port = 5432, AutoStart = true };
            Check(sample.Yaml.Contains("jdbc:postgresql://localhost:5432/app_dev"), "JDBC URL");
            File.WriteAllBytes(Path.Combine(testRoot, "state.dat"), System.Security.Cryptography.ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new[] { sample }), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            Check(new Store(testRoot).Profiles.Single().Password == secret, "Legacy profile migration");
            store.Profiles.Add(sample); store.Save();
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.StateFile)).Contains(secret), "Encrypted credentials");
            Check(new Store(testRoot).Profiles.Single().Password == secret, "Credential round trip");
            Check(new Store(testRoot).Profiles.Single().AutoStart, "Auto-start preference persisted");
            store.Profiles.Clear(); store.Save(); results.Add(new { test = "validation-and-DPAPI", passed = true });
            foreach (var engine in new[] { "postgres", "mysql" })
            {
                // Install real official binaries once in the project's shared engine directory.
                if (!engines.Installed(engine)) await new Engines(new Store(root)).Install(engine);
                var port = engine == "postgres" ? 15432 : 13306; while (!Engines.PortFree(port)) port++;
                var p = new Profile { Engine = engine, Database = "sql_light_test", Port = port, Password = secret };
                store.Profiles.Add(p); store.Save();
                await engines.Start(p); Check(engines.Running(p), "Process running");
                await using (var conn = engines.Connection(p, false))
                {
                    await conn.OpenAsync(); await Execute(conn, "CREATE TABLE persistence_check (id INT PRIMARY KEY, value VARCHAR(50))");
                    await Execute(conn, "INSERT INTO persistence_check VALUES (1, 'still here')");
                }
                using var process = engines.OwnedProcess(p); var ram = ProcessMemory.Tree(process!) / 1048576;
                var wrongIdentity = new Profile { Engine = engine, Pid = p.Pid, ProcessStart = p.ProcessStart - 1 };
                Check(engines.OwnedProcess(wrongIdentity) == null, "Reject reused PID");
                await engines.Stop(p); Check(!engines.Running(p), "Graceful stop");
                await engines.Start(p);
                await using (var conn = engines.Connection(p, false))
                {
                    await conn.OpenAsync(); await using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT value FROM persistence_check WHERE id=1";
                    Check((string?)await cmd.ExecuteScalarAsync() == "still here", "Persistence after restart");
                }
                var jdbc = await Jdbc(root, p);
                var blocked = new Profile { Engine = engine, Database = "blocked_port", Port = port };
                var rejected = false; try { await engines.Start(blocked); } catch { rejected = true; }
                Check(rejected && !Directory.Exists(store.Folder(blocked)), "Busy port does not initialize data");
                await engines.Delete(p); Check(store.Profiles.Count == 0 && Directory.EnumerateDirectories(Path.Combine(testRoot, "trash")).Any(), "Delete preserves portable data");
                var archive = store.Archives().First(x => x.Profile.Id == p.Id); engines.Restore(archive.Key);
                await engines.Start(p = store.Profiles.Single());
                await using (var conn = engines.Connection(p, false)) { await conn.OpenAsync(); await using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT value FROM persistence_check WHERE id=1"; Check((string?)await cmd.ExecuteScalarAsync() == "still here", "Trash restores data and credentials"); }
                await engines.Delete(p);
                results.Add(new { test = engine + "-real-integration", passed = true, processTreeMemoryMb = ram, jdbc, checks = "create/user/SQL/write/stop/restart/persistence/port conflict/PID/delete/restore" });
            }
            panel = new PanelServer(store, engines); await panel.Start();
            using var client = new HttpClient();
            Check((await client.GetAsync(panel.Url + "api/state")).StatusCode == HttpStatusCode.Forbidden, "API token required");
            var html = await client.GetStringAsync(panel.Url); var token = Regex.Match(html, "const token='([A-F0-9]+)'").Groups[1].Value; Check(token.Length == 64, "Session token");
            client.DefaultRequestHeaders.Add("X-SqlLight-Token", token);
            Check((await client.GetAsync(panel.Url + "api/state")).IsSuccessStatusCode, "Authorized API");
            using var hostile = new HttpRequestMessage(HttpMethod.Post, panel.Url + "api/profiles") { Content = JsonContent.Create(new Profile { Port = 5432 }) };
            hostile.Headers.Add("Origin", "https://example.com");
            Check((await client.SendAsync(hostile)).StatusCode == HttpStatusCode.Forbidden, "Cross-origin request rejected");
            var create = await client.PostAsJsonAsync(panel.Url + "api/profiles", new Profile { Engine = "postgres", Database = "api_test", Port = 15432 });
            Check(create.IsSuccessStatusCode, "HTTP create profile");
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            Check((await client.PostAsJsonAsync(panel.Url + "api/profiles/" + id + "/start", new { })).IsSuccessStatusCode, "HTTP start for memory sampling");
            var live = JsonDocument.Parse(await client.GetStringAsync(panel.Url + "api/state")).RootElement;
            var memory = live.GetProperty("profiles")[0].GetProperty("memoryMb").GetDouble();
            Check(memory > 0 && live.GetProperty("totalMemoryMb").GetDouble() == memory, "Live environment RAM and total");
            Check(live.GetProperty("appMemoryMb").GetDouble() > 0, "Manager memory is separate");
            Check(live.GetProperty("serviceMemoryMb").GetDouble() == 0 && live.GetProperty("desktopMemoryMb").GetDouble() > 0, "Desktop mode memory breakdown");
            Check(Math.Abs(live.GetProperty("overallMemoryMb").GetDouble() - (memory + live.GetProperty("desktopMemoryMb").GetDouble())) < 0.15, "Combined application and database memory");
            Check((await client.PostAsJsonAsync(panel.Url + "api/profiles/" + id + "/auto-start", new { enabled = true })).IsSuccessStatusCode, "Save auto-start through API");
            Check(new Store(testRoot).Profiles.Single().AutoStart, "Saved environment survives reopening");
            Check((await client.PostAsJsonAsync(panel.Url + "api/profiles/" + id + "/stop", new { })).IsSuccessStatusCode, "HTTP stop for memory sampling");
            var stopped = JsonDocument.Parse(await client.GetStringAsync(panel.Url + "api/state")).RootElement;
            Check(stopped.GetProperty("totalMemoryMb").GetDouble() == 0 && stopped.GetProperty("profiles")[0].GetProperty("memoryMb").GetDouble() == 0, "Stopped environment uses zero measured RAM");
            var restoredStore = new Store(testRoot);
            var restoredEngines = new Engines(restoredStore, Path.Combine(root, "engines"));
            var servicePanel = new PanelServer(restoredStore, restoredEngines, serviceMode: true);
            await servicePanel.Start();
            for (var i = 0; i < 120 && (!restoredEngines.Running(restoredStore.Profiles.Single()) || servicePanel.Busy); i++) await Task.Delay(250);
            Check(restoredEngines.Running(restoredStore.Profiles.Single()), "Headless host resumes saved environment automatically");
            var serviceHtml = await client.GetStringAsync(servicePanel.Url);
            using var serviceClient = new HttpClient();
            serviceClient.DefaultRequestHeaders.Add("X-SqlLight-Token", Regex.Match(serviceHtml, "const token='([A-F0-9]+)'").Groups[1].Value);
            var serviceState = JsonDocument.Parse(await serviceClient.GetStringAsync(servicePanel.Url + "api/state")).RootElement;
            Check(serviceState.GetProperty("serviceMemoryMb").GetDouble() > 0 && serviceState.GetProperty("desktopMemoryMb").GetDouble() == 0, "Headless service memory breakdown");
            await servicePanel.Shutdown();
            Check(!restoredEngines.Running(restoredStore.Profiles.Single()), "Headless shutdown stops managed database gracefully");
            Check((await client.GetStringAsync(panel.Url + "api/profiles/" + id + "/connection")).Contains("org.postgresql.Driver"), "Spring export API");
            Check(!(await client.GetStringAsync(panel.Url + "api/state")).Contains("adminPassword", StringComparison.OrdinalIgnoreCase), "State does not disclose credentials");
            Check((await client.PostAsJsonAsync(panel.Url + "api/profiles/" + id + "/delete", new { })).IsSuccessStatusCode, "HTTP delete profile");
            results.Add(new { test = "http-panel", passed = true, checks = "API authentication/CSRF/profile CRUD/connection export/persistence/auto-start/headless lifecycle/service+desktop+database memory" });
            results.Add(new { test = "sqlserver-integration", skipped = true, engineInstalled = Engines.SqlInstalled(), reason = "Requires installed SQLLIGHT, setup wizard and Windows elevation; not installed by tests." });
            results.Add(new { test = "windows-service-registration", skipped = true, reason = "Headless hosting, auto-start and shutdown verified. Actual SCM registration under LocalService requires Windows administrator approval and is not performed by self-tests." });
            await panel.Shutdown();
            await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "self-test.json"), JsonSerializer.Serialize(new { passed = true, testRoot, results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            foreach (var p in store.Profiles.ToArray()) try { await engines.Stop(p); } catch { }
            await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "self-test.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), testRoot, results }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
    public static async Task Cleanup(string root)
    {
        var folder = Path.Combine(root, "artifacts"); if (!Directory.Exists(folder)) return;
        foreach (var dir in Directory.EnumerateDirectories(folder, "test-*"))
        {
            if (!File.Exists(Path.Combine(dir, "state.dat")) && !File.Exists(Path.Combine(dir, "private", "state.dat"))) continue;
            var store = new Store(dir); var engines = new Engines(store, Path.Combine(root, "engines"));
            foreach (var p in store.Snapshot())
            {
                if (p.Engine == "sqlserver") continue;
                await engines.Stop(p);
                var file = Path.Combine(store.Folder(p), "bootstrap.sql"); if (File.Exists(file)) File.Delete(file);
            }
        }
    }
    static async Task<string> Jdbc(string root, Profile p)
    {
        try { await Engines.Run("java.exe", new[] { "-version" }); }
        catch { return "Skipped: java.exe not found"; }
        var folder = Path.Combine(root, "artifacts", "jdbc"); Directory.CreateDirectory(folder);
        var url = p.Engine == "postgres" ? "https://repo.maven.apache.org/maven2/org/postgresql/postgresql/42.7.8/postgresql-42.7.8.jar" : "https://repo.maven.apache.org/maven2/com/mysql/mysql-connector-j/8.4.0/mysql-connector-j-8.4.0.jar";
        var jar = Path.Combine(folder, p.Engine + ".jar");
        if (!File.Exists(jar)) { using var client = new HttpClient(); await File.WriteAllBytesAsync(jar, await client.GetByteArrayAsync(url)); }
        return (await Engines.Run("java.exe", new[] { "--class-path", jar, Path.Combine(AppContext.BaseDirectory, "tests", "JdbcProbe.java"), p.Driver, p.Jdbc }, new Dictionary<string, string> { ["SQL_LIGHT_TEST_PASSWORD"] = p.Password })).Trim();
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Test failed: " + message); }
    static async Task Execute(DbConnection conn, string sql) { await using var cmd = conn.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
}

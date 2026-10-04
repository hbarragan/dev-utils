using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlLight;

internal static class EmulatorTests
{
    public static async Task<int> Run(string root)
    {
        var fixture = Path.Combine(root, "artifacts", "tds-test-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var store = new Store(fixture); var engines = new Engines(store, Path.Combine(root, "engines"));
        var p = new Profile { Engine = "tds", Database = "emu_dev", Port = engines.SuggestPort("tds") };
        store.Profiles.Add(p); store.Save();
        PanelServer? panel = null;
        var checks = new List<string>();
        double memory = 0;
        try
        {
            await engines.Start(p); checks.Add("Native Microsoft.Data.SqlClient connects");
            await using (var conn = engines.Connection(p, false))
            {
                await conn.OpenAsync(); await using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT 1";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 1) throw new Exception("Incorrect SELECT 1 result");
            }
            var jar = Path.Combine(root, "artifacts", "jdbc", "mssql-jdbc.jar");
            if (!File.Exists(jar))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(jar)!); using var client = new HttpClient();
                await File.WriteAllBytesAsync(jar, await client.GetByteArrayAsync("https://repo.maven.apache.org/maven2/com/microsoft/sqlserver/mssql-jdbc/12.8.1.jre11/mssql-jdbc-12.8.1.jre11.jar"));
            }
            var args = new[] { "--class-path", jar, Path.Combine(AppContext.BaseDirectory, "tests", "TdsProbe.java"), p.Jdbc + ";loginTimeout=5;socketTimeout=10000" };
            checks.Add((await Engines.Run("java.exe", args, new Dictionary<string, string> { ["SQL_LIGHT_TEST_PASSWORD"] = p.Password })).Trim());
            using (var process = engines.OwnedProcess(p)) memory = Math.Round(process!.WorkingSet64 / 1048576d, 1);
            panel = new PanelServer(store, engines, resumeOnStart: false); await panel.Start();
            using (var http = new HttpClient())
            {
                var html = await http.GetStringAsync(panel.Url);
                http.DefaultRequestHeaders.Add("X-SqlLight-Token", Regex.Match(html, "const token='([A-F0-9]+)'").Groups[1].Value);
                var state = JsonDocument.Parse(await http.GetStringAsync(panel.Url + "api/state"));
                var profile = state.RootElement.GetProperty("profiles")[0];
                if (!profile.GetProperty("running").GetBoolean() || profile.GetProperty("memoryShared").GetBoolean() || profile.GetProperty("memoryMb").GetDouble() <= 0) throw new Exception("HTTP memory/profile mismatch");
                using var test = await http.PostAsync(panel.Url + $"api/profiles/{p.Id}/test", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")); test.EnsureSuccessStatusCode();
            }
            checks.Add("HTTP profile, connection test and dedicated process RAM");
            await engines.Stop(p);
            if (engines.Running(p) || !Engines.PortFree(p.Port)) throw new Exception("Emulator failed to stop or release port");
            checks.Add("Graceful stop and released TCP port");
            await engines.Start(p);
            checks.Add((await Engines.Run("java.exe", args.Concat(new[] { "read" }), new Dictionary<string, string> { ["SQL_LIGHT_TEST_PASSWORD"] = p.Password })).Trim());
            await panel.Shutdown(); panel = null;
            if (engines.Running(p)) throw new Exception("Host shutdown left emulator running");
            var saved = new Store(fixture);
            if (saved.Profiles.Single().Driver != "com.microsoft.sqlserver.jdbc.SQLServerDriver") throw new Exception("Saved driver mismatch");
            checks.Add("Manager shutdown, saved profile and driver preserved");
            File.WriteAllText(Path.Combine(root, "artifacts", "tds-test.json"), JsonSerializer.Serialize(new { passed = true, fixture, memoryMb = memory, checks, appCompatibility = "aplicaciones Hibernate complejas: not compatible yet (ps schema, metadata, Quartz locking, datetimeoffset)" }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "artifacts", "tds-test.json"), JsonSerializer.Serialize(new { passed = false, fixture, checks, error = ex.Message.Replace(p.Password, "[hidden]") }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally
        {
            try { if (panel != null) await panel.Shutdown(); else await engines.Stop(p); } catch { }
        }
    }
}

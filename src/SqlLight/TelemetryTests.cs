using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace SqlLight;

public static class TelemetryTests
{
    public static async Task<int> Run(string root)
    {
        var store = new Store(Path.Combine(root, "artifacts", "telemetry-test-" + Guid.NewGuid().ToString("N"))); var engines = new Engines(store, Path.Combine(root, "engines"));
        var p = new Profile { Engine = "babelfish", Database = "telemetry_test", Port = engines.SuggestPort("babelfish") };
        store.Profiles.Add(p); store.Save(); await engines.Start(p);
        var monitor = new EnvironmentTelemetry(store, engines);
        var table = "SQLLIGHT_PROBE_" + Guid.NewGuid().ToString("N")[..10];
        await using var connection = (SqlConnection)engines.Connection(p, false);
        var builder = new SqlConnectionStringBuilder(connection.ConnectionString) { ApplicationName = "SQL Light telemetry test" };
        connection.ConnectionString = builder.ConnectionString; await connection.OpenAsync();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE dbo.[{table}] (id INT NOT NULL)"; await command.ExecuteNonQueryAsync();
            var first = JsonSerializer.SerializeToElement(await monitor.Read(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var before = first[0].GetProperty("telemetry");
            if (!before.GetProperty("available").GetBoolean() || !before.GetProperty("sessions").EnumerateArray().Any(s => s.GetProperty("application").GetString() == "SQL Light telemetry test")) throw new Exception("La sesión TDS no aparece en la monitorización: " + before);
            command.CommandText = $"INSERT INTO dbo.[{table}] VALUES (1),(2),(3)"; await command.ExecuteNonQueryAsync();
            await Task.Delay(6500);
            var second = JsonSerializer.SerializeToElement(await monitor.Read(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var after = second[0].GetProperty("telemetry");
            if (after.GetProperty("inserts").GetInt64() < before.GetProperty("inserts").GetInt64() + 3 || after.GetProperty("writesPerSecond").GetDouble() <= 0) throw new Exception("No se detectaron las escrituras.");
            await StorageTest(root);
            await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "telemetry-test.json"), JsonSerializer.Serialize(new { passed = true, storageRecoveryAndPurgePassed = true, before, after, dataPath = monitor.DataPath(p), diskBytes = EnvironmentTelemetry.DiskBytes(store.Folder(p)) }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            using var cleanup = connection.CreateCommand(); cleanup.CommandText = $"DROP TABLE IF EXISTS dbo.[{table}]"; await cleanup.ExecuteNonQueryAsync();
            await connection.CloseAsync(); await engines.Stop(p);
        }
    }
    static async Task StorageTest(string root)
    {
        var fixture = new Store(Path.Combine(root, "artifacts", "storage-test-" + Guid.NewGuid().ToString("N")));
        var engines = new Engines(fixture); var profile = new Profile { Database = "storage_test", Engine = "babelfish", Port = 15490 };
        fixture.Profiles.Add(profile); fixture.Save(); var path = fixture.Folder(profile);
        Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "fixture.txt"), "disposable test data");
        await engines.Delete(profile); var key = fixture.Archives().Single().Key;
        engines.Restore(key);
        if (fixture.Folder(fixture.Profiles.Single()) != path || !File.Exists(Path.Combine(path, "fixture.txt"))) throw new Exception("La recuperación cambió la ruta o perdió los datos.");
        await engines.Delete(fixture.Profiles.Single()); key = fixture.Archives().Single().Key;
        var server = new PanelServer(fixture, engines, resumeOnStart: false); await server.Start();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
            var html = await client.GetStringAsync(""); var token = System.Text.RegularExpressions.Regex.Match(html, "const token='([A-F0-9]+)'").Groups[1].Value;
            client.DefaultRequestHeaders.Add("X-SqlLight-Token", token);
            var wrong = await client.PostAsync("api/trash/" + key + "/purge", new StringContent("{\"database\":\"wrong\"}", System.Text.Encoding.UTF8, "application/json"));
            if (wrong.IsSuccessStatusCode || !fixture.Archives().Any()) throw new Exception("El borrado no exige el nombre correcto.");
            var correct = await client.PostAsync("api/trash/" + key + "/purge", new StringContent("{\"database\":\"storage_test\"}", System.Text.Encoding.UTF8, "application/json"));
            correct.EnsureSuccessStatusCode();
            if (Directory.Exists(Path.Combine(fixture.Root, "trash", key))) throw new Exception("No se liberaron los archivos de prueba.");
        }
        finally { await server.Shutdown(); }
    }
}

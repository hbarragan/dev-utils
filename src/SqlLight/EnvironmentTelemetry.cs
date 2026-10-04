using System.Collections.Concurrent;
using System.Data.Common;
using Npgsql;

namespace SqlLight;

// Sample only while the panel asks for it. Never keep monitoring connections pooled.
public sealed class EnvironmentTelemetry(Store store, Engines engines)
{
    readonly ConcurrentDictionary<string, Sample> samples = new();
    readonly SemaphoreSlim gate = new(1, 1);
    public sealed record Session(int Pid, string User, string Application, string State, string Client);
    public sealed class Sample
    {
        public DateTimeOffset At { get; set; }
        public bool Available { get; set; }
        public string? Error { get; set; }
        public string Scope { get; set; } = "Base de datos";
        public int Connections { get; set; }
        public int Active { get; set; }
        public int Idle { get; set; }
        public int Limit { get; set; }
        public long Inserts { get; set; }
        public long Updates { get; set; }
        public long Deletes { get; set; }
        public long Reads { get; set; }
        public double? WritesPerSecond { get; set; }
        public DateTimeOffset? LastWriteObserved { get; set; }
        public Session[] Sessions { get; set; } = [];
    }
    public string DataPath(Profile p) => p.Engine == "sqlserver" ? Path.Combine(store.Root, "data", "sqlserver") : Path.GetFullPath(Path.Combine(store.Folder(p), p.Engine == "tds" ? "database.sqlite" : "cluster"));
    public static long? DiskBytes(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return File.Exists(path) ? new FileInfo(path).Length : 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            return new DirectoryInfo(path).EnumerateFiles("*", options).Sum(f => f.Length);
        }
        catch { return null; }
    }
    public async Task<object[]> Read()
    {
        await gate.WaitAsync();
        try
        {
            var result = new List<object>();
            foreach (var p in store.Snapshot())
            {
                var running = engines.Running(p);
                samples.TryGetValue(p.Id, out var previous);
                var current = previous;
                if (!running) { current = new Sample { At = DateTimeOffset.UtcNow }; samples.TryRemove(p.Id, out _); }
                else if (current == null || DateTimeOffset.UtcNow - current.At >= TimeSpan.FromSeconds(5))
                {
                    current = new Sample { At = DateTimeOffset.UtcNow, LastWriteObserved = previous?.LastWriteObserved };
                    try
                    {
                        if (p.Engine is "postgres" or "babelfish") await Postgres(p, current);
                        else if (p.Engine == "mysql") await Mysql(p, current);
                        else current.Error = "Este motor no expone estas métricas.";
                        if (current.Available && previous?.Available == true)
                        {
                            var delta = current.Inserts + current.Updates + current.Deletes - previous.Inserts - previous.Updates - previous.Deletes;
                            if (delta >= 0) current.WritesPerSecond = Math.Round(delta / (current.At - previous.At).TotalSeconds, 1);
                            if (delta > 0) current.LastWriteObserved = current.At;
                        }
                    }
                    catch (Exception ex) { current.Error = ex.Message.Replace(p.AdminPassword, "[oculto]").Replace(p.Password, "[oculto]"); }
                    samples[p.Id] = current;
                }
                result.Add(new { p.Id, running, dataPath = DataPath(p), profilePath = store.Folder(p), telemetry = current });
            }
            return result.ToArray();
        }
        finally { gate.Release(); }
    }
    async Task Postgres(Profile p, Sample s)
    {
        await using var conn = p.Engine == "babelfish"
            ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = p.PostgresPort, Database = "wilton", Username = "postgres", Password = p.AdminPassword, Pooling = false, Timeout = 2, CommandTimeout = 3, ApplicationName = "SQL Light monitor" }.ConnectionString)
            : (NpgsqlConnection)engines.Connection(p, true, p.Database);
        await conn.OpenAsync();
        s.Scope = p.Engine == "babelfish" ? "Motor Babelfish (incluye sus bases lógicas)" : "Base de datos";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 3;
            cmd.CommandText = "SELECT COALESCE(sum(n_tup_ins),0)::bigint, COALESCE(sum(n_tup_upd),0)::bigint, COALESCE(sum(n_tup_del),0)::bigint, COALESCE(sum(seq_tup_read+idx_tup_fetch),0)::bigint FROM pg_stat_user_tables";
            await using var r = await cmd.ExecuteReaderAsync(); await r.ReadAsync();
            s.Inserts = r.GetInt64(0); s.Updates = r.GetInt64(1); s.Deletes = r.GetInt64(2); s.Reads = r.GetInt64(3);
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 3; cmd.CommandText = "SELECT current_setting('max_connections')::int";
            s.Limit = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        await using (var cmd = conn.CreateCommand())
        {
            // Do not expose query text: it may contain user data or credentials.
            cmd.CommandTimeout = 3;
            cmd.CommandText = "SELECT pid, COALESCE(usename,''), COALESCE(application_name,''), COALESCE(state,'desconocido'), COALESCE(client_addr::text,'local') FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND client_port IS NOT NULL ORDER BY pid";
            await using var r = await cmd.ExecuteReaderAsync(); var sessions = new List<Session>();
            while (await r.ReadAsync()) sessions.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            s.Sessions = sessions.ToArray(); s.Connections = sessions.Count; s.Active = sessions.Count(x => x.State == "active"); s.Idle = sessions.Count - s.Active;
        }
        s.Available = true;
    }
    async Task Mysql(Profile p, Sample s)
    {
        await using var conn = engines.Connection(p); await conn.OpenAsync(); s.Scope = "Motor MySQL (todas sus bases)";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 3;
            cmd.CommandText = "SHOW GLOBAL STATUS WHERE Variable_name IN ('Innodb_rows_inserted','Innodb_rows_updated','Innodb_rows_deleted','Innodb_rows_read')";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) { var value = Convert.ToInt64(r.GetValue(1)); switch (r.GetString(0)) { case "Innodb_rows_inserted": s.Inserts = value; break; case "Innodb_rows_updated": s.Updates = value; break; case "Innodb_rows_deleted": s.Deletes = value; break; case "Innodb_rows_read": s.Reads = value; break; } }
        }
        await using (var cmd = conn.CreateCommand()) { cmd.CommandTimeout = 3; cmd.CommandText = "SELECT @@max_connections"; s.Limit = Convert.ToInt32(await cmd.ExecuteScalarAsync()); }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 3; cmd.CommandText = "SELECT ID, USER, HOST, COMMAND FROM information_schema.PROCESSLIST WHERE ID<>CONNECTION_ID()";
            await using var r = await cmd.ExecuteReaderAsync(); var sessions = new List<Session>();
            while (await r.ReadAsync()) sessions.Add(new(Convert.ToInt32(r.GetValue(0)), r.GetString(1), "Cliente MySQL", r.GetString(3) == "Sleep" ? "idle" : "active", r.GetString(2)));
            s.Sessions = sessions.ToArray(); s.Connections = sessions.Count; s.Active = sessions.Count(x => x.State == "active"); s.Idle = sessions.Count(x => x.State == "idle");
        }
        s.Available = true;
    }
}

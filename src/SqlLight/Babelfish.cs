using System.IO.Compression;
using Npgsql;

namespace SqlLight;

public sealed partial class Engines
{
    async Task InstallBabelfish()
    {
        Activity = "Descargando Babelfish portable para Windows…";
        var zip = Path.Combine(store.Root, "downloads", "wiltondb_3_lts_13.18.1.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        if (!File.Exists(zip))
        {
            using var http = new HttpClient();
            await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync("https://github.com/wiltondb/wiltondb/releases/download/3-lts-13-18-1/wiltondb_3_lts_13.18.1.zip"));
        }
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(zip))) != "BF285C4A6CEF7152D78D7A7E05A865028A06F116E71744900B94A012608E21AA") throw new Exception("El ZIP de WiltonDB no coincide con el hash publicado.");
        var target = Path.Combine(EngineRoot, "wiltondb");
        ZipFile.ExtractToDirectory(zip, target);
        if (!Installed("babelfish")) throw new Exception("Motor Babelfish incompleto.");
        Activity = "Listo";
    }
    NpgsqlConnection BabelfishPostgres(Profile p, string db = "postgres") => new(new NpgsqlConnectionStringBuilder { Host="127.0.0.1", Port=p.PostgresPort, Database=db, Username="postgres", Password=p.AdminPassword, Pooling=false, Timeout=3, CommandTimeout=60 }.ConnectionString);
    async Task StartBabelfish(Profile p)
    {
        if (Running(p)) return;
        if (!PortFree(p.Port)) throw new Exception($"Puerto {p.Port} ocupado.");
        await Install("babelfish");
        var folder=store.Folder(p); Directory.CreateDirectory(folder);
        var cluster=Path.Combine(folder,"cluster");
        if (p.PostgresPort==0) { p.PostgresPort=15432; while (!PortFree(p.PostgresPort) || p.PostgresPort==p.Port || store.Profiles.Any(other=>other.Id!=p.Id && (other.Port==p.PostgresPort || other.PostgresPort==p.PostgresPort))) p.PostgresPort++; store.Save(); }
        if (!PortFree(p.PostgresPort)) throw new Exception($"Puerto interno {p.PostgresPort} ocupado.");
        if (!File.Exists(Path.Combine(cluster,"PG_VERSION")))
        {
            var pw=Path.Combine(folder,"init-password.tmp");
            try { await File.WriteAllTextAsync(pw,p.AdminPassword); await Run(Bin(p.Engine,"initdb"),new[]{"-D",cluster,"-U","postgres","--pwfile="+pw,"--auth=scram-sha-256","--encoding=UTF8","--locale=C"}); }
            finally { if(File.Exists(pw)) File.Delete(pw); }
            await File.AppendAllTextAsync(Path.Combine(cluster,"postgresql.conf"),$"\nlisten_addresses='127.0.0.1'\nport={p.PostgresPort}\nshared_buffers=32MB\nwork_mem=2MB\nmaintenance_work_mem=16MB\nmax_connections=40\nmax_parallel_workers=0\nshared_preload_libraries='babelfishpg_tds'\nbabelfishpg_tds.port={p.Port}\nbabelfishpg_tds.listen_addresses='127.0.0.1'\nbabelfishpg_tsql.database_name='wilton'\nlogging_collector=on\nlog_directory='../logs'\nlog_statement='none'\nlog_min_error_statement=panic\n");
        }
        Directory.CreateDirectory(Path.Combine(folder,"logs"));
        using var proc=Launch(p,"postgres",new[]{"-D",cluster});
        if (!File.Exists(Path.Combine(folder,"babelfish.initialized")))
        {
            for (int i=0;;i++) { try { await using var ready=BabelfishPostgres(p); await ready.OpenAsync(); break; } catch when(i<60) { await Task.Delay(500); } }
            await using (var pg=BabelfishPostgres(p))
            {
                await pg.OpenAsync();
                await Exec(pg,$"CREATE USER wilton WITH SUPERUSER CREATEDB CREATEROLE PASSWORD {PgLiteral(p.AdminPassword)} INHERIT");
                await Exec(pg,"CREATE DATABASE wilton OWNER wilton");
            }
            await using (var pg=BabelfishPostgres(p,"wilton"))
            {
                await pg.OpenAsync();
                await Exec(pg,"CREATE EXTENSION babelfishpg_tds CASCADE");
                await Exec(pg,"GRANT ALL ON SCHEMA sys TO wilton");
                await Exec(pg,"ALTER DATABASE wilton SET babelfishpg_tsql.migration_mode='multi-db'");
                await Exec(pg,"CALL sys.initialize_babelfish('wilton')");
            }
            await File.WriteAllTextAsync(Path.Combine(folder,"babelfish.initialized"),"1");
            await File.WriteAllTextAsync(Path.Combine(cluster,"pg_hba.conf"),"host wilton all 127.0.0.1/32 password\nhost all all 127.0.0.1/32 scram-sha-256\n");
            await using var reload=BabelfishPostgres(p); await reload.OpenAsync(); await Exec(reload,"SELECT pg_reload_conf()");
        }
        await WaitReady(p);
        if (!p.Provisioned)
        {
            await using var admin=Connection(p); await admin.OpenAsync();
            await Exec(admin,$"IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name={PgLiteral(p.Username)}) CREATE LOGIN [{p.Username}] WITH PASSWORD={PgLiteral(p.Password)}");
            await Exec(admin,$"IF DB_ID({PgLiteral(p.Database)}) IS NULL CREATE DATABASE [{p.Database}]");
            await Exec(admin,$"ALTER AUTHORIZATION ON DATABASE::[{p.Database}] TO [{p.Username}]");
            p.Provisioned=true;
        }
        await using var conn=Connection(p,false); await conn.OpenAsync(); await Exec(conn,"SELECT 1");
        p.Error=null; store.Save();
    }
}

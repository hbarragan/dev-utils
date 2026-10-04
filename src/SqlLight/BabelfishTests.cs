using System.Text.Json;

namespace SqlLight;

internal static class BabelfishTests
{
    public static async Task<int> Run(string root)
    {
        var fixture=Path.Combine(root,"artifacts","babelfish-test-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var store=new Store(fixture);var engines=new Engines(store,Path.Combine(root,"engines"));
        var profile=new Profile{Engine="babelfish",Database="portable_test",Port=engines.SuggestPort("babelfish")};
        store.Profiles.Add(profile);store.Save();var checks=new List<string>();
        try
        {
            await engines.Start(profile);
            await using(var conn=engines.Connection(profile,false))
            {
                await conn.OpenAsync();await using var command=conn.CreateCommand();
                command.CommandText="CREATE TABLE dbo.portable_check (id int PRIMARY KEY, stamp datetimeoffset, text nvarchar(200)); INSERT dbo.portable_check VALUES (1, '2026-10-03T23:00:00+02:00', N'persistente ñ');";
                await command.ExecuteNonQueryAsync();
                command.CommandText="SELECT compatibility_level FROM sys.databases WHERE name=db_name()";
                if(Convert.ToInt32(await command.ExecuteScalarAsync())!=120)throw new Exception("Catalog metadata mismatch");
            }
            checks.Add("Fresh cluster and SQL login, DDL, Unicode, datetimeoffset and Hibernate catalog query");
            using(var proc=engines.OwnedProcess(profile)) if(proc==null || ProcessMemory.Tree(proc)<=0)throw new Exception("Missing process tree memory");
            await engines.Stop(profile);
            if(!Engines.PortFree(profile.Port) || !Engines.PortFree(profile.PostgresPort))throw new Exception("Ports not released");
            var restored=new Store(fixture).Profiles.Single();
            await engines.Start(restored);
            await using(var conn=engines.Connection(restored,false))
            {
                await conn.OpenAsync();await using var cmd=conn.CreateCommand();cmd.CommandText="SELECT text FROM dbo.portable_check WHERE id=1";
                if((string?)await cmd.ExecuteScalarAsync()!="persistente ñ")throw new Exception("Persistence mismatch");
            }
            await engines.Stop(restored);checks.Add("Saved profile, graceful stop, both ports released and persisted data after restart");
            File.WriteAllText(Path.Combine(root,"artifacts","babelfish-test.json"),JsonSerializer.Serialize(new{passed=true,fixture,checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception ex)
        {
            try{await engines.Stop(profile);}catch{}
            File.WriteAllText(Path.Combine(root,"artifacts","babelfish-test.json"),JsonSerializer.Serialize(new{passed=false,fixture,checks,error=ex.Message.Replace(profile.Password,"[hidden]").Replace(profile.AdminPassword,"[hidden]")},new JsonSerializerOptions{WriteIndented=true}));return 1;
        }
    }
}

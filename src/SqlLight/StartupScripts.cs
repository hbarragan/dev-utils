using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlLight;

public sealed class StartupScript
{
    public string Name { get; set; } = "";
    public string Sql { get; set; } = "";
    public bool Always { get; set; }
}
public sealed class ScriptReceipt
{
    public string Name { get; set; } = "";
    public string Checksum { get; set; } = "";
    public DateTimeOffset AppliedAt { get; set; }
    public int Runs { get; set; }
    public string? Error { get; set; }
}

public static class StartupScripts
{
    public static string Hash(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));
    // Sort numeric components numerically (2.9 precedes 2.10), without integer overflow.
    public static string OrderKey(string name) => Regex.Replace(name.ToLowerInvariant(), @"\d+", m => {
        var n = m.Value.TrimStart('0'); if (n.Length == 0) n = "0";
        return n.Length.ToString("D4") + ":" + n;
    });
    public static StartupScript[] Ordered(IEnumerable<StartupScript> scripts) => scripts.OrderBy(s => OrderKey(s.Name), StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
    public static void Validate(StartupScript script)
    {
        script.Name = script.Name.Replace('\\', '/');
        if (script.Name.Length is < 1 or > 240 || !script.Name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
            script.Name.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c))))
            throw new Exception("Usa un nombre relativo .sql, por ejemplo 2.0.0/01-schema.sql.");
        if (string.IsNullOrWhiteSpace(script.Sql) || Encoding.UTF8.GetByteCount(script.Sql) > 1048576)
            throw new Exception("Cada script debe contener SQL y ocupar como máximo 1 MB.");
    }
    public static void Save(Profile p, StartupScript[] scripts)
    {
        if (scripts.Length > 100 || scripts.Sum(s => Encoding.UTF8.GetByteCount(s.Sql)) > 4194304)
            throw new Exception("Máximo 100 scripts y 4 MB de SQL por entorno.");
        foreach (var s in scripts)
        {
            Validate(s);
            Batches(s.Sql, p.Engine is "sqlserver" or "babelfish" or "tds");
        }
        if (scripts.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scripts.Length)
            throw new Exception("Hay nombres de scripts repetidos.");
        foreach (var s in scripts)
        {
            var applied = p.ScriptHistory.Find(h => h.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase) && h.Runs > 0);
            if (applied != null && !s.Always && applied.Checksum != Hash(s.Sql))
                throw new Exception($"{s.Name} ya se aplicó. Añade una nueva versión para modificar la base.");
        }
        p.Scripts = Ordered(scripts).ToList();
    }
    // Placeholders are complete SQL tokens. Never place them inside quotes.
    public static string Bind(Profile p, string sql)
    {
        string Id(string s) => p.Engine switch { "postgres" => "\"" + s.Replace("\"", "\"\"") + "\"", "mysql" => "`" + s.Replace("`", "``") + "`", _ => "[" + s.Replace("]", "]]") + "]" };
        var password = p.Password.Replace("'", "''");
        if (p.Engine == "mysql") password = password.Replace("\\", "\\\\");
        return sql.Replace("{{DB_NAME}}", Id(p.Database)).Replace("{{DB_USER}}", Id(p.Username))
            .Replace("{{DB_PASSWORD}}", (p.Engine is "sqlserver" or "babelfish" or "tds" ? "N" : "") + "'" + password + "'");
    }
    public static string DefaultSql(Profile p) => p.Engine switch
    {
        "postgres" => $"-- El lanzador comprueba si el rol y la base existen antes de crearlos.\n-- Primero: CREATE ROLE \"{p.Username}\" LOGIN; (si falta)\nALTER ROLE \"{p.Username}\" WITH LOGIN PASSWORD {{{{DB_PASSWORD}}}};\n-- Después: CREATE DATABASE \"{p.Database}\" OWNER \"{p.Username}\"; (si falta)",
        "mysql" => $"CREATE DATABASE IF NOT EXISTS `{p.Database}` CHARACTER SET utf8mb4;\nCREATE USER IF NOT EXISTS '{p.Username}'@'localhost' IDENTIFIED BY {{{{DB_PASSWORD}}}};\nALTER USER '{p.Username}'@'localhost' IDENTIFIED BY {{{{DB_PASSWORD}}}};\nGRANT ALL PRIVILEGES ON `{p.Database}`.* TO '{p.Username}'@'localhost';",
        "tds" => "-- El emulador configura la base {{DB_NAME}} y el login {{DB_USER}}\n-- con la contraseña {{DB_PASSWORD}} al iniciar.",
        "babelfish" => $"USE [master];\nGO\nIF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{p.Username}')\n    CREATE LOGIN [{p.Username}] WITH PASSWORD = {{{{DB_PASSWORD}}}};\nGO\nIF DB_ID(N'{p.Database}') IS NULL\n    CREATE DATABASE [{p.Database}];\nGO\nALTER AUTHORIZATION ON DATABASE::[{p.Database}] TO [{p.Username}];",
        _ => $"-- El lanzador rechaza bases existentes que no haya creado este entorno.\nUSE [master];\nGO\nIF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{p.Username}')\n    CREATE LOGIN [{p.Username}] WITH PASSWORD = {{{{DB_PASSWORD}}}}, CHECK_POLICY = OFF;\nGO\nIF DB_ID(N'{p.Database}') IS NULL\n    CREATE DATABASE [{p.Database}];\nGO\nUSE [{p.Database}];\nIF USER_ID(N'{p.Username}') IS NULL\n    CREATE USER [{p.Username}] FOR LOGIN [{p.Username}];\nALTER ROLE db_owner ADD MEMBER [{p.Username}];"
    };
    public static async Task Apply(Profile p, Func<StartupScript, Task> execute, Action persist)
    {
        foreach (var script in Ordered(p.Scripts))
        {
            Validate(script);
            var hash = Hash(script.Sql);
            var receipt = p.ScriptHistory.Find(h => h.Name.Equals(script.Name, StringComparison.OrdinalIgnoreCase));
            if (receipt?.Runs > 0 && !script.Always)
            {
                if (receipt.Checksum != hash) throw new Exception($"{script.Name}: versión aplicada modificada. Añade una migración nueva.");
                continue;
            }
            receipt ??= new ScriptReceipt { Name = script.Name };
            if (!p.ScriptHistory.Contains(receipt)) p.ScriptHistory.Add(receipt);
            try
            {
                await execute(script);
                receipt.Checksum = hash; receipt.AppliedAt = DateTimeOffset.UtcNow; receipt.Runs++; receipt.Error = null; persist();
            }
            catch (Exception ex)
            {
                // Provider messages may echo SQL or secrets. Keep only a safe migration identity.
                var code = ex switch
                {
                    Microsoft.Data.SqlClient.SqlException sql => $" SQL {sql.Number}, línea {sql.LineNumber} del bloque.",
                    Npgsql.PostgresException pg => $" SQLSTATE {pg.SqlState}, posición {pg.Position}.",
                    MySqlConnector.MySqlException my => $" MySQL {my.Number}.",
                    _ => ""
                };
                receipt.Error = "Ejecución fallida." + code + " Puede haber cambios parciales; revisa el script antes de reintentar.";
                persist(); throw new Exception($"Falló el script {script.Name}." + code + " Se detuvo la secuencia. Puede haber cambios parciales; revisa el SQL antes de reintentar.");
            }
        }
    }
    public static string[] Batches(string sql, bool tsql)
    {
        var batches = new List<string>(); var current = new StringBuilder();
        char quote = '\0'; int comments = 0;
        foreach (var line in sql.Replace("\r\n", "\n").Split('\n'))
        {
            if (quote == '\0' && comments == 0)
            {
                if (Regex.IsMatch(line, @"^\s*(?:GO)(?:\s+\d+)?\s*(?:--.*)?$", RegexOptions.IgnoreCase))
                {
                    if (!tsql) throw new Exception("GO solo se admite para motores T-SQL.");
                    if (!Regex.IsMatch(line, @"^\s*GO\s*(?:--.*)?$", RegexOptions.IgnoreCase)) throw new Exception("Usa GO sin contador de repeticiones.");
                    if (!string.IsNullOrWhiteSpace(current.ToString())) batches.Add(current.ToString()); current.Clear(); continue;
                }
                if (Regex.IsMatch(line, @"^\s*(?:[:!]|DELIMITER\b)", RegexOptions.IgnoreCase)) throw new Exception("Importa SQL puro; las directivas de sqlcmd y DELIMITER no están soportadas.");
            }
            current.Append(line).Append('\n');
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i], next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (comments > 0) { if (c == '/' && next == '*') { comments++; i++; } else if (c == '*' && next == '/') { comments--; i++; } continue; }
                if (quote != '\0') { if (c == quote) { if (next == quote) i++; else quote = '\0'; } continue; }
                if (c == '-' && next == '-') break;
                if (c == '/' && next == '*') { comments++; i++; }
                else if (c is '\'' or '"' or '[') quote = c == '[' ? ']' : c;
            }
        }
        if (!string.IsNullOrWhiteSpace(current.ToString())) batches.Add(current.ToString());
        return tsql ? batches.ToArray() : new[] { sql };
    }
}

public sealed partial class Engines
{
    async Task RunStartupScripts(Profile p)
    {
        try
        {
        await StartupScripts.Apply(p, async script =>
        {
            Activity = "Aplicando " + script.Name;
            var batches = StartupScripts.Batches(StartupScripts.Bind(p, script.Sql), p.Engine is "sqlserver" or "babelfish" or "tds");
            await using var connection = Connection(p, true, p.Database);
            await connection.OpenAsync();
            foreach (var batch in batches)
            {
                await using var cmd = connection.CreateCommand(); cmd.CommandText = batch; cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }
        }, store.Save);
        }
        finally { Activity = "Listo"; }
    }
}

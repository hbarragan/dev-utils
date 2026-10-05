using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SqlLight;

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Engine { get; set; } = "postgres";
    public string Database { get; set; } = "app_dev";
    public string Username { get; set; } = "user";
    public string Password { get; set; } = "password";
    public int Port { get; set; }
    public int PostgresPort { get; set; }
    public string AdminPassword { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public bool Provisioned { get; set; }
    public bool AutoStart { get; set; }
    public string? Error { get; set; }
    public int? Pid { get; set; }
    public long ProcessStart { get; set; }
    public bool Busy { get; set; }
    public List<StartupScript> Scripts { get; set; } = new();
    public List<ScriptReceipt> ScriptHistory { get; set; } = new();
    public void Validate()
    {
        if (Engine is not ("postgres" or "mysql" or "sqlserver" or "tds" or "babelfish")) throw new Exception("Motor desconocido.");
        if (!Regex.IsMatch(Database, "^[A-Za-z][A-Za-z0-9_]{0,62}$")) throw new Exception("Base de datos: empieza por una letra y usa letras, números o _ (máximo 63).");
        if (!Regex.IsMatch(Username, "^[A-Za-z][A-Za-z0-9_]{0,31}$") || new[] { "postgres", "root", "sa", "sql_light_admin" }.Contains(Username.ToLowerInvariant())) throw new Exception("Usuario inválido o reservado (máximo 32 caracteres).");
        if (Password.Length is < 1 or > 128 || Password.Any(char.IsControl)) throw new Exception("Contraseña: entre 1 y 128 caracteres, sin saltos de línea.");
        if (Engine == "babelfish" && Username.Equals("wilton",StringComparison.OrdinalIgnoreCase)) throw new Exception("wilton es el usuario de administración reservado de Babelfish.");
        if (Port is < 1024 or > 65535) throw new Exception("El puerto debe estar entre 1024 y 65535.");
    }
    public string Jdbc => Engine switch
    {
        "postgres" => $"jdbc:postgresql://localhost:{Port}/{Database}",
        "mysql" => $"jdbc:mysql://localhost:{Port}/{Database}?useSSL=false&allowPublicKeyRetrieval=true&serverTimezone=UTC",
        _ => $"jdbc:sqlserver://localhost:{Port};databaseName={Database};encrypt=false;trustServerCertificate=true"
    };
    public string Driver => Engine switch { "postgres" => "org.postgresql.Driver", "mysql" => "com.mysql.cj.jdbc.Driver", _ => "com.microsoft.sqlserver.jdbc.SQLServerDriver" };
    public string Yaml => $"spring:\n  datasource:\n    url: {JsonSerializer.Serialize(Jdbc)}\n    driver-class-name: {Driver}\n    username: {JsonSerializer.Serialize(Username)}\n    password: {JsonSerializer.Serialize(Password)}\n";
}

public sealed class Store
{
    static readonly byte[] Magic = "SQLLIGHT2\n"u8.ToArray();
    public string StateFile => Path.Combine(Root, "private", "state.dat");
    public string Root { get; }
    public List<Profile> Profiles { get; private set; } = new();
    public Store(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        var privateDirectory = Path.Combine(Root, "private");
        if (!Directory.Exists(privateDirectory))
        {
            Directory.CreateDirectory(privateDirectory);
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Distinct())
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(privateDirectory).SetAccessControl(acl);
            foreach (var folder in new[] { "data", "trash" })
            {
                var path = Path.Combine(Root, folder); Directory.CreateDirectory(path);
                new DirectoryInfo(path).SetAccessControl(acl);
            }
        }
        var file = File.Exists(StateFile) ? StateFile : Path.Combine(Root, "state.dat");
        if (File.Exists(file))
        {
            var bytes = Decode(File.ReadAllBytes(file));
            Profiles = JsonSerializer.Deserialize<List<Profile>>(bytes) ?? new();
            foreach (var p in Profiles) { p.Busy = false; p.Validate(); if (!Regex.IsMatch(p.Id, "^[a-f0-9]{32}$")) throw new Exception("Identificador inválido en state.dat."); }
        }
    }
    public string Folder(Profile p) => Path.Combine(Root, "data", p.Id);
    public Profile[] Snapshot() { lock (Profiles) return Profiles.ToArray(); }
    static byte[] Encode(byte[] bytes) => Magic.Concat(ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)).ToArray();
    static byte[] Decode(byte[] bytes) => bytes.AsSpan().StartsWith(Magic) ? ProtectedData.Unprotect(bytes[Magic.Length..], null, DataProtectionScope.LocalMachine) : ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    public static void Archive(string file, Profile p) => File.WriteAllBytes(file, Encode(JsonSerializer.SerializeToUtf8Bytes(p)));
    public static Profile ReadArchive(string file) => JsonSerializer.Deserialize<Profile>(Decode(File.ReadAllBytes(file))) ?? throw new Exception("Archivo de recuperación inválido.");
    public void MigrateArchives()
    {
        foreach (var archive in Archives().ToArray()) Archive(Path.Combine(Root, "trash", archive.Key, "profile.dat"), archive.Profile);
    }
    public IEnumerable<(string Key, Profile Profile)> Archives()
    {
        var trash = Path.Combine(Root, "trash");
        if (!Directory.Exists(trash)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(trash))
        {
            if (!Regex.IsMatch(Path.GetFileName(dir), "^[a-f0-9]{32}-[0-9]{14}$")) continue;
            var file = Path.Combine(dir, "profile.dat");
            if (!File.Exists(file)) continue;
            Profile? p = null; try { p = ReadArchive(file); } catch { }
            if (p?.Engine is "postgres" or "mysql" or "tds" or "babelfish") yield return (Path.GetFileName(dir), p);
        }
    }
    public void Save()
    {
        var bytes = Encode(JsonSerializer.SerializeToUtf8Bytes(Snapshot()));
        File.WriteAllBytes(StateFile + ".tmp", bytes);
        File.Move(StateFile + ".tmp", StateFile, true);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SqlLight;

namespace AppUtilDev;

internal static class StartupScriptTests
{
 internal static async Task RunAsync(Action<string,bool> check, string root)
 {
  var p=new Profile {Engine="babelfish",Database="scripts_test",Username="devuser",Password="a'b\\c",Port=55445};
  var calls=new List<string>();
  StartupScript Script(string name,bool always=false)=>new(){Name=name,Sql="SELECT 1;",Always=always};
  StartupScripts.Save(p,new[]{Script("2.10/01.sql"),Script("2.9/02.sql"),Script("2.9/01.sql"),Script("9-refresh.sql",true)});
  async Task Execute(StartupScript s){calls.Add(s.Name);await Task.CompletedTask;}
  await StartupScripts.Apply(p,Execute,()=>{});
  check("Scripts ordenados por versión numérica y nombre",calls.SequenceEqual(new[]{"2.9/01.sql","2.9/02.sql","2.10/01.sql","9-refresh.sql"}));
  calls.Clear();await StartupScripts.Apply(p,Execute,()=>{});
  check("Migraciones una vez y script repetible en cada arranque",calls.SequenceEqual(new[]{"9-refresh.sql"}) && p.ScriptHistory.Single(h=>h.Name=="9-refresh.sql").Runs==2);
  bool rejects=false;try{StartupScripts.Save(p,new[]{new StartupScript{Name="2.9/01.sql",Sql="SELECT 2;"}});}catch{rejects=true;}
  check("Checksum rechaza modificar una migración aplicada",rejects);
  StartupScripts.Save(p,Array.Empty<StartupScript>());StartupScripts.Save(p,new[]{Script("2.9/01.sql")});calls.Clear();await StartupScripts.Apply(p,Execute,()=>{});
  check("Quitar y reimportar conserva historial",calls.Count==0);
  rejects=false;try{StartupScripts.Save(p,new[]{Script("../secret.sql")});}catch{rejects=true;}check("Importación rechaza rutas fuera del entorno",rejects);
  rejects=false;try{StartupScripts.Save(p,new[]{Script("A.sql"),Script("a.sql")});}catch{rejects=true;}check("Nombres duplicados rechazados",rejects);
  var bound=StartupScripts.Bind(p,"USE {{DB_NAME}}; CREATE LOGIN {{DB_USER}} WITH PASSWORD={{DB_PASSWORD}};");
  check("Variables escapadas con configuración del entorno",bound=="USE [scripts_test]; CREATE LOGIN [devuser] WITH PASSWORD=N'a''b\\c';" && !StartupScripts.DefaultSql(p).Contains(p.Password));
  var batches=StartupScripts.Batches("SELECT 'first\nGO\nlast';\nGO -- batch\n/* comment\nGO\n*/\nSELECT [a\nGO\nb];\nGO\nSELECT 3;",true);
  check("GO respeta literales, comentarios e identificadores",batches.Length==3 && batches[0].Contains("first\nGO") && batches[1].Contains("/* comment\nGO"));
  rejects=false;try{StartupScripts.Batches("SELECT 1;\nGO 3",true);}catch{rejects=true;}check("Repeticiones GO no se ejecutan accidentalmente",rejects);
  rejects=false;try{StartupScripts.Batches(":r other.sql",true);}catch{rejects=true;}check("No ejecuta directivas externas sqlcmd",rejects);
  var failed=new Profile();StartupScripts.Save(failed,new[]{Script("01.sql"),Script("02.sql"),Script("03.sql")});calls.Clear();
  try{await StartupScripts.Apply(failed,s=>{calls.Add(s.Name);if(s.Name=="02.sql")throw new Exception("private business SQL");return Task.CompletedTask;},()=>{});}catch{}
  check("Fallo interrumpe secuencia sin filtrar SQL privado",calls.SequenceEqual(new[]{"01.sql","02.sql"}) && failed.ScriptHistory.Last().Runs==0 && !failed.ScriptHistory.Last().Error!.Contains("private"));
  calls.Clear();await StartupScripts.Apply(failed,Execute,()=>{});check("Reintento omite anteriores y aplica pendientes",calls.SequenceEqual(new[]{"02.sql","03.sql"}));
  var store=new Store(Path.Combine(root,"script-storage"));p.Scripts.Single().Sql="SELECT 'private-script-marker';";store.Profiles.Add(p);store.Save();
  var saved=new Store(store.Root).Profiles.Single();check("SQL e historial cifrados y persistentes",saved.Scripts.Single().Sql==p.Scripts.Single().Sql && saved.ScriptHistory.Count==4 && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.StateFile)).Contains("private-script-marker"));
 }

 internal static async Task<int> RunEngineAsync(string root)
 {
  var checks=new List<object>();var ok=true;
  void Check(string name,bool passed){checks.Add(new{name,passed});ok &= passed;}
  var store=new Store(Path.Combine(root,"artifacts","startup-engine-"+Guid.NewGuid().ToString("N")));
  var engines=new Engines(store,Path.Combine(root,"engines"));
  var p=new Profile{Engine="babelfish",Database="startup_test",Username="migrationuser",Password="local-test-only",Port=engines.SuggestPort("babelfish")};store.Profiles.Add(p);
  StartupScripts.Save(p,new[]{
   new StartupScript{Name="2.0/01-schema.sql",Sql="CREATE TABLE dbo.startup_check(id int PRIMARY KEY, runs int);\nGO\nINSERT dbo.startup_check VALUES (1,0);"},
   new StartupScript{Name="2.0/02-repeat.sql",Sql="UPDATE dbo.startup_check SET runs=runs+1 WHERE id=1;",Always=true}});store.Save();
  async Task<int> Count(){await using var c=engines.Connection(p,false);await c.OpenAsync();await using var cmd=c.CreateCommand();cmd.CommandText="SELECT runs FROM dbo.startup_check WHERE id=1";return Convert.ToInt32(await cmd.ExecuteScalarAsync());}
  try
  {
   await engines.Start(p);Check("Motor real: bootstrap, GO, DDL y script repetible",await Count()==1 && p.ScriptHistory.Count==2);
   await engines.Stop(p);p=new Store(store.Root).Profiles.Single();await engines.Start(p);
   Check("Motor real: reinicio conserva schema y repite solo el script marcado",await Count()==2 && p.ScriptHistory[0].Runs==1 && p.ScriptHistory[1].Runs==2);
   await engines.Stop(p);
  }
  catch(Exception ex){ok=false;checks.Add(new{name="Motor real",passed=false,error=ex.Message.Replace(p.Password,"[oculto]").Replace(p.AdminPassword,"[oculto]")});}
  finally{try{await engines.Stop(p);}catch{}File.WriteAllText(Path.Combine(root,"artifacts","startup-engine-test.json"),System.Text.Json.JsonSerializer.Serialize(new{passed=ok,checks},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));}
  return ok?0:1;
 }
}

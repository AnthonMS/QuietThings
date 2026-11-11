using QuietThings.WebBridge;
using System.IO.Compression;
using System.Text.Json;
class Tests {
 static int checks;
 static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
 static void Reject(Action action,string label){try{action();}catch(InvalidDataException){checks++;return;}catch(InvalidOperationException){checks++;return;}throw new Exception("Expected rejection: "+label);}
 static void Package(string file,params(string Name,string Text)[] files){using var zip=ZipFile.Open(file,ZipArchiveMode.Create);foreach(var entry in files){using var w=new StreamWriter(zip.CreateEntry(entry.Name).Open());w.Write(entry.Text);}}
 static async Task Main(){
  string work=Path.Combine(Path.GetTempPath(),"quiet-root-recovery-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
  try{
   string installed=Path.Combine(work,"installed");Directory.CreateDirectory(installed);File.WriteAllText(Path.Combine(installed,"main.js"),"known-good");
   string package=Path.Combine(work,"valid.wgt");Package(package,("main.js","known-good"),("author-signature.xml","metadata"));ManagedUpdateRecovery.VerifyFiles(package,installed);Check(true,"actual content matches and relocated signature allowed");
   File.WriteAllText(Path.Combine(installed,"main.js"),"wrong-new-code");Reject(()=>ManagedUpdateRecovery.VerifyFiles(package,installed),"different installed content");File.Delete(Path.Combine(installed,"main.js"));Reject(()=>ManagedUpdateRecovery.VerifyFiles(package,installed),"missing installed content");
   string traversal=Path.Combine(work,"traversal.wgt");Package(traversal,("../outside","bad"));Reject(()=>ManagedUpdateRecovery.VerifyFiles(traversal,installed),"path traversal");
   string absolute=Path.Combine(work,"absolute.wgt");Package(absolute,("/outside","bad"));Reject(()=>ManagedUpdateRecovery.VerifyFiles(absolute,installed),"absolute path");
   string duplicate=Path.Combine(work,"duplicate.wgt");Package(duplicate,("main.js","a"),("MAIN.JS","b"));Reject(()=>ManagedUpdateRecovery.VerifyFiles(duplicate,installed),"duplicate normalized identity");
   string root=Path.Combine(work,"must-not-create");var manager=new ManagedUpdateRecovery(root,work,work,work,new Dictionary<string,string[]>{["MoonfinApp"]=new[]{"MoonfinApp.moonfin"}});
   foreach(string request in new[]{"{\"package\":\"system\",\"transaction\":\"11111111111111111111111111111111\",\"operation\":\"begin\"}","{\"package\":\"MoonfinApp\",\"transaction\":\"../outside\",\"operation\":\"begin\"}"}){
    using var doc=JsonDocument.Parse(request);try{await manager.Handle(doc.RootElement,CancellationToken.None);throw new Exception("Expected invalid request");}catch(InvalidDataException){Check(!Directory.Exists(root),"invalid target/transaction rejected before filesystem changes");}
   }
   Console.WriteLine(checks+" recovery package-integrity and request-boundary checks passed.");
  }finally{Directory.Delete(work,true);}
 }
}

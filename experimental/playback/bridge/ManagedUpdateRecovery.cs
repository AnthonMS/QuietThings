// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.InteropServices;

namespace QuietThings.WebBridge;

// Fixed, installation-owned targets. Requests never contain filesystem paths or commands.
public sealed class ManagedUpdateRecovery
{
    readonly string root, home, apps, writable;
    readonly IReadOnlyDictionary<string,string[]> targets;
    readonly SemaphoreSlim gate = new(1,1);
    public sealed class State {
        public string Package {get;set;} public string Transaction {get;set;} public string BeforeVersion {get;set;}
        public string TargetVersion {get;set;} public string BeforeHash {get;set;} public string TargetHash {get;set;}
        public bool Ready {get;set;} public bool Restored {get;set;} public bool Restoring {get;set;}
    }
    public ManagedUpdateRecovery(string directory, string launcherData, string applications, string appData,
        IReadOnlyDictionary<string,string[]> allowedTargets) {
        root=Path.GetFullPath(directory);home=Path.GetFullPath(launcherData);apps=Path.GetFullPath(applications);writable=Path.GetFullPath(appData);targets=allowedTargets;
    }
    static void SafeDirectory(string path) {
        if(!Directory.Exists(path)||new DirectoryInfo(path).LinkTarget!=null)throw new InvalidOperationException("Recovery directory is missing or linked.");
    }
    static void SafeTree(string path,ISet<string> allowedLinks=null) {
        SafeDirectory(path);
        var pending=new Stack<string>();pending.Push(path);int count=0;
        while(pending.Count>0)foreach(var name in Directory.EnumerateFileSystemEntries(pending.Pop())) {
            var attributes=File.GetAttributes(name);
            if(++count>100000)throw new InvalidOperationException("Excessive app data needs maintenance before updating.");
            if((attributes&FileAttributes.ReparsePoint)!=0) {
                // Tizen creates bin/res/shared-res links to this app's read-only package.
                // cp -a preserves them as links; never follow them while inspecting data.
                if(allowedLinks==null||!allowedLinks.Contains(new FileInfo(name).LinkTarget))throw new InvalidOperationException("Unexpected linked app data needs maintenance before updating.");
                continue;
            }
            if((attributes&FileAttributes.Directory)!=0)pending.Push(name);
        }
    }
    static byte[] Digest(Stream stream) {using var sha=SHA256.Create();return sha.ComputeHash(stream);}
    static string Hash(string file) {using var s=File.OpenRead(file);return Convert.ToHexString(Digest(s)).ToLowerInvariant();}
    static string VersionOf(string file, string package) {
        using var zip=ZipFile.OpenRead(file);var entry=zip.GetEntry("config.xml")??throw new InvalidDataException();
        using var stream=entry.Open();using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});
        var xml=XDocument.Load(reader);var app=xml.Root.Elements().Single(x=>x.Name.LocalName=="application");
        if((string)app.Attribute("package")!=package)throw new InvalidDataException("Recovery package identity differs.");
        string version=(string)xml.Root.Attribute("version");if(!Version.TryParse(version,out _))throw new InvalidDataException();return version;
    }
    public static void VerifyFiles(string packageFile,string installed) {
        SafeDirectory(installed);using var zip=ZipFile.OpenRead(packageFile);long total=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(zip.Entries.Count>15000)throw new InvalidDataException();
        foreach(var entry in zip.Entries) {
            string name=entry.FullName;
            if(name.Contains('\\')||name.Contains(':')||name.StartsWith('/')||name.Split('/').Any(x=>x is "." or "..")||!names.Add(name)||(total+=entry.Length)>160L*1024*1024)throw new InvalidDataException("Unsafe recovery package.");
            if(name.EndsWith('/'))continue;
            // Samsung moves signatures into package-manager metadata on some builds.
            if(name is "signature1.xml" or "author-signature.xml")continue;
            string actual=Path.Combine(installed,name);if(!File.Exists(actual)||new FileInfo(actual).LinkTarget!=null)throw new InvalidOperationException("Installed package contents differ.");
            using var a=entry.Open();using var b=File.OpenRead(actual);
            if(!CryptographicOperations.FixedTimeEquals(Digest(a),Digest(b)))throw new InvalidOperationException("Installed package contents differ.");
        }
    }
    static async Task Run(string executable,IEnumerable<string> args,CancellationToken ct) {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start);var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync(ct);}catch{try{process.Kill(true);}catch{}throw;}
        await output;await error;if(process.ExitCode!=0)throw new IOException("Recovery file operation failed.");
    }
    static string StartTime(int pid){string value=File.ReadAllText("/proc/"+pid+"/stat");return value.Substring(value.LastIndexOf(')')+2).Split(' ',StringSplitOptions.RemoveEmptyEntries)[19];}
    static (int Pid,string Start)? ProxyWorker() {
        var ports=new Dictionary<string,string>();
        foreach(string line in File.ReadLines("/proc/net/tcp").Skip(1)){
            var fields=line.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);if(fields.Length<10||fields[3]!="0A")continue;
            string port=fields[1].Split(':').Last();if(port is "1FA4" or "1FA5")ports[port]=fields[9];
        }
        if(ports.Count!=2)return null;var matches=new List<(int Pid,string Start)>();
        foreach(string directory in Directory.EnumerateDirectories("/proc")){
            if(!int.TryParse(Path.GetFileName(directory),out int pid))continue;
            try{
                if(new FileInfo(directory+"/exe").LinkTarget!="/usr/bin/wrt-service")continue;
                var sockets=Directory.EnumerateFiles(directory+"/fd").Select(x=>new FileInfo(x).LinkTarget).ToHashSet();
                if(ports.Values.All(inode=>sockets.Contains("socket:["+inode+"]")))matches.Add((pid,StartTime(pid)));
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        if(matches.Count!=1)throw new InvalidOperationException("Playback worker identity is ambiguous or unavailable.");return matches[0];
    }
    [DllImport("libc.so.6")]static extern int kill(int pid,int signal);
    async Task Stop(string package,CancellationToken ct) {
        var proxy=package=="xvvl3S1TT1"?ProxyWorker():null;
        foreach(string app in targets[package])try{await Run("/usr/bin/app_launcher",new[]{"-u","5001","-k",app},ct);}catch(IOException){}
        if(proxy.HasValue){
            string path="/proc/"+proxy.Value.Pid;
            if(Directory.Exists(path)&&new FileInfo(path+"/exe").LinkTarget=="/usr/bin/wrt-service"&&StartTime(proxy.Value.Pid)==proxy.Value.Start&&kill(proxy.Value.Pid,15)!=0)throw new IOException("Playback worker could not be stopped.");
            for(int n=0;n<20&&Directory.Exists(path);n++)await Task.Delay(100,ct);
            if(Directory.Exists(path)&&new FileInfo(path+"/exe").LinkTarget=="/usr/bin/wrt-service"&&StartTime(proxy.Value.Pid)==proxy.Value.Start)throw new IOException("Playback worker did not stop in time.");
        }
        await Task.Delay(300,ct);
    }
    void Save(string dir,State state) {
        File.WriteAllText(Path.Combine(dir,"state.json.tmp"),JsonSerializer.Serialize(state));File.Move(Path.Combine(dir,"state.json.tmp"),Path.Combine(dir,"state.json"),true);
    }
    [DllImport("libc.so.6")]static extern int chmod(string path,uint mode);
    static void Private(string path) {if(OperatingSystem.IsLinux()&&chmod(path,448)!=0)throw new IOException("Could not protect recovery directory.");}
    public async Task<object> Handle(JsonElement request,CancellationToken ct) {
        if(request.ValueKind!=JsonValueKind.Object||request.EnumerateObject().Count()>4)throw new InvalidDataException();
        string package=request.GetProperty("package").GetString(),operation=request.GetProperty("operation").GetString(),id=request.GetProperty("transaction").GetString();
        if(package==null||!targets.ContainsKey(package)||id==null||id.Length!=32||id.Any(c=>!"0123456789abcdef".Contains(c)))throw new InvalidDataException();
        string parent=Path.Combine(root,package),dir=Path.Combine(parent,id),data=Path.Combine(writable,package);
        var packageLinks=new HashSet<string>{Path.Combine(apps,package,"bin"),Path.Combine(apps,package,"res"),Path.Combine(apps,package,"shared","res")};
        await gate.WaitAsync(ct);
        try {
            Directory.CreateDirectory(root);Private(root);SafeDirectory(root);Directory.CreateDirectory(parent);Private(parent);SafeDirectory(parent);
            if(operation=="cancel") {
                string current=Path.Combine(home,"community-apps","retained",package,"current.wgt");
                VersionOf(current,package);VerifyFiles(current,Path.Combine(apps,package,"res","wgt"));
                if(Directory.Exists(dir)){SafeTree(dir,packageLinks);Directory.Delete(dir,true);}return new{ok=true};
            }
            string marker=Path.Combine(parent,"completed.json");
            if(!Directory.Exists(dir)&&operation is "commit" or "finish-rollback") {
                if(!File.Exists(marker))throw new InvalidOperationException("Recovery result is not recorded.");
                var completed=JsonSerializer.Deserialize<State>(File.ReadAllText(marker));
                if(completed.Transaction!=id||completed.Package!=package||(operation=="finish-rollback")!=completed.Restored)throw new InvalidOperationException();
                string current=Path.Combine(home,"community-apps","retained",package,"current.wgt");
                if(Hash(current)!=(completed.Restored?completed.BeforeHash:completed.TargetHash))throw new InvalidOperationException();
                VerifyFiles(current,Path.Combine(apps,package,"res","wgt"));return new{ok=true};
            }
            if(operation=="begin") {
                if(Directory.Exists(dir)) {SafeDirectory(dir);var prior=JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(dir,"state.json")));if(prior?.Ready==true&&prior.Package==package&&prior.Transaction==id)return new {ok=true,version=prior.BeforeVersion};throw new InvalidOperationException("Incomplete recovery snapshot needs maintenance.");}
                if(Directory.EnumerateDirectories(parent).Any())throw new InvalidOperationException("An earlier recovery is unresolved.");
                string job=request.GetProperty("job").GetString();if(job==null||job.Length!=32||job.Any(c=>!"0123456789abcdef".Contains(c)))throw new InvalidDataException();
                string before=Path.Combine(home,"community-apps","retained",package,"current.wgt"),candidate=Path.Combine(home,"community-apps","job-"+job,"signed.wgt");
                SafeTree(Path.GetDirectoryName(before));SafeTree(Path.GetDirectoryName(candidate));
                string version=VersionOf(before,package),next=VersionOf(candidate,package);VerifyFiles(before,Path.Combine(apps,package,"res","wgt"));
                SafeTree(data,packageLinks);long bytes=Directory.EnumerateFiles(data,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}).Sum(x=>new FileInfo(x).Length);
                if(bytes>256L*1024*1024||new DriveInfo(root).AvailableFreeSpace<bytes*3+new FileInfo(before).Length+new FileInfo(candidate).Length+64L*1024*1024)throw new IOException("Insufficient recovery space.");
                Directory.CreateDirectory(dir);Private(dir);
                var state=new State{Package=package,Transaction=id,BeforeVersion=version,TargetVersion=next,BeforeHash=Hash(before),TargetHash=Hash(candidate)};Save(dir,state);
                await Stop(package,ct);
                await Run("/usr/bin/cp",new[]{"-a","--",before,Path.Combine(dir,"before.wgt")},ct);
                await Run("/usr/bin/cp",new[]{"-a","--",candidate,Path.Combine(dir,"target.wgt")},ct);
                await Run("/usr/bin/cp",new[]{"-a","--",data,Path.Combine(dir,"appdata")},ct);
                state.Ready=true;Save(dir,state);return new {ok=true,version};
            }
            SafeDirectory(dir);var saved=JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(dir,"state.json")));
            if(saved==null||!saved.Ready||saved.Package!=package||saved.Transaction!=id||Hash(Path.Combine(dir,"before.wgt"))!=saved.BeforeHash||Hash(Path.Combine(dir,"target.wgt"))!=saved.TargetHash)throw new InvalidOperationException("Recovery snapshot is incomplete.");
            if(operation=="verify") {VerifyFiles(Path.Combine(dir,"target.wgt"),Path.Combine(apps,package,"res","wgt"));if(package=="xvvl3S1TT1")await Stop(package,ct);return new {ok=true};}
            if(operation=="stage") {
                await Stop(package,ct);
                string destination=Path.Combine(home,"community-apps","recovery",package);Directory.CreateDirectory(destination);SafeDirectory(destination);
                await Run("/usr/bin/cp",new[]{"-a","--",Path.Combine(dir,"before.wgt"),Path.Combine(destination,"rollback.wgt")},ct);
                return new {ok=true,version=saved.BeforeVersion};
            }
            if(operation=="restore-data") {
                VerifyFiles(Path.Combine(dir,"before.wgt"),Path.Combine(apps,package,"res","wgt"));await Stop(package,ct);
                if(saved.Restored)return new {ok=true};
                string replacement=data+".quiet-restore-"+id,failed=Path.Combine(dir,"failed-appdata");
                if(saved.Restoring&&Directory.Exists(failed)) {
                    if(!Directory.Exists(data)&&Directory.Exists(replacement))Directory.Move(replacement,data);
                    SafeDirectory(data);saved.Restored=true;Save(dir,saved);return new {ok=true};
                }
                SafeTree(data,packageLinks);SafeTree(Path.Combine(dir,"appdata"),packageLinks);
                // Restore the whole per-app writable tree, including Web storage and shared data.
                if(Directory.Exists(replacement)){SafeTree(replacement,packageLinks);Directory.Delete(replacement,true);}
                await Run("/usr/bin/cp",new[]{"-a","--",Path.Combine(dir,"appdata"),replacement},ct);
                saved.Restoring=true;Save(dir,saved);Directory.Move(data,failed);Directory.Move(replacement,data);saved.Restored=true;Save(dir,saved);return new {ok=true};
            }
            if(operation=="commit"||operation=="finish-rollback") {
                if(operation=="finish-rollback"&&!saved.Restored)throw new InvalidOperationException();
                VerifyFiles(Path.Combine(dir,saved.Restored?"before.wgt":"target.wgt"),Path.Combine(apps,package,"res","wgt"));
                File.WriteAllText(marker+".tmp",JsonSerializer.Serialize(saved));File.Move(marker+".tmp",marker,true);
                SafeTree(dir,packageLinks);Directory.Delete(dir,true);
                string rollback=Path.Combine(home,"community-apps","recovery",package,"rollback.wgt");if(File.Exists(rollback))File.Delete(rollback);
                return new {ok=true};
            }
            throw new InvalidDataException();
        } finally {gate.Release();}
    }
}

// SPDX-License-Identifier: MIT
using System.Net;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace QuietThings.WebBridge;

// A shared loopback namespace is not an app identity. Verify both live TCP owners.
public sealed class CobaltProcessAttestation
{
    const string Cobalt = "/usr/apps/com.samsung.tv.cobalt/bin/cobalt_launcher";
    readonly string proc;
    public CobaltProcessAttestation(string procRoot = "/proc") { proc = procRoot; }
    string Start(int pid) {
        string text = File.ReadAllText($"{proc}/{pid}/stat");
        return text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
    }
    string Exe(int pid) => new FileInfo($"{proc}/{pid}/exe").LinkTarget;
    bool Owns(int pid, string inode) {
        return Directory.EnumerateFiles($"{proc}/{pid}/fd").Take(8192).Any(f => new FileInfo(f).LinkTarget == "socket:[" + inode + "]");
    }
    static IPAddress Address(string hex) {
        byte[] bytes = Convert.FromHexString(hex);
        for (int offset = 0; offset < bytes.Length; offset += 4) Array.Reverse(bytes, offset, 4);
        var value = new IPAddress(bytes); return value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;
    }
    static bool Endpoint(string raw, IPAddress ip, int port) {
        var parts = raw.Split(':');
        return parts.Length == 2 && Convert.ToInt32(parts[1], 16) == port && Address(parts[0]).Equals(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    }
    IEnumerable<string[]> Sockets() {
        foreach (var file in new[] { "tcp", "tcp6" }) {
            string path = proc + "/net/" + file;
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path).Skip(1).Take(8192)) {
                var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 10) yield return fields;
            }
        }
    }
    string Connection(IPAddress local, int localPort, IPAddress remote, int remotePort) {
        var found = Sockets().Where(x => x[3] == "01" && Endpoint(x[1], local, localPort) && Endpoint(x[2], remote, remotePort)).ToArray();
        if (found.Length != 1) throw new InvalidOperationException();
        return found[0][9];
    }
    bool Listener(int pid, int port) => Sockets().Any(x => x[3] == "0A" && Convert.ToInt32(x[1].Split(':')[1], 16) == port && Owns(pid, x[9]));
    IEnumerable<int> Processes(string executable) {
        foreach (string directory in Directory.EnumerateDirectories(proc).Take(8192)) {
            if (!int.TryParse(Path.GetFileName(directory), out int pid)) continue;
            bool match = false; try { match = Exe(pid) == executable; } catch { }
            if (match) yield return pid;
        }
    }
    public static string ModeFromCommandLine(byte[] bytes) {
        if (bytes == null || bytes.Length > 65536) return null;
        var args = Encoding.UTF8.GetString(bytes).Split('\0'); int field = Array.IndexOf(args, "USERDATA");
        if (field < 0 || field + 1 >= args.Length || args[field + 1].Length > 8192) return null;
        try {
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(args[field + 1]));
            var found = Regex.Matches(value, @"--proxy=http://127\.0\.0\.2:810([12])(?:\s|\x00|$)");
            return found.Count == 1 ? found[0].Groups[1].Value == "1" ? "tizentube" : "youtube" : null;
        } catch { return null; }
    }
    // This classifier chooses a launch transition only. It is never playback data.
    // A cold boot can preload Cobalt through the platform's generic loader.
    // Its kernel package label and argv identify a launch candidate; accepting
    // its playback socket additionally requires the mapped native executable.
    public static bool IsPreloadedCobalt(string executable, string label, byte[] commandLine) {
        if (executable != "/usr/bin/app-defined-loader" || label?.Trim() != "User::Pkg::com.samsung.tv.cobalt" ||
            commandLine == null || commandLine.Length > 65536) return false;
        int end = Array.IndexOf(commandLine, (byte)0);
        return end >= 0 && Encoding.UTF8.GetString(commandLine, 0, end) == Cobalt;
    }
    IEnumerable<int> LaunchCandidates() {
        foreach (string directory in Directory.EnumerateDirectories(proc).Take(8192)) {
            if (!int.TryParse(Path.GetFileName(directory), out int pid)) continue;
            bool match = false;
            try { match = Exe(pid) == Cobalt || IsPreloadedCobalt(Exe(pid),
                File.ReadAllText($"{proc}/{pid}/attr/current"), File.ReadAllBytes($"{proc}/{pid}/cmdline")); } catch { }
            if (match) yield return pid;
        }
    }
    public static bool HasNativeMapping(IEnumerable<string> lines) => lines.Take(8192).Any(line => {
        var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 6 && fields[1].Contains('x') && fields[4] != "0" && fields[5] == Cobalt;
    });
    bool Browser(int pid) {
        try {
            string exe = Exe(pid);
            return exe == Cobalt || (exe == "/usr/bin/app-defined-loader" &&
                IsPreloadedCobalt(exe, File.ReadAllText($"{proc}/{pid}/attr/current"), File.ReadAllBytes($"{proc}/{pid}/cmdline")) &&
                HasNativeMapping(File.ReadLines($"{proc}/{pid}/maps")));
        } catch { return false; }
    }
    string Identity(int browser, int worker, string mode) => string.Join("|", browser, Start(browser),
        File.ReadAllText(proc + "/sys/kernel/random/boot_id").Trim(), worker, Start(worker), mode);
    public bool NeedsRestart(string mode, Func<string, bool> currentIdentity = null) {
        if (mode is not ("youtube" or "tizentube")) throw new InvalidDataException();
        var proxies = Processes("/usr/bin/wrt-service").Where(p => Listener(p, 8100) && Listener(p, 8101) && Listener(p, 8102)).ToArray();
        if (proxies.Length != 1) throw new InvalidOperationException("Proxy unavailable");
        var browsers = LaunchCandidates().ToArray();
        if (browsers.Length > 1) throw new InvalidOperationException("Ambiguous native app");
        if (browsers.Length == 0) return false;
        int browser = browsers[0];
        if (Browser(browser) && currentIdentity?.Invoke(Identity(browser, proxies[0], mode)) == true) return false;
        return Exe(browser) != Cobalt || ModeFromCommandLine(File.ReadAllBytes($"{proc}/{browser}/cmdline")) != mode;
    }
    public string Verify(JsonElement report, IPEndPoint nodePeer, IPEndPoint rootLocal) {
        int worker = report.GetProperty("worker_pid").GetInt32();
        if (worker < 1 || Exe(worker) != "/usr/bin/wrt-service" || !Listener(worker, 8100) || !Listener(worker, 8101)) throw new InvalidOperationException();
        string workerStart = Start(worker);
        if (!IPAddress.IsLoopback(nodePeer.Address) || !Owns(worker, Connection(nodePeer.Address, nodePeer.Port, rootLocal.Address, rootLocal.Port))) throw new InvalidOperationException();
        var peer = report.GetProperty("peer");
        var address = IPAddress.Parse(peer.GetProperty("address").GetString());
        var local = IPAddress.Parse(peer.GetProperty("local_address").GetString());
        int remotePort = peer.GetProperty("port").GetInt32(), localPort = peer.GetProperty("local_port").GetInt32();
        string mode = report.GetProperty("mode").GetString();
        if (!IPAddress.IsLoopback(address) || !IPAddress.IsLoopback(local) || remotePort < 1 || remotePort > 65535 ||
            (mode == "tizentube" ? localPort != 8101 : mode != "youtube" || localPort != 8102) || !Listener(worker, localPort)) throw new InvalidOperationException();
        string inode = Connection(address, remotePort, local, localPort);
        var candidates = Directory.EnumerateDirectories(proc).Select(Path.GetFileName).Where(x => int.TryParse(x, out _)).Take(8192);
        var owners = new List<int>();
        foreach (var candidate in candidates) {
            int pid = int.Parse(candidate);
            try { if (Browser(pid) && Owns(pid, inode)) owners.Add(pid); } catch (IOException) {} catch (UnauthorizedAccessException) {}
        }
        if (owners.Count != 1 || Start(worker) != workerStart) throw new InvalidOperationException();
        int browser = owners[0];
        return Identity(browser, worker, mode);
    }
    public bool Alive(string identity) {
        try {
            var values = identity.Split('|');
            return values.Length == 6 && Browser(int.Parse(values[0])) && Start(int.Parse(values[0])) == values[1] &&
                File.ReadAllText(proc + "/sys/kernel/random/boot_id").Trim() == values[2] && Exe(int.Parse(values[3])) == "/usr/bin/wrt-service" && Start(int.Parse(values[3])) == values[4];
        } catch { return false; }
    }
}

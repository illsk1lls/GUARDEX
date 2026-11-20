using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Guardex;

// Known remote-access products. A distinctive process name matches on its own.
// A generic name matches only when the file's version info or signature says which product it is.
// The allow list starts empty. Nothing else on the machine is in this list.
static class RemoteCatalog
{
	static readonly RemoteProduct[] Products =
	[
		new("screenconnect", "ScreenConnect", "ConnectWise Control. Often used to cover the screen.", "ScreenConnect.ClientService", "ScreenConnect.WindowsClient", "ScreenConnect.Client"),
		new("teamviewer", "TeamViewer", "Remote control.", "TeamViewer", "TeamViewer_Service", "TeamViewer_Desktop", "TeamViewerQS"),
		new("anydesk", "AnyDesk", "Remote control.", "AnyDesk"),
		new("rustdesk", "RustDesk", "Remote control.", "rustdesk"),
		new("splashtop", "Splashtop", "Remote control.", "SplashtopStreamer", "SplashtopSOS", "SRManager", "SRService"),
		new("chrome-remote", "Chrome Remote Desktop", "The host side, not the browser.", "remoting_host"),
		new("quick-assist", "Quick Assist", "Windows built-in. Allow it if you ask someone for help with it.", "QuickAssist"),
		new("remote-assist", "Windows Remote Assistance", "Windows built-in. Allow it if you ask someone for help with it.", "msra"),
		new("ultravnc", "UltraVNC", "VNC server on this machine.", "winvnc"),
		new("tightvnc", "TightVNC", "VNC server on this machine.", "tvnserver"),
		new("tigervnc", "TigerVNC", "VNC server on this machine."),
		new("realvnc", "RealVNC", "VNC server on this machine.", "vncserver", "vncserverui"),
		new("dameware", "Dameware", "Remote control service.", "DWRCS", "DWRCC"),
		new("logmein", "LogMeIn", "Remote control.", "LogMeIn", "LMIGuardianSvc", "LMIGuardian", "LogMeInSystray"),
		new("logmein-rescue", "LogMeIn Rescue", "One-time support client.", "LMIRescue"),
		new("supremo", "Supremo", "Remote control.", "Supremo", "SupremoService", "SupremoHelper"),
		new("remote-utilities", "Remote Utilities", "Remote control host.", "rutserv", "rfusclient"),
		new("meshagent", "MeshAgent", "Remote management agent.", "MeshAgent"),
		new("atera", "Atera", "Remote management agent.", "AteraAgent"),
		new("ninja", "NinjaOne", "Remote management agent.", "NinjaRMMAgent"),
		new("beyondtrust", "BeyondTrust", "Remote support host on this machine.", "bomgar-scc", "bomgar-rdp", "bomgar-ps"),
		new("zoho-assist", "Zoho Assist", "Unattended host.", "ZohoURS", "ZohoURSService"),
		new("parsec", "Parsec", "Remote desktop.", "parsecd"),
		new("remotepc", "RemotePC", "Remote control.", "RemotePC", "RemotePCService"),
		new("dwservice", "DWService", "Remote management agent.", "dwagent", "dwagsvc"),
		new("todesk", "ToDesk", "Remote control.", "ToDesk", "ToDesk_Service"),
		new("sunlogin", "Sunlogin", "Oray remote control.", "SunloginClient"),
		new("awesun", "AweSun", "Remote control.", "AweSun"),
		new("anyviewer", "AnyViewer", "Remote control.", "AnyViewer"),
		new("ultraviewer", "UltraViewer", "Remote control.", "UltraViewer", "UltraViewer_Service"),
		new("aeroadmin", "AeroAdmin", "Remote control.", "AeroAdmin"),
		new("radmin", "Radmin", "Remote control server.", "rserver3"),
		new("litemanager", "LiteManager", "Remote control server.", "ROMServer"),
		new("ammyy", "Ammyy Admin", "Remote control.", "AA_v3"),
		new("netsupport", "NetSupport", "Remote control client.", "client32", "pcinssvc"),
		new("impero", "Impero", "Remote control client.", "ImperoClientSVC"),
		new("syncro", "Syncro", "Remote management agent.", "Syncro.Service"),
		new("automate", "ConnectWise Automate", "Remote management agent.", "LTSvcMon", "LTSVC"),
		new("datto", "Datto RMM", "Remote management agent.", "AEMAgent", "CagService"),
		new("kaseya", "Kaseya", "Remote management agent.", "AgentMon"),
		new("pulseway", "Pulseway", "Remote management agent.", "Pulseway", "PulsewayService"),
		new("action1", "Action1", "Remote management agent.", "action1_agent"),
		new("psexec", "PsExec", "Remote execution service.", "PSEXESVC"),
		new("goto", "GoTo", "GoToMyPC, GoTo Resolve, or GoToAssist host.", "g2host", "g2comm", "g2main", "g2tray"),
		new("nomachine", "NoMachine", "Remote desktop.", "nxservice", "nxnode"),
		new("isl", "ISL Light", "Remote control.", "ISLLight"),
		new("simplehelp", "SimpleHelp", "Remote support host.", "SimpleHelp"),
		new("getscreen", "Getscreen", "Remote control.", "Getscreen"),
		new("hoptodesk", "HopToDesk", "Remote control.", "HopToDesk"),
		new("iperius", "Iperius Remote", "Remote control.", "IperiusRemote"),
		new("veyon", "Veyon", "Classroom remote control.", "veyon-service"),
		new("showmypc", "ShowMyPC", "Remote control.", "ShowMyPC"),
		new("openssh", "OpenSSH Server", "Windows OpenSSH server. Allow it if you sign in over ssh.", "sshd"),
		new("nable", "N-able", "N-central or N-able remote agent."),
		new("rdp", "Windows Remote Desktop", "An incoming session is disconnected only when someone is also at this keyboard. Allow it if you sign in from another computer.")
	];

	static readonly (string Id, string Phrase)[] Marks =
	[
		("logmein-rescue", "logmein rescue"),
		("chrome-remote", "chrome remote desktop"),
		("remote-utilities", "remote utilities"),
		("automate", "connectwise automate"),
		("iperius", "iperius remote"),
		("zoho-assist", "zoho assist"),
		("screenconnect", "connectwise control"),
		("datto", "centrastage"),
		("datto", "datto rmm"),
		("datto", "aem agent"),
		("beyondtrust", "beyondtrust remote support"),
		("screenconnect", "screenconnect"),
		("teamviewer", "teamviewer"),
		("anydesk", "anydesk"),
		("rustdesk", "rustdesk"),
		("splashtop", "splashtop"),
		("ultravnc", "ultravnc"),
		("tightvnc", "tightvnc"),
		("tigervnc", "tigervnc"),
		("realvnc", "realvnc"),
		("dameware", "dameware"),
		("logmein", "logmein"),
		("supremo", "supremo"),
		("meshagent", "meshcentral"),
		("meshagent", "meshagent"),
		("atera", "atera"),
		("ninja", "ninjarmm"),
		("ninja", "ninjaone"),
		("beyondtrust", "bomgar"),
		("parsec", "parsec"),
		("remotepc", "remotepc"),
		("dwservice", "dwservice"),
		("dwservice", "dwagent"),
		("todesk", "todesk"),
		("sunlogin", "sunlogin"),
		("awesun", "awesun"),
		("anyviewer", "anyviewer"),
		("ultraviewer", "ultraviewer"),
		("aeroadmin", "aeroadmin"),
		("radmin", "radmin"),
		("litemanager", "litemanager"),
		("ammyy", "ammyy"),
		("netsupport", "netsupport"),
		("impero", "impero"),
		("syncro", "syncro"),
		("automate", "labtech"),
		("kaseya", "kaseya"),
		("pulseway", "pulseway"),
		("action1", "action1"),
		("psexec", "psexesvc"),
		("goto", "gotoassist"),
		("goto", "gotomypc"),
		("goto", "goto resolve"),
		("nomachine", "nomachine"),
		("isl", "isl online"),
		("isl", "isllight"),
		("simplehelp", "simplehelp"),
		("getscreen", "getscreen"),
		("hoptodesk", "hoptodesk"),
		("veyon", "veyon"),
		("showmypc", "showmypc"),
		("nable", "n-central"),
		("nable", "n-able")
	];

	static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
	{
		"agent", "client", "service", "svc", "runtime", "host", "update", "updater",
		"helper", "winagent", "server", "remote", "support", "syshost", "windowsagent",
		"remoteservice", "svchost", "runtimebroker"
	};

	static readonly Dictionary<string, RemoteProduct> ById;
	static readonly Dictionary<string, string> ByProcess;
	static readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
	static readonly HashSet<string> Allow = new(StringComparer.Ordinal);
	static readonly object Gate = new();

	public static bool StopEnabled { get; private set; } = true;
	public static IReadOnlyList<RemoteProduct> All => Products;

	static RemoteCatalog()
	{
		ById = Products.ToDictionary(p => p.Id, StringComparer.Ordinal);
		ByProcess = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var product in Products)
			foreach (var name in product.Processes)
				ByProcess[name] = product.Id;
		Load();
	}

	public static RemoteProduct? Match(string processName)
	{
		var key = Bare(processName);
		if (key.Length == 0 || Generic.Contains(key)) return null;
		if (!ByProcess.TryGetValue(key, out var id)) return null;
		return ById[id];
	}

	public static RemoteProduct? MatchFacts(string processName, string product, string description, string company, string originalName, string internalName, string signer)
	{
		var blob = string.Join('\n', product, description, company, originalName, internalName, signer).ToLowerInvariant();
		string? bestId = null;
		var bestLen = 0;
		if (blob.Trim().Length > 0)
		{
			foreach (var mark in Marks)
			{
				if (mark.Phrase.Length < bestLen) continue;
				if (!HasPhrase(blob, mark.Phrase)) continue;
				bestId = mark.Id;
				bestLen = mark.Phrase.Length;
			}
		}
		if (bestId != null) return ById[bestId];

		foreach (var candidate in new[] { originalName, internalName })
		{
			var bare = Bare(candidate);
			if (bare.Length == 0 || Generic.Contains(bare)) continue;
			if (ByProcess.TryGetValue(bare, out var fromFile)) return ById[fromFile];
		}
		return Match(processName);
	}

	public static RemoteProduct? Identify(string processName, string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return Match(processName);
		lock (Gate)
		{
			if (_files.TryGetValue(path, out var cached))
				return cached.Length == 0 ? null : ById[cached];
		}

		var (product, description, company, original, internalName) = ReadVersion(path);
		var found = MatchFacts(processName, product, description, company, original, internalName, "");
		if (found == null)
		{
			var signer = ReadSigner(path);
			if (signer.Length > 0)
				found = MatchFacts(processName, product, description, company, original, internalName, signer);
		}
		lock (Gate)
		{
			if (_files.Count > 2000) _files.Clear();
			_files[path] = found?.Id ?? "";
		}
		return found;
	}

	public static string? ExePath(string command)
	{
		if (string.IsNullOrWhiteSpace(command)) return null;
		var text = command.Trim();
		if (text.StartsWith('"'))
		{
			var end = text.IndexOf('"', 1);
			if (end > 1)
			{
				var quoted = text[1..end];
				if (quoted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return quoted;
			}
		}
		var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
		if (exe < 0) return null;
		var slice = text[..(exe + 4)];
		var quote = slice.LastIndexOf('"');
		if (quote >= 0) slice = slice[(quote + 1)..];
		else
		{
			var drive = slice.LastIndexOf(":\\", StringComparison.Ordinal);
			if (drive > 0) slice = slice[(drive - 1)..];
		}
		slice = slice.Trim().Trim('"');
		return slice.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? slice : null;
	}

	public static string? ExeFile(string command)
	{
		var path = ExePath(command);
		if (path == null) return null;
		var name = Path.GetFileName(path);
		return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : null;
	}

	static string Bare(string name)
	{
		var key = (name ?? "").Trim().Trim('"');
		key = Path.GetFileName(key);
		if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && key.Length > 4)
			key = key[..^4];
		return key;
	}

	static bool HasPhrase(string blob, string phrase)
	{
		var i = 0;
		while ((i = blob.IndexOf(phrase, i, StringComparison.Ordinal)) >= 0)
		{
			var before = i == 0 ? '\0' : blob[i - 1];
			var after = i + phrase.Length < blob.Length ? blob[i + phrase.Length] : '\0';
			if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after)) return true;
			i++;
		}
		return false;
	}

	static (string Product, string Description, string Company, string Original, string Internal) ReadVersion(string path)
	{
		try
		{
			var info = FileVersionInfo.GetVersionInfo(path);
			return (info.ProductName ?? "", info.FileDescription ?? "", info.CompanyName ?? "", info.OriginalFilename ?? "", info.InternalName ?? "");
		}
		catch
		{
			return ("", "", "", "", "");
		}
	}

	static string ReadSigner(string path)
	{
		try
		{
#pragma warning disable SYSLIB0057
			using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
			return cert.Subject ?? "";
		}
		catch
		{
			return "";
		}
	}

	public static bool IsAllowed(string id)
	{
		lock (Gate) return Allow.Contains(id);
	}

	public static bool SetAllowed(string id, bool on)
	{
		if (!ById.ContainsKey(id)) return false;
		lock (Gate)
		{
			var changed = on ? Allow.Add(id) : Allow.Remove(id);
			if (!changed) return false;
			SaveAllow();
			return true;
		}
	}

	public static bool Clear()
	{
		lock (Gate)
		{
			if (Allow.Count == 0) return false;
			Allow.Clear();
			SaveAllow();
			return true;
		}
	}

	public static bool SetStopEnabled(bool on)
	{
		lock (Gate)
		{
			if (StopEnabled == on) return false;
			StopEnabled = on;
			try
			{
				File.WriteAllText(Path.Combine(Dir(), "remote-stop.txt"), on ? "1" : "0");
			}
			catch { }
			return true;
		}
	}

	static void Load()
	{
		try
		{
			var allowPath = Path.Combine(Dir(), "remote-allow.txt");
			if (File.Exists(allowPath))
			{
				lock (Gate)
				{
					Allow.Clear();
					foreach (var line in File.ReadAllLines(allowPath))
					{
						var id = line.Trim().ToLowerInvariant();
						if (ById.ContainsKey(id)) Allow.Add(id);
					}
				}
			}
			var stopPath = Path.Combine(Dir(), "remote-stop.txt");
			if (File.Exists(stopPath) && File.ReadAllText(stopPath).Trim() == "0")
				StopEnabled = false;
		}
		catch { }
	}

	static void SaveAllow()
	{
		try
		{
			File.WriteAllLines(Path.Combine(Dir(), "remote-allow.txt"), Allow.OrderBy(s => s));
		}
		catch { }
	}

	static string Dir()
	{
		var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");
		Directory.CreateDirectory(dir);
		return dir;
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (Match("ScreenConnect.ClientService")?.Id != "screenconnect") failures.Add("screenconnect process");
		if (Match("ScreenConnect.ClientService.exe")?.Id != "screenconnect") failures.Add("screenconnect exe");
		if (Match("ScreenConnect") != null) failures.Add("partial screenconnect name matched");
		if (Match("explorer") != null) failures.Add("explorer matched");
		if (Match("chrome") != null) failures.Add("chrome matched");
		if (Match("TeamViewer_Service")?.Id != "teamviewer") failures.Add("teamviewer service");
		if (Match("agent") != null) failures.Add("generic agent name matched");
		if (Match("service") != null) failures.Add("generic service name matched");
		if (Match("NinjaRMMAgent")?.Id != "ninja") failures.Add("ninja");
		if (Match("AEMAgent")?.Id != "datto") failures.Add("datto");
		if (Match("PSEXESVC")?.Id != "psexec") failures.Add("psexec");
		if (MatchFacts("agent", "", "", "", "ScreenConnect.ClientService.exe", "", "")?.Id != "screenconnect")
			failures.Add("renamed screenconnect original filename");
		if (MatchFacts("update", "ScreenConnect Client", "", "", "", "", "")?.Id != "screenconnect")
			failures.Add("renamed screenconnect product");
		if (MatchFacts("agent", "Windows Service", "Host Process", "Microsoft Corporation", "agent.exe", "agent", "") != null)
			failures.Add("generic microsoft file matched");
		if (MatchFacts("agent", "Helper", "Background helper", "", "helper.exe", "", "") != null)
			failures.Add("generic helper matched");
		if (MatchFacts("tvnserver", "TigerVNC", "TigerVNC Server", "", "tvnserver.exe", "", "")?.Id != "tigervnc")
			failures.Add("tigervnc lost to the tightvnc process name");
		if (MatchFacts("winvnc", "RealVNC", "VNC Server", "RealVNC", "vncserver.exe", "", "")?.Id != "realvnc")
			failures.Add("realvnc file was called ultravnc");
		if (ExeFile(@"C:\Program Files (x86)\ScreenConnect Client\ScreenConnect.ClientService.exe") != "ScreenConnect.ClientService.exe")
			failures.Add("exe parse");
		if (ExePath("\"C:\\Program Files\\Atera\\AteraAgent.exe\" /service") != "C:\\Program Files\\Atera\\AteraAgent.exe")
			failures.Add("quoted exe path");
		if (!RemoteSessions.ShouldCut(true, true, false)) failures.Add("local plus incoming remote should cut");
		if (RemoteSessions.ShouldCut(false, true, false)) failures.Add("remote-only session should stay");
		if (RemoteSessions.ShouldCut(true, true, true)) failures.Add("allowed remote desktop should stay");
		if (RemoteSessions.ShouldCut(true, false, false)) failures.Add("no incoming session should cut");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}
}

sealed class RemoteProduct
{
	public string Id { get; }
	public string Name { get; }
	public string Note { get; }
	public string[] Processes { get; }

	public RemoteProduct(string id, string name, string note, params string[] processes)
	{
		Id = id;
		Name = name;
		Note = note;
		Processes = processes;
	}
}

static class RemoteSessions
{
	public readonly record struct Picture(bool ConsoleActive, bool RdpActive);

	public static bool ShouldCut(bool consoleActive, bool rdpActive, bool allowed) =>
		consoleActive && rdpActive && !allowed;

	public static Picture Look()
	{
		var console = false;
		var rdp = false;
		IntPtr ptr = IntPtr.Zero;
		try
		{
			if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out ptr, out var count) || ptr == IntPtr.Zero)
				return new Picture(false, false);
			var size = Marshal.SizeOf<WtsSession>();
			for (var i = 0; i < count; i++)
			{
				var row = Marshal.PtrToStructure<WtsSession>(ptr + i * size);
				var name = Marshal.PtrToStringUni(row.Name) ?? "";
				if (row.State != 0) continue;
				if (name.Equals("Console", StringComparison.OrdinalIgnoreCase)) console = true;
				else if (name.StartsWith("RDP-Tcp", StringComparison.OrdinalIgnoreCase)) rdp = true;
			}
		}
		catch { }
		finally
		{
			if (ptr != IntPtr.Zero) WTSFreeMemory(ptr);
		}
		return new Picture(console, rdp);
	}

	public static int DisconnectIncoming()
	{
		IntPtr ptr = IntPtr.Zero;
		var cut = 0;
		try
		{
			if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out ptr, out var count) || ptr == IntPtr.Zero)
				return -1;
			var size = Marshal.SizeOf<WtsSession>();
			for (var i = 0; i < count; i++)
			{
				var row = Marshal.PtrToStructure<WtsSession>(ptr + i * size);
				var name = Marshal.PtrToStringUni(row.Name) ?? "";
				if (row.State != 0) continue;
				if (!name.StartsWith("RDP-Tcp", StringComparison.OrdinalIgnoreCase)) continue;
				if (WTSDisconnectSession(IntPtr.Zero, row.SessionId, false)) cut++;
			}
		}
		catch
		{
			return -1;
		}
		finally
		{
			if (ptr != IntPtr.Zero) WTSFreeMemory(ptr);
		}
		return cut;
	}

	[DllImport("wtsapi32.dll", SetLastError = true)]
	static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

	[DllImport("wtsapi32.dll")]
	static extern void WTSFreeMemory(IntPtr memory);

	[DllImport("wtsapi32.dll", SetLastError = true)]
	static extern bool WTSDisconnectSession(IntPtr server, int sessionId, bool wait);

	[StructLayout(LayoutKind.Sequential)]
	struct WtsSession
	{
		public int SessionId;
		public IntPtr Name;
		public int State;
	}
}

sealed class RemoteGuard
{
	readonly SessionStore _store;
	readonly object _look = new();
	readonly HashSet<string> _announced = new(StringComparer.Ordinal);
	readonly Dictionary<string, DateTime> _quietSince = new(StringComparer.Ordinal);
	readonly Dictionary<string, (string Mode, string Product)> _held = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, DateTime> _holdTried = new(StringComparer.Ordinal);
	readonly Dictionary<string, (string State, string Line, DateTime Until)> _recent = new(StringComparer.Ordinal);
	CancellationTokenSource? _cts;

	public event Action<string>? Alerted;

	public RemoteGuard(SessionStore store)
	{
		_store = store;
		LoadHeld();
	}

	public void Start()
	{
		if (_cts != null) return;
		_cts = new CancellationTokenSource();
		var ct = _cts.Token;
		Task.Run(() =>
		{
			try { lock (_look) RestoreAllowed(); }
			catch { }
			while (!ct.IsCancellationRequested)
			{
				try { lock (_look) LookCore(); }
				catch (Exception ex) { _store.SetRemoteStatus(ex.Message); }
				if (ct.WaitHandle.WaitOne(2000)) break;
			}
		}, ct);
	}

	public void Stop()
	{
		var cts = _cts;
		_cts = null;
		try { cts?.Cancel(); } catch { }
	}

	public void SetAllowed(string id, bool on)
	{
		if (!RemoteCatalog.SetAllowed(id, on)) return;
		_ = Task.Run(() =>
		{
			lock (_look)
			{
				if (on) Restore(id);
				LookCore();
			}
		});
	}

	public void AllowNone()
	{
		RemoteCatalog.Clear();
		_ = Task.Run(() => { lock (_look) LookCore(); });
	}

	public void SetStopEnabled(bool on)
	{
		if (!RemoteCatalog.SetStopEnabled(on)) return;
		_ = Task.Run(() => { lock (_look) LookCore(); });
	}

	void LookCore()
	{
		var running = new Dictionary<string, int>(StringComparer.Ordinal);
		var killed = new HashSet<string>(StringComparer.Ordinal);
		var failed = new HashSet<string>(StringComparer.Ordinal);
		foreach (var proc in Process.GetProcesses())
		{
			try
			{
				string path = "";
				try { path = proc.MainModule?.FileName ?? ""; } catch { }
				var product = RemoteCatalog.Identify(proc.ProcessName, path);
				if (product == null) continue;
				running.TryGetValue(product.Id, out var count);
				running[product.Id] = count + 1;
				if (RemoteCatalog.IsAllowed(product.Id) || !RemoteCatalog.StopEnabled) continue;
				try
				{
					proc.Kill(true);
					killed.Add(product.Id);
				}
				catch
				{
					failed.Add(product.Id);
				}
			}
			catch { }
			finally
			{
				try { proc.Dispose(); } catch { }
			}
		}

		if (RemoteCatalog.StopEnabled)
		{
			foreach (var id in running.Keys)
			{
				if (!RemoteCatalog.IsAllowed(id)) TryHoldServices(id);
			}
		}

		var sessions = RemoteSessions.Look();
		var rdpAllowed = RemoteCatalog.IsAllowed("rdp");
		var cut = 0;
		var cutFailed = false;
		if (sessions.RdpActive && RemoteSessions.ShouldCut(sessions.ConsoleActive, true, rdpAllowed) && RemoteCatalog.StopEnabled)
		{
			cut = RemoteSessions.DisconnectIncoming();
			if (cut < 0) cutFailed = true;
		}

		var cover = "";
		if (running.Keys.Any(id => !RemoteCatalog.IsAllowed(id)))
			cover = CoverText();

		Publish(running, killed, failed, sessions, cut, cutFailed, cover);
		_store.SetRemoteStatus("");
	}

	void Publish(
		Dictionary<string, int> running,
		HashSet<string> killed,
		HashSet<string> failed,
		RemoteSessions.Picture sessions,
		int cut,
		bool cutFailed,
		string cover)
	{
		var rows = new List<RemoteRow>();
		string? alert = null;
		foreach (var product in RemoteCatalog.All)
		{
			var isRdp = product.Id == "rdp";
			var live = isRdp ? sessions.RdpActive : running.ContainsKey(product.Id);
			var allowed = RemoteCatalog.IsAllowed(product.Id);
			var (state, line) = Describe(product, live, allowed, sessions, killed, failed, cut, cutFailed, cover);
			if (state is "Stopped" or "Needs admin" or "Running" or "Watching")
				_recent[product.Id] = (state, line, DateTime.UtcNow.AddMinutes(1));
			else if (_recent.TryGetValue(product.Id, out var recent) && recent.Until > DateTime.UtcNow)
			{
				state = recent.State;
				line = recent.Line;
			}
			else
				_recent.Remove(product.Id);
			rows.Add(new RemoteRow
			{
				Id = product.Id,
				Name = product.Name,
				Line = line,
				State = state,
				Allowed = allowed,
				Running = live
			});
			if (state is "Stopped" or "Needs admin" or "Running" or "Watching")
				Announce(product.Id, line, ref alert);
			else
				MarkQuiet(product.Id);
		}
		rows.Sort((a, b) =>
		{
			var rank = Rank(b.State).CompareTo(Rank(a.State));
			return rank != 0 ? rank : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
		});
		_store.SetRemote(rows, alert ?? "", RemoteCatalog.StopEnabled);
	}

	static int Rank(string state) => state switch
	{
		"Needs admin" => 4,
		"Stopped" => 3,
		"Running" => 3,
		"Watching" => 2,
		"Allowed" => 1,
		_ => 0
	};

	static (string State, string Line) Describe(
		RemoteProduct product,
		bool live,
		bool allowed,
		RemoteSessions.Picture sessions,
		HashSet<string> killed,
		HashSet<string> failed,
		int cut,
		bool cutFailed,
		string cover)
	{
		if (!live)
			return ("Not running", product.Note);
		if (allowed)
			return ("Allowed", product.Name + " is allowed, so it was left running.");

		if (product.Id == "rdp")
		{
			if (!RemoteCatalog.StopEnabled)
				return ("Running", "Not allowed. Stopping is off, so the session was left up.");
			if (!sessions.ConsoleActive)
				return ("Watching", "Someone is signed in remotely. It was left up so this machine is not locked out.");
			if (cutFailed || cut == 0)
				return ("Needs admin", "An incoming Remote Desktop session is up. Run GUARDEX as admin to disconnect it.");
			return ("Stopped", "An incoming Remote Desktop session was disconnected. Someone is at this keyboard.");
		}

		var extra = cover.Length > 0 ? " " + cover : "";
		if (!RemoteCatalog.StopEnabled)
			return ("Running", product.Name + " is not allowed. Stopping is off, so it was left running." + extra);
		if (failed.Contains(product.Id) && !killed.Contains(product.Id))
			return ("Needs admin", product.Name + " is not allowed. Run GUARDEX as admin to stop it." + extra);
		return ("Stopped", product.Name + " is not allowed. Stopping it." + extra);
	}

	void Announce(string id, string text, ref string? alert)
	{
		alert ??= text;
		_quietSince.Remove(id);
		if (!_announced.Add(id)) return;
		_store.Note(text);
		Alerted?.Invoke(text);
	}

	void MarkQuiet(string id)
	{
		if (!_announced.Contains(id)) return;
		if (!_quietSince.TryGetValue(id, out var since))
		{
			_quietSince[id] = DateTime.UtcNow;
			return;
		}
		if (DateTime.UtcNow - since > TimeSpan.FromMinutes(1))
		{
			_announced.Remove(id);
			_quietSince.Remove(id);
		}
	}

	void TryHoldServices(string productId)
	{
		if (_held.Values.Any(v => v.Product == productId)) return;
		if (_holdTried.TryGetValue(productId, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30)) return;
		_holdTried[productId] = DateTime.UtcNow;
		try
		{
			dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!)!;
			dynamic cim = locator.ConnectServer(".", @"root\cimv2");
			dynamic set = cim.ExecQuery("SELECT Name, PathName, StartMode FROM Win32_Service");
			foreach (dynamic row in set)
			{
				string path = row.PathName is string s ? s : "";
				var full = RemoteCatalog.ExePath(path);
				var product = full == null ? null : RemoteCatalog.Identify(Path.GetFileName(full), full);
				if (product == null || product.Id != productId) continue;
				string mode = row.StartMode is string m ? m : "";
				if (mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) continue;
				string name = row.Name is string n ? n : "";
				if (name.Length == 0) continue;
				var changed = false;
				try { row.StopService(); changed = true; } catch { }
				try { row.ChangeStartMode("Disabled"); changed = true; } catch { }
				if (changed) _held[name] = (mode, productId);
			}
			if (_held.Values.Any(v => v.Product == productId)) SaveHeld();
		}
		catch { }
	}

	void RestoreAllowed()
	{
		foreach (var product in RemoteCatalog.All)
		{
			if (RemoteCatalog.IsAllowed(product.Id)) Restore(product.Id);
		}
	}

	void Restore(string productId)
	{
		var names = _held.Where(kv => kv.Value.Product == productId).Select(kv => kv.Key).ToList();
		if (names.Count == 0) return;
		try
		{
			dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!)!;
			dynamic cim = locator.ConnectServer(".", @"root\cimv2");
			foreach (var name in names)
			{
				var mode = MapMode(_held[name].Mode);
				dynamic set = cim.ExecQuery("SELECT Name FROM Win32_Service WHERE Name='" + EscapeWql(name) + "'");
				foreach (dynamic row in set)
				{
					try { row.ChangeStartMode(mode); } catch { }
				}
				_held.Remove(name);
			}
		}
		catch { }
		SaveHeld();
		var product = RemoteCatalog.All.First(p => p.Id == productId);
		_store.Note(product.Name + " is allowed again. Its service start mode was restored. It was not started.");
	}

	static string MapMode(string mode) => mode.ToLowerInvariant() switch
	{
		"auto" or "automatic" => "Automatic",
		"manual" => "Manual",
		_ => "Manual"
	};

	static string EscapeWql(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");

	void LoadHeld()
	{
		try
		{
			var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex", "remote-held.txt");
			if (!File.Exists(path)) return;
			foreach (var line in File.ReadAllLines(path))
			{
				var parts = line.Split('\t');
				if (parts.Length != 3 || parts[1].Length == 0) continue;
				_held[parts[1]] = (parts[2], parts[0]);
			}
		}
		catch { }
	}

	void SaveHeld()
	{
		try
		{
			var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");
			Directory.CreateDirectory(dir);
			var lines = _held.Select(kv => kv.Value.Product + "\t" + kv.Key + "\t" + kv.Value.Mode);
			File.WriteAllLines(Path.Combine(dir, "remote-held.txt"), lines);
		}
		catch { }
	}

	static string CoverText()
	{
		try
		{
			var screen = System.Windows.Forms.Screen.PrimaryScreen;
			if (screen == null) return "";
			var bounds = screen.Bounds;
			var found = false;
			EnumProc cb = (hwnd, _) =>
			{
				if (!IsWindowVisible(hwnd)) return true;
				var title = new StringBuilder(512);
				if (GetWindowText(hwnd, title, title.Capacity) <= 0) return true;
				if (!GetWindowRect(hwnd, out var rect)) return true;
				var w = rect.Right - rect.Left;
				var h = rect.Bottom - rect.Top;
				if (w < bounds.Width * 0.85 || h < bounds.Height * 0.85) return true;
				if (!LooksLikeCover(title.ToString())) return true;
				found = true;
				return false;
			};
			EnumWindows(cb, IntPtr.Zero);
			GC.KeepAlive(cb);
			return found ? "A full-screen window says it is a Windows update." : "";
		}
		catch
		{
			return "";
		}
	}

	static bool LooksLikeCover(string title)
	{
		var text = title.ToLowerInvariant();
		return text.Contains("windows update")
			|| text.Contains("do not turn off")
			|| text.Contains("don't turn off")
			|| text.Contains("do not shut");
	}

	delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

	[DllImport("user32.dll")]
	static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

	[DllImport("user32.dll")]
	static extern bool IsWindowVisible(IntPtr hwnd);

	[DllImport("user32.dll")]
	static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

	[StructLayout(LayoutKind.Sequential)]
	struct Rect
	{
		public int Left, Top, Right, Bottom;
	}
}

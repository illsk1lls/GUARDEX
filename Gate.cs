using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Guardex;

// A page script already has no filesystem rights. This watches the moment that
// script gets a process started, or a dropped program calls out, or the
// clipboard wallet address changes on its own.
static class LaunchRules
{
	static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
	{
		"chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore", "msedgewebview2"
	};

	static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
	{
		"crashpad_handler", "software_reporter_tool", "elevation_service", "notification_helper",
		"chrome_pwa_launcher", "chrome_proxy", "identity_helper", "pingsender", "maintenanceservice",
		"default-browser-agent", "plugin-container", "crashhelper", "msedgewebview2", "pwahelper",
		"microsoftedgeupdate", "googleupdate", "googlecrashhandler", "googlecrashhandler64",
		"opera_crashreporter", "opera_autoupdate"
	};

	static readonly HashSet<string> Vehicles = new(StringComparer.OrdinalIgnoreCase)
	{
		"powershell", "pwsh", "cmd", "wscript", "cscript", "mshta", "certutil", "bitsadmin",
		"curl", "wget", "rundll32", "regsvr32", "msbuild", "bash", "wsl",
		"python", "pythonw", "node", "php", "perl", "ruby", "hh", "ftp", "wmic",
		"cmstp", "diskshadow", "forfiles", "odbcconf", "presentationhost", "autoit3", "autoit"
	};

	// Exact names. A browser that starts one is stopped. Explorer is not a browser, so the person's own copy is left alone.
	static readonly HashSet<string> Tools = new(StringComparer.OrdinalIgnoreCase)
	{
		"nuclei", "sqlmap", "nmap", "masscan", "rustscan", "naabu", "ffuf", "gobuster",
		"feroxbuster", "nikto", "wpscan", "wfuzz", "hydra", "medusa", "mimikatz",
		"sharphound", "rubeus", "lazagne", "metasploit", "msfconsole", "msfvenom",
		"meterpreter", "sliver", "havoc", "netexec", "crackmapexec", "responder",
		"kerbrute", "bloodhound", "chisel", "ligolo", "ligolo-proxy", "ligolo-agent",
		"burpsuite", "burpsuitecommunity", "burpsuitepro"
	};

	public static string Bare(string name)
	{
		var text = name.Trim().Trim('"');
		var slash = Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/'));
		if (slash >= 0 && slash < text.Length - 1) text = text[(slash + 1)..];
		if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && text.Length > 4)
			text = text[..^4];
		return text;
	}

	public static bool IsBrowser(string name) => Browsers.Contains(Bare(name));

	public static string Decide(string parent, string name, string path, bool allowed)
	{
		if (allowed) return "allow";
		var parentName = Bare(parent);
		var child = Bare(name);
		if (child.Length == 0 || !Browsers.Contains(parentName)) return "allow";
		if (child.Equals(parentName, StringComparison.OrdinalIgnoreCase)) return "allow";
		if (Helpers.Contains(child)) return "allow";
		if (Vehicles.Contains(child) || Tools.Contains(child)) return "stop";
		var dropped = DropFolder(path);
		if (dropped == "Temp") return "stop";
		if (dropped == "Downloads") return "watch";
		if (Vendor(path) || Boxed(path)) return "allow";
		return "allow";
	}

	public static string? Drop(string name, string path)
	{
		if (Bare(name).Length == 0 || path.Length == 0) return null;
		if (IsBrowser(name) || Helpers.Contains(Bare(name))) return null;
		var dropped = DropFolder(path);
		if (dropped != null) return dropped;
		return null;
	}

	static string? DropFolder(string path)
	{
		if (Has(path, "\\temp\\")) return "Temp";
		if (Has(path, "\\downloads\\")) return "Downloads";
		return null;
	}

	static bool Vendor(string path)
	{
		var text = path.Replace('/', '\\');
		return Has(text, "\\google\\chrome\\")
			|| Has(text, "\\google\\update\\")
			|| Has(text, "\\microsoft\\edge\\")
			|| Has(text, "\\mozilla firefox\\")
			|| Has(text, "\\bravesoftware\\")
			|| Has(text, "\\opera software\\")
			|| Has(text, "\\vivaldi\\");
	}

	// A real install directory, as a prefix. A folder named windows inside Temp is not Windows.
	static bool Boxed(string path)
	{
		var text = path.Replace('/', '\\');
		foreach (var root in new[]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.Windows),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
		})
		{
			if (root.Length == 0) continue;
			var prefix = root.TrimEnd('\\') + "\\";
			if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	static bool Has(string path, string part) =>
		path.Contains(part, StringComparison.OrdinalIgnoreCase);

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (Decide("chrome", "powershell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", false) != "stop")
			failures.Add("browser powershell");
		if (Decide("chrome.exe", "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", false) != "allow")
			failures.Add("browser child");
		if (Decide("msedge", "crashpad_handler", @"C:\Program Files\Microsoft\Edge\Application\crashpad_handler.exe", false) != "allow")
			failures.Add("helper");
		if (Decide("chrome", "payload", @"C:\Users\a\AppData\Local\Temp\payload.exe", false) != "stop")
			failures.Add("temp child");
		if (Decide("firefox", "setup", @"C:\Users\a\Downloads\setup.exe", false) != "watch")
			failures.Add("download child");
		if (Decide("explorer", "powershell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", false) != "allow")
			failures.Add("user shell");
		if (Decide("chrome", "powershell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true) != "allow")
			failures.Add("allowed vehicle");
		if (Decide("msedgewebview2", "cmd", @"C:\Windows\System32\cmd.exe", false) != "stop")
			failures.Add("webview cmd");
		if (Decide("chrome", "Discord", @"C:\Users\a\AppData\Local\Discord\app.exe", false) != "allow")
			failures.Add("appdata app");
		if (Decide("", "powershell", @"C:\Windows\System32\cmd.exe", false) != "allow")
			failures.Add("orphan shell");
		if (Drop("chrome", @"C:\Users\a\AppData\Local\Temp\chrome.exe") != null)
			failures.Add("browser temp path");
		if (Drop("payload", @"C:\Users\a\AppData\Local\Temp\payload.exe") != "Temp")
			failures.Add("temp drop");
		if (Drop("setup", @"C:\Users\a\Downloads\setup.exe") != "Downloads")
			failures.Add("download drop");
		if (Drop("OneDrive", @"C:\Program Files\Microsoft OneDrive\OneDrive.exe") != null)
			failures.Add("program files drop");
		if (Decide("chrome", "payload", @"C:\Users\a\AppData\Local\Temp\windows\x.exe", false) != "stop")
			failures.Add("temp windows bypass");
		if (Decide("chrome", "payload", @"C:\Users\a\AppData\Local\Temp\Program Files\x.exe", false) != "stop")
			failures.Add("temp program files bypass");
		if (Decide("chrome", "payload", @"C:\Windows\Temp\x.exe", false) != "stop")
			failures.Add("windows temp");
		if (Decide("chrome", "widget", @"C:\Program Files\App\widget.exe", false) != "allow")
			failures.Add("real program files");
		if (Drop("payload", @"C:\Users\a\AppData\Local\Temp\windows\x.exe") != "Temp")
			failures.Add("temp windows drop");
		if (Drop("payload", @"C:\Users\a\AppData\Local\Temp\google\chrome\x.exe") != "Temp")
			failures.Add("temp vendor bypass");
		if (Decide("chrome", "nuclei.exe", @"C:\Tools\nuclei.exe", false) != "stop")
			failures.Add("browser nuclei");
		if (Decide("explorer", "nuclei.exe", @"C:\Tools\nuclei.exe", false) != "allow")
			failures.Add("explorer nuclei");
		if (Decide("chrome", "nmap", @"C:\Program Files\Nmap\nmap.exe", false) != "stop")
			failures.Add("browser nmap in program files");
		if (Decide("explorer", "mimikatz", @"C:\Users\a\AppData\Local\Temp\mimikatz.exe", false) != "allow")
			failures.Add("explorer tool");
		if (Decide("chrome", "nc", @"C:\Tools\nc.exe", false) != "allow")
			failures.Add("bare nc");
		if (Decide("chrome", "nuclei", @"C:\Tools\nuclei.exe", true) != "allow")
			failures.Add("allowed tool");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}
}

static class ClipRules
{
	public static bool Swap(string previous, string next, int elapsedMs)
	{
		if (elapsedMs < 0 || elapsedMs > 1000) return false;
		var left = previous.Trim();
		var right = next.Trim();
		if (left.Length == 0 || right.Equals(left, StringComparison.Ordinal)) return false;
		var family = Family(left);
		return family.Length > 0 && family == Family(right);
	}

	public static string Family(string text)
	{
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length == 42 && Hex(text, 2))
			return "eth";
		if (text.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) && text.Length is >= 26 and <= 90 && Bech32(text, 3))
			return "btc";
		if (text.Length is >= 26 and <= 35 && text[0] is '1' or '3' && Base58(text, 1))
			return "btc";
		if (text.StartsWith("ltc1", StringComparison.OrdinalIgnoreCase) && text.Length is >= 26 and <= 90 && Bech32(text, 4))
			return "ltc";
		if (text.Length is >= 26 and <= 35 && text[0] is 'L' or 'M' && Base58(text, 1))
			return "ltc";
		return "";
	}

	static bool Hex(string text, int start)
	{
		for (var i = start; i < text.Length; i++)
		{
			var c = text[i];
			var hex = c is >= '0' and <= '9' || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F';
			if (!hex) return false;
		}
		return true;
	}

	static bool Base58(string text, int start)
	{
		for (var i = start; i < text.Length; i++)
		{
			var c = text[i];
			var ok = c is >= '1' and <= '9' || c is >= 'A' and <= 'H' || c is >= 'J' and <= 'N' || c is >= 'P' and <= 'Z' || c is >= 'a' and <= 'k' || c is >= 'm' and <= 'z';
			if (!ok) return false;
		}
		return true;
	}

	static bool Bech32(string text, int start)
	{
		for (var i = start; i < text.Length; i++)
		{
			var c = char.ToLowerInvariant(text[i]);
			var ok = c is >= '0' and <= '9' || c is 'a' or 'c' or 'd' or 'e' or 'f' or 'g' or 'h' or 'j' or 'k' or 'l' or 'm' or 'n' or 'p' or 'q' or 'r' or 's' or 't' or 'u' or 'v' or 'w' or 'x' or 'y' or 'z';
			if (!ok) return false;
		}
		return true;
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		const string EthA = "0x1111111111111111111111111111111111111111";
		const string EthB = "0x2222222222222222222222222222222222222222";
		const string BtcA = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";
		const string BtcB = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
		if (!Swap(EthA, EthB, 400)) failures.Add("fast eth swap");
		if (Swap(EthA, EthB, 5000)) failures.Add("slow eth swap");
		if (Swap(EthA, BtcA, 200)) failures.Add("cross family");
		if (Swap(EthA, EthA, 200)) failures.Add("same address");
		if (Swap("hello", EthB, 200)) failures.Add("plain text");
		if (Family(BtcA) != "btc" || Family(BtcB) != "btc") failures.Add("btc family");
		if (Swap(BtcA, BtcB, 300) != true) failures.Add("fast btc swap");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}
}

static class LaunchList
{
	public static bool StopEnabled { get; private set; } = true;

	public static HashSet<string> Allow()
	{
		var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		StopEnabled = true;
		try
		{
			var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");
			var allowPath = Path.Combine(dir, "launch-allow.txt");
			if (File.Exists(allowPath))
			{
				foreach (var line in File.ReadAllLines(allowPath))
				{
					var name = LaunchRules.Bare(line);
					if (name.Length > 0 && !name.StartsWith('#')) allow.Add(name);
				}
			}
			var stopPath = Path.Combine(dir, "launch-stop.txt");
			if (File.Exists(stopPath) && File.ReadAllText(stopPath).Trim() == "0")
				StopEnabled = false;
		}
		catch { }
		return allow;
	}
}

sealed class LaunchGuard
{
	readonly SessionStore _store;
	readonly LocalJudge _judge;
	readonly object _look = new();
	readonly Dictionary<int, string> _paths = [];
	readonly HashSet<int> _announced = [];
	readonly int _self = Environment.ProcessId;
	string _launchText = "";
	string _flowText = "";
	DateTime _launchUntil;
	DateTime _flowUntil;
	CancellationTokenSource? _cts;

	public event Action<string>? Alerted;

	public LaunchGuard(SessionStore store, LocalJudge judge)
	{
		_store = store;
		_judge = judge;
	}

	public void Start()
	{
		if (_cts != null) return;
		_cts = new CancellationTokenSource();
		var ct = _cts.Token;
		Task.Run(() =>
		{
			while (!ct.IsCancellationRequested)
			{
				try { lock (_look) Look(); }
				catch (Exception ex) { _store.Note("launch watch: " + ex.Message); }
				if (ct.WaitHandle.WaitOne(400)) break;
			}
		}, ct);
	}

	public void Stop()
	{
		var cts = _cts;
		_cts = null;
		try { cts?.Cancel(); } catch { }
	}

	void Look()
	{
		var rows = ProcList.Snapshot();
		var byPid = new Dictionary<int, ProcList.Row>(rows.Count);
		foreach (var row in rows) byPid[row.Pid] = row;
		var live = new HashSet<int>(byPid.Keys);
		Forget(live);

		var allow = LaunchList.Allow();
		var stopOn = LaunchList.StopEnabled;
		string? launch = null;
		var handled = new HashSet<int>();
		foreach (var row in rows)
		{
			if (row.Pid <= 4 || row.Pid == _self) continue;
			if (!byPid.TryGetValue(row.Parent, out var parent)) continue;
			if (!LaunchRules.IsBrowser(parent.Name)) continue;
			var path = Image(row.Pid);
			var act = LaunchRules.Decide(parent.Name, row.Name, path, allow.Contains(LaunchRules.Bare(row.Name)));
			if (act == "allow") continue;
			handled.Add(row.Pid);
			var stopped = false;
			var denied = false;
			if (act == "stop" && stopOn)
			{
				try
				{
					using var proc = Process.GetProcessById(row.Pid);
					proc.Kill(true);
					stopped = true;
				}
				catch (InvalidOperationException) { stopped = true; }
				catch { denied = true; }
			}
			var line = LaunchLine(LaunchRules.Bare(row.Name), LaunchRules.Bare(parent.Name), act, stopped, denied, stopOn);
			launch ??= line;
			Say(row.Pid, line);
			if (act == "watch")
				_judge.Notice("launch", LaunchRules.Bare(row.Name), line);
		}

		string? flow = null;
		try
		{
			var table = TcpTable.Snapshot();
			var listen = new HashSet<int>();
			foreach (var row in table)
				if (row.State == 2 && row.LocalPort > 0) listen.Add(row.LocalPort);
			foreach (var row in table)
			{
				if (row.State != 5 || row.Pid <= 4 || row.Pid == _self) continue;
				if (listen.Contains(row.LocalPort) || Skip(row.Remote)) continue;
				if (handled.Contains(row.Pid) || !byPid.TryGetValue(row.Pid, out var owner)) continue;
				var where = LaunchRules.Drop(owner.Name, Image(row.Pid));
				if (where == null) continue;
				var line = LaunchRules.Bare(owner.Name) + " in " + where + " connected to " + row.Remote + ".";
				flow ??= line;
				Say(row.Pid, line);
				_judge.Notice("flow", LaunchRules.Bare(owner.Name), line);
			}
		}
		catch { }

		if (launch != null)
		{
			_launchText = launch;
			_launchUntil = DateTime.UtcNow.AddMinutes(1);
		}
		if (flow != null)
		{
			_flowText = flow;
			_flowUntil = DateTime.UtcNow.AddMinutes(1);
		}
		_store.SetLaunchAlert(DateTime.UtcNow < _launchUntil ? _launchText : "");
		_store.SetFlowAlert(DateTime.UtcNow < _flowUntil ? _flowText : "");
	}

	static string LaunchLine(string name, string parent, string act, bool stopped, bool denied, bool stopOn)
	{
		if (act == "watch")
			return name + " was started by " + parent + " from Downloads. It was left running.";
		if (!stopOn)
			return name + " was started by " + parent + ". Stopping is off, so it was left running.";
		if (denied)
			return name + " was started by " + parent + ". Run GUARDEX as admin to stop it.";
		if (stopped)
			return name + " was started by " + parent + ". Stopping it.";
		return name + " was started by " + parent + ".";
	}

	void Say(int pid, string line)
	{
		if (!_announced.Add(pid)) return;
		_store.Note(line);
		Alerted?.Invoke(line);
	}

	string Image(int pid)
	{
		if (_paths.TryGetValue(pid, out var path)) return path;
		path = ProcList.Image(pid);
		_paths[pid] = path;
		return path;
	}

	void Forget(HashSet<int> live)
	{
		foreach (var pid in _paths.Keys.Where(pid => !live.Contains(pid)).ToList())
			_paths.Remove(pid);
		foreach (var pid in _announced.Where(pid => !live.Contains(pid)).ToList())
			_announced.Remove(pid);
	}

	static bool Skip(string ip)
	{
		if (ip is "0.0.0.0" or "127.0.0.1" or "::1" or "::") return true;
		return IPAddress.TryParse(ip, out var addr) && IPAddress.IsLoopback(addr);
	}
}

sealed class ClipGuard
{
	readonly SessionStore _store;
	readonly object _look = new();
	string _prev = "";
	DateTime _prevUtc;
	string _held = "";
	DateTime _heldUtc;
	string _text = "";
	DateTime _until;
	DateTime _said;
	System.Windows.Threading.DispatcherTimer? _timer;

	public event Action<string>? Alerted;

	public ClipGuard(SessionStore store) => _store = store;

	public void Hook()
	{
		if (_timer != null) return;
		_timer = new System.Windows.Threading.DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(250)
		};
		_timer.Tick += (_, _) =>
		{
			try { Look(); }
			catch { }
		};
		_timer.Start();
	}

	public void Stop() => _timer?.Stop();

	void Look()
	{
		if (!System.Windows.Clipboard.ContainsText()) return;
		var text = System.Windows.Clipboard.GetText().Trim();
		if (text.Length == 0) return;
		lock (_look)
		{
			if (text == _held && DateTime.UtcNow - _heldUtc < TimeSpan.FromSeconds(2))
			{
				_prev = text;
				_prevUtc = DateTime.UtcNow;
				Publish();
				return;
			}
			if (text == _prev)
			{
				Publish();
				return;
			}
			var elapsed = _prev.Length == 0 ? int.MaxValue : (int)(DateTime.UtcNow - _prevUtc).TotalMilliseconds;
			if (ClipRules.Swap(_prev, text, elapsed))
			{
				var keep = _prev;
				System.Windows.Clipboard.SetText(keep);
				_held = keep;
				_heldUtc = DateTime.UtcNow;
				_text = "A clipboard wallet address was swapped. The earlier address was put back.";
				_until = DateTime.UtcNow.AddMinutes(1);
				if (DateTime.UtcNow - _said > TimeSpan.FromMinutes(1))
				{
					_said = DateTime.UtcNow;
					_store.Note(_text);
					Alerted?.Invoke(_text);
				}
				Publish();
				return;
			}
			_prev = text;
			_prevUtc = DateTime.UtcNow;
			Publish();
		}
	}

	void Publish() => _store.SetClipAlert(DateTime.UtcNow < _until ? _text : "");
}

static class ProcList
{
	const uint SnapProcess = 2;

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern bool Process32First(IntPtr snap, ref Entry entry);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern bool Process32Next(IntPtr snap, ref Entry entry);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

	[DllImport("kernel32.dll")]
	static extern bool CloseHandle(IntPtr handle);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct Entry
	{
		public uint dwSize;
		public uint cntUsage;
		public uint th32ProcessID;
		public IntPtr th32DefaultHeapID;
		public uint th32ModuleID;
		public uint cntThreads;
		public uint th32ParentProcessID;
		public int pcPriClassBase;
		public uint dwFlags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szExeFile;
	}

	public readonly record struct Row(int Pid, int Parent, string Name);

	public static List<Row> Snapshot()
	{
		var list = new List<Row>();
		var snap = CreateToolhelp32Snapshot(SnapProcess, 0);
		if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return list;
		try
		{
			var entry = new Entry { dwSize = (uint)Marshal.SizeOf<Entry>() };
			if (!Process32First(snap, ref entry)) return list;
			do
			{
				list.Add(new Row((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
				entry.dwSize = (uint)Marshal.SizeOf<Entry>();
			}
			while (Process32Next(snap, ref entry));
			return list;
		}
		finally
		{
			CloseHandle(snap);
		}
	}

	public static string SelfCheck()
	{
		var rows = Snapshot();
		if (rows.Count < 5) return "process list was " + rows.Count;
		if (!rows.Any(row => row.Pid == Environment.ProcessId)) return "own process missing";
		return "PASS";
	}

	public static string Image(int pid)
	{
		if (pid <= 0) return "";
		var handle = OpenProcess(0x1000, false, pid);
		if (handle == IntPtr.Zero) return "";
		try
		{
			var buffer = new StringBuilder(1024);
			var size = buffer.Capacity;
			return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : "";
		}
		finally
		{
			CloseHandle(handle);
		}
	}
}

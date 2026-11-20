using System.IO;
using Microsoft.Win32;

namespace Guardex;

// Startup folders, run keys, services, tasks, and the other start locations
// Autoruns and the common Sysmon configs treat as high signal.
// This sweep reports. It does not delete or disable anything.
static class PostureRules
{
	public static (string Severity, string Why)? Decide(string kind, string name, string command)
	{
		if (kind == "Ifeo") return ("block", "A debugger is set on this program.");
		if (kind == "AppInit") return ("block", "An AppInit DLL loads into other programs.");
		if (kind == "AppCert") return ("block", "An AppCert DLL is set.");
		if (kind == "Winlogon" && name == "Shell")
		{
			if (command.Contains("explorer.exe", StringComparison.OrdinalIgnoreCase)) return null;
			return ("block", "The logon shell is not Explorer.");
		}
		if (kind == "Winlogon" && name == "Userinit")
		{
			if (!ExtraUserinit(command)) return null;
			return Rate(command) ?? ("watch", "Userinit starts more than the Windows default.");
		}
		if (kind == "BootExecute")
		{
			if (IsDefaultBoot(command)) return null;
			return Rate(command) ?? ("watch", "Boot execute is not the Windows default.");
		}
		if (kind == "Lsa")
			return Rate(command) ?? ("watch", "This LSA package is not the Windows default.");
		if (kind == "LogonScript")
			return Rate(command) ?? ("watch", "A logon script is set.");
		if (kind == "Policy" || kind == "Wmi")
		{
			var rated = Rate(command);
			if (rated != null) return rated;
			if (kind == "Wmi") return ("watch", "WMI will run this command.");
			return ("watch", "A group policy script is present.");
		}

		var full = RemoteCatalog.ExePath(command);
		var product = full == null ? null : RemoteCatalog.Identify(Path.GetFileName(full), File.Exists(full) ? full : null);
		if (product != null && !RemoteCatalog.IsAllowed(product.Id))
			return ("block", product.Name + " is set to start and is not on the allow list.");
		return Rate(command);
	}

	public static (string Severity, string Why)? Rate(string command)
	{
		var lower = (command ?? "").ToLowerInvariant();
		if (lower.Length == 0) return null;
		if (Encoded(lower)) return ("block", "Encoded or download-and-run command.");
		if (HasSegment(lower, "temp")) return ("block", "Points at a temp folder.");
		if (ScriptHost(lower)) return ("watch", "A script host or loader in a start location.");
		if (HasSegment(lower, "downloads")) return ("watch", "Points at Downloads.");
		return null;
	}

	static bool ExtraUserinit(string command)
	{
		var parts = command.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (var part in parts)
		{
			if (part.Contains("userinit.exe", StringComparison.OrdinalIgnoreCase)) continue;
			if (part.Length > 0) return true;
		}
		return false;
	}

	static bool IsDefaultBoot(string command)
	{
		var text = command.Trim().ToLowerInvariant();
		return text is "autocheck autochk *" or "autocheck autochk * ";
	}

	static bool Encoded(string lower)
	{
		if (lower.Contains("frombase64string") || lower.Contains("downloadstring") || lower.Contains("invoke-expression") || lower.Contains("iex("))
			return true;
		return HasSwitch(lower, "-encodedcommand") || HasSwitch(lower, "-enc");
	}

	static bool ScriptHost(string lower)
	{
		var powershell = HasFile(lower, "powershell.exe") || HasFile(lower, "pwsh.exe");
		var script = HasFile(lower, "mshta.exe") || HasFile(lower, "wscript.exe") || HasFile(lower, "cscript.exe");
		var cmd = HasFile(lower, "cmd.exe") && (HasSwitch(lower, "/c") || HasSwitch(lower, "/k"));
		var dll = HasFile(lower, "rundll32.exe") || HasFile(lower, "regsvr32.exe") || HasFile(lower, "msbuild.exe") || HasFile(lower, "installutil.exe") || HasFile(lower, "certutil.exe") || HasFile(lower, "bitsadmin.exe");
		if (!powershell && !script && !cmd && !dll) return false;
		if (OutsideTrusted(lower)) return true;
		if (dll || cmd) return false;
		return !HasTrustedPath(lower);
	}

	static bool OutsideTrusted(string lower)
	{
		foreach (var token in Tokens(lower))
		{
			if (LooksLikePath(token) && !Trusted(token)) return true;
		}
		return false;
	}

	static bool HasTrustedPath(string lower)
	{
		foreach (var token in Tokens(lower))
		{
			if (LooksLikePath(token) && Trusted(token)) return true;
		}
		return false;
	}

	static bool LooksLikePath(string token) =>
		token.Contains(@":\", StringComparison.Ordinal) || token.Contains('%') || token.StartsWith(@"\\", StringComparison.Ordinal);

	static bool Trusted(string token) =>
		token.Contains("%windir%") || token.Contains("%systemroot%") || token.Contains(@"\windows\") || token.Contains(@"\program files") || token.Contains(@"\microsoft\");

	static IEnumerable<string> Tokens(string lower) =>
		lower.Split([' ', '\t', '"', '\'', '|'], StringSplitOptions.RemoveEmptyEntries);

	static bool HasFile(string lower, string file)
	{
		var i = 0;
		while ((i = lower.IndexOf(file, i, StringComparison.Ordinal)) >= 0)
		{
			var before = i == 0 ? '\0' : lower[i - 1];
			var after = i + file.Length < lower.Length ? lower[i + file.Length] : '\0';
			var boundaryBefore = before is '\0' or '\\' or '"' or ' ' or '\'';
			var boundaryAfter = after is '\0' or '"' or ' ' or '\'' || !char.IsLetterOrDigit(after);
			if (boundaryBefore && boundaryAfter) return true;
			i++;
		}
		return false;
	}

	static bool HasSwitch(string lower, string name)
	{
		var i = 0;
		while ((i = lower.IndexOf(name, i, StringComparison.Ordinal)) >= 0)
		{
			var before = i == 0 ? ' ' : lower[i - 1];
			var after = i + name.Length < lower.Length ? lower[i + name.Length] : ' ';
			if ((before is ' ' or '"' or '\'' or '\t') && !char.IsLetter(after)) return true;
			i++;
		}
		return false;
	}

	static bool HasSegment(string lower, string name)
	{
		var needle = "\\" + name;
		var i = 0;
		while ((i = lower.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
		{
			var after = i + needle.Length;
			if (after >= lower.Length) return true;
			var c = lower[after];
			if (c is '\\' or '"' or ' ' or '\'') return true;
			i++;
		}
		return false;
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		var temp = Rate(@"C:\Users\a\AppData\Local\Temp\a.exe");
		if (temp?.Severity != "block") failures.Add("temp was " + temp?.Severity);
		var enc = Rate(@"powershell.exe -enc AAA");
		if (enc?.Severity != "block") failures.Add("encoded was " + enc?.Severity);
		var encoding = Rate(@"powershell.exe -Encoding UTF8 -File a.ps1");
		if (encoding?.Severity == "block") failures.Add("encoding flag looked encoded");
		if (encoding?.Severity != "watch") failures.Add("powershell file was " + encoding?.Severity);
		if (Rate(@"C:\Windows\System32\svchost.exe -k netsvcs") != null) failures.Add("svchost flagged");
		if (Rate(@"C:\Program Files\Google\Chrome\Application\chrome.exe") != null) failures.Add("chrome flagged");
		if (Rate(@"C:\Users\a\AppData\Local\Discord\app.exe") != null) failures.Add("appdata exe flagged");
		var download = Rate(@"C:\Users\a\Downloads\setup.exe");
		if (download?.Severity != "watch") failures.Add("downloads was " + download?.Severity);
		var host = Rate(@"C:\Windows\System32\wscript.exe C:\Users\a\script.vbs");
		if (host?.Severity != "watch") failures.Add("wscript was " + host?.Severity);
		if (Rate(@"%windir%\system32\rundll32.exe %windir%\system32\AppxDeploymentClient.dll,ScheduleAppInstallerBackgroundUpdate") != null)
			failures.Add("windows rundll32 flagged");
		if (Rate(@"c:\windows\system32\cmd.exe /q /c del /q c:\users\a\appdata\local\microsoft\onedrive\update\onedrivesetup.exe") != null)
			failures.Add("microsoft cleanup flagged");
		if (Rate(@"c:\windows\system32\cmd.exe /c c:\users\public\run.bat")?.Severity != "watch")
			failures.Add("public cmd");
		if (Decide("Winlogon", "Shell", "explorer.exe") != null) failures.Add("explorer shell flagged");
		if (Decide("Winlogon", "Shell", "cmd.exe")?.Severity != "block") failures.Add("replaced shell");
		if (Decide("Winlogon", "Userinit", @"C:\Windows\system32\userinit.exe,") != null) failures.Add("default userinit");
		if (Decide("BootExecute", "BootExecute", "autocheck autochk *") != null) failures.Add("default boot");
		if (Decide("Ifeo", "sethc.exe", "cmd.exe")?.Severity != "block") failures.Add("debugger");
		if (!RemoteCatalog.IsAllowed("screenconnect"))
		{
			var planted = Decide("Service", "ScreenConnect", @"C:\Program Files\ScreenConnect\ScreenConnect.ClientService.exe");
			if (planted?.Severity != "block") failures.Add("screenconnect service");
		}
		var kept = KeepSeen(["Run|HKCU\\a|One", "Run|HKCU\\a|Two"], ["Run|HKCU\\a|Two", "Run|HKCU\\a|Three"], true);
		if (kept.Count != 1 || kept[0] != "Run|HKCU\\a|Two") failures.Add("new run key entered the baseline");
		var seeded = KeepSeen([], ["Run|HKCU\\a|One"], false);
		if (seeded.Count != 1 || seeded[0] != "Run|HKCU\\a|One") failures.Add("first sweep did not seed");
		if (KeepSeen(seeded, ["Run|HKCU\\a|One", "Run|HKCU\\a|New"], true).Contains("Run|HKCU\\a|New"))
			failures.Add("second sweep absorbed the new key");
		if (KeepSeen(seeded, [], true).Count != 0) failures.Add("removed key stayed in the baseline");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	public static List<string> KeepSeen(IEnumerable<string> previous, IEnumerable<string> now, bool seeded)
	{
		if (!seeded)
			return now.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
		var live = new HashSet<string>(now, StringComparer.OrdinalIgnoreCase);
		return previous.Where(id => live.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
	}
}

sealed class PostureWatch
{
	readonly SessionStore _store;
	readonly LocalJudge _judge;
	readonly HashSet<string> _announced = new(StringComparer.Ordinal);
	CancellationTokenSource? _cts;

	public event Action<string>? Alerted;

	public PostureWatch(SessionStore store, LocalJudge judge)
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
				try { Sweep(ct); }
				catch (Exception ex) { _store.SetFindings(_store.CopyFindings(), "Host check failed. " + ex.Message); }
				if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(45))) break;
			}
		}, ct);
	}

	public void Stop()
	{
		var cts = _cts;
		_cts = null;
		try { cts?.Cancel(); } catch { }
	}

	public static IReadOnlyList<string> Inspect()
	{
		var sweep = Collect(false);
		var lines = new List<string> { sweep.Status };
		if (sweep.Findings.Count == 0) lines.Add("No start-item findings.");
		foreach (var finding in sweep.Findings)
			lines.Add(finding.Severity + "\t" + finding.Kind + "\t" + finding.Name + "\t" + finding.Why + "\t" + finding.Detail);
		return lines;
	}

	void Sweep(CancellationToken ct)
	{
		var sweep = Collect(true);
		var prior = _store.CopyFindings().ToDictionary(f => f.Id, StringComparer.Ordinal);
		foreach (var finding in sweep.Findings)
		{
			if (!prior.TryGetValue(finding.Id, out var old) || old.Judge.Length == 0) continue;
			finding.Judge = old.Judge;
			finding.Reason = old.Reason;
			if (old.Severity == "block" && finding.Severity != "block" && old.Judge == "malicious")
				finding.Severity = "block";
		}
		_store.SetFindings(sweep.Findings, sweep.Status);
		foreach (var finding in sweep.Findings.Where(f => f.Severity == "watch" && f.Judge.Length == 0 && !prior.ContainsKey(f.Id)).Take(3))
			_judge.Notice("host", finding.Id, finding.Name + " showed up in startup. " + finding.Why);
		var batch = sweep.Findings.Where(f => f.Severity == "watch" && f.Judge.Length == 0).Take(6).ToList();
		if (batch.Count > 0 && _judge.Ready)
			_ = Task.Run(() => _judge.ReviewHostAsync(batch, ct));
		var fresh = sweep.Findings.Where(f => f.Severity == "block" && _announced.Add(f.Id)).ToList();
		if (fresh.Count == 1) Alerted?.Invoke(fresh[0].Name + ". " + fresh[0].Why);
		else if (fresh.Count > 1) Alerted?.Invoke(fresh.Count + " start items need a look. First: " + fresh[0].Name + ".");
	}

	static SweepResult Collect(bool saveBaseline)
	{
		var items = new List<RawItem>();
		var errors = new List<string>();
		var counts = new Counts();
		Try(errors, "startup", () => ReadStartup(items, counts));
		Try(errors, "run keys", () => ReadRuns(items, counts));
		Try(errors, "winlogon", () => ReadWinlogon(items));
		Try(errors, "image hijacks", () => ReadIfeo(items));
		Try(errors, "appinit", () => ReadAppInit(items));
		Try(errors, "boot", () => ReadBoot(items));
		Try(errors, "lsa", () => ReadLsa(items));
		Try(errors, "logon script", () => ReadLogonScript(items));
		Try(errors, "policy scripts", () => ReadPolicy(items));
		Try(errors, "services", () => ReadServices(items, counts));
		Try(errors, "tasks", () => ReadTasks(items, counts));
		Try(errors, "wmi", () => ReadWmi(items, counts));

		var path = BaselinePath();
		var existed = File.Exists(path);
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (existed)
		{
			try { previous.UnionWith(File.ReadAllLines(path)); }
			catch { existed = false; }
		}
		var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var findings = new List<Finding>();
		foreach (var item in items)
		{
			now.Add(item.Id);
			var decision = PostureRules.Decide(item.Kind, item.Name, item.Command);
			var isNew = existed && !previous.Contains(item.Id);
			if (decision == null && isNew && item.Kind is "Startup" or "Run" or "RunOnce")
				decision = ("watch", "Appeared after the baseline.");
			if (decision == null) continue;
			findings.Add(new Finding
			{
				Id = item.Id,
				Kind = item.Kind,
				Name = item.Name,
				Location = item.Location,
				Detail = Clip(item.Command, 500),
				Severity = decision.Value.Severity,
				Why = decision.Value.Why,
				IsNew = isNew
			});
		}
		if (saveBaseline && errors.Count == 0)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllLines(path, PostureRules.KeepSeen(previous, now, existed));
			}
			catch (Exception ex) { errors.Add("baseline: " + ex.Message); }
		}
		findings = findings
			.OrderByDescending(f => f.Severity == "block")
			.ThenBy(f => f.Kind)
			.ThenBy(f => f.Name)
			.Take(80)
			.ToList();
		var status = "Scanned " + counts.Startup + " startup items, " + counts.Run + " run keys, " + counts.Services + " services, " + counts.Tasks + " tasks, " + counts.Wmi + " WMI consumers";
		if (errors.Count > 0) status += " · " + string.Join("; ", errors.Take(3));
		return new SweepResult { Findings = findings, Status = status };
	}

	static void Try(List<string> errors, string name, Action action)
	{
		try { action(); }
		catch (Exception ex) { errors.Add(name + ": " + ex.Message); }
	}

	static void ReadStartup(List<RawItem> items, Counts counts)
	{
		var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			Environment.GetFolderPath(Environment.SpecialFolder.Startup),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup")
		};
		foreach (var folder in folders)
		{
			if (!Directory.Exists(folder)) continue;
			foreach (var file in Directory.EnumerateFiles(folder))
			{
				counts.Startup++;
				items.Add(new RawItem("Startup", Path.GetFileName(file), folder, ResolveLink(file)));
			}
		}
	}

	static void ReadRuns(List<RawItem> items, Counts counts)
	{
		ReadRun(items, counts, RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run");
		ReadRun(items, counts, RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce");
		ReadRun(items, counts, RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "Run");
		ReadRun(items, counts, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run");
		ReadRun(items, counts, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce");
		ReadRun(items, counts, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "Run");
		ReadRun(items, counts, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run");
		ReadRun(items, counts, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce");
	}

	static void ReadRun(List<RawItem> items, Counts counts, RegistryHive hive, RegistryView view, string sub, string kind)
	{
		using var root = RegistryKey.OpenBaseKey(hive, view);
		using var key = root.OpenSubKey(sub);
		if (key == null) return;
		var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
		foreach (var name in key.GetValueNames())
		{
			var data = ReadValue(key, name);
			if (data.Length == 0) continue;
			counts.Run++;
			items.Add(new RawItem(kind, name, hiveName + "\\" + sub, data));
		}
	}

	static void ReadWinlogon(List<RawItem> items)
	{
		using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
		using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
		if (key == null) return;
		foreach (var name in new[] { "Shell", "Userinit" })
		{
			var data = ReadValue(key, name);
			if (data.Length == 0) continue;
			items.Add(new RawItem("Winlogon", name, @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", data));
		}
	}

	static void ReadIfeo(List<RawItem> items)
	{
		foreach (var sub in new[]
		{
			@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
			@"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
		})
		{
			using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
			using var key = root.OpenSubKey(sub);
			if (key == null) continue;
			foreach (var name in key.GetSubKeyNames())
			{
				using var image = key.OpenSubKey(name);
				var dbg = image?.GetValue("Debugger")?.ToString() ?? "";
				if (dbg.Length == 0) continue;
				items.Add(new RawItem("Ifeo", name, "HKLM\\" + sub, dbg));
			}
		}
	}

	static void ReadAppInit(List<RawItem> items)
	{
		foreach (var sub in new[]
		{
			@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
			@"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows"
		})
		{
			using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
			using var key = root.OpenSubKey(sub);
			var dlls = key?.GetValue("AppInit_DLLs")?.ToString() ?? "";
			if (dlls.Trim().Length == 0) continue;
			items.Add(new RawItem("AppInit", "AppInit_DLLs", "HKLM\\" + sub, dlls));
		}
		using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls");
		if (session == null) return;
		foreach (var name in session.GetValueNames())
		{
			var data = ReadValue(session, name);
			if (data.Length == 0) continue;
			items.Add(new RawItem("AppCert", name, @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls", data));
		}
	}

	static void ReadBoot(List<RawItem> items)
	{
		using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
		if (key == null) return;
		var text = ReadValue(key, "BootExecute");
		if (text.Length == 0) return;
		items.Add(new RawItem("BootExecute", "BootExecute", @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", text));
	}

	static void ReadLsa(List<RawItem> items)
	{
		using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Lsa");
		if (key == null) return;
		CheckLsa(items, key, "Authentication Packages", ["msv1_0"]);
		CheckLsa(items, key, "Security Packages", ["", "kerberos", "schannel", "wdigest", "tspkg", "pku2u", "cloudap"]);
		CheckLsa(items, key, "Notification Packages", ["", "scecli"]);
	}

	static void CheckLsa(List<RawItem> items, RegistryKey key, string name, string[] defaults)
	{
		var raw = key.GetValue(name);
		var parts = raw switch
		{
			string[] many => many,
			string one => new[] { one },
			_ => Array.Empty<string>()
		};
		var odd = parts
			.Select(p => (p ?? "").Trim().Trim('"'))
			.Where(p => p.Length > 0 && !defaults.Contains(p, StringComparer.OrdinalIgnoreCase))
			.ToList();
		if (odd.Count == 0) return;
		items.Add(new RawItem("Lsa", name, @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa", string.Join(" ", odd)));
	}

	static void ReadLogonScript(List<RawItem> items)
	{
		using var key = Registry.CurrentUser.OpenSubKey("Environment");
		var script = key?.GetValue("UserInitMprLogonScript")?.ToString() ?? "";
		if (script.Trim().Length == 0) return;
		items.Add(new RawItem("LogonScript", "UserInitMprLogonScript", @"HKCU\Environment", script));
	}

	static void ReadPolicy(List<RawItem> items)
	{
		var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		foreach (var relative in new[]
		{
			@"System32\GroupPolicy\Machine\Scripts",
			@"System32\GroupPolicy\User\Scripts"
		})
		{
			var folder = Path.Combine(windows, relative);
			if (!Directory.Exists(folder)) continue;
			foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
				items.Add(new RawItem("Policy", Path.GetFileName(file), Path.GetDirectoryName(file) ?? folder, file));
		}
	}

	static void ReadServices(List<RawItem> items, Counts counts)
	{
		using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
		if (key == null) return;
		foreach (var name in key.GetSubKeyNames())
		{
			using var service = key.OpenSubKey(name);
			if (service == null) continue;
			var start = service.GetValue("Start");
			var mode = start is int i ? i : 4;
			if (mode > 2) continue;
			var image = service.GetValue("ImagePath")?.ToString() ?? "";
			if (image.Length == 0) continue;
			counts.Services++;
			image = Environment.ExpandEnvironmentVariables(image.Replace("%SystemRoot%", Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase));
			items.Add(new RawItem("Service", name, @"HKLM\SYSTEM\CurrentControlSet\Services", image));
		}
	}

	static void ReadTasks(List<RawItem> items, Counts counts)
	{
		var type = Type.GetTypeFromProgID("Schedule.Service");
		if (type == null) return;
		dynamic svc = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Task scheduler is unavailable.");
		svc.Connect();
		WalkTasks(svc.GetFolder("\\"), items, counts);
	}

	static void WalkTasks(dynamic folder, List<RawItem> items, Counts counts)
	{
		dynamic tasks = folder.GetTasks(1);
		var n = Convert.ToInt32(tasks.Count);
		for (var i = 1; i <= n; i++)
		{
			try
			{
				dynamic task = tasks.Item(i);
				string name = task.Name;
				string path = task.Path;
				dynamic actions = task.Definition.Actions;
				var a = Convert.ToInt32(actions.Count);
				var commands = new List<string>();
				for (var j = 1; j <= a; j++)
				{
					try
					{
						dynamic action = actions.Item(j);
						string exe = "";
						string args = "";
						try { exe = action.Path ?? ""; } catch { }
						try { args = action.Arguments ?? ""; } catch { }
						if (exe.Length > 0) commands.Add((exe + " " + args).Trim());
					}
					catch { }
				}
				counts.Tasks++;
				if (commands.Count == 0) continue;
				items.Add(new RawItem("Task", name, path, string.Join(" | ", commands)));
			}
			catch { }
		}
		dynamic subs = folder.GetFolders(0);
		var m = Convert.ToInt32(subs.Count);
		for (var i = 1; i <= m; i++)
		{
			try { WalkTasks(subs.Item(i), items, counts); }
			catch { }
		}
	}

	static void ReadWmi(List<RawItem> items, Counts counts)
	{
		var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
		if (type == null) return;
		dynamic locator = Activator.CreateInstance(type) ?? throw new InvalidOperationException("WMI is unavailable.");
		dynamic svc = locator.ConnectServer(".", @"root\subscription");
		dynamic set = svc.ExecQuery("SELECT Name, CommandLineTemplate FROM CommandLineEventConsumer");
		foreach (dynamic row in set)
		{
			string name = row.Name is string n ? n : "";
			string command = row.CommandLineTemplate is string c ? c : "";
			if (name.Length == 0 && command.Length == 0) continue;
			counts.Wmi++;
			items.Add(new RawItem("Wmi", name.Length == 0 ? "consumer" : name, @"root\subscription", command));
		}
	}

	static string ResolveLink(string path)
	{
		if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
		try
		{
			var type = Type.GetTypeFromProgID("WScript.Shell");
			if (type == null) return path;
			dynamic shell = Activator.CreateInstance(type)!;
			dynamic link = shell.CreateShortcut(path);
			string target = link.TargetPath ?? "";
			string args = link.Arguments ?? "";
			if (target.Length == 0) return path;
			return (target + " " + args).Trim();
		}
		catch
		{
			return path;
		}
	}

	static string ReadValue(RegistryKey key, string name)
	{
		var value = key.GetValue(name);
		return value switch
		{
			string[] many => string.Join(" ", many),
			null => "",
			_ => value.ToString() ?? ""
		};
	}

	static string BaselinePath()
	{
		var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");
		return Path.Combine(dir, "posture-seen.txt");
	}

	static string Clip(string text, int n) => text.Length <= n ? text : text[..n];

	sealed class RawItem
	{
		public RawItem(string kind, string name, string location, string command)
		{
			Kind = kind;
			Name = name;
			Location = location;
			Command = command;
		}

		public string Kind { get; }
		public string Name { get; }
		public string Location { get; }
		public string Command { get; }
		public string Id => Kind + "|" + Location + "|" + Name;
	}

	sealed class Counts
	{
		public int Startup;
		public int Run;
		public int Services;
		public int Tasks;
		public int Wmi;
	}

	sealed class SweepResult
	{
		public List<Finding> Findings { get; init; } = [];
		public string Status { get; init; } = "";
	}
}

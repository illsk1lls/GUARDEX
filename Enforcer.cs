using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace Guardex;

// Reflex blocks. The model can step a soft block down. It cannot lift a hard signal.
sealed class Enforcer
{
	public const string RuleGroup = "GUARDEX";
	public const string OutboundKey = "all outbound";
	const string Prefix = "GUARDEX ";
	readonly SessionStore _store;
	readonly object _gate = new();
	readonly Dictionary<string, Hold> _holds = new(StringComparer.Ordinal);
	readonly Dictionary<string, (int Count, DateTime UntilUtc)> _strikes = new(StringComparer.Ordinal);
	readonly bool _admin;
	bool _want;
	int _cutting;
	CancellationTokenSource? _cts;

	public Enforcer(SessionStore store)
	{
		_store = store;
		try
		{
			_admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch
		{
			_admin = false;
		}
		var off = string.Equals(Environment.GetEnvironmentVariable("GUARDEX_ENFORCE"), "0", StringComparison.Ordinal);
		_want = _admin && !off;
	}

	public void Start()
	{
		if (_cts != null) return;
		_cts = new CancellationTokenSource();
		if (_admin)
		{
			try { DropOrphans(); } catch { }
		}
		var ct = _cts.Token;
		Task.Run(() =>
		{
			while (!ct.IsCancellationRequested)
			{
				try { Expire(); } catch { }
				if (ct.WaitHandle.WaitOne(15000)) break;
			}
		}, ct);
		Publish();
	}

	public void Shutdown()
	{
		try { _cts?.Cancel(); } catch { }
		_cts = null;
		if (!_admin) return;
		try { DropOrphans(); } catch { }
	}

	public void SetEnabled(bool on)
	{
		lock (_gate) _want = on;
		if (!on) RemoveApplied();
		else
		{
			ApplyAll();
			StartCutter();
		}
		Publish();
	}

	public void Release(string src) => Lift(src, "Released from the window.", false);

	public void EndLockdown() => Lift(OutboundKey, "Lockdown ended.", false);

	public bool HasOutboundHold
	{
		get
		{
			lock (_gate)
				return _holds.TryGetValue(OutboundKey, out var hold) && hold.UntilUtc > DateTime.UtcNow;
		}
	}

	public string HaltOutbound(string why)
	{
		var now = DateTime.UtcNow;
		var fresh = false;
		var apply = false;
		lock (_gate)
		{
			_holds.TryGetValue(OutboundKey, out var existing);
			fresh = existing == null || existing.UntilUtc <= now;
			var hold = existing ?? new Hold { Ip = OutboundKey, Origin = "Reflex" };
			hold.Scenario = "clipboard";
			hold.Why = why;
			hold.Strikes = 4;
			hold.Hard = true;
			hold.UntilUtc = now.AddMinutes(OutboundHoldMinutes());
			if (hold.Note.Length == 0 || fresh)
				hold.Note = !_want ? "Blocking is off. Reported only." : !_admin ? "Needs elevation to change the firewall." : "";
			_holds[OutboundKey] = hold;
			apply = _want && _admin && (fresh || !hold.Applied);
		}
		if (apply) EnsureOutboundRule();
		var applied = false;
		lock (_gate)
			applied = _holds.TryGetValue(OutboundKey, out var held) && held.Applied && held.UntilUtc > DateTime.UtcNow;
		if (_want && _admin)
		{
			try { TcpTable.CutOutbound(); } catch { }
			StartCutter();
		}
		Publish();
		return OutboundPhrase(_want, _admin, applied);
	}

	bool OutboundRuleMissing()
	{
		lock (_gate)
			return _holds.TryGetValue(OutboundKey, out var hold) && !hold.Applied && hold.UntilUtc > DateTime.UtcNow;
	}

	public void OnVerdict(Verdict verdict, bool fromModel)
	{
		if (!IPAddress.TryParse(verdict.Src, out var parsed)) return;
		if (parsed.IsIPv4MappedToIPv6) parsed = parsed.MapToIPv4();
		var ip = parsed.ToString();
		if (IsReserved(ip) || IsOwn(parsed)) return;
		var want = verdict.Action == "block" || verdict.Band == "block";
		if (fromModel && !want)
		{
			Hold? existing;
			lock (_gate) _holds.TryGetValue(ip, out existing);
			if (existing != null && !existing.Hard)
				Lift(ip, "The model stepped this source down.", false);
			return;
		}
		if (!want) return;

		var scenario = Scenario(verdict);
		var apply = false;
		var noteFeed = false;
		lock (_gate)
		{
			_holds.TryGetValue(ip, out var existing);
			var now = DateTime.UtcNow;
			var still = existing != null && existing.UntilUtc > now.AddMinutes(2);
			var firstConfirm = fromModel && existing != null && !existing.Confirmed;
			if (still && !firstConfirm) return;
			int previous;
			bool fresh;
			if (existing == null)
			{
				previous = _strikes.TryGetValue(ip, out var saved) ? Remembered(saved.Count, saved.UntilUtc, now) : 0;
				fresh = true;
			}
			else
			{
				previous = existing.Strikes;
				fresh = existing.UntilUtc <= now;
			}
			var hold = existing ?? new Hold { Ip = ip, Origin = fromModel ? "Model" : "Reflex" };
			hold.Scenario = scenario;
			hold.Why = verdict.Why;
			hold.Strikes = NextStrikes(previous, fresh, firstConfirm);
			hold.UntilUtc = now.AddMinutes(Minutes(hold.Strikes));
			_strikes[ip] = (hold.Strikes, hold.UntilUtc);
			hold.Hard = hold.Hard || verdict.Hard;
			hold.Confirmed = hold.Confirmed || fromModel;
			if (hold.Note.Length == 0)
				hold.Note = !_want ? "Blocking is off. Reported only." : !_admin ? "Needs elevation to change the firewall." : "";
			_holds[ip] = hold;
			noteFeed = existing == null || !still;
			apply = _want && _admin;
		}
		if (apply)
		{
			try
			{
				ApplyRule(ip, scenario);
				lock (_gate)
				{
					if (_holds.TryGetValue(ip, out var hold))
					{
						hold.Applied = true;
						hold.Note = "Firewall rule is in.";
					}
				}
			}
			catch (Exception ex)
			{
				lock (_gate)
				{
					if (_holds.TryGetValue(ip, out var hold))
						hold.Note = Clip(ex.Message);
				}
			}
		}
		if (noteFeed)
		{
			string until;
			lock (_gate) until = _holds.TryGetValue(ip, out var hold) ? hold.UntilUtc.ToLocalTime().ToString("HH:mm") : "";
			_store.Note(ip + " blocked until " + until + " (" + scenario + ")");
		}
		Publish();
	}

	void Expire()
	{
		List<string> gone;
		lock (_gate)
		{
			var now = DateTime.UtcNow;
			gone = _holds.Values.Where(h => h.UntilUtc <= now).Select(h => h.Ip).ToList();
		}
		foreach (var ip in gone)
			Lift(ip, "The block expired.", true);
	}

	void ApplyAll()
	{
		List<Hold> due;
		lock (_gate) due = _holds.Values.Where(h => !h.Applied && h.UntilUtc > DateTime.UtcNow).ToList();
		if (!_want || !_admin) return;
		foreach (var hold in due)
		{
			try
			{
				ApplyRule(hold.Ip, hold.Scenario);
				lock (_gate)
				{
					hold.Applied = true;
					hold.Note = "Firewall rule is in.";
				}
			}
			catch (Exception ex)
			{
				lock (_gate) hold.Note = Clip(ex.Message);
			}
		}
	}

	void RemoveApplied()
	{
		List<Hold> applied;
		lock (_gate) applied = _holds.Values.Where(h => h.Applied).ToList();
		foreach (var hold in applied)
		{
			try { if (_admin) Remove(RuleName(hold.Ip)); } catch { }
			lock (_gate)
			{
				hold.Applied = false;
				hold.Note = "Blocking is off. Reported only.";
			}
		}
	}

	void Lift(string ip, string why, bool keepStrikes)
	{
		Hold? hold;
		lock (_gate)
		{
			if (!_holds.Remove(ip, out hold)) return;
			if (!keepStrikes) _strikes.Remove(ip);
			else _strikes[ip] = (hold.Strikes, hold.UntilUtc);
		}
		if (hold.Applied && _admin)
		{
			try { Remove(RuleName(ip)); } catch { }
		}
		_store.Note(ip + " block lifted. " + why);
		Publish();
	}

	void Publish()
	{
		List<BlockRow> rows;
		string status;
		lock (_gate)
		{
			status = !_want ? "reporting" : !_admin ? "needs admin" : "blocking";
			rows = _holds.Values
				.OrderByDescending(h => h.UntilUtc)
				.Select(h => new BlockRow
				{
					Src = h.Ip,
					Origin = h.Origin,
					Scenario = h.Scenario,
					Until = h.UntilUtc.ToLocalTime().ToString("HH:mm"),
					State = h.Applied ? "Blocking" : status == "needs admin" ? "Needs admin" : "Reported",
					Why = h.Why,
					Note = h.Note
				})
				.ToList();
		}
		_store.SetEnforce(status);
		_store.SetBlocks(rows);
	}

	static int Minutes(int strikes) => strikes switch
	{
		<= 1 => 10,
		2 => 30,
		3 => 120,
		_ => 720
	};

	public static int OutboundHoldMinutes() => 12 * 60;

	public static string OutboundPhrase(bool want, bool admin, bool applied)
	{
		if (!want) return "Blocking is off, so traffic was not stopped.";
		if (!admin) return "GUARDEX needs elevation, so traffic was not stopped.";
		if (!applied) return "The firewall did not take the stop.";
		return "Outbound traffic is stopped. Release the stop on the Blocks page when this PC is clean.";
	}

	bool WantCut()
	{
		lock (_gate)
			return _want && _admin && _holds.TryGetValue(OutboundKey, out var hold) && hold.UntilUtc > DateTime.UtcNow;
	}

	void StartCutter()
	{
		if (!WantCut()) return;
		if (Interlocked.CompareExchange(ref _cutting, 1, 0) != 0) return;
		var token = _cts?.Token ?? CancellationToken.None;
		Task.Run(() =>
		{
			try
			{
				while (!token.IsCancellationRequested && WantCut())
				{
					if (OutboundRuleMissing())
					{
						try { EnsureOutboundRule(); } catch { }
					}
					try { TcpTable.CutOutbound(); } catch { }
					if (token.WaitHandle.WaitOne(400)) break;
				}
			}
			finally { Interlocked.Exchange(ref _cutting, 0); }
		}, token);
	}

	void EnsureOutboundRule()
	{
		if (!WantCut()) return;
		try
		{
			ApplyRule(OutboundKey, "clipboard");
			var keep = false;
			lock (_gate)
			{
				if (_want && _admin && _holds.TryGetValue(OutboundKey, out var hold) && hold.UntilUtc > DateTime.UtcNow)
				{
					hold.Applied = true;
					hold.Note = "Firewall rule is in.";
					keep = true;
				}
			}
			if (!keep)
			{
				try { Remove(RuleName(OutboundKey)); } catch { }
			}
		}
		catch (Exception ex)
		{
			lock (_gate)
			{
				if (_holds.TryGetValue(OutboundKey, out var hold))
					hold.Note = Clip(ex.Message);
			}
		}
	}

	static string Scenario(Verdict verdict)
	{
		var top = verdict.Signals.OrderByDescending(s => s.Weight).ThenBy(s => s.Name).FirstOrDefault();
		return top != null && top.Name.Length > 0 ? top.Name : verdict.Band;
	}

	static string RuleName(string ip) => Prefix + ip.Replace(':', '.');

	static string Clip(string text) => text.Length <= 140 ? text : text[..140];

	static void ApplyRule(string ip, string scenario)
	{
		if (ip == OutboundKey) AddOutbound(RuleName(ip));
		else Add(RuleName(ip), ip, scenario);
	}

	static void Add(string name, string ip, string scenario)
	{
		dynamic policy = Policy();
		dynamic rules = policy.Rules;
		try { rules.Remove(name); } catch { }
		dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
		rule.Name = name;
		rule.Description = "GUARDEX " + scenario;
		rule.Grouping = RuleGroup;
		rule.Action = 0;
		rule.Direction = 1;
		rule.Enabled = true;
		rule.InterfaceTypes = "All";
		rule.RemoteAddresses = ip;
		rule.Protocol = 256;
		rule.Profiles = 0x7FFFFFFF;
		rules.Add(rule);
	}

	static void AddOutbound(string name)
	{
		dynamic policy = Policy();
		dynamic rules = policy.Rules;
		try { rules.Remove(name); } catch { }
		dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
		rule.Name = name;
		rule.Description = "GUARDEX clipboard wallet swap. Outbound is stopped.";
		rule.Grouping = RuleGroup;
		rule.Action = 0;
		rule.Direction = 2;
		rule.Enabled = true;
		rule.InterfaceTypes = "All";
		rule.RemoteAddresses = "*";
		rule.Protocol = 256;
		rule.Profiles = 0x7FFFFFFF;
		rules.Add(rule);
	}

	static void Remove(string name)
	{
		dynamic rules = Policy().Rules;
		rules.Remove(name);
	}

	// Temporary address rules use the "GUARDEX " name prefix and are removed on exit.
	// Telemetry rules share RuleGroup so uninstall can find them, and a different name so this sweep leaves them.
	public static bool TaggedRule(string name, string? group)
	{
		if (string.Equals(group, RuleGroup, StringComparison.Ordinal)) return true;
		if (name.StartsWith(Prefix, StringComparison.Ordinal)) return true;
		if (name.StartsWith("GUARDEX-Telemetry ", StringComparison.Ordinal)) return true;
		return false;
	}

	static void DropOrphans()
	{
		dynamic rules = Policy().Rules;
		var names = new List<string>();
		foreach (dynamic rule in rules)
		{
			try
			{
				string name = rule.Name;
				if (name.StartsWith(Prefix, StringComparison.Ordinal)) names.Add(name);
			}
			catch { }
		}
		foreach (var name in names)
		{
			try { rules.Remove(name); } catch { }
		}
	}

	static dynamic Policy()
	{
		var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
		if (type == null) throw new InvalidOperationException("Firewall API is unavailable.");
		return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Firewall API is unavailable.");
	}

	public static bool IsReserved(string text)
	{
		if (!IPAddress.TryParse(text, out var addr)) return true;
		if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
		if (IPAddress.IsLoopback(addr)) return true;
		if (addr.IsIPv6LinkLocal || addr.IsIPv6Multicast) return true;
		if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any) || addr.Equals(IPAddress.Broadcast)) return true;
		if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
		var b = addr.GetAddressBytes();
		if (b[0] == 169 && b[1] == 254) return true;
		if (b[0] >= 224) return true;
		return false;
	}

	static bool IsOwn(IPAddress addr)
	{
		try
		{
			foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				IPInterfaceProperties props;
				try { props = nic.GetIPProperties(); }
				catch { continue; }
				foreach (var uni in props.UnicastAddresses)
					if (uni.Address.Equals(addr)) return true;
				foreach (var gw in props.GatewayAddresses)
					if (gw.Address != null && gw.Address.Equals(addr)) return true;
			}
		}
		catch { }
		return false;
	}

	public static int Remembered(int count, DateTime untilUtc, DateTime now)
	{
		if (count <= 0) return 0;
		if (untilUtc <= now.AddHours(-24)) return 0;
		return Math.Min(count, 4);
	}

	public static int NextStrikes(int previous, bool fresh, bool firstConfirm)
	{
		var strikes = firstConfirm || fresh ? previous + 1 : previous;
		if (strikes < 1) strikes = 1;
		if (strikes > 4) strikes = 4;
		return strikes;
	}

	public static int StrikesAfterGap(int finished, DateTime blockEndedUtc, DateTime now, bool cleared)
	{
		var remembered = cleared ? 0 : Remembered(finished, blockEndedUtc, now);
		return NextStrikes(remembered, true, false);
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (!IsReserved("127.0.0.1")) failures.Add("loopback");
		if (!IsReserved("169.254.4.4")) failures.Add("link local");
		if (!IsReserved("0.0.0.0")) failures.Add("unspecified");
		if (!IsReserved("224.0.0.1")) failures.Add("multicast");
		if (IsReserved("203.0.113.8")) failures.Add("documentation address was reserved");
		if (IsReserved("10.1.1.8")) failures.Add("lan address was reserved");
		var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		if (StrikesAfterGap(1, now.AddMinutes(-1), now, false) != 2) failures.Add("expired block forgot the strike");
		if (StrikesAfterGap(2, now.AddMinutes(-30), now, false) != 3) failures.Add("third strike");
		if (StrikesAfterGap(4, now.AddMinutes(-30), now, false) != 4) failures.Add("strike cap");
		if (StrikesAfterGap(3, now.AddMinutes(-30), now, true) != 1) failures.Add("release kept the strike");
		if (StrikesAfterGap(2, now.AddHours(-25), now, false) != 1) failures.Add("old strike did not decay");
		if (NextStrikes(2, false, true) != 3) failures.Add("confirm did not step the strike");
		if (Minutes(1) != 10 || Minutes(2) != 30 || Minutes(3) != 120 || Minutes(4) != 720)
			failures.Add("block lengths");
		if (OutboundHoldMinutes() != 720) failures.Add("outbound hold");
		var stopped = OutboundPhrase(true, true, true);
		var reported = OutboundPhrase(false, true, false);
		var unelevated = OutboundPhrase(true, false, false);
		var missed = OutboundPhrase(true, true, false);
		if (!stopped.Contains("stopped", StringComparison.Ordinal) || !stopped.Contains("Release", StringComparison.Ordinal))
			failures.Add("outbound phrase");
		if (!reported.Contains("not stopped", StringComparison.Ordinal) || !unelevated.Contains("not stopped", StringComparison.Ordinal))
			failures.Add("outbound phrase when it cannot block");
		if (!missed.Contains("did not take", StringComparison.Ordinal)) failures.Add("outbound rule failure");
		if (stopped.Contains("put back", StringComparison.OrdinalIgnoreCase)) failures.Add("outbound phrase restores");
		if (!TaggedRule(RuleName(OutboundKey), "")) failures.Add("outbound rule name");
		if (!TcpTable.CutRow("203.0.113.8", 5, 52341, false)) failures.Add("outbound session");
		if (TcpTable.CutRow("203.0.113.8", 5, 3389, true)) failures.Add("listening session");
		if (TcpTable.CutRow("127.0.0.1", 5, 52341, false)) failures.Add("loopback session");
		if (!TaggedRule("other", RuleGroup)) failures.Add("group tag");
		if (!TaggedRule("GUARDEX 203.0.113.8", "")) failures.Add("address name");
		if (!TaggedRule("GUARDEX-Telemetry WerFault.exe", "")) failures.Add("telemetry name");
		if (TaggedRule("Windows Defender Firewall", "")) failures.Add("stranger rule");
		if ("GUARDEX-Telemetry ".StartsWith(Prefix, StringComparison.Ordinal)) failures.Add("telemetry would be swept");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	sealed class Hold
	{
		public string Ip { get; init; } = "";
		public string Origin { get; set; } = "";
		public string Scenario { get; set; } = "";
		public string Why { get; set; } = "";
		public DateTime UntilUtc { get; set; }
		public int Strikes { get; set; }
		public bool Hard { get; set; }
		public bool Confirmed { get; set; }
		public bool Applied { get; set; }
		public string Note { get; set; } = "";
	}
}

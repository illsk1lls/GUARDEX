using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace Guardex;

// Reflex blocks. The model can step a soft block down. It cannot lift a hard signal.
sealed class Enforcer
{
	const string Prefix = "GUARDEX ";
	readonly SessionStore _store;
	readonly object _gate = new();
	readonly Dictionary<string, Hold> _holds = new(StringComparer.Ordinal);
	readonly bool _admin;
	bool _want;
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
		else ApplyAll();
		Publish();
	}

	public void Release(string src) => Lift(src, "Released from the window.");

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
				Lift(ip, "The model stepped this source down.");
			return;
		}
		if (!want) return;

		var scenario = Scenario(verdict);
		var apply = false;
		var noteFeed = false;
		lock (_gate)
		{
			_holds.TryGetValue(ip, out var existing);
			var still = existing != null && existing.UntilUtc > DateTime.UtcNow.AddMinutes(2);
			var firstConfirm = fromModel && existing != null && !existing.Confirmed;
			if (still && !firstConfirm) return;
			var strikes = existing == null || existing.UntilUtc <= DateTime.UtcNow ? (existing?.Strikes ?? 0) + 1 : existing.Strikes;
			if (firstConfirm) strikes = Math.Min(4, existing!.Strikes + 1);
			var hold = existing ?? new Hold { Ip = ip, Origin = fromModel ? "Model" : "Reflex" };
			hold.Scenario = scenario;
			hold.Why = verdict.Why;
			hold.Strikes = Math.Min(4, Math.Max(1, strikes));
			hold.UntilUtc = DateTime.UtcNow.AddMinutes(Minutes(hold.Strikes));
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
				Add(RuleName(ip), ip, scenario);
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
			Lift(ip, "The block expired.");
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
				Add(RuleName(hold.Ip), hold.Ip, hold.Scenario);
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

	void Lift(string ip, string why)
	{
		Hold? hold;
		lock (_gate)
		{
			if (!_holds.Remove(ip, out hold)) return;
		}
		if (hold.Applied && _admin)
		{
			try { Remove(RuleName(ip)); } catch { }
		}
		_store.Note(ip + " released. " + why);
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

	static string Scenario(Verdict verdict)
	{
		var top = verdict.Signals.OrderByDescending(s => s.Weight).ThenBy(s => s.Name).FirstOrDefault();
		return top != null && top.Name.Length > 0 ? top.Name : verdict.Band;
	}

	static string RuleName(string ip) => Prefix + ip.Replace(':', '.');

	static string Clip(string text) => text.Length <= 140 ? text : text[..140];

	static void Add(string name, string ip, string scenario)
	{
		dynamic policy = Policy();
		dynamic rules = policy.Rules;
		try { rules.Remove(name); } catch { }
		dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
		rule.Name = name;
		rule.Description = "GUARDEX " + scenario;
		rule.Action = 0;
		rule.Direction = 1;
		rule.Enabled = true;
		rule.InterfaceTypes = "All";
		rule.RemoteAddresses = ip;
		rule.Protocol = 256;
		rule.Profiles = 0x7FFFFFFF;
		rules.Add(rule);
	}

	static void Remove(string name)
	{
		dynamic rules = Policy().Rules;
		rules.Remove(name);
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

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (!IsReserved("127.0.0.1")) failures.Add("loopback");
		if (!IsReserved("169.254.4.4")) failures.Add("link local");
		if (!IsReserved("0.0.0.0")) failures.Add("unspecified");
		if (!IsReserved("224.0.0.1")) failures.Add("multicast");
		if (IsReserved("203.0.113.8")) failures.Add("documentation address was reserved");
		if (IsReserved("10.1.1.8")) failures.Add("lan address was reserved");
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

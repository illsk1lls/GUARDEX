using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Guardex;

// Single-machine sensor. A later firewall box would push the same Observation objects.
sealed class HostWatch
{
	readonly SessionStore _store;
	readonly LocalJudge _judge;
	readonly Enforcer _enforcer;
	readonly Dictionary<int, string> _proc = [];
	CancellationTokenSource? _cts;

	public HostWatch(SessionStore store, LocalJudge judge, Enforcer enforcer)
	{
		_store = store;
		_judge = judge;
		_enforcer = enforcer;
	}

	public bool IsRunning => _cts is not null;

	public void Start()
	{
		if (_cts is not null) return;
		var cts = new CancellationTokenSource();
		_cts = cts;
		var ct = cts.Token;
		_store.SetStatus(true, "on");
		Task.Run(() => TcpLoop(ct), ct);
		Task.Run(() => HttpLoop(ct), ct);
	}

	public void Stop()
	{
		var cts = _cts;
		_cts = null;
		if (cts is null) return;
		try { cts.Cancel(); } catch { }
		_store.SetStatus(true, "off");
		_store.SetStatus(false, "off");
		_store.SetModel("paused");
	}

	void TcpLoop(CancellationToken ct)
	{
		var prev = new HashSet<string>(StringComparer.Ordinal);
		while (!ct.IsCancellationRequested)
		{
			try
			{
				var rows = TcpTable.Snapshot();
				var listen = new HashSet<int>();
				foreach (var row in rows)
				{
					if (row.State == 2 && row.LocalPort > 0) listen.Add(row.LocalPort);
				}
				_store.SetListenPorts(listen);

				var now = new HashSet<string>(StringComparer.Ordinal);
				foreach (var row in rows)
				{
					// 5 established, 8 close-wait, 11 time-wait. State 4 is SYN received and can be spoofed.
					if (row.State is not (5 or 8 or 11)) continue;
					if (!listen.Contains(row.LocalPort)) continue;
					if (Skip(row.Remote)) continue;
					var key = row.Remote + "|" + row.RemotePort + "|" + row.LocalPort;
					now.Add(key);
					if (prev.Contains(key)) continue;
					var verdict = _store.Add(new Observation
					{
						Utc = DateTime.UtcNow,
						Src = row.Remote,
						LocalPort = row.LocalPort,
						LocalProcess = ProcessName(row.Pid),
						Kind = ObsKind.Tcp,
						Method = "TCP"
					});
					NoteVerdict(verdict, ct);
				}
				prev = now;
			}
			catch (Exception ex)
			{
				_store.SetStatus(true, "error: " + ex.Message);
			}
			ct.WaitHandle.WaitOne(500);
		}
	}

	void NoteVerdict(Verdict? verdict, CancellationToken ct)
	{
		if (verdict == null) return;
		_enforcer.OnVerdict(verdict, false);
		var fact = verdict.Src + " just reached this PC.";
		if (verdict.Band.Length > 0) fact += "\n" + verdict.Band + "  " + verdict.Score;
		if (verdict.Why.Length > 0) fact += "\n" + verdict.Why;
		_judge.Notice("client", verdict.Src, fact);
		var ask = verdict.Gray || (verdict.Band == "block" && !verdict.Hard);
		if (!ask) return;
		var src = verdict.Src;
		var score = verdict.Score;
		_ = Task.Run(() => _judge.ConsiderAsync(src, score, ct));
	}

	void HttpLoop(CancellationToken ct)
	{
		try
		{
			using var session = new TraceEventSession("GuardexHttp");
			session.StopOnDispose = true;
			session.EnableProvider("Microsoft-Windows-HttpService", TraceEventLevel.Verbose, ulong.MaxValue);
			session.Source.Dynamic.All += data =>
			{
				try { OnHttp(data, ct); }
				catch { }
			};
			_store.SetStatus(false, ct.IsCancellationRequested ? "off" : "on");
			if (ct.IsCancellationRequested) return;
			using var reg = ct.Register(() => { try { session.Stop(); } catch { } });
			session.Source.Process();
		}
		catch (UnauthorizedAccessException)
		{
			_store.SetStatus(false, "needs elevation for request paths and user-agents");
		}
		catch (Exception ex)
		{
			_store.SetStatus(false, "off: " + ex.Message);
		}
	}

	void OnHttp(TraceEvent data, CancellationToken ct)
	{
		var path = Payload(data, "Url", "RequestUrl", "URI", "url");
		var method = Payload(data, "Verb", "Method", "HttpMethod");
		var src = Payload(data, "RemoteAddress", "ClientIp", "SrcIp", "RemoteIp");
		var statusText = Payload(data, "StatusCode", "Status", "HttpStatus");
		var ua = Payload(data, "UserAgent", "User-Agent");
		if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(src)) return;
		if (string.IsNullOrWhiteSpace(src)) return;
		src = src.Trim();
		if (Skip(src)) return;
		var query = "";
		var clean = path ?? "/";
		var q = clean.IndexOf('?');
		if (q >= 0)
		{
			query = clean[(q + 1)..];
			clean = clean[..q];
		}
		if (clean.Length == 0) clean = "/";
		int.TryParse(statusText, out var status);
		int.TryParse(Payload(data, "LocalPort", "SrcPort", "ServerPort"), out var port);
		var verdict = _store.Add(new Observation
		{
			Utc = DateTime.UtcNow,
			Src = src,
			LocalPort = port,
			Kind = ObsKind.Http,
			Path = clean.Length > 180 ? clean[..180] : clean,
			Query = query.Length > 80 ? query[..80] : query,
			Method = string.IsNullOrWhiteSpace(method) ? "GET" : method,
			Status = status,
			UserAgent = ua ?? "",
			// HTTP.sys gives the user-agent string, not the header set. Scorer ignores a lone user-agent name.
			Headers = string.IsNullOrWhiteSpace(ua) ? [] : ["user-agent"]
		});
		NoteVerdict(verdict, ct);
	}

	static string? Payload(TraceEvent data, params string[] names)
	{
		foreach (var name in data.PayloadNames)
		{
			foreach (var want in names)
			{
				if (!name.Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
				var i = data.PayloadIndex(name);
				if (i < 0) continue;
				var value = data.PayloadValue(i);
				return value?.ToString();
			}
		}
		return null;
	}

	static bool Skip(string ip)
	{
		if (string.IsNullOrWhiteSpace(ip)) return true;
		if (ip is "0.0.0.0" or "127.0.0.1" or "::1" or "::") return true;
		if (IPAddress.TryParse(ip, out var addr) && IPAddress.IsLoopback(addr)) return true;
		return false;
	}

	string ProcessName(int pid)
	{
		if (pid <= 0) return "";
		if (_proc.TryGetValue(pid, out var name)) return name;
		try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
		catch { name = ""; }
		if (_proc.Count > 400) _proc.Clear();
		_proc[pid] = name;
		return name;
	}
}

static class TcpTable
{
	const int AfInet = 2;
	const int OwnerPidAll = 5;

	[DllImport("iphlpapi.dll", SetLastError = true)]
	static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

	public static List<Row> Snapshot()
	{
		var size = 0;
		GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, OwnerPidAll, 0);
		if (size <= 0) return [];
		var buf = Marshal.AllocHGlobal(size);
		try
		{
			var rc = GetExtendedTcpTable(buf, ref size, true, AfInet, OwnerPidAll, 0);
			if (rc != 0) return [];
			var count = Marshal.ReadInt32(buf);
			var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
			var list = new List<Row>(count);
			var ptr = buf + 4;
			for (var i = 0; i < count; i++)
			{
				var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr);
				list.Add(new Row(Ip(row.LocalAddr), Port(row.LocalPort), Ip(row.RemoteAddr), Port(row.RemotePort), (int)row.State, (int)row.OwningPid));
				ptr += rowSize;
			}
			return list;
		}
		finally
		{
			Marshal.FreeHGlobal(buf);
		}
	}

	static int Port(uint p) => ((int)(p & 0xFF) << 8) | (int)((p >> 8) & 0xFF);

	static string Ip(uint addr)
	{
		var b = BitConverter.GetBytes(addr);
		return $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";
	}

	public static int CutOutbound()
	{
		var size = 0;
		GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, OwnerPidAll, 0);
		if (size <= 0) return 0;
		var buf = Marshal.AllocHGlobal(size);
		try
		{
			var rc = GetExtendedTcpTable(buf, ref size, true, AfInet, OwnerPidAll, 0);
			if (rc != 0) return 0;
			var count = Marshal.ReadInt32(buf);
			var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
			var listening = new HashSet<int>();
			var ptr = buf + 4;
			for (var i = 0; i < count; i++)
			{
				var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr);
				if (row.State == 2) listening.Add(Port(row.LocalPort));
				ptr += rowSize;
			}
			var cut = 0;
			ptr = buf + 4;
			for (var i = 0; i < count; i++)
			{
				var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr);
				ptr += rowSize;
				var localPort = Port(row.LocalPort);
				if (!CutRow(Ip(row.RemoteAddr), (int)row.State, localPort, listening.Contains(localPort))) continue;
				var entry = new MibTcpRow
				{
					State = 12,
					LocalAddr = row.LocalAddr,
					LocalPort = row.LocalPort,
					RemoteAddr = row.RemoteAddr,
					RemotePort = row.RemotePort
				};
				if (SetTcpEntry(ref entry) == 0) cut++;
			}
			return cut;
		}
		finally
		{
			Marshal.FreeHGlobal(buf);
		}
	}

	public static bool CutRow(string remote, int state, int localPort, bool localPortListening)
	{
		if (localPort <= 0 || localPortListening || !Sends(remote, state)) return false;
		return true;
	}

	public static bool Sends(string remote, int state)
	{
		if (state is < 3 or > 10) return false;
		if (string.IsNullOrWhiteSpace(remote)) return false;
		if (remote is "0.0.0.0" or "127.0.0.1" or "::1" or "::") return false;
		return !(IPAddress.TryParse(remote, out var addr) && IPAddress.IsLoopback(addr));
	}

	[DllImport("iphlpapi.dll")]
	static extern uint SetTcpEntry(ref MibTcpRow row);

	public readonly record struct Row(string Local, int LocalPort, string Remote, int RemotePort, int State, int Pid);

	[StructLayout(LayoutKind.Sequential)]
	struct MibTcpRow
	{
		public uint State;
		public uint LocalAddr;
		public uint LocalPort;
		public uint RemoteAddr;
		public uint RemotePort;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct MibTcpRowOwnerPid
	{
		public uint State;
		public uint LocalAddr;
		public uint LocalPort;
		public uint RemoteAddr;
		public uint RemotePort;
		public uint OwningPid;
	}
}

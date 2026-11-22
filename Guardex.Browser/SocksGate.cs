using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Guardex.Tunnel;

namespace Guardex.Browser;

public enum SocksPath
{
	Direct,
	Hold,
	Tunnel
}

public static class SocksGate
{
	static TcpListener? _listener;
	static volatile bool _run;
	public static int Port { get; private set; }
	public static volatile bool ForceHold;

	public static SocksPath Choose(bool armed, bool tunnelOn)
	{
		if (tunnelOn) return SocksPath.Tunnel;
		if (armed || ForceHold) return SocksPath.Hold;
		return SocksPath.Direct;
	}

	public static byte Decide(int cmd, int atyp, SocksPath path)
	{
		if (cmd != 1) return 0x07;
		if (atyp != 1 && atyp != 3) return 0x08;
		if (path == SocksPath.Hold) return 0x02;
		return 0x00;
	}

	public static string BrowserArguments(int port)
	{
		if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
		return "--proxy-server=socks5://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)
			+ " --host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE 127.0.0.1\""
			+ " --disable-quic"
			+ " --disable-ipv6"
			+ " --force-webrtc-ip-handling-policy=disable_non_proxied_udp"
			+ " --disable-background-networking"
			+ " --disable-component-update"
			+ " --disable-domain-reliability"
			+ " --disable-features=DnsOverHttps,AsyncDns"
			+ " --disable-breakpad"
			+ " --disable-crash-reporter"
			+ " --no-pings";
	}

	public static void Start()
	{
		if (_listener != null) return;
		_listener = new TcpListener(IPAddress.Loopback, 0);
		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		_run = true;
		var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "gx-proxy" };
		thread.Start();
	}

	public static void Stop()
	{
		_run = false;
		try { _listener?.Stop(); } catch { }
		_listener = null;
	}

	public static string SelfCheck()
	{
		var saved = ForceHold;
		try
		{
			ForceHold = false;
			if (Choose(false, false) != SocksPath.Direct) return "direct";
			if (Choose(true, false) != SocksPath.Hold) return "hold";
			if (Choose(true, true) != SocksPath.Tunnel) return "tunnel";
			if (Choose(false, true) != SocksPath.Tunnel) return "tunnel-wins";
			ForceHold = true;
			if (Choose(false, false) != SocksPath.Hold) return "force";
			if (Decide(1, 1, SocksPath.Hold) != 0x02) return "hold-reply";
			if (Decide(1, 3, SocksPath.Tunnel) != 0x00) return "domain";
			if (Decide(1, 4, SocksPath.Tunnel) != 0x08) return "ipv6";
			if (Decide(3, 1, SocksPath.Direct) != 0x07) return "udp";
			if (Decide(2, 1, SocksPath.Direct) != 0x07) return "bind";
			var args = BrowserArguments(9);
			if (!args.Contains("socks5://127.0.0.1:9", StringComparison.Ordinal)) return "proxy";
			if (!args.Contains("MAP * ~NOTFOUND", StringComparison.Ordinal)) return "resolver";
			if (!args.Contains("--disable-quic", StringComparison.Ordinal)) return "quic";
			if (!args.Contains("--disable-ipv6", StringComparison.Ordinal)) return "ipv6-flag";
			if (!args.Contains("disable_non_proxied_udp", StringComparison.Ordinal)) return "webrtc";
			if (!args.Contains("--disable-breakpad", StringComparison.Ordinal)) return "breakpad";
			if (!args.Contains("--disable-crash-reporter", StringComparison.Ordinal)) return "crash-reporter";
			if (args.Contains("bypass", StringComparison.OrdinalIgnoreCase)) return "bypass";
			return "PASS";
		}
		finally { ForceHold = saved; }
	}

	static void AcceptLoop()
	{
		while (_run)
		{
			TcpClient client;
			try { client = _listener!.AcceptTcpClient(); }
			catch { if (!_run) return; continue; }
			var thread = new Thread(() => Handle(client)) { IsBackground = true, Name = "gx-socks" };
			thread.Start();
		}
	}

	static void Handle(TcpClient client)
	{
		try
		{
			using (client)
			{
				if (client.Client.RemoteEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address)) return;
				client.NoDelay = true;
				var stream = client.GetStream();
				if (!Greet(stream)) return;
				if (!TryRequest(stream, out var cmd, out var atyp, out var host, out var port))
				{
					Reply(stream, 0x08);
					return;
				}
				var path = Choose(VpnHub.Require && VpnHub.HasConfig, VpnHub.TunnelOn);
				var code = Decide(cmd, atyp, path);
				if (code != 0x00)
				{
					Reply(stream, code);
					return;
				}
				if (path == SocksPath.Tunnel) ServeTunnel(stream, host, port);
				else if (path == SocksPath.Direct) ServeDirect(stream, host, port);
				else Reply(stream, 0x02);
			}
		}
		catch { try { client.Close(); } catch { } }
	}

	static bool Greet(NetworkStream stream)
	{
		int ver = stream.ReadByte();
		int count = stream.ReadByte();
		if (ver != 5 || count <= 0) return false;
		var methods = ReadFull(stream, count);
		var ok = false;
		for (var i = 0; i < methods.Length; i++) if (methods[i] == 0) ok = true;
		stream.Write(ok ? new byte[] { 5, 0 } : new byte[] { 5, 0xFF }, 0, 2);
		return ok;
	}

	static bool TryRequest(NetworkStream stream, out int cmd, out int atyp, out string host, out int port)
	{
		cmd = 0;
		atyp = 0;
		host = "";
		port = 0;
		var head = ReadFull(stream, 4);
		if (head[0] != 5) return false;
		cmd = head[1];
		atyp = head[3];
		if (atyp == 1)
		{
			host = new IPAddress(ReadFull(stream, 4)).ToString();
		}
		else if (atyp == 3)
		{
			int n = stream.ReadByte();
			if (n <= 0) return false;
			host = Encoding.Latin1.GetString(ReadFull(stream, n));
		}
		else if (atyp == 4)
		{
			ReadFull(stream, 16);
			ReadFull(stream, 2);
			return false;
		}
		else return false;
		var p = ReadFull(stream, 2);
		port = (p[0] << 8) | p[1];
		return port > 0 && host.Length > 0 && host.Length <= 253;
	}

	static void ServeTunnel(NetworkStream stream, string host, int port)
	{
		if (!VpnHub.TunnelOn)
		{
			Reply(stream, 0x02);
			return;
		}
		var remote = VpnHub.OpenTcp(host, port, 20000);
		if (remote == null)
		{
			Reply(stream, 0x04);
			return;
		}
		Reply(stream, 0x00);
		Splice(stream, remote);
	}

	static void ServeDirect(NetworkStream stream, string host, int port)
	{
		if (Current() != SocksPath.Direct)
		{
			Reply(stream, 0x02);
			return;
		}
		IPAddress? ip = null;
		if (!IPAddress.TryParse(host, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
		{
			try
			{
				foreach (var address in Dns.GetHostAddresses(host))
				{
					if (address.AddressFamily == AddressFamily.InterNetwork) { ip = address; break; }
				}
			}
			catch { ip = null; }
		}
		else ip = parsed;
		if (Current() != SocksPath.Direct)
		{
			Reply(stream, 0x02);
			return;
		}
		if (ip == null)
		{
			Reply(stream, 0x04);
			return;
		}
		var tcp = new TcpClient(AddressFamily.InterNetwork);
		try
		{
			var wait = tcp.BeginConnect(ip, port, null, null);
			var opened = wait.AsyncWaitHandle.WaitOne(20000, false);
			if (!opened || Current() != SocksPath.Direct)
			{
				tcp.Close();
				Reply(stream, opened ? (byte)0x02 : (byte)0x04);
				return;
			}
			tcp.EndConnect(wait);
			Reply(stream, 0x00);
			Splice(stream, tcp.GetStream());
		}
		catch
		{
			try { tcp.Close(); } catch { }
			Reply(stream, 0x05);
		}
	}

	static SocksPath Current() => Choose(VpnHub.Require && VpnHub.HasConfig, VpnHub.TunnelOn);

	static void Reply(NetworkStream stream, byte code)
	{
		var buf = new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 };
		stream.Write(buf, 0, buf.Length);
	}

	static byte[] ReadFull(Stream stream, int count)
	{
		var buf = new byte[count];
		var offset = 0;
		while (offset < count)
		{
			var read = stream.Read(buf, offset, count - offset);
			if (read <= 0) throw new IOException("closed");
			offset += read;
		}
		return buf;
	}

	static void Splice(Stream left, Stream right)
	{
		var a = new Thread(() => Pump(left, right)) { IsBackground = true, Name = "gx-socks" };
		var b = new Thread(() => Pump(right, left)) { IsBackground = true, Name = "gx-socks" };
		a.Start();
		b.Start();
		a.Join();
		b.Join();
	}

	static void Pump(Stream from, Stream to)
	{
		var buf = new byte[16384];
		try
		{
			while (true)
			{
				var n = from.Read(buf, 0, buf.Length);
				if (n <= 0) break;
				to.Write(buf, 0, n);
				to.Flush();
			}
		}
		catch { }
		try { to.Close(); } catch { }
		try { from.Close(); } catch { }
	}
}

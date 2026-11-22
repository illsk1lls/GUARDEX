using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Guardex.Tunnel;
using Microsoft.Web.WebView2.Core;

namespace Guardex;

static class Favicon
{
	static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
	static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
	static readonly HashSet<string> Tried = new(StringComparer.OrdinalIgnoreCase);
	static readonly Queue<string> Waiters = new();
	static int Running;
	static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
	static readonly SemaphoreSlim Draw = new(1, 1);
	static Action? Ready;

	public static string Host(string? url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
		if (uri.Scheme is not "http" and not "https") return "";
		return uri.IdnHost.Trim('.').ToLowerInvariant();
	}

	public static string Letter(string? url)
	{
		var host = Host(url);
		foreach (var ch in host)
		{
			if (char.IsAsciiLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
		}
		return "";
	}

	public static ImageSource? Image(string? url)
	{
		var host = Host(url);
		if (host.Length == 0) return null;
		if (Cache.TryGetValue(host, out var ready)) return ready;
		foreach (var ext in new[] { ".png", ".ico" })
		{
			var path = Path.Combine(Dir(), host + ext);
			if (!File.Exists(path)) continue;
			try
			{
				var image = Load(File.ReadAllBytes(path));
				Cache[host] = image;
				return image;
			}
			catch
			{
			}
		}
		return null;
	}

	public static void Watch(Action ready) => Ready = ready;

	public static void Queue(string? url)
	{
		var host = Host(url);
		if (host.Length == 0 || HasFile(host)) return;
		lock (Pending)
		{
			if (Pending.Contains(host) || !Tried.Add(host)) return;
			Pending.Add(host);
			Waiters.Enqueue(host);
		}
		Pump();
	}

	static void Pump()
	{
		while (true)
		{
			string host;
			lock (Pending)
			{
				if (Running >= 4 || Waiters.Count == 0) return;
				host = Waiters.Dequeue();
				Running++;
			}
			_ = Run(host);
		}
	}

	static async Task Run(string host)
	{
		try { await Fetch(host); }
		finally
		{
			lock (Pending) Running--;
			Pump();
		}
	}

	public static async Task Store(CoreWebView2 core)
	{
		var host = Host(core.Source);
		if (host.Length == 0) return;
		await using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
		if (stream == null) return;
		using var copy = new MemoryStream();
		await stream.CopyToAsync(copy);
		var bytes = copy.ToArray();
		if (bytes.Length < 16 || bytes.Length > 512 * 1024) return;
		try
		{
			Directory.CreateDirectory(Dir());
			var path = Path.Combine(Dir(), host + ".png");
			await File.WriteAllBytesAsync(path, bytes);
			Cache[host] = Load(bytes);
		}
		catch
		{
		}
	}

	public static string SelfCheck()
	{
		if (Host("https://EN.Wikipedia.org/wiki/Cat") != "en.wikipedia.org") return "host";
		if (Host("about:blank").Length != 0) return "blank";
		if (Letter("https://en.wikipedia.org/") != "E") return "letter";
		if (Letter("https://3.example/") != "3") return "digit";
		if (Letter("not a url").Length != 0) return "bad";
		return "PASS";
	}

	static async Task Fetch(string host)
	{
		try
		{
			if (HasFile(host)) return;
			var bytes = await Get("https://" + host + "/favicon.ico");
			if (bytes == null) return;
			await Draw.WaitAsync();
			byte[]? png;
			try
			{
				if (HasFile(host)) return;
				png = AsPng(bytes);
				if (png == null) return;
				Directory.CreateDirectory(Dir());
				var path = Path.Combine(Dir(), host + ".png");
				if (File.Exists(path)) return;
				await File.WriteAllBytesAsync(path, png);
			}
			finally
			{
				Draw.Release();
			}
			Ready?.Invoke();
		}
		catch
		{
		}
		finally
		{
			lock (Pending) Pending.Remove(host);
		}
	}

	static async Task<byte[]?> Get(string url)
	{
		if (VpnHub.Require && VpnHub.HasConfig)
		{
			if (!VpnHub.TunnelOn) return null;
			return await Task.Run(() =>
			{
				var got = VpnHub.HttpGet(url, 15000, "GUARDEX", out var status, out var data);
				return got && status == 200 ? data : null;
			});
		}
		using var response = await Http.GetAsync(url);
		if (!response.IsSuccessStatusCode) return null;
		return await response.Content.ReadAsByteArrayAsync();
	}

	static bool HasFile(string host)
	{
		var dir = Dir();
		return File.Exists(Path.Combine(dir, host + ".png")) || File.Exists(Path.Combine(dir, host + ".ico"));
	}

	static byte[]? AsPng(byte[] bytes)
	{
		if (bytes.Length < 16 || bytes.Length > 512 * 1024 || Html(bytes)) return null;
		try
		{
			using var input = new MemoryStream(bytes);
			using var image = new System.Drawing.Bitmap(input);
			if (image.Width < 1 || image.Height < 1 || image.Width > 512 || image.Height > 512) return null;
			using var output = new MemoryStream();
			image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
			var png = output.ToArray();
			if (png.Length < 16 || png.Length > 512 * 1024) return null;
			if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return null;
			return png;
		}
		catch
		{
			return null;
		}
	}

	static bool Html(byte[] bytes)
	{
		var start = 0;
		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
		var end = Math.Min(bytes.Length, start + 64);
		while (start < end && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
		return start < end && bytes[start] == (byte)'<';
	}

	static ImageSource Load(byte[] bytes)
	{
		var image = new BitmapImage();
		image.BeginInit();
		image.StreamSource = new MemoryStream(bytes);
		image.CacheOption = BitmapCacheOption.OnLoad;
		image.EndInit();
		image.Freeze();
		return image;
	}

	static string Dir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex", "favicons");
}

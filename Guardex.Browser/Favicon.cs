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
		var small = TinyPng(16);
		var big = TinyPng(48);
		var picked = AsPng(PackIco((16, small), (48, big)));
		if (picked == null || PngWidth(picked) != 48) return "ico";
		var chosen = Prefer(AsPng(PackIco((16, small))), TinyPng(64));
		if (chosen == null || PngWidth(chosen) != 64) return "touch";
		if (PngWidth(Prefer(big, small) ?? []) != 48) return "keep";
		return "PASS";
	}

	static async Task Fetch(string host)
	{
		try
		{
			if (HasFile(host)) return;
			var ico = await Get("https://" + host + "/favicon.ico");
			var png = ico == null ? null : AsPng(ico);
			if (png == null || PngWidth(png) < 32)
			{
				var touch = await Get("https://" + host + "/apple-touch-icon.png");
				png = Prefer(png, touch == null ? null : AsPng(touch));
			}
			if (png == null) return;
			await Draw.WaitAsync();
			try
			{
				if (HasFile(host)) return;
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

	static byte[]? Prefer(byte[]? current, byte[]? alt)
	{
		if (alt == null) return current;
		if (current == null) return alt;
		return PngWidth(current) < 32 && PngWidth(alt) > PngWidth(current) ? alt : current;
	}

	static byte[]? AsPng(byte[] bytes)
	{
		if (bytes.Length < 16 || bytes.Length > 512 * 1024 || Html(bytes)) return null;
		if (IsPng(bytes)) return PngWidth(bytes) > 0 ? bytes : null;
		var fromIco = IcoPng(bytes);
		return fromIco ?? DrawPng(bytes);
	}

	static byte[]? IcoPng(byte[] bytes)
	{
		if (bytes.Length < 22 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0) return null;
		var count = bytes[4] | (bytes[5] << 8);
		if (count < 1 || count > 64 || 6 + count * 16 > bytes.Length) return null;
		var best = -1;
		var bestArea = -1;
		var bestPng = false;
		for (var i = 0; i < count; i++)
		{
			var at = 6 + i * 16;
			var width = bytes[at] == 0 ? 256 : bytes[at];
			var height = bytes[at + 1] == 0 ? 256 : bytes[at + 1];
			var size = BitConverter.ToInt32(bytes, at + 8);
			var offset = BitConverter.ToInt32(bytes, at + 12);
			if (size < 16 || offset < 0 || offset > bytes.Length - size) continue;
			var png = offset + 8 <= bytes.Length && IsPng(bytes.AsSpan(offset, 8));
			var area = width * height;
			if (area < bestArea || (area == bestArea && bestPng && !png)) continue;
			best = i;
			bestArea = area;
			bestPng = png;
		}
		if (best < 0) return null;
		var chosen = 6 + best * 16;
		var length = BitConverter.ToInt32(bytes, chosen + 8);
		var start = BitConverter.ToInt32(bytes, chosen + 12);
		if (bestPng)
		{
			var slice = bytes.AsSpan(start, length).ToArray();
			return PngWidth(slice) > 0 && slice.Length <= 512 * 1024 ? slice : null;
		}
		return DrawPng(bytes);
	}

	static byte[]? DrawPng(byte[] bytes)
	{
		try
		{
			using var input = new MemoryStream(bytes);
			System.Drawing.Image image;
			if (bytes.Length > 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1)
			{
				using var icon = new System.Drawing.Icon(input, 256, 256);
				image = icon.ToBitmap();
			}
			else image = new System.Drawing.Bitmap(input);
			using (image)
			{
				if (image.Width < 1 || image.Height < 1 || image.Width > 512 || image.Height > 512) return null;
				using var output = new MemoryStream();
				image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
				var png = output.ToArray();
				return png.Length >= 16 && png.Length <= 512 * 1024 && IsPng(png) ? png : null;
			}
		}
		catch
		{
			return null;
		}
	}

	static bool IsPng(ReadOnlySpan<byte> bytes) =>
		bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

	static int PngWidth(byte[] png)
	{
		if (png.Length < 24 || !IsPng(png)) return 0;
		var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
		var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
		if (width < 1 || height < 1 || width > 512 || height > 512) return 0;
		return width;
	}

	static byte[] TinyPng(int size)
	{
		using var image = new System.Drawing.Bitmap(size, size);
		using var output = new MemoryStream();
		image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
		return output.ToArray();
	}

	static byte[] PackIco(params (int Size, byte[] Png)[] images)
	{
		var output = new MemoryStream();
		output.Write(new byte[] { 0, 0, 1, 0, (byte)images.Length, 0 });
		var offset = 6 + images.Length * 16;
		foreach (var image in images)
		{
			output.WriteByte((byte)image.Size);
			output.WriteByte((byte)image.Size);
			output.Write(new byte[] { 0, 0, 1, 0, 32, 0 });
			var size = BitConverter.GetBytes(image.Png.Length);
			var at = BitConverter.GetBytes(offset);
			output.Write(size);
			output.Write(at);
			offset += image.Png.Length;
		}
		foreach (var image in images) output.Write(image.Png);
		return output.ToArray();
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

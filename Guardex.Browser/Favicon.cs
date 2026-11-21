using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Guardex;

static class Favicon
{
	static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

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
		var path = Path.Combine(Dir(), host + ".png");
		if (!File.Exists(path)) return null;
		try
		{
			var image = Load(File.ReadAllBytes(path));
			Cache[host] = image;
			return image;
		}
		catch
		{
			return null;
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

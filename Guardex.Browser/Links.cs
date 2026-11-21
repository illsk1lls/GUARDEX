using System.Net;
using System.Text.Json;

namespace Guardex;

public sealed class LinkRow
{
	public string Band { get; init; } = "";
	public string Kind { get; init; } = "";
	public string Host { get; init; } = "";
	public string Path { get; init; } = "";
	public string Why { get; init; } = "";
}

static class LinkRules
{
	public static bool TryPage(string text, out string url, out string error)
	{
		url = "";
		error = "";
		var raw = text.Trim();
		if (raw.Length == 0)
		{
			error = "Enter a site.";
			return false;
		}
		if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
		if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
		{
			error = "Use an http or https site.";
			return false;
		}
		url = uri.ToString();
		return true;
	}

	public static string Rate(string pageUrl, string requestUrl, string type, string initiatorUrl, out string why)
	{
		why = "Same site.";
		if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var req) || req.Host.Length == 0 || req.Scheme is not ("http" or "https"))
		{
			why = "Not a web call.";
			return "allow";
		}
		var kind = type.Trim();
		if (!IsCall(kind))
		{
			why = "Page file.";
			return "allow";
		}
		var reqHost = req.IdnHost.Length > 0 ? req.IdnHost : req.Host;
		if (IsIp(reqHost))
		{
			why = "The host is an IP address.";
			return "block";
		}
		var pageSite = Site(HostOf(pageUrl));
		var reqSite = Site(reqHost);
		if (pageSite.Length == 0 || reqSite == pageSite)
			return "allow";
		var initSite = Site(HostOf(initiatorUrl));
		if (initSite.Length > 0 && initSite != pageSite && initSite != reqSite)
		{
			why = "A third-party script called a different host.";
			return "block";
		}
		why = kind == "Script" ? "Third-party script." : "Third-party call.";
		return "watch";
	}

	public static bool Read(string json, string pageUrl, out string url, out string type, out string initiator)
	{
		url = "";
		type = "";
		initiator = "";
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (!root.TryGetProperty("request", out var request)) return false;
			if (!request.TryGetProperty("url", out var urlValue)) return false;
			url = urlValue.GetString() ?? "";
			if (root.TryGetProperty("type", out var typeValue))
				type = typeValue.GetString() ?? "";
			if (root.TryGetProperty("initiator", out var init) && init.TryGetProperty("url", out var initUrl))
				initiator = initUrl.GetString() ?? "";
			return url.Length > 0;
		}
		catch
		{
			return false;
		}
	}

	public static LinkRow? Row(string pageUrl, string requestUrl, string type, string initiatorUrl)
	{
		var band = Rate(pageUrl, requestUrl, type, initiatorUrl, out var why);
		if (band == "allow") return null;
		if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)) return null;
		var path = uri.AbsolutePath;
		if (path.Length > 180) path = path[..180];
		return new LinkRow
		{
			Band = band,
			Kind = type.Trim(),
			Host = uri.IdnHost.Length > 0 ? uri.IdnHost : uri.Host,
			Path = path,
			Why = why
		};
	}

	static bool IsCall(string type) =>
		type is "Script" or "XHR" or "Fetch" or "WebSocket" or "EventSource" or "Ping" or "Document";

	static bool IsIp(string host)
	{
		var text = host.Trim().Trim('[', ']');
		return IPAddress.TryParse(text, out _);
	}

	static string HostOf(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return "";
		return uri.IdnHost.Length > 0 ? uri.IdnHost : uri.Host;
	}

	static string Site(string host)
	{
		host = host.Trim().Trim('[', ']').Trim('.').ToLowerInvariant();
		if (host.Length == 0 || IsIp(host)) return host;
		var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length <= 2) return string.Join('.', parts);
		var last = parts[^1];
		var mid = parts[^2];
		if (last.Length == 2 && mid is "co" or "com" or "org" or "net" or "ac" or "gov" && parts.Length >= 3)
			return string.Join('.', parts[^3..]);
		return string.Join('.', parts[^2..]);
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (!TryPage("foxnews.com", out var page, out _)) failures.Add("bare host");
		if (!page.StartsWith("https://foxnews.com", StringComparison.Ordinal)) failures.Add("scheme " + page);
		if (TryPage("file:///c/windows", out _, out _)) failures.Add("file url");
		var same = Rate("https://www.foxnews.com/", "https://static.foxnews.com/a.js", "Script", "https://www.foxnews.com/", out _);
		if (same != "allow") failures.Add("same site was " + same);
		var third = Rate("https://www.foxnews.com/", "https://cdn.optimizely.com/js/1.js", "Script", "https://www.foxnews.com/", out var thirdWhy);
		if (third != "watch") failures.Add("third party was " + third);
		if (thirdWhy != "Third-party script.") failures.Add("third why " + thirdWhy);
		var hop = Rate("https://www.foxnews.com/", "https://evil.example/track.js", "Script", "https://cdn.optimizely.com/js/1.js", out var hopWhy);
		if (hop != "block") failures.Add("second hop was " + hop);
		if (!hopWhy.Contains("different host", StringComparison.Ordinal)) failures.Add("hop why " + hopWhy);
		var ip = Rate("https://www.foxnews.com/", "https://203.0.113.8/a.js", "Script", "https://www.foxnews.com/", out _);
		if (ip != "block") failures.Add("ip host was " + ip);
		var image = Rate("https://www.foxnews.com/", "https://ads.example/a.png", "Image", "https://www.foxnews.com/", out _);
		if (image != "allow") failures.Add("image was " + image);
		const string json = """{"type":"Script","request":{"url":"https://evil.example/track.js","method":"GET","postData":"card"},"initiator":{"type":"script","url":"https://cdn.optimizely.com/js/1.js"}}""";
		if (!Read(json, "https://www.foxnews.com/", out var url, out var type, out var initiator)) failures.Add("read");
		if (url.Contains("card", StringComparison.Ordinal) || initiator.Contains("card", StringComparison.Ordinal)) failures.Add("body kept");
		var row = Row("https://www.foxnews.com/", url, type, initiator);
		if (row == null || row.Band != "block" || row.Path.Contains('?')) failures.Add("row");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}
}

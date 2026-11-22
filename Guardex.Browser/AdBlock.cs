using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Guardex.Tunnel;

namespace Guardex.Browser;

// EasyList, EasyPrivacy, Peter Lowe's ad servers, and a malicious-address list.
// The lists are downloaded, not shipped. Easy uses the lists. Medium also blocks
// third-party scripts and frames. Hard also blocks every other third-party request.
sealed class AdBlock
{
	const int Script = 1;
	const int Image = 2;
	const int Style = 4;
	const int Xhr = 8;
	const int Other = 16;
	const int Font = 32;
	const int Media = 64;
	const int Sub = 128;
	const int Ping = 256;
	const int Socket = 512;
	const int Document = 1024;
	const int DefaultTypes = Script | Image | Style | Xhr | Other | Font | Media | Sub | Ping | Socket;
	const int AllTypes = DefaultTypes | Document;

	public static readonly AdBlock Shared = new();

	static readonly HttpClient Http = new(new HttpClientHandler
	{
		AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
	})
	{ Timeout = TimeSpan.FromSeconds(90) };

	static readonly (string Name, string Url)[] Sources =
	[
		("easylist.txt", "https://easylist.to/easylist/easylist.txt"),
		("easyprivacy.txt", "https://easylist.to/easylist/easyprivacy.txt"),
		("peter-lowe.txt", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext"),
		("urlhaus.txt", "https://malware-filter.gitlab.io/malware-filter/urlhaus-filter-ag-online.txt")
	];

	readonly object _gate = new();
	readonly SemaphoreSlim _load = new(1, 1);
	FilterSet _set = FilterSet.Parse("");
	string _status = "On. Loading filter lists.";
	string _loaded = "";
	int _started;
	volatile int _mode;
	volatile bool _enabled;

	AdBlock()
	{
		_enabled = ReadFlag();
		_mode = ReadMode();
		if (!_enabled) _status = "Off.";
	}

	public bool Enabled => _enabled;

	public int Mode => _mode;

	public string Status
	{
		get { lock (_gate) return _status; }
	}

	public event Action? Changed;

	public void Start()
	{
		if (Interlocked.Exchange(ref _started, 1) != 0) return;
		Task.Run(Load);
	}

	public void SetEnabled(bool on)
	{
		_enabled = on;
		try
		{
			Directory.CreateDirectory(Root());
			File.WriteAllText(FlagPath(), on ? "1" : "0");
		}
		catch { }
		if (on && _set.Network == 0 && _started != 0) Task.Run(Load);
		Note(on ? (_loaded.Length > 0 ? _loaded : "On. Loading filter lists.") : "Off.");
		Changed?.Invoke();
	}

	public void SetMode(int mode)
	{
		var next = mode == 1 ? 1 : mode == 2 ? 2 : 0;
		if (next == _mode) return;
		_mode = next;
		try
		{
			Directory.CreateDirectory(Root());
			File.WriteAllText(ModePath(), next switch { 1 => "medium", 2 => "hard", _ => "easy" });
		}
		catch { }
		Changed?.Invoke();
	}

	public bool Block(string pageUrl, string requestUrl, string kind, bool mainFrame) =>
		_enabled && _set.Blocks(pageUrl, requestUrl, kind, mainFrame, _mode);

	public string CosmeticScript() => _enabled ? _set.Script() : "";

	public static string SelfCheck()
	{
		const string list =
			"||ads.example^\n" +
			"@@||ads.example/good.js^\n" +
			"@@||ads.example/bad.js^\n" +
			"||ads.example/bad.js^$important\n" +
			"||news.example^$document\n" +
			"||skip.example^$csp=script-src 'none'\n" +
			"||popup.example^$popup\n" +
			"||tracker.example^$third-party\n" +
			"||cdn.example^$~third-party\n" +
			"||only.example^$domain=news.example\n" +
			"||logs.example.co.uk^$third-party\n" +
			"||file.example/banner.js^\n" +
			"||wild.example/bann*.js^\n" +
			"||127.0.0.1^\n" +
			"##.ad-box\n" +
			"example.com#@#.ad-box\n" +
			"ads.example##.sponsor\n" +
			"||plain.example/banner.js^$match-case\n" +
			"||bad.example^$all\n" +
			"||redir.example/ad.js^$script,redirect=noopjs\n" +
			"||rule.example/ad.js^$script,redirect-rule=noopjs\n" +
			"||obj.example/ad^$object\n" +
			"||beacon.example/p^$beacon\n" +
			"||keep.example/a.js^$script,collapse\n" +
			"||strip.example/hit^$removeparam=utm_source\n" +
			"@@||cdn-3p.example/lib.js^$important\n" +
			"@@||cdn-3p.example/widget.js^\n";
		var set = FilterSet.Parse(list);
		var failures = new List<string>();
		if (!set.Blocks("https://news.example/", "https://ads.example/banner.js", "script", false)) failures.Add("anchor");
		if (set.Blocks("https://news.example/", "https://ads.example/good.js", "script", false)) failures.Add("exception");
		if (!set.Blocks("https://news.example/", "https://ads.example/bad.js", "script", false)) failures.Add("important");
		if (set.Blocks("https://news.example/", "https://news.example/", "document", true)) failures.Add("main frame");
		if (!set.Blocks("https://news.example/", "https://ads.example/", "subdocument", false)) failures.Add("frame");
		if (set.Blocks("https://news.example/", "https://skip.example/a.js", "script", false)) failures.Add("csp");
		if (set.Blocks("https://news.example/", "https://popup.example/a.js", "script", false)) failures.Add("popup");
		if (set.Blocks("https://tracker.example/", "https://tracker.example/a.js", "script", false)) failures.Add("first party");
		if (!set.Blocks("https://news.example/", "https://tracker.example/a.js", "script", false)) failures.Add("third party");
		if (set.Blocks("https://news.example/", "https://cdn.example/a.js", "script", false)) failures.Add("first-party option");
		if (!set.Blocks("https://cdn.example/", "https://cdn.example/a.js", "script", false)) failures.Add("same site");
		if (set.Blocks("https://other.example/", "https://only.example/a.js", "script", false)) failures.Add("domain option");
		if (!set.Blocks("https://news.example/", "https://only.example/a.js", "script", false)) failures.Add("domain hit");
		if (set.Blocks("https://www.example.co.uk/", "https://logs.example.co.uk/a.js", "script", false)) failures.Add("suffix");
		if (!set.Blocks("https://www.example.com/", "https://logs.example.co.uk/a.js", "script", false)) failures.Add("suffix third");
		if (set.Blocks("https://news.example/", "https://file.example/banner.js.extra/x", "script", false)) failures.Add("separator");
		if (!set.Blocks("https://news.example/", "https://file.example/banner.js", "script", false)) failures.Add("file");
		if (!set.Blocks("https://news.example/", "https://wild.example/banner.js", "image", false)) failures.Add("wildcard");
		if (!set.Blocks("https://news.example/", "https://x.ads.example/a.js", "script", false)) failures.Add("subdomain");
		if (set.Blocks("https://news.example/", "https://notads.example/a.js", "script", false)) failures.Add("host boundary");
		if (set.Blocks("https://news.example/", "http://127.0.0.1/a.js", "script", false)) failures.Add("loopback");
		if (set.Blocks("https://news.example/", "http://localhost/a.js", "script", false)) failures.Add("localhost");
		var home = set.CosmeticFor("news.example");
		if (!home.Contains(".ad-box", StringComparison.Ordinal)) failures.Add("cosmetic");
		if (set.CosmeticFor("example.com").Contains(".ad-box", StringComparison.Ordinal)) failures.Add("cosmetic exception");
		if (set.CosmeticFor("www.example.com").Contains(".ad-box", StringComparison.Ordinal)) failures.Add("parent exception");
		if (!set.CosmeticFor("www.ads.example").Contains(".sponsor", StringComparison.Ordinal)) failures.Add("specific");
		var script = set.Script();
		if (!script.Contains("guardex-ads", StringComparison.Ordinal) || !script.Contains(".ad-box", StringComparison.Ordinal))
			failures.Add("script");
		if (!script.Contains("guardex-player", StringComparison.Ordinal)) failures.Add("player");
		var tube = FilterSet.Parse("||youtube.com^\n@@||www.youtube.com^$generichide\n##.ad-box\n");
		if (!tube.Blocks("https://news.example/", "https://www.youtube.com/embed/x", "subdocument", false))
			failures.Add("generichide network");
		var tubeScript = tube.Script();
		if (!tubeScript.Contains("www.youtube.com", StringComparison.Ordinal) || !tubeScript.Contains("genericOff", StringComparison.Ordinal))
			failures.Add("generichide");
		var bare = FilterSet.Parse("");
		var bareScript = bare.Script();
		if (!bareScript.Contains("guardex-player", StringComparison.Ordinal)) failures.Add("player only");
		if (bareScript.Contains("ytInitialData", StringComparison.Ordinal) || bareScript.Contains("depth>", StringComparison.Ordinal))
			failures.Add("yt walk");
		if (!bareScript.Contains("ytInitialPlayerResponse", StringComparison.Ordinal)) failures.Add("yt arm");
		if (bareScript.Contains("guardex-ads", StringComparison.Ordinal)) failures.Add("empty css");
		if (!bare.Blocks("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/api/stats/ads?v=1", "xmlhttprequest", false))
			failures.Add("yt ads");
		if (!bare.Blocks("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/pagead/interaction", "script", false))
			failures.Add("yt pagead");
		if (!bare.Blocks("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/get_midroll_info", "script", false))
			failures.Add("yt midroll");
		if (!bare.Blocks("https://news.example/", "https://ad.doubleclick.net/pagead", "script", false))
			failures.Add("doubleclick");
		if (bare.Blocks("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc", "document", true))
			failures.Add("yt watch");
		if (bare.Blocks("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc", "xmlhttprequest", false))
			failures.Add("yt watch request");
		if (bare.Blocks("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/youtubei/v1/player?key=1", "xmlhttprequest", false))
			failures.Add("yt player");
		if (bare.Blocks("https://www.youtube.com/watch?v=abc", "https://rr3---sn-abc.googlevideo.com/videoplayback?id=1", "media", false))
			failures.Add("yt video");
		if (bare.Blocks("https://news.example/", "https://notdoubleclick.net/a.js", "script", false))
			failures.Add("doubleclick boundary");
		const string page = "https://news.example/";
		const string thirdScript = "https://cdn-3p.example/app.js";
		if (set.Blocks(page, thirdScript, "script", false)) failures.Add("easy 3p script");
		if (!set.Blocks(page, thirdScript, "script", false, 1)) failures.Add("medium 3p script");
		if (!set.Blocks(page, "https://cdn-3p.example/frame.html", "subdocument", false, 1)) failures.Add("medium 3p frame");
		if (set.Blocks(page, "https://cdn-3p.example/a.png", "image", false, 1)) failures.Add("medium 3p image");
		if (set.Blocks(page, "https://news.example/app.js", "script", false, 1)) failures.Add("medium 1p script");
		if (set.Blocks(page, "https://cdn-3p.example/lib.js", "script", false, 1)) failures.Add("medium important exception");
		if (!set.Blocks(page, "https://cdn-3p.example/widget.js", "script", false, 1)) failures.Add("medium plain exception");
		if (set.Blocks(page, "https://cdn-3p.example/widget.js", "script", false)) failures.Add("easy plain exception");
		if (!set.Blocks(page, "https://ads.example/bad.js", "script", false, 1)) failures.Add("medium important");
		if (!set.Blocks(page, "https://cdn-3p.example/a.png", "image", false, 2)) failures.Add("hard 3p image");
		if (set.Blocks(page, "https://news.example/a.png", "image", false, 2)) failures.Add("hard 1p image");
		if (set.Blocks(page, "https://news.example/", "document", true, 2)) failures.Add("hard main");
		if (set.Blocks(page, "http://127.0.0.1/a.js", "script", false, 2)) failures.Add("hard loopback");
		if (set.Blocks(page, "http://localhost/a.js", "script", false, 2)) failures.Add("hard localhost");
		if (!set.Blocks("https://bad.example/", "https://bad.example/a.js", "script", false)) failures.Add("all script");
		if (!set.Blocks(page, "https://bad.example/a.png", "image", false)) failures.Add("all image");
		if (set.Blocks("https://bad.example/", "https://bad.example/", "document", true)) failures.Add("all main");
		if (!set.Blocks(page, "https://redir.example/ad.js", "script", false)) failures.Add("redirect");
		if (set.Blocks(page, "https://redir.example/ad.js", "image", false)) failures.Add("redirect type");
		if (set.Blocks(page, "https://rule.example/ad.js", "script", false)) failures.Add("redirect-rule");
		if (!set.Blocks(page, "https://obj.example/ad", "other", false)) failures.Add("object");
		if (set.Blocks(page, "https://obj.example/ad", "script", false)) failures.Add("object type");
		if (!set.Blocks(page, "https://beacon.example/p", "ping", false)) failures.Add("beacon");
		if (set.Blocks(page, "https://beacon.example/p", "script", false)) failures.Add("beacon type");
		if (!set.Blocks(page, "https://keep.example/a.js", "script", false)) failures.Add("collapse");
		if (set.Blocks(page, "https://strip.example/hit", "script", false)) failures.Add("removeparam");
		if (bare.Blocks("https://www.youtube.com/watch?v=abc", "https://rr3---sn-abc.googlevideo.com/videoplayback?id=1", "media", false, 1))
			failures.Add("yt video medium");
		if (bare.Blocks("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc", "document", true, 2))
			failures.Add("yt watch hard");
		if (!bare.Blocks("https://www.youtube.com/watch?v=abc", "https://rr3---sn-abc.googlevideo.com/videoplayback?id=1", "media", false, 2))
			failures.Add("yt video hard");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	async Task Load()
	{
		await _load.WaitAsync();
		try
		{
			var dir = FilterDir();
			Directory.CreateDirectory(dir);
			var paths = Sources.Select(item => Path.Combine(dir, item.Name)).ToArray();
			if (paths.Any(File.Exists)) Publish(FilterSet.Parse(ReadAll(paths)), true);
			if (!_enabled) return;
			if (!paths.Any(Stale)) return;
			if (VpnHub.Require && VpnHub.HasConfig && !VpnHub.TunnelOn)
			{
				Note(_set.Network == 0
					? "On. Filter lists are waiting for the tunnel."
					: "On. Saved lists are in use. An update is waiting for the tunnel.");
				Changed?.Invoke();
				return;
			}
			var fresh = true;
			foreach (var item in Sources)
			{
				var path = Path.Combine(dir, item.Name);
				if (!Stale(path) || await Download(item.Url, path)) continue;
				fresh = false;
			}
			if (paths.Any(File.Exists)) Publish(FilterSet.Parse(ReadAll(paths)), fresh);
			else
			{
				Note("On. Filter lists are not on disk yet.");
				Changed?.Invoke();
			}
		}
		catch (Exception ex)
		{
			Note("On. " + ex.Message);
			Changed?.Invoke();
		}
		finally
		{
			_load.Release();
		}
	}

	void Publish(FilterSet set, bool fresh)
	{
		_set = set;
		var state = set.Network == 0
			? "On. No filter rules are loaded."
			: "On. " + set.Network + " network rules and " + set.CosmeticCount + " hiding rules are loaded.";
		if (!fresh) state += " The latest download did not finish, so the saved lists stay.";
		_loaded = state;
		if (_enabled) Note(state);
		Changed?.Invoke();
	}

	static async Task<bool> Download(string url, string dest)
	{
		byte[]? body;
		if (VpnHub.Require && VpnHub.HasConfig)
		{
			if (!VpnHub.TunnelOn) return false;
			var status = 0;
			var raw = await Task.Run(() =>
			{
				var got = VpnHub.HttpGet(url, 90000, "GUARDEX", out status, out var data);
				return got ? data : null;
			});
			if (raw == null || status != 200) return false;
			body = Gunzip(raw);
		}
		else
		{
			using var response = await Http.GetAsync(url);
			if (!response.IsSuccessStatusCode) return false;
			body = await response.Content.ReadAsByteArrayAsync();
		}
		if (body == null || body.Length < 64 || body.Length > 20 * 1024 * 1024) return false;
		var text = Encoding.UTF8.GetString(body);
		if (!FilterText(text)) return false;
		var part = dest + ".part";
		await File.WriteAllTextAsync(part, text, new UTF8Encoding(false));
		File.Move(part, dest, true);
		return true;
	}

	static byte[] Gunzip(byte[] body)
	{
		if (body.Length < 2 || body[0] != 0x1f || body[1] != 0x8b) return body;
		try
		{
			using var input = new MemoryStream(body);
			using var gzip = new GZipStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			gzip.CopyTo(output);
			var done = output.ToArray();
			return done.Length == 0 || done.Length > 20 * 1024 * 1024 ? body : done;
		}
		catch
		{
			return body;
		}
	}

	static string ReadAll(IEnumerable<string> paths)
	{
		var sb = new StringBuilder();
		foreach (var path in paths)
		{
			try { sb.Append(File.ReadAllText(path)).Append('\n'); }
			catch { }
		}
		return sb.ToString();
	}

	static bool Stale(string path)
	{
		if (!File.Exists(path)) return true;
		try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(4); }
		catch { return true; }
	}

	static bool FilterText(string text)
	{
		if (text.Contains("<html", StringComparison.OrdinalIgnoreCase) || text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
			return false;
		if (text.Contains("[Adblock", StringComparison.OrdinalIgnoreCase)) return true;
		return text.Contains("! Title:", StringComparison.Ordinal) && text.Contains("||", StringComparison.Ordinal);
	}

	static int ReadMode()
	{
		try
		{
			var path = ModePath();
			if (!File.Exists(path)) return 0;
			var text = File.ReadAllText(path).Trim().ToLowerInvariant();
			if (text is "medium" or "1") return 1;
			if (text is "hard" or "2") return 2;
			return 0;
		}
		catch
		{
			return 0;
		}
	}

	static bool ReadFlag()
	{
		try
		{
			var path = FlagPath();
			if (!File.Exists(path)) return true;
			return File.ReadAllText(path).Trim() != "0";
		}
		catch
		{
			return true;
		}
	}

	void Note(string text)
	{
		lock (_gate) _status = text;
	}

	static string Root() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");

	static string FilterDir() => Path.Combine(Root(), "filters");

	static string FlagPath() => Path.Combine(Root(), "adblock.txt");

	static string ModePath() => Path.Combine(Root(), "adblock-mode.txt");

	sealed class FilterSet
	{
		public static readonly FilterSet Empty = new();
		readonly Dictionary<string, List<Rule>> _hosts = new(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, List<Rule>> _tokens = new(StringComparer.Ordinal);
		readonly List<Rule> _slow = [];
		readonly List<string> _generic = [];
		readonly HashSet<string> _globalBan = new(StringComparer.Ordinal);
		readonly Dictionary<string, List<string>> _specific = new(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, List<string>> _ban = new(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> _genericHide = new(StringComparer.OrdinalIgnoreCase);
		string? _script;

		public int Network { get; private set; }
		public int CosmeticCount { get; private set; }

		static readonly string[] BuiltIn =
		[
			"||youtube.com/api/stats/ads^",
			"||youtube.com/pagead^",
			"||youtube.com/get_midroll_",
			"||youtube.com/ptracking^",
			"||doubleclick.net^$important",
			"||googlesyndication.com^$important",
			"||googleadservices.com^$important"
		];

		public static FilterSet Parse(string text)
		{
			var set = new FilterSet();
			foreach (var line in BuiltIn) set.Take(line);
			var start = 0;
			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] != '\n' && text[i] != '\r') continue;
				set.Take(text.Substring(start, i - start));
				if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
				start = i + 1;
			}
			if (start < text.Length) set.Take(text[start..]);
			return set;
		}

		public bool Blocks(string pageUrl, string requestUrl, string kind, bool mainFrame, int mode = 0)
		{
			if (mainFrame || requestUrl.Length == 0) return false;
			if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)) return false;
			if (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ws" && uri.Scheme != "wss") return false;
			var host = uri.IdnHost.Trim('.').ToLowerInvariant();
			if (host.Length == 0 || Loopback(host)) return false;
			var bit = TypeBit(kind);
			if (bit == 0) return false;
			var pageHost = PageHost(pageUrl);
			var third = pageHost.Length == 0 || Registrable(pageHost) != Registrable(host);
			var blocked = false;
			var exception = false;
			var exceptionImportant = false;
			var important = false;
			void Consider(Rule rule)
			{
				if ((rule.Types & bit) == 0) return;
				if (rule.Party == 1 && !third) return;
				if (rule.Party == 2 && third) return;
				if (!PageOk(rule, pageHost)) return;
				if (!rule.Matches(requestUrl)) return;
				if (rule.Exception)
				{
					if (rule.Important) exceptionImportant = true;
					else exception = true;
				}
				else if (rule.Important) important = true;
				else blocked = true;
			}
			var name = host;
			while (name.Length > 0)
			{
				if (_hosts.TryGetValue(name, out var list))
					foreach (var rule in list) Consider(rule);
				var dot = name.IndexOf('.');
				if (dot <= 0 || dot >= name.Length - 1) break;
				name = name[(dot + 1)..];
			}
			foreach (var token in Tokens(requestUrl))
			{
				if (_tokens.TryGetValue(token, out var list))
					foreach (var rule in list) Consider(rule);
			}
			foreach (var rule in _slow) Consider(rule);
			if (exceptionImportant) return false;
			if (important) return true;
			if (ModeHit(mode, third, bit)) return true;
			if (exception) return false;
			return blocked;
		}

		public string CosmeticFor(string host)
		{
			var ban = new HashSet<string>(_globalBan, StringComparer.Ordinal);
			var extra = new List<string>();
			var name = (host ?? "").Trim('.').ToLowerInvariant();
			while (name.Length > 0)
			{
				if (_ban.TryGetValue(name, out var banned))
					foreach (var selector in banned) ban.Add(selector);
				if (_specific.TryGetValue(name, out var added)) extra.AddRange(added);
				var dot = name.IndexOf('.');
				if (dot <= 0 || dot >= name.Length - 1) break;
				name = name[(dot + 1)..];
			}
			var sb = new StringBuilder();
			foreach (var selector in _generic)
			{
				if (ban.Contains(selector)) continue;
				if (sb.Length > 0) sb.Append('\n');
				sb.Append(selector);
			}
			foreach (var selector in extra)
			{
				if (ban.Contains(selector)) continue;
				if (sb.Length > 0) sb.Append('\n');
				sb.Append(selector);
			}
			return sb.ToString();
		}

		public string Script()
		{
			if (_script != null) return _script;
			var sb = new StringBuilder();
			if (_generic.Count > 0 || _specific.Count > 0)
			{
				sb.Append("(function(){var generic=").Append(Js(string.Join('\n', _generic)));
				sb.Append(";var globalBan=");
				Map(sb, _globalBan.ToDictionary(item => item, _ => 1));
				sb.Append(";var specific=");
				Obj(sb, _specific);
				sb.Append(";var ban=");
				Obj(sb, _ban);
				sb.Append(";var genericHide=");
				Map(sb, _genericHide.ToDictionary(item => item, _ => 1));
				sb.Append(";function walk(host,fn){var parts=(host||'').split('.');for(var i=0;i<parts.length;i++){var d=parts.slice(i).join('.');if(d)fn(d);}}");
				sb.Append("function genericOff(host){var off=false;walk(host,function(d){if(genericHide[d])off=true;});return off;}");
				sb.Append("function build(host){var set={};for(var k in globalBan)set[k]=1;walk(host,function(d){var list=ban[d];if(list)for(var j=0;j<list.length;j++)set[list[j]]=1;});");
				sb.Append("var css='',lines=genericOff(host)||!generic.length?[]:generic.split('\\n'),extra=[];");
				sb.Append("for(var n=0;n<lines.length;n++){var s=lines[n];if(s&&!set[s])css+=s+'{display:none!important}\\n';}");
				sb.Append("walk(host,function(d){var list=specific[d];if(!list)return;for(var j=0;j<list.length;j++)if(!set[list[j]])extra.push(list[j]);});");
				sb.Append("for(var m=0;m<extra.length;m++)css+=extra[m]+'{display:none!important}\\n';return css;}");
				sb.Append("function apply(){var root=document.documentElement;if(!root)return;var css=build((location.hostname||'').toLowerCase());if(!css)return;");
				sb.Append("var el=document.getElementById('guardex-ads');if(!el){el=document.createElement('style');el.id='guardex-ads';(document.head||root).appendChild(el);}if(el.textContent!==css)el.textContent=css;}");
				sb.Append("apply();if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',apply);");
				sb.Append("try{new MutationObserver(function(){if(!document.getElementById('guardex-ads'))apply();}).observe(document.documentElement||document,{childList:true,subtree:true});}catch(e){}})();");
			}
			sb.Append(PlayerScript());
			return _script = sb.ToString();
		}

		static string PlayerScript() => """
			(function(){
			var host=(location.hostname||'').toLowerCase();
			if(!/(^|\.)youtube(-nocookie)?\.com$/.test(host))return;
			window.__guardexPlayerStop=0;
			if(window.__guardexPlayer)return;
			window.__guardexPlayer=1;
			/* guardex-player */
			function clearAds(node){
			  if(!node||typeof node!=='object')return;
			  try{if(node.adPlacements)node.adPlacements=[];}catch(e){}
			  try{if(node.playerAds)node.playerAds=[];}catch(e){}
			  try{if(node.adSlots)node.adSlots=[];}catch(e){}
			}
			function arm(name){
			  var slot;
			  try{
			    Object.defineProperty(window,name,{
			      configurable:true,enumerable:true,
			      get:function(){return slot;},
			      set:function(value){try{clearAds(value);}catch(e){}slot=value;}
			    });
			  }catch(e){}
			}
			arm('ytInitialPlayerResponse');
			if(!window.__guardexParse)window.__guardexParse=JSON.parse;
			JSON.parse=function(text,reviver){
			  var value=window.__guardexParse.call(JSON,text,reviver);
			  try{
			    if(!window.__guardexPlayerStop&&value&&typeof value==='object'&&(value.playabilityStatus||value.videoDetails))
			      clearAds(value);
			  }catch(e){}
			  return value;
			};
			var slots='ytd-ad-slot-renderer,ytd-banner-promo-renderer,ytd-merch-shelf-renderer,ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"]';
			function tick(){
			  if(window.__guardexPlayerStop)return;
			  var player=document.querySelector('.html5-video-player.ad-showing');
			  if(player){
			    var video=player.querySelector('video');
			    if(video&&isFinite(video.duration)&&video.duration>0&&video.currentTime<video.duration-0.25){
			      try{video.currentTime=video.duration;}catch(e){}
			    }
			    var btn=player.querySelector('.ytp-skip-ad-button,.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-ad-skip-button-slot button');
			    if(btn){try{btn.click();}catch(e){}}
			  }
			  var ads=document.querySelectorAll(slots);
			  for(var n=0;n<ads.length;n++)ads[n].style.setProperty('display','none','important');
			}
			tick();
			setInterval(tick,400);
			})();
			""";

		void Take(string raw)
		{
			var line = raw.Trim();
			if (line.Length == 0 || line.Length > 16384 || line[0] == '!' || line[0] == '[') return;
			if (line.Contains("#?#", StringComparison.Ordinal) || line.Contains("#$#", StringComparison.Ordinal)) return;
			if (TakeCosmetic(line)) return;
			if (TakeGenericHide(line)) return;
			var rule = Rule.Try(line);
			if (rule == null) return;
			Network++;
			if (rule.Domain && rule.Host.Length > 0)
			{
				Add(_hosts, rule.Host, rule);
				return;
			}
			var token = LongestToken(rule.Body);
			if (token.Length >= 4) Add(_tokens, token, rule);
			else _slow.Add(rule);
		}

		bool TakeGenericHide(string line)
		{
			if (!line.StartsWith("@@", StringComparison.Ordinal)) return false;
			var dollar = line.IndexOf('$');
			if (dollar < 0) return false;
			var hide = false;
			foreach (var raw in line[(dollar + 1)..].Split(','))
			{
				var bare = raw.Trim().ToLowerInvariant();
				if (bare.Length == 0) continue;
				if (bare is "generichide" or "ghide") hide = true;
				else return false;
			}
			if (!hide) return false;
			var pattern = line[2..dollar].Trim();
			if (!pattern.StartsWith("||", StringComparison.Ordinal)) return false;
			pattern = pattern[2..];
			var n = 0;
			while (n < pattern.Length && IsHostChar(pattern[n])) n++;
			if (n == 0) return false;
			_genericHide.Add(pattern[..n].ToLowerInvariant());
			return true;
		}

		bool TakeCosmetic(string line)
		{
			var at = line.IndexOf("#@#", StringComparison.Ordinal);
			var exception = at >= 0;
			var width = 3;
			if (!exception)
			{
				at = line.IndexOf("##", StringComparison.Ordinal);
				width = 2;
			}
			if (at < 0) return false;
			var domains = line[..at].Trim();
			if (!DomainList(domains)) return false;
			var selector = line[(at + width)..].Trim();
			if (selector.Length == 0 || selector.Length > 1000) return true;
			foreach (var ch in selector)
				if (ch is '{' or '}' or '<' or '>' or '\\' || ch < 32) return true;
			var includes = new List<string>();
			var excludes = new List<string>();
			if (domains.Length > 0)
			{
				foreach (var part in domains.Split(','))
				{
					var item = part.Trim().TrimStart('.').ToLowerInvariant();
					if (item.Length == 0) continue;
					if (item[0] == '~')
					{
						var rest = item[1..].TrimStart('.');
						if (rest.Length > 0) excludes.Add(rest);
					}
					else includes.Add(item);
				}
			}
			if (exception)
			{
				if (includes.Count == 0) _globalBan.Add(selector);
				foreach (var item in includes) Ban(item, selector);
				CosmeticCount++;
				return true;
			}
			if (includes.Count == 0) _generic.Add(selector);
			else foreach (var item in includes) Add(_specific, item, selector);
			foreach (var item in excludes) Ban(item, selector);
			CosmeticCount++;
			return true;
		}

		void Ban(string domain, string selector) => Add(_ban, domain, selector);

		static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
		{
			if (!map.TryGetValue(key, out var list)) map[key] = list = [];
			list.Add(value);
		}

		static void Obj(StringBuilder sb, Dictionary<string, List<string>> map)
		{
			sb.Append('{');
			var first = true;
			foreach (var pair in map)
			{
				if (!first) sb.Append(',');
				first = false;
				sb.Append(Js(pair.Key)).Append(':').Append('[');
				for (var i = 0; i < pair.Value.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append(Js(pair.Value[i]));
				}
				sb.Append(']');
			}
			sb.Append('}');
		}

		static void Map(StringBuilder sb, Dictionary<string, int> map)
		{
			sb.Append('{');
			var first = true;
			foreach (var pair in map)
			{
				if (!first) sb.Append(',');
				first = false;
				sb.Append(Js(pair.Key)).Append(":1");
			}
			sb.Append('}');
		}
	}

	sealed class Rule
	{
		public bool Exception;
		public bool Important;
		public bool Domain;
		public bool Start;
		public bool End;
		public int Types;
		public int Party;
		public string Host = "";
		public string Body = "";
		public string[]? Include;
		public string[]? Exclude;

		public static Rule? Try(string line)
		{
			var exception = false;
			if (line.StartsWith("@@", StringComparison.Ordinal))
			{
				exception = true;
				line = line[2..];
			}
			var dollar = line.IndexOf('$');
			var pattern = (dollar < 0 ? line : line[..dollar]).Trim();
			var options = dollar < 0 ? "" : line[(dollar + 1)..];
			if (!TryOptions(options, out var types, out var party, out var important, out var include, out var exclude))
				return null;
			var end = pattern.EndsWith('|') && !pattern.EndsWith("||", StringComparison.Ordinal);
			if (end) pattern = pattern[..^1];
			var domain = false;
			var start = false;
			if (pattern.StartsWith("||", StringComparison.Ordinal))
			{
				domain = true;
				pattern = pattern[2..];
			}
			else if (pattern.StartsWith("|", StringComparison.Ordinal))
			{
				start = true;
				pattern = pattern[1..];
			}
			if (pattern.Length == 0 || pattern is "*" or "^" or "*^" or "^*") return null;
			if (pattern.Count(ch => ch == '*') > 6) return null;
			var host = "";
			if (domain)
			{
				var n = 0;
				while (n < pattern.Length && IsHostChar(pattern[n])) n++;
				if (n == 0) return null;
				host = pattern[..n].ToLowerInvariant();
			}
			return new Rule
			{
				Exception = exception,
				Important = important,
				Domain = domain,
				Start = start,
				End = end,
				Types = types,
				Party = party,
				Host = host,
				Body = pattern,
				Include = include,
				Exclude = exclude
			};
		}

		public bool Matches(string url)
		{
			if (Domain)
			{
				if (!Locate(url, out var at, out var length)) return false;
				var host = url.Substring(at, length);
				if (!HostUnder(host, Host)) return false;
				var from = at + host.Length - Host.Length;
				return from >= at && MatchHere(url, from, Body, 0, End);
			}
			var literal = LongestLiteral(Body);
			if (literal.Length > 0 && url.IndexOf(literal, StringComparison.OrdinalIgnoreCase) < 0) return false;
			if (Start) return MatchHere(url, 0, Body, 0, End);
			for (var i = 0; i <= url.Length; i++)
				if (MatchHere(url, i, Body, 0, End)) return true;
			return false;
		}
	}

	static bool TryOptions(string text, out int types, out int party, out bool important, out string[]? include, out string[]? exclude)
	{
		types = DefaultTypes;
		party = 0;
		important = false;
		include = null;
		exclude = null;
		if (text.Length == 0) return true;
		var positive = 0;
		var negative = 0;
		var any = false;
		var includes = new List<string>();
		var excludes = new List<string>();
		foreach (var raw in text.Split(','))
		{
			var opt = raw.Trim();
			if (opt.Length == 0) continue;
			var eq = opt.IndexOf('=');
			var name = (eq >= 0 ? opt[..eq] : opt).Trim();
			var value = eq >= 0 ? opt[(eq + 1)..] : "";
			var neg = name.StartsWith('~');
			var bare = (neg ? name[1..] : name).ToLowerInvariant();
			if (bare is "match-case" or "collapse" or "donottrack" or "redirect") continue;
			if (bare is "csp" or "popup" or "popunder" or "elemhide" or "ehide"
				or "generichide" or "ghide" or "genericblock" or "specifichide" or "shide"
				or "badfilter" or "removeparam" or "replace" or "rewrite" or "redirect-rule"
				or "header" or "method" or "webrtc" or "inline-script" or "inline-font")
				return false;
			if (bare == "important")
			{
				if (!neg) important = true;
				continue;
			}
			if (bare == "domain")
			{
				if (neg || value.Length == 0) return false;
				foreach (var part in value.Split('|'))
				{
					var item = part.Trim().TrimStart('.').ToLowerInvariant();
					if (item.Length == 0) continue;
					if (item[0] == '~')
					{
						var rest = item[1..].TrimStart('.');
						if (rest.Length > 0) excludes.Add(rest);
					}
					else includes.Add(item);
				}
				continue;
			}
			if (bare is "third-party" or "3p")
			{
				var next = neg ? 2 : 1;
				if (party != 0 && party != next) return false;
				party = next;
				continue;
			}
			if (bare is "first-party" or "1p")
			{
				var next = neg ? 1 : 2;
				if (party != 0 && party != next) return false;
				party = next;
				continue;
			}
			if (bare == "all" && !neg)
			{
				any = true;
				positive = AllTypes;
				continue;
			}
			if (!TryType(bare, out var bit)) return false;
			if (neg) negative |= bit;
			else
			{
				any = true;
				positive |= bit;
			}
		}
		types = (any ? positive : DefaultTypes) & ~negative;
		if (types == 0) return false;
		if (includes.Count > 0) include = includes.ToArray();
		if (excludes.Count > 0) exclude = excludes.ToArray();
		return true;
	}

	static bool TryType(string name, out int bit)
	{
		bit = name switch
		{
			"script" => Script,
			"image" => Image,
			"stylesheet" or "css" => Style,
			"xmlhttprequest" or "xhr" => Xhr,
			"other" or "object" or "object-subrequest" => Other,
			"font" => Font,
			"media" => Media,
			"subdocument" or "frame" => Sub,
			"ping" or "beacon" => Ping,
			"websocket" => Socket,
			"document" or "doc" => Document,
			_ => 0
		};
		return bit != 0;
	}

	static bool ModeHit(int mode, bool third, int bit)
	{
		if (mode <= 0 || !third || (bit & Document) != 0) return false;
		if (mode == 1) return (bit & (Script | Sub)) != 0;
		return true;
	}

	static int TypeBit(string kind) => kind switch
	{
		"script" => Script,
		"image" => Image,
		"stylesheet" => Style,
		"xmlhttprequest" or "xhr" => Xhr,
		"other" => Other,
		"font" => Font,
		"media" => Media,
		"subdocument" => Sub,
		"ping" => Ping,
		"websocket" => Socket,
		"document" => Document,
		_ => 0
	};

	static bool PageOk(Rule rule, string pageHost)
	{
		if (rule.Exclude != null)
			foreach (var domain in rule.Exclude)
				if (HostUnder(pageHost, domain)) return false;
		if (rule.Include == null) return true;
		if (pageHost.Length == 0) return false;
		foreach (var domain in rule.Include)
			if (HostUnder(pageHost, domain)) return true;
		return false;
	}

	static bool HostUnder(string host, string needle)
	{
		if (host.Length == 0 || needle.Length == 0) return false;
		if (host.Equals(needle, StringComparison.OrdinalIgnoreCase)) return true;
		return host.Length > needle.Length
			&& host.EndsWith(needle, StringComparison.OrdinalIgnoreCase)
			&& host[host.Length - needle.Length - 1] == '.';
	}

	static bool MatchHere(string url, int ui, string pat, int pi, bool endAnchor)
	{
		while (pi < pat.Length)
		{
			var ch = pat[pi];
			if (ch == '*')
			{
				pi++;
				if (pi >= pat.Length) return true;
				for (var u = ui; u <= url.Length; u++)
					if (MatchHere(url, u, pat, pi, endAnchor)) return true;
				return false;
			}
			if (ch == '^')
			{
				if (ui < url.Length && IsSep(url[ui])) ui++;
				else if (ui != url.Length) return false;
				pi++;
				continue;
			}
			if (ui >= url.Length || char.ToLowerInvariant(url[ui]) != char.ToLowerInvariant(ch)) return false;
			ui++;
			pi++;
		}
		return !endAnchor || ui >= url.Length;
	}

	static bool Locate(string url, out int start, out int length)
	{
		start = 0;
		length = 0;
		var scheme = url.IndexOf("://", StringComparison.Ordinal);
		var i = scheme >= 0 ? scheme + 3 : 0;
		if (i > url.Length) return false;
		var slash = url.IndexOfAny(['/', '?', '#'], i);
		var authEnd = slash < 0 ? url.Length : slash;
		if (authEnd > i)
		{
			var at = url.LastIndexOf('@', authEnd - 1, authEnd - i);
			if (at >= i) i = at + 1;
		}
		if (i >= url.Length || i >= authEnd) return false;
		if (url[i] == '[')
		{
			var close = url.IndexOf(']', i + 1);
			if (close < 0 || close >= authEnd) return false;
			start = i + 1;
			length = close - start;
			return length > 0;
		}
		var hostEnd = authEnd;
		var colon = url.LastIndexOf(':', authEnd - 1, authEnd - i);
		if (colon > i) hostEnd = colon;
		start = i;
		length = hostEnd - start;
		return length > 0;
	}

	static bool Loopback(string host)
	{
		if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
		var text = host.Trim('[', ']');
		return IPAddress.TryParse(text, out var ip) && IPAddress.IsLoopback(ip);
	}

	static string PageHost(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
		return uri.IdnHost.Trim('.').ToLowerInvariant();
	}

	static string Registrable(string host)
	{
		if (host.Length == 0 || IPAddress.TryParse(host, out _)) return host;
		var labels = host.Split('.');
		if (labels.Length <= 2) return host;
		var tail = labels[^2] + "." + labels[^1];
		if (Multi.Contains(tail) && labels.Length >= 3)
			return labels[^3] + "." + tail;
		return tail;
	}

	static IEnumerable<string> Tokens(string url)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var count = 0;
		var i = 0;
		while (i < url.Length && count < 48)
		{
			while (i < url.Length && !TokenChar(url[i])) i++;
			var start = i;
			while (i < url.Length && TokenChar(url[i])) i++;
			if (i - start < 4) continue;
			var token = url.Substring(start, i - start).ToLowerInvariant();
			if (!seen.Add(token)) continue;
			count++;
			yield return token;
		}
	}

	static string LongestToken(string pattern)
	{
		var best = "";
		var i = 0;
		while (i < pattern.Length)
		{
			while (i < pattern.Length && !TokenChar(pattern[i])) i++;
			var start = i;
			while (i < pattern.Length && TokenChar(pattern[i])) i++;
			if (i - start >= 4 && i - start > best.Length)
				best = pattern.Substring(start, i - start).ToLowerInvariant();
		}
		return best;
	}

	static string LongestLiteral(string pattern)
	{
		var best = "";
		var i = 0;
		while (i < pattern.Length)
		{
			while (i < pattern.Length && (pattern[i] == '*' || pattern[i] == '^')) i++;
			var start = i;
			while (i < pattern.Length && pattern[i] != '*' && pattern[i] != '^') i++;
			if (i - start > best.Length) best = pattern.Substring(start, i - start);
		}
		return best;
	}

	static bool TokenChar(char ch) =>
		ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '%';

	static bool IsHostChar(char ch) =>
		ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-';

	static bool IsSep(char ch)
	{
		if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or '%') return false;
		return true;
	}

	static bool DomainList(string text)
	{
		foreach (var ch in text)
			if (!(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '~' or ',' or ' ' or '_')) return false;
		return true;
	}

	static string Js(string text)
	{
		var sb = new StringBuilder(text.Length + 2);
		sb.Append('"');
		foreach (var ch in text)
		{
			switch (ch)
			{
				case '\\': sb.Append("\\\\"); break;
				case '"': sb.Append("\\\""); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '<': sb.Append("\\u003c"); break;
				default:
					if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
					else sb.Append(ch);
					break;
			}
		}
		sb.Append('"');
		return sb.ToString();
	}

	static readonly HashSet<string> Multi = new(StringComparer.OrdinalIgnoreCase)
	{
		"co.uk", "org.uk", "ac.uk", "gov.uk", "com.au", "net.au", "org.au", "asn.au", "id.au",
		"co.nz", "com.br", "co.jp", "ne.jp", "or.jp", "co.kr", "com.mx", "co.za", "com.tr",
		"co.in", "com.cn", "com.tw", "com.hk", "com.sg", "co.id", "com.ar", "com.pl", "com.ua",
		"com.ng", "co.il", "com.eg", "com.sa", "com.pk", "com.vn", "co.th", "com.my", "com.ph",
		"com.bd", "com.pe", "com.co", "com.ve", "com.ec", "com.gt", "com.do", "com.uy", "com.pt", "com.es"
	};
}

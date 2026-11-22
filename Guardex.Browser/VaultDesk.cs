using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Guardex.Browser;

// Passwords and sign-in cookies are sealed for this PC.
// The sealing key cannot be exported. Each sealed value also checks this machine,
// so a copied credential does not open on another computer.
// A value is opened only for the site that asked for it.
static class VaultDesk
{
	const string Prefix = "Guardex.Vault.";
	const string NameTarget = Prefix + "Name";
	const string IndexTarget = Prefix + "Index";
	const int Chunk = 1800;

	static readonly object Gate = new();
	static VaultBox? _live;

	public readonly record struct SeenCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, bool Session, long Expires, string SameSite);

	public readonly record struct SavedLogin(string Site, string User);

	public readonly record struct CsvRow(string Site, string User, string Pass);

	public static string Script { get; } = """
		(function(){
		if(window.__guardexVault)return;
		window.__guardexVault=1;
		function pageOk(){
		  var h=(location.hostname||'').toLowerCase();
		  return location.protocol==='https:'||h==='localhost'||h==='127.0.0.1'||h==='::1';
		}
		function read(form){
		  if(!form||!form.querySelector)return null;
		  var pass=form.querySelector('input[type="password"]');
		  if(!pass||!pass.value)return null;
		  var user='';
		  var inputs=form.querySelectorAll('input');
		  for(var i=0;i<inputs.length;i++){
		    var t=(inputs[i].type||'').toLowerCase();
		    if(t==='password'||t==='hidden'||t==='submit'||t==='button'||t==='checkbox'||t==='radio')continue;
		    if(inputs[i].value){user=inputs[i].value;break;}
		  }
		  return {t:'save',u:user,p:pass.value};
		}
		function send(root){
		  var got=read(root);
		  if(!got)return;
		  try{chrome.webview.postMessage(got);}catch(e){}
		}
		function formOf(el){
		  if(!el)return null;
		  if(el.form)return el.form;
		  var node=el;
		  while(node&&node!==document){
		    if(node.tagName==='FORM')return node;
		    node=node.parentNode;
		  }
		  return null;
		}
		function isGo(el){
		  if(!el||!el.tagName)return false;
		  var tag=el.tagName;
		  if(tag==='INPUT'){
		    var t=(el.type||'').toLowerCase();
		    return t==='submit'||t==='image';
		  }
		  if(tag==='BUTTON'){
		    var bt=(el.type||'submit').toLowerCase();
		    return bt==='submit'||bt==='button'||bt==='';
		  }
		  var role=(el.getAttribute&&el.getAttribute('role')||'').toLowerCase();
		  return role==='button';
		}
		document.addEventListener('submit',function(ev){
		  if(!pageOk())return;
		  send(ev.target);
		},true);
		document.addEventListener('keydown',function(ev){
		  if(!pageOk()||ev.key!=='Enter')return;
		  var el=ev.target;
		  if(!el||(el.type||'').toLowerCase()!=='password')return;
		  send(formOf(el)||document);
		},true);
		document.addEventListener('click',function(ev){
		  if(!pageOk())return;
		  var el=ev.target;
		  while(el&&el!==document&&!isGo(el))el=el.parentNode;
		  if(!isGo(el))return;
		  var form=formOf(el);
		  if(form){
		    if(!form.querySelector('input[type="password"]'))return;
		    send(form);
		    return;
		  }
		  send(document);
		},true);
		document.addEventListener('focusin',function(ev){
		  if(!pageOk())return;
		  var el=ev.target;
		  if(!el||(el.type||'').toLowerCase()!=='password')return;
		  try{chrome.webview.postMessage({t:'focus'});}catch(e){}
		},true);
		})();
		""";

	public static bool TrySite(string? url, out string site)
	{
		site = "";
		if (string.IsNullOrWhiteSpace(url) || url.Length > 2000) return false;
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
		var scheme = uri.Scheme.ToLowerInvariant();
		var host = uri.IdnHost.Trim('.').ToLowerInvariant();
		if (host.Length == 0 || host.Length > 253) return false;
		var local = host is "localhost" or "127.0.0.1" or "::1";
		if (scheme == "http")
		{
			if (!local) return false;
		}
		else if (scheme != "https") return false;
		var shown = host.Contains(':') ? "[" + host + "]" : host;
		var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
		site = scheme + "://" + shown + port;
		return true;
	}

	public static string Label(string site)
	{
		if (!Uri.TryCreate(site, UriKind.Absolute, out var uri)) return site;
		var host = uri.IdnHost.Trim('.').ToLowerInvariant();
		if (host.Contains(':')) host = "[" + host + "]";
		return uri.IsDefaultPort ? host : host + ":" + uri.Port;
	}

	public static string Offer(string site, string user, string pass)
	{
		if (pass.Length == 0 || pass.Length > 1024 || user.Length > 256) return "";
		var box = TryLive(false);
		if (box == null) return "save";
		if (box.IsNever(site)) return "";
		if (box.SamePassword(site, user, pass)) return "";
		return box.HasPassword(site) ? "update" : "save";
	}

	public static bool HasPassword(string site) => TryLive(false)?.HasPassword(site) == true;

	public static bool HasSignIn(string site) => TryLive(false)?.HasSign(site) == true;

	public static int Remembered() => TryLive(false)?.Sites().Count ?? 0;

	public static string SavePassword(string site, string user, string pass)
	{
		if (!TrySite(site, out site) || pass.Length == 0 || pass.Length > 1024 || user.Length > 256)
			return "This PC could not store the password.";
		var box = TryLive(true);
		if (box == null) return "This PC could not store the password.";
		return box.SavePassword(site, user, pass) ? "" : "This PC could not store the password.";
	}

	public static bool TryPassword(string site, out string user, out string pass)
	{
		user = "";
		pass = "";
		var box = TryLive(false);
		if (box == null) return false;
		return box.TryPassword(site, out user, out pass);
	}

	public static SavedLogin[] Logins() => TryLive(false)?.Logins() ?? [];

	public static CsvRow[] ReadCsv(string? text, out int skipped, out int empty)
	{
		skipped = 0;
		empty = 0;
		if (string.IsNullOrWhiteSpace(text) || text.Length > 2_000_000) return [];
		var table = CsvTable(text.TrimStart('\uFEFF'));
		if (table.Count < 2) return [];
		var urlCol = Column(table[0], "url", "login_uri", "hostname", "origin");
		var userCol = Column(table[0], "username", "user", "login");
		var passCol = Column(table[0], "password", "pass");
		if (urlCol < 0 || passCol < 0) return [];
		var map = new Dictionary<string, CsvRow>(StringComparer.Ordinal);
		for (var i = 1; i < table.Count; i++)
		{
			var row = table[i];
			var pass = Cell(row, passCol);
			if (pass.Length == 0 || pass.Length > 1024)
			{
				empty++;
				continue;
			}
			var user = Cell(row, userCol);
			if (user.Length > 256) user = user[..256];
			if (!TrySite(Cell(row, urlCol), out var site))
			{
				skipped++;
				continue;
			}
			map[site] = new CsvRow(site, user, pass);
		}
		return map.Values.ToArray();
	}

	public static string ImportNote(int saved, int skipped, int empty)
	{
		if (saved == 1) return "Saved 1 password.";
		if (saved > 1) return "Saved " + saved + " passwords.";
		if (skipped > 0) return "Those rows need an https address, so they were not saved.";
		return "That file has no passwords to save.";
	}

	public static string ImportCsv(string? text)
	{
		var rows = ReadCsv(text, out var skipped, out var empty);
		var saved = 0;
		var failed = false;
		foreach (var row in rows)
		{
			if (SavePassword(row.Site, row.User, row.Pass).Length == 0) saved++;
			else failed = true;
		}
		if (saved > 0) return ImportNote(saved, 0, 0);
		if (failed) return "This PC could not store the password.";
		return ImportNote(0, skipped, empty);
	}

	public static bool SaveCookies(string site, IReadOnlyList<SeenCookie> cookies)
	{
		var box = TryLive(true);
		return box != null && box.SaveCookies(site, cookies);
	}

	public static SeenCookie[] Cookies(string site) => TryLive(false)?.Cookies(site) ?? [];

	// A domain cookie is stored with a leading dot. Older saves dropped that dot, which
	// made www.youtube.com a host cookie for youtube.com and Google reported CookieMismatch.
	public static string ScopeDomain(string site, string name, string? domain)
	{
		var raw = (domain ?? "").Trim().ToLowerInvariant();
		if (raw.Length == 0 || raw.StartsWith('.')) return raw;
		if ((name ?? "").StartsWith("__Host-", StringComparison.OrdinalIgnoreCase)) return raw;
		if (!TrySite(site, out var typed) || !Uri.TryCreate(typed, UriKind.Absolute, out var uri)) return raw;
		var host = uri.IdnHost.Trim('.').ToLowerInvariant();
		if (host.Length == 0 || host.Equals(raw, StringComparison.Ordinal)) return raw;
		if (host.EndsWith("." + raw, StringComparison.Ordinal)) return "." + raw;
		return raw;
	}

	public static string[] CookieNames(string site) => TryLive(false)?.CookieNames(site) ?? [];

	public static string[] SignSites() => TryLive(false)?.SignSites() ?? [];

	public static void Never(string site) => TryLive(true)?.Never(site);

	public static void ForgetSite(string site) => TryLive(false)?.ForgetSite(site);

	public static void ForgetAll()
	{
		lock (Gate)
		{
			_live?.ForgetAll();
			_live = null;
			CredShelf.DeleteAll();
		}
	}

	public static string FillScript(string user, string pass) =>
		"((u,p)=>{var pass=document.querySelector('input[type=\"password\"]');if(!pass)return;var root=pass.form||document;var user=null;var inputs=root.querySelectorAll('input');for(var i=0;i<inputs.length;i++){var t=(inputs[i].type||'').toLowerCase();if(t==='password'||t==='hidden'||t==='submit'||t==='button'||t==='checkbox'||t==='radio')continue;user=inputs[i];break;}if(user&&u){user.value=u;user.dispatchEvent(new Event('input',{bubbles:true}));user.dispatchEvent(new Event('change',{bubbles:true}));}pass.value=p;pass.dispatchEvent(new Event('input',{bubbles:true}));pass.dispatchEvent(new Event('change',{bubbles:true}));})("
		+ JsonSerializer.Serialize(user) + "," + JsonSerializer.Serialize(pass) + ")";

	public static bool ReadMessage(string? json, out string kind, out string user, out string pass)
	{
		kind = "";
		user = "";
		pass = "";
		if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return false;
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
			if (!doc.RootElement.TryGetProperty("t", out var type)) return false;
			kind = type.GetString() ?? "";
			if (kind == "focus") return true;
			if (kind != "save") return false;
			if (doc.RootElement.TryGetProperty("u", out var u)) user = u.GetString() ?? "";
			if (doc.RootElement.TryGetProperty("p", out var p)) pass = p.GetString() ?? "";
			if (user.Length > 256) user = user[..256];
			return pass.Length > 0 && pass.Length <= 1024;
		}
		catch
		{
			return false;
		}
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (!TrySite("https://Example.com/a", out var site) || site != "https://example.com") failures.Add("host");
		if (TrySite("http://example.com/", out _)) failures.Add("http");
		if (!TrySite("http://localhost:8080/a", out var local) || local != "http://localhost:8080") failures.Add("localhost");
		if (TrySite("https://www.example.com/", out var sub) && sub == "https://example.com") failures.Add("subdomain");
		if (!Script.Contains("https:", StringComparison.Ordinal) || !Script.Contains("password", StringComparison.Ordinal)
			|| !Script.Contains("submit", StringComparison.Ordinal) || !Script.Contains("keydown", StringComparison.Ordinal))
			failures.Add("script");
		var fill = FillScript("a\"b", "p\nq");
		var userJson = JsonSerializer.Serialize("a\"b");
		var passJson = JsonSerializer.Serialize("p\nq");
		if (!fill.EndsWith(userJson + "," + passJson + ")", StringComparison.Ordinal)
			|| passJson.Contains('\n')
			|| userJson.Contains("a\"b", StringComparison.Ordinal))
			failures.Add("fill");
		if (!ReadMessage("{\"t\":\"focus\"}", out var focusKind, out _, out _) || focusKind != "focus") failures.Add("focus message");
		if (ReadMessage("{\"t\":\"save\",\"u\":\"me\",\"p\":\"\"}", out _, out _, out _)) failures.Add("empty password");
		using var rsa = RSA.Create(2048);
		var machine = "machine-a"u8.ToArray();
		var other = "machine-b"u8.ToArray();
		var shelf = new MemoryShelf();
		var name = RandomNumberGenerator.GetBytes(32);
		var box = new VaultBox(shelf, rsa, machine, name, false);
		if (!box.SavePassword("https://example.com", "ada", "correct horse")) failures.Add("save");
		if (!box.TryPassword("https://example.com", out var user, out var pass) || user != "ada" || pass != "correct horse")
			failures.Add("open");
		var listed = box.Logins();
		if (listed.Length != 1 || listed[0].Site != "https://example.com" || listed[0].User != "ada")
			failures.Add("logins");
		if (box.TryPassword("https://other.example", out _, out _)) failures.Add("swap");
		if (box.OfferSeen("https://example.com", "ada", "correct horse") != "") failures.Add("same");
		if (box.OfferSeen("https://example.com", "ada", "next") != "update") failures.Add("update");
		box.Never("https://ads.example");
		if (box.OfferSeen("https://ads.example", "a", "b") != "") failures.Add("never");
		var moved = new VaultBox(shelf, rsa, other, name, false);
		if (moved.TryPassword("https://example.com", out _, out _)) failures.Add("machine");
		var cookies = new[]
		{
			new SeenCookie("sid", "secret-cookie", "example.com", "/", true, true, true, 0, "lax"),
			new SeenCookie("prefs", "theme", "example.com", "/", false, false, true, 0, "lax"),
			new SeenCookie("__Host-auth", "tok", "example.com", "/", true, false, true, 0, "strict"),
			new SeenCookie("sid", "nope", "evil.com", "/", true, true, true, 0, "lax"),
			new SeenCookie("x", "y", "com", "/", true, true, true, 0, "lax")
		};
		if (!box.SaveCookies("https://example.com", cookies)) failures.Add("cookies");
		var jar = box.Cookies("https://example.com");
		if (jar.Length != 2 || jar.All(item => item.Name != "sid") || jar.All(item => item.Name != "__Host-auth"))
			failures.Add("pick");
		if (jar.Any(item => item.Domain == "evil.com" || item.Name == "prefs")) failures.Add("dropped");
		var scoped = new[]
		{
			new SeenCookie("SID", "sid-value", ".youtube.com", "/", false, true, false, 10, "lax"),
			new SeenCookie("SAPISID", "sapi", ".youtube.com", "/", true, false, false, 10, "lax"),
			new SeenCookie("__Host-GAPS", "gaps", "www.youtube.com", "/", true, true, false, 10, "lax")
		};
		if (!box.SaveCookies("https://www.youtube.com", scoped)) failures.Add("scope save");
		var scope = box.Cookies("https://www.youtube.com");
		if (!scope.Any(item => item.Name == "SID" && item.Domain == ".youtube.com")) failures.Add("domain dot");
		if (!scope.Any(item => item.Name == "SAPISID" && item.Domain == ".youtube.com")) failures.Add("sapisid");
		if (!scope.Any(item => item.Name == "__Host-GAPS" && item.Domain == "www.youtube.com")) failures.Add("host cookie");
		if (ScopeDomain("https://www.youtube.com", "SID", "youtube.com") != ".youtube.com") failures.Add("repair dot");
		if (ScopeDomain("https://www.youtube.com", "__Host-GAPS", "www.youtube.com") != "www.youtube.com") failures.Add("repair host");
		if (moved.Cookies("https://example.com").Length != 0) failures.Add("cookie machine");
		var index = shelf.Joined();
		if (index.Contains("correct horse", StringComparison.Ordinal) || index.Contains("secret-cookie", StringComparison.Ordinal))
			failures.Add("index leak");
		foreach (var target in shelf.Names())
			if (target.Contains("example.com", StringComparison.OrdinalIgnoreCase)) failures.Add("target name");
		var bulky = new MemoryShelf();
		var bulk = new VaultBox(bulky, rsa, machine, name, false);
		var payload = RandomNumberGenerator.GetBytes(5000);
		bulk.PutRaw("item", payload);
		var back = bulk.GetRaw("item");
		if (back == null || !back.AsSpan().SequenceEqual(payload)) failures.Add("chunk");
		bulk.DeleteRaw("item");
		if (bulk.GetRaw("item") != null) failures.Add("chunk delete");
		var sealedBlob = box.Seal(Encoding.UTF8.GetBytes("token-value"), "sign\0https://example.com");
		if (sealedBlob == null || Encoding.UTF8.GetString(sealedBlob).Contains("token-value", StringComparison.Ordinal))
			failures.Add("sealed");
		if (box.Open(sealedBlob!, "sign\0https://other.example") != null) failures.Add("purpose");
		if (sealedBlob != null && sealedBlob.Length > 20)
		{
			sealedBlob[^1] ^= 0x5A;
			if (box.Open(sealedBlob, "sign\0https://example.com") != null) failures.Add("tamper");
		}
		var locked = new MemoryShelf();
		var seeded = new VaultBox(locked, rsa, machine, name, false);
		seeded.PutRaw(NameTarget, [9, 9, 9]);
		var broken = new VaultBox(locked, rsa, machine, new byte[32], false);
		var threw = false;
		try { broken.LoadOrCreateName(); }
		catch (CryptographicException) { threw = true; }
		if (!threw) failures.Add("name replace");
		var kept = seeded.GetRaw(NameTarget);
		if (kept == null || kept.Length != 3 || kept[0] != 9) failures.Add("name kept");
		try
		{
			var shelfS = new MemoryShelf();
			var made = new VaultBox(shelfS, rsa, machine, new byte[32], true);
			made.LoadOrCreateName();
			if (!made.SavePassword("https://example.com", "ada", "round")) failures.Add("shield save");
			var again = new VaultBox(shelfS, rsa, machine, new byte[32], true);
			again.LoadOrCreateName();
			if (!again.TryPassword("https://example.com", out var roundUser, out var roundPass) || roundUser != "ada" || roundPass != "round")
				failures.Add("shield open");
		}
		catch
		{
			failures.Add("shield open");
		}
		var chrome = "name,url,username,password,note\nExample,https://example.com/a,ada,one,hi\nBad,http://example.com/a,x,two,\nEmpty,https://example.org/,z,\nAgain,https://example.com/login,ada,three,x\n";
		var rows = ReadCsv(chrome, out var skipped, out var empty);
		if (skipped != 1 || empty != 1 || rows.Length != 1 || rows[0].Site != "https://example.com" || rows[0].User != "ada" || rows[0].Pass != "three")
			failures.Add("csv chrome");
		var fox = "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timeLastUsed\",\"timePasswordChanged\"\n\"http://127.0.0.1:8080/a\",\"sam\",\"a,b\",\"\",\"\",\"g\",\"1\",\"2\",\"3\"\n\"javascript:alert(1)\",\"no\",\"p\",\"\",\"\",\"g\",\"1\",\"2\",\"3\"\n";
		var foxRows = ReadCsv(fox, out var foxSkip, out var foxEmpty);
		if (foxEmpty != 0 || foxSkip != 1 || foxRows.Length != 1 || foxRows[0].Site != "http://127.0.0.1:8080" || foxRows[0].User != "sam" || foxRows[0].Pass != "a,b")
			failures.Add("csv firefox");
		if (ReadCsv("https://example.com,ada,secret", out _, out _).Length != 0) failures.Add("csv header");
		if (ImportNote(2, 1, 0) != "Saved 2 passwords."
			|| ImportNote(1, 0, 0) != "Saved 1 password."
			|| ImportNote(0, 3, 0) != "Those rows need an https address, so they were not saved."
			|| ImportNote(0, 0, 2) != "That file has no passwords to save.")
			failures.Add("csv note");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	static List<string[]> CsvTable(string text)
	{
		var rows = new List<string[]>();
		var row = new List<string>();
		var field = new StringBuilder();
		var quoted = false;
		for (var i = 0; i < text.Length; i++)
		{
			var ch = text[i];
			if (quoted)
			{
				if (ch == '"')
				{
					if (i + 1 < text.Length && text[i + 1] == '"')
					{
						field.Append('"');
						i++;
					}
					else quoted = false;
				}
				else field.Append(ch);
				continue;
			}
			if (ch == '"' && field.Length == 0)
			{
				quoted = true;
				continue;
			}
			if (ch == ',')
			{
				row.Add(field.ToString());
				field.Clear();
				continue;
			}
			if (ch is '\n' or '\r')
			{
				if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
				row.Add(field.ToString());
				field.Clear();
				if (row.Any(cell => cell.Length > 0)) rows.Add(row.ToArray());
				row.Clear();
				continue;
			}
			field.Append(ch);
		}
		if (field.Length > 0 || row.Count > 0)
		{
			row.Add(field.ToString());
			if (row.Any(cell => cell.Length > 0)) rows.Add(row.ToArray());
		}
		return rows;
	}

	static int Column(string[] header, params string[] names)
	{
		foreach (var name in names)
		{
			for (var i = 0; i < header.Length; i++)
			{
				if (header[i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
			}
		}
		return -1;
	}

	static string Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index].Trim() : "";

	static VaultBox? TryLive(bool create)
	{
		if (_live != null) return _live;
		lock (Gate)
		{
			if (_live != null) return _live;
			if (!create && !CredShelf.Has(NameTarget)) return null;
			try { _live = VaultBox.Open(); }
			catch { return null; }
		}
		return _live;
	}

	sealed class VaultBox
	{
		readonly IShelf _shelf;
		readonly RSA _rsa;
		readonly byte[] _machine;
		readonly byte[] _name;
		readonly bool _shield;
		bool _shielded;
		IndexFile _index = new();

		public VaultBox(IShelf shelf, RSA rsa, byte[] machine, byte[] name, bool shield)
		{
			_shelf = shelf;
			_rsa = rsa;
			_machine = machine;
			_name = name;
			_shield = shield;
			Shield();
			if (shelf.Get(IndexTarget) != null) LoadIndex();
		}

		public static VaultBox Open()
		{
			var box = new VaultBox(new CredShelf(), MachineKey.Load(), MachineTag.Bytes(), new byte[32], true);
			box.LoadOrCreateName();
			return box;
		}

		public bool HasPassword(string site) => _index.Pass.Contains(site);

		public bool HasSign(string site) => _index.Sign.Any(item => item.H == site);

		public bool IsNever(string site) => _index.Never.Contains(site);

		public List<string> Sites()
		{
			var all = new HashSet<string>(_index.Pass, StringComparer.Ordinal);
			foreach (var item in _index.Sign) all.Add(item.H);
			return all.ToList();
		}

		public string[] SignSites() => _index.Sign.Select(item => item.H).ToArray();

		public string[] CookieNames(string site) =>
			_index.Sign.FirstOrDefault(item => item.H == site)?.N.ToArray() ?? [];

		public string OfferSeen(string site, string user, string pass)
		{
			if (IsNever(site)) return "";
			if (SamePassword(site, user, pass)) return "";
			return HasPassword(site) ? "update" : "save";
		}

		public bool SamePassword(string site, string user, string pass)
		{
			if (!TryPassword(site, out var savedUser, out var savedPass)) return false;
			var same = savedUser == user && savedPass == pass;
			savedPass = "";
			return same;
		}

		public bool SavePassword(string site, string user, string pass)
		{
			var body = JsonSerializer.SerializeToUtf8Bytes(new PassFile { H = site, U = user, P = pass });
			if (!PutSealed(Item("pass", site), body, "pass\0" + site)) return false;
			CryptographicOperations.ZeroMemory(body);
			if (!_index.Pass.Contains(site)) _index.Pass.Add(site);
			_index.Never.Remove(site);
			return SaveIndex();
		}

		public bool TryPassword(string site, out string user, out string pass)
		{
			user = "";
			pass = "";
			var plain = GetSealed(Item("pass", site), "pass\0" + site);
			if (plain == null) return false;
			try
			{
				var row = JsonSerializer.Deserialize<PassFile>(plain);
				if (row == null || row.H != site) return false;
				user = row.U ?? "";
				pass = row.P ?? "";
				return pass.Length > 0;
			}
			catch
			{
				return false;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plain);
			}
		}

		public SavedLogin[] Logins()
		{
			var list = new List<SavedLogin>();
			foreach (var site in _index.Pass.ToArray())
			{
				if (!TryPassword(site, out var user, out var pass)) continue;
				pass = "";
				list.Add(new SavedLogin(site, user));
			}
			return list.ToArray();
		}

		public bool SaveCookies(string site, IReadOnlyList<SeenCookie> cookies)
		{
			var picked = Pick(cookies, site);
			if (picked.Count == 0) return false;
			var body = JsonSerializer.SerializeToUtf8Bytes(new JarFile { H = site, C = picked });
			if (!PutSealed(Item("sign", site), body, "sign\0" + site)) return false;
			CryptographicOperations.ZeroMemory(body);
			var names = picked.Select(item => item.Name).Distinct().ToList();
			var head = _index.Sign.FirstOrDefault(item => item.H == site);
			if (head == null) _index.Sign.Add(new SignHead { H = site, N = names });
			else head.N = names;
			return SaveIndex();
		}

		public SeenCookie[] Cookies(string site)
		{
			var plain = GetSealed(Item("sign", site), "sign\0" + site);
			if (plain == null) return [];
			try
			{
				var jar = JsonSerializer.Deserialize<JarFile>(plain);
				if (jar == null || jar.H != site || jar.C == null) return [];
				return jar.C.Select(item => new SeenCookie(item.Name, item.Value, item.Domain, item.Path, item.Secure, item.HttpOnly, item.Session, item.Expires, item.SameSite)).ToArray();
			}
			catch
			{
				return [];
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plain);
			}
		}

		public void Never(string site)
		{
			if (site.Length == 0 || _index.Never.Contains(site)) return;
			_index.Never.Add(site);
			SaveIndex();
		}

		public void ForgetSite(string site)
		{
			DeleteRaw(Item("pass", site));
			DeleteRaw(Item("sign", site));
			_index.Pass.Remove(site);
			_index.Sign.RemoveAll(item => item.H == site);
			SaveIndex();
		}

		public void ForgetAll()
		{
			foreach (var site in _index.Pass.ToArray()) DeleteRaw(Item("pass", site));
			foreach (var item in _index.Sign.ToArray()) DeleteRaw(Item("sign", item.H));
			_shelf.Delete(IndexTarget);
			_shelf.Delete(NameTarget);
			_index = new IndexFile();
		}

		public void PutRaw(string target, byte[] data) => WriteChunks(target, data);

		public byte[]? GetRaw(string target) => ReadChunks(target);

		public void DeleteRaw(string target) => DeleteChunks(target);

		public byte[]? Seal(byte[] plain, string purpose) => SealBytes(plain, purpose);

		public byte[]? Open(byte[] blob, string purpose) => OpenBytes(blob, purpose);

		internal void LoadOrCreateName()
		{
			if (!UnshieldOk()) throw new CryptographicException("name");
			var exists = _shelf.Get(NameTarget) != null;
			var plain = GetSealed(NameTarget, "name");
			if (plain is { Length: 32 })
			{
				plain.CopyTo(_name, 0);
				CryptographicOperations.ZeroMemory(plain);
				Shield();
				LoadIndex();
				return;
			}
			if (plain != null) CryptographicOperations.ZeroMemory(plain);
			if (exists) throw new CryptographicException("name");
			RandomNumberGenerator.Fill(_name);
			var copy = _name.ToArray();
			Shield();
			if (!PutSealed(NameTarget, copy, "name"))
			{
				CryptographicOperations.ZeroMemory(copy);
				throw new CryptographicException("name");
			}
			CryptographicOperations.ZeroMemory(copy);
			_index = new IndexFile();
			if (!SaveIndex()) throw new CryptographicException("index");
		}

		void LoadIndex()
		{
			var plain = GetSealed(IndexTarget, "index");
			if (plain == null) return;
			try
			{
				_index = JsonSerializer.Deserialize<IndexFile>(plain) ?? new IndexFile();
				_index.Pass ??= [];
				_index.Sign ??= [];
				_index.Never ??= [];
			}
			catch
			{
				_index = new IndexFile();
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plain);
			}
		}

		bool SaveIndex()
		{
			var body = JsonSerializer.SerializeToUtf8Bytes(_index);
			var ok = PutSealed(IndexTarget, body, "index");
			CryptographicOperations.ZeroMemory(body);
			return ok;
		}

		string Item(string kind, string site)
		{
			if (!UnshieldOk()) throw new CryptographicException("name");
			try
			{
				var mac = HMACSHA256.HashData(_name, Encoding.UTF8.GetBytes(kind + "\0" + site));
				return Prefix + kind + "." + Convert.ToHexString(mac, 0, 16).ToLowerInvariant();
			}
			finally
			{
				Shield();
			}
		}

		bool PutSealed(string target, byte[] plain, string purpose)
		{
			var blob = SealBytes(plain, purpose);
			if (blob == null) return false;
			try
			{
				WriteChunks(target, blob);
				return true;
			}
			catch
			{
				return false;
			}
		}

		byte[]? GetSealed(string target, string purpose)
		{
			var blob = ReadChunks(target);
			return blob == null ? null : OpenBytes(blob, purpose);
		}

		byte[]? SealBytes(byte[] plain, string purpose)
		{
			var key = new byte[32];
			var nonce = new byte[12];
			var tag = new byte[16];
			var cipher = new byte[plain.Length];
			try
			{
				RandomNumberGenerator.Fill(key);
				RandomNumberGenerator.Fill(nonce);
				var wrapped = _rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
				using var aes = new AesGcm(key, 16);
				aes.Encrypt(nonce, plain, cipher, tag, Bind(purpose));
				var blob = new byte[1 + 2 + wrapped.Length + nonce.Length + tag.Length + cipher.Length];
				blob[0] = 1;
				BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(1), (ushort)wrapped.Length);
				wrapped.CopyTo(blob.AsSpan(3));
				var at = 3 + wrapped.Length;
				nonce.CopyTo(blob.AsSpan(at));
				at += nonce.Length;
				tag.CopyTo(blob.AsSpan(at));
				at += tag.Length;
				cipher.CopyTo(blob.AsSpan(at));
				return blob;
			}
			catch
			{
				return null;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(key);
			}
		}

		byte[]? OpenBytes(byte[] blob, string purpose)
		{
			if (blob.Length < 1 + 2 + 12 + 16 + 1 || blob[0] != 1) return null;
			var wrappedLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(1));
			if (wrappedLen < 32 || wrappedLen > 512 || blob.Length < 3 + wrappedLen + 12 + 16) return null;
			var wrapped = blob.AsSpan(3, wrappedLen);
			var at = 3 + wrappedLen;
			var nonce = blob.AsSpan(at, 12);
			at += 12;
			var tag = blob.AsSpan(at, 16);
			at += 16;
			var cipher = blob.AsSpan(at);
			var key = Array.Empty<byte>();
			try
			{
				key = _rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
				if (key.Length != 32) return null;
				var plain = new byte[cipher.Length];
				using var aes = new AesGcm(key, 16);
				aes.Decrypt(nonce, cipher, tag, plain, Bind(purpose));
				return plain;
			}
			catch
			{
				return null;
			}
			finally
			{
				if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
			}
		}

		byte[] Bind(string purpose)
		{
			var extra = Encoding.UTF8.GetBytes(purpose);
			var all = new byte[_machine.Length + 1 + extra.Length];
			_machine.CopyTo(all, 0);
			all[_machine.Length] = 0;
			extra.CopyTo(all, _machine.Length + 1);
			return all;
		}

		void WriteChunks(string target, byte[] data)
		{
			var count = Math.Max(1, (data.Length + Chunk - 1) / Chunk);
			for (var i = 0; i < count; i++)
			{
				var take = Math.Min(Chunk, data.Length - (i * Chunk));
				var part = new byte[take];
				Buffer.BlockCopy(data, i * Chunk, part, 0, take);
				_shelf.Put(target + "/" + i, part);
			}
			var manifest = new byte[10];
			manifest[0] = (byte)'G';
			manifest[1] = (byte)'1';
			BinaryPrimitives.WriteInt32LittleEndian(manifest.AsSpan(2), count);
			BinaryPrimitives.WriteInt32LittleEndian(manifest.AsSpan(6), data.Length);
			_shelf.Put(target, manifest);
			for (var i = count; i < 1000; i++)
			{
				if (_shelf.Get(target + "/" + i) == null) break;
				_shelf.Delete(target + "/" + i);
			}
		}

		byte[]? ReadChunks(string target)
		{
			var manifest = _shelf.Get(target);
			if (manifest is not { Length: 10 } || manifest[0] != (byte)'G' || manifest[1] != (byte)'1') return null;
			var count = BinaryPrimitives.ReadInt32LittleEndian(manifest.AsSpan(2));
			var length = BinaryPrimitives.ReadInt32LittleEndian(manifest.AsSpan(6));
			if (count <= 0 || count > 1000 || length < 0 || length > 300_000) return null;
			var data = new byte[length];
			var filled = 0;
			for (var i = 0; i < count; i++)
			{
				var part = _shelf.Get(target + "/" + i);
				if (part == null || filled + part.Length > data.Length) return null;
				Buffer.BlockCopy(part, 0, data, filled, part.Length);
				filled += part.Length;
			}
			return filled == length ? data : null;
		}

		void DeleteChunks(string target)
		{
			var manifest = _shelf.Get(target);
			var count = manifest is { Length: 10 } ? BinaryPrimitives.ReadInt32LittleEndian(manifest.AsSpan(2)) : 0;
			_shelf.Delete(target);
			for (var i = 0; i < Math.Max(count, 0) + 2; i++) _shelf.Delete(target + "/" + i);
		}

		void Shield()
		{
			if (!_shield || _shielded) return;
			if (!Guard(_name, true)) return;
			_shielded = true;
		}

		bool UnshieldOk()
		{
			if (!_shielded) return true;
			if (!Guard(_name, false)) return false;
			_shielded = false;
			return true;
		}

		static bool Guard(byte[] data, bool protect)
		{
			if (data.Length == 0 || data.Length % 16 != 0) return false;
			var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				return protect
					? CryptProtectMemory(pin.AddrOfPinnedObject(), (uint)data.Length, 0)
					: CryptUnprotectMemory(pin.AddrOfPinnedObject(), (uint)data.Length, 0);
			}
			catch
			{
				return false;
			}
			finally
			{
				pin.Free();
			}
		}

		static List<CookieFile> Pick(IReadOnlyList<SeenCookie> cookies, string site)
		{
			if (!Uri.TryCreate(site, UriKind.Absolute, out var uri)) return [];
			var host = uri.IdnHost.Trim('.').ToLowerInvariant();
			var picked = new List<CookieFile>();
			foreach (var cookie in cookies)
			{
				var name = (cookie.Name ?? "").Trim();
				if (name.Length == 0 || name.Length > 256 || name.Any(char.IsWhiteSpace)) continue;
				var value = cookie.Value ?? "";
				if (value.Length == 0 || value.Length > 4096) continue;
				if (!Auth(name, cookie.HttpOnly)) continue;
				var raw = (cookie.Domain ?? "").Trim().ToLowerInvariant();
				var bare = raw.TrimStart('.');
				if (!Covers(bare, host)) continue;
				var path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path;
				if (!path.StartsWith('/')) path = "/";
				picked.Add(new CookieFile
				{
					Name = name,
					Value = value,
					Domain = raw.StartsWith('.') ? "." + bare : bare,
					Path = path,
					Secure = cookie.Secure,
					HttpOnly = cookie.HttpOnly,
					Session = cookie.Session || cookie.Expires <= 0,
					Expires = cookie.Session ? 0 : cookie.Expires,
					SameSite = cookie.SameSite?.ToLowerInvariant() switch
					{
						"none" => "none",
						"strict" => "strict",
						_ => "lax"
					}
				});
			}
			picked.Sort((left, right) => Rank(left.Name).CompareTo(Rank(right.Name)));
			if (picked.Count > 48) picked.RemoveRange(48, picked.Count - 48);
			return picked;
		}

		static int Rank(string name)
		{
			var text = name.ToLowerInvariant();
			if (text.StartsWith("__host-", StringComparison.Ordinal) || text.StartsWith("__secure-", StringComparison.Ordinal))
				return 0;
			if (text is "sid" or "hsid" or "ssid" or "sidcc" or "apisid" or "sapisid" or "login_info") return 1;
			return 2;
		}

		static bool Auth(string name, bool httpOnly)
		{
			if (httpOnly) return true;
			var text = name.ToLowerInvariant();
			if (text.StartsWith("__secure-", StringComparison.Ordinal) || text.StartsWith("__host-", StringComparison.Ordinal))
				return true;
			return text is "session" or "sid" or "hsid" or "ssid" or "sidcc" or "apisid" or "sapisid"
				or "sess" or "sessionid" or "auth" or "token" or "jwt"
				or "access_token" or "refresh" or "refresh_token" or "login" or "login_info" or "remember"
				|| text.Contains("session", StringComparison.Ordinal)
				|| text.Contains("auth", StringComparison.Ordinal);
		}

		static bool Covers(string domain, string host)
		{
			if (domain.Length == 0 || host.Length == 0) return false;
			if (domain == host) return true;
			if (!host.EndsWith("." + domain, StringComparison.Ordinal)) return false;
			if (!domain.Contains('.')) return false;
			if (domain is "co.uk" or "org.uk" or "com.au" or "net.au" or "co.jp" or "com.br" or "co.nz" or "com.mx")
				return false;
			return true;
		}
	}

	sealed class IndexFile
	{
		public List<string> Pass { get; set; } = [];
		public List<SignHead> Sign { get; set; } = [];
		public List<string> Never { get; set; } = [];
	}

	sealed class SignHead
	{
		public string H { get; set; } = "";
		public List<string> N { get; set; } = [];
	}

	sealed class PassFile
	{
		public string H { get; set; } = "";
		public string U { get; set; } = "";
		public string P { get; set; } = "";
	}

	sealed class JarFile
	{
		public string H { get; set; } = "";
		public List<CookieFile>? C { get; set; }
	}

	sealed class CookieFile
	{
		public string Name { get; set; } = "";
		public string Value { get; set; } = "";
		public string Domain { get; set; } = "";
		public string Path { get; set; } = "/";
		public bool Secure { get; set; }
		public bool HttpOnly { get; set; }
		public bool Session { get; set; } = true;
		public long Expires { get; set; }
		public string SameSite { get; set; } = "lax";
	}

	interface IShelf
	{
		byte[]? Get(string target);
		void Put(string target, byte[] data);
		void Delete(string target);
		IEnumerable<string> Names();
	}

	sealed class MemoryShelf : IShelf
	{
		readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);

		public byte[]? Get(string target) => _items.TryGetValue(target, out var data) ? data.ToArray() : null;

		public void Put(string target, byte[] data) => _items[target] = data.ToArray();

		public void Delete(string target) => _items.Remove(target);

		public IEnumerable<string> Names() => _items.Keys.ToArray();

		public string Joined()
		{
			var sb = new StringBuilder();
			foreach (var pair in _items)
			{
				sb.Append(pair.Key).Append('\n');
				sb.Append(Encoding.UTF8.GetString(pair.Value)).Append('\n');
			}
			return sb.ToString();
		}
	}

	static class MachineKey
	{
		const string KeyName = "Guardex.Browser.Seal";

		public static RSA Load()
		{
			var tpm = new CngProvider("Microsoft Platform Crypto Provider");
			var software = CngProvider.MicrosoftSoftwareKeyStorageProvider;
			return TryOpen(tpm) ?? TryOpen(software) ?? TryCreate(tpm) ?? TryCreate(software)
				?? throw new CryptographicException("seal");
		}

		static RSA? TryOpen(CngProvider provider)
		{
			try { return new RSACng(CngKey.Open(KeyName, provider)); }
			catch { return null; }
		}

		static RSA? TryCreate(CngProvider provider)
		{
			try
			{
				var parameters = new CngKeyCreationParameters
				{
					ExportPolicy = CngExportPolicies.None,
					KeyUsage = CngKeyUsages.Decryption,
					Provider = provider
				};
				parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
				return new RSACng(CngKey.Create(CngAlgorithm.Rsa, KeyName, parameters));
			}
			catch
			{
				return null;
			}
		}
	}

	static class MachineTag
	{
		public static byte[] Bytes()
		{
			var guid = "";
			try
			{
				guid = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid") as string ?? "";
			}
			catch { }
			var sid = "";
			try { sid = WindowsIdentity.GetCurrent().User?.Value ?? ""; }
			catch { }
			return Encoding.UTF8.GetBytes("guardex1\0" + guid.Trim().ToLowerInvariant() + "\0" + sid);
		}
	}

	sealed class CredShelf : IShelf
	{
		const int Generic = 1;
		const uint LocalMachine = 2;

		public static bool Has(string target) => Read(target) != null;

		public static void DeleteAll()
		{
			foreach (var name in List(Prefix))
			{
				try { CredDelete(name, Generic, 0); } catch { }
			}
		}

		public byte[]? Get(string target) => Read(target);

		public void Put(string target, byte[] data)
		{
			if (data.Length == 0 || data.Length > 2500) throw new InvalidOperationException("store");
			var blob = Marshal.AllocHGlobal(data.Length);
			try
			{
				Marshal.Copy(data, 0, blob, data.Length);
				var cred = new NativeCred
				{
					Type = Generic,
					TargetName = target,
					Comment = "",
					CredentialBlobSize = (uint)data.Length,
					CredentialBlob = blob,
					Persist = LocalMachine,
					UserName = "Guardex",
					TargetAlias = ""
				};
				if (!CredWrite(ref cred, 0)) throw new InvalidOperationException("store");
			}
			finally
			{
				Marshal.FreeHGlobal(blob);
			}
		}

		public void Delete(string target)
		{
			try { CredDelete(target, Generic, 0); } catch { }
		}

		public IEnumerable<string> Names() => List(Prefix);

		static byte[]? Read(string target)
		{
			if (!CredRead(target, Generic, 0, out var ptr) || ptr == IntPtr.Zero) return null;
			try
			{
				var cred = Marshal.PtrToStructure<NativeCred>(ptr);
				if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return [];
				var data = new byte[cred.CredentialBlobSize];
				Marshal.Copy(cred.CredentialBlob, data, 0, data.Length);
				return data;
			}
			finally
			{
				CredFree(ptr);
			}
		}

		static List<string> List(string filter)
		{
			var found = new List<string>();
			if (!CredEnumerate(filter + "*", 0, out var count, out var ptr) || ptr == IntPtr.Zero) return found;
			try
			{
				var rows = new IntPtr[count];
				Marshal.Copy(ptr, rows, 0, count);
				foreach (var row in rows)
				{
					var cred = Marshal.PtrToStructure<NativeCred>(row);
					if (!string.IsNullOrEmpty(cred.TargetName)) found.Add(cred.TargetName);
				}
			}
			finally
			{
				CredFree(ptr);
			}
			return found;
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool CredWrite([In] ref NativeCred credential, uint flags);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool CredDelete(string target, int type, int flags);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

		[DllImport("advapi32.dll")]
		static extern void CredFree(IntPtr buffer);
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct NativeCred
	{
		public uint Flags;
		public uint Type;
		public string? TargetName;
		public string? Comment;
		public long LastWritten;
		public uint CredentialBlobSize;
		public IntPtr CredentialBlob;
		public uint Persist;
		public uint AttributeCount;
		public IntPtr Attributes;
		public string? TargetAlias;
		public string? UserName;
	}

	[DllImport("crypt32.dll", SetLastError = true)]
	static extern bool CryptProtectMemory(IntPtr data, uint bytes, uint flags);

	[DllImport("crypt32.dll", SetLastError = true)]
	static extern bool CryptUnprotectMemory(IntPtr data, uint bytes, uint flags);
}

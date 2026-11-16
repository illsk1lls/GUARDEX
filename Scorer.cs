namespace Guardex;

static class Scorer
{
	public static Verdict Score(string src, IReadOnlyList<Observation> events, string canary)
	{
		var items = events.OrderBy(e => e.Utc).ToList();
		var signals = new List<Signal>();
		var http = items.Where(e => e.Kind == ObsKind.Http).ToList();
		var timeline = http.Count > 0 ? http : items;

		var families = new HashSet<string>();
		var paths = new HashSet<string>(StringComparer.Ordinal);
		var queries = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		var headerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		var ports = new HashSet<int>();
		var browser = 0;
		var cookieBare = 0;
		var status4 = 0;
		var seenHard = new HashSet<string>();

		foreach (var e in items)
		{
			if (e.LocalPort > 0) ports.Add(e.LocalPort);
		}

		foreach (var e in http)
		{
			var path = string.IsNullOrEmpty(e.Path) ? "/" : e.Path;
			paths.Add(path);
			if (!queries.TryGetValue(path, out var qs))
			{
				qs = new HashSet<string>(StringComparer.Ordinal);
				queries[path] = qs;
			}
			qs.Add(e.Query ?? "");

			if (CanaryHit(path, canary) && seenHard.Add("canary"))
				signals.Add(new Signal { Name = "canary", Weight = 100, Detail = "hit " + canary, Hard = true });

			var fam = UaFamily(e.UserAgent);
			if (fam is not ("empty" or "other")) families.Add(fam);
			if (IsScanner(fam) && seenHard.Add("scanner"))
				signals.Add(new Signal { Name = "scanner_ua", Weight = 100, Detail = "family " + fam, Hard = true });

			var hk = HeaderKey(e.Headers);
			headerCounts.TryGetValue(hk, out var hc);
			headerCounts[hk] = hc + 1;
			var joined = "," + hk + ",";
			if (joined.Contains(",sec-fetch-site,", StringComparison.Ordinal)) browser++;
			if (joined.Contains(",cookie,", StringComparison.Ordinal) && !joined.Contains(",sec-fetch-", StringComparison.Ordinal))
				cookieBare++;
			if (e.Status >= 400 && e.Status < 500) status4++;
		}

		foreach (var e in items.Where(e => e.Kind != ObsKind.Http))
		{
			var fam = UaFamily(e.UserAgent);
			if (fam is not ("empty" or "other")) families.Add(fam);
		}

		if (families.Contains("go"))
			signals.Add(Soft("go_client", 20, "Go-http-client, the platform default when no target User-Agent is set"));
		if (families.Contains("curl") || families.Contains("python"))
			signals.Add(Soft("shell_http", 8, "curl or python-requests"));
		if (families.Count >= 2)
			signals.Add(Soft("mixed_clients", 25, "families " + string.Join(", ", families.OrderBy(f => f))));

		var (pace, paceDetail) = ToolPace(timeline);
		if (pace.Length > 0)
			signals.Add(Soft("paced", 15, paceDetail));

		var (cycle, thinkMedian, thinkGaps, cycleDetail) = ThinkCycles(timeline);
		if (cycle == "slow")
			signals.Add(Soft("slow_cycle", 35, cycleDetail));
		else if (cycle == "fast")
			signals.Add(Soft("fast_cycle", 30, cycleDetail));

		var phases = Phases(timeline);
		if (phases.Distinct >= 2 && phases.Solid >= 2)
			signals.Add(Soft("episodic", 25, "bursts " + string.Join(" -> ", phases.Names)));

		if (http.Count >= 12 && paths.Count > 0)
		{
			var uniq = paths.Count / (double)http.Count;
			var miss = status4 / (double)http.Count;
			if (uniq >= 0.75 && miss >= 0.40)
				signals.Add(Soft("breadth", 20, $"{paths.Count} paths, {status4} misses"));
		}

		var maxQ = queries.Count == 0 ? 0 : queries.Max(kv => kv.Value.Count);
		if (maxQ >= 8)
			signals.Add(Soft("depth", 15, $"{maxQ} distinct queries on one path"));

		if (http.Count >= 10 && headerCounts.Count > 0)
		{
			var top = headerCounts.Max(kv => kv.Value);
			if (top / (double)http.Count >= 0.80 && browser == 0)
				signals.Add(Soft("frozen_headers", 15, "one header set on most requests, no browser fetch metadata"));
		}
		if (http.Count >= 8 && cookieBare >= 6 && paths.Count >= 6)
			signals.Add(Soft("cookie_replay", 15, "one cookie style across many paths, no browser fetch metadata"));
		if (http.Count >= 5 && browser / (double)http.Count >= 0.70)
			signals.Add(Soft("browser_hints", -25, "sec-fetch-site on most requests"));

		if (ports.Count >= 6)
			signals.Add(Soft("port_sweep", 50, $"{ports.Count} local ports touched"));

		if (http.Count == 0 && items.Count >= 12 && ports.Count == 1)
			signals.Add(Soft("repeat_connect", 30, $"{items.Count} connections to :{ports.First()}, no request detail yet"));

		var score = signals.Where(s => !s.Hard).Sum(s => s.Weight);
		if (score < 0) score = 0;
		if (score > 100) score = 100;
		var hard = signals.Any(s => s.Hard);
		var band = score >= 70 ? "block" : score >= 45 ? "challenge" : score >= 25 ? "watch" : "allow";
		if (hard)
		{
			band = "block";
			if (score < 70) score = 70;
		}

		var verdict = new Verdict
		{
			Src = src,
			Score = score,
			Band = band,
			Gray = !hard && score >= 25 && score <= 69,
			Count = items.Count,
			SpanSec = items.Count < 2 ? 0 : (items[^1].Utc - items[0].Utc).TotalSeconds,
			Pace = pace,
			Cycle = cycle,
			ThinkMedianSec = thinkMedian,
			ThinkGaps = thinkGaps,
			Signals = signals,
			Ports = ports.OrderBy(p => p).ToList(),
			Families = families.Count == 0 ? "none" : string.Join(",", families.OrderBy(f => f)),
			UpdatedUtc = DateTime.UtcNow
		};
		var (action, why) = Recommend(verdict);
		verdict.Action = action;
		verdict.Why = why;
		return verdict;
	}

	static (string Action, string Why) Recommend(Verdict v)
	{
		if (v.Band == "allow")
			return ("none", "The session sits in the allow band.");
		if (v.Band == "watch")
			return ("watch", "Keep this source. The score is not high enough to act.");
		if (v.Band == "block")
			return ("block", "Drop this source on the host. Guardex does not create a firewall rule.");
		if (v.Signals.Any(s => s.Name is "slow_cycle" or "episodic"))
			return ("tarpit", "A slow reply spends the quiet time while their model is already the slow part. A 429 only brakes a client that bothers to read it.");
		if (v.Signals.Any(s => s.Name == "port_sweep"))
			return ("block", "A browser check does not stop a port sweep. Dropping the source is the useful response. Guardex does not create the rule.");
		return ("challenge", "A browser check stops a bare HTTP client. A worker driving a real browser can pass it. A pass means watch that source more closely.");
	}

	static (string Pace, string Detail) ToolPace(List<Observation> items)
	{
		if (items.Count < 10) return ("", "");
		var gaps = ToolGaps(items);
		if (gaps.Count < 6) return ("", "");
		var tail = gaps.Count > 8 ? gaps.Skip(4).ToList() : gaps;
		var median = Median(tail);
		var perSec = PeakPerSecond(items);
		var name = median >= 0.40 && median <= 0.95 && perSec <= 4 ? "quiet"
			: median >= 0.14 && median <= 0.35 && perSec <= 8 ? "balanced"
			: median >= 0.03 && median <= 0.12 && perSec <= 25 ? "aggressive"
			: "";
		if (name.Length == 0) return ("", "");
		return (name, $"tool gap {Math.Round(median * 1000)} ms, peak {perSec}/s, matches the {name} governor");
	}

	static (string Cycle, double MedianSec, int Gaps, string Detail) ThinkCycles(List<Observation> items)
	{
		if (items.Count < 3) return ("", 0, 0, "");
		var thinks = new List<double>();
		for (var i = 1; i < items.Count; i++)
		{
			var g = (items[i].Utc - items[i - 1].Utc).TotalSeconds;
			if (g >= Timing.FastestThinkSec && g <= Timing.SlowestThinkSec)
				thinks.Add(g);
		}
		if (thinks.Count < 2) return ("", 0, thinks.Count, "");
		var med = Median(thinks);
		var kind = med >= Timing.TypicalThinkSec ? "slow" : "fast";
		var who = kind == "slow"
			? "slow local model, the common home-PC case"
			: "fast local model";
		var detail = $"{thinks.Count} pauses, median {FormatGap(med)}, {who}";
		return (kind, med, thinks.Count, detail);
	}

	static (List<string> Names, int Distinct, int Solid) Phases(List<Observation> items)
	{
		var bursts = SplitBursts(items);
		var names = new List<string>();
		var solid = 0;
		foreach (var b in bursts)
		{
			names.Add(Phase(b));
			if (b.Count >= 3) solid++;
		}
		return (names, names.Distinct().Count(), solid);
	}

	static List<List<Observation>> SplitBursts(List<Observation> items)
	{
		var bursts = new List<List<Observation>>();
		if (items.Count == 0) return bursts;
		var cur = new List<Observation> { items[0] };
		for (var i = 1; i < items.Count; i++)
		{
			var g = (items[i].Utc - items[i - 1].Utc).TotalSeconds;
			var portChanged = items[i].LocalPort != items[i - 1].LocalPort && items[i].LocalPort > 0 && items[i - 1].LocalPort > 0;
			if (g > Timing.SlowestThinkSec || g >= Timing.FastestThinkSec || portChanged)
			{
				bursts.Add(cur);
				cur = [];
			}
			cur.Add(items[i]);
		}
		if (cur.Count > 0) bursts.Add(cur);
		return bursts;
	}

	static string Phase(List<Observation> burst)
	{
		var http = burst.Where(e => e.Kind == ObsKind.Http).ToList();
		var use = http.Count > 0 ? http : burst;
		if (use.Count == 0) return "empty";
		var paths = use.Select(e => string.IsNullOrEmpty(e.Path) ? "/" : e.Path).Distinct().Count();
		var maxQ = use.GroupBy(e => string.IsNullOrEmpty(e.Path) ? "/" : e.Path)
			.Select(g => g.Select(e => e.Query ?? "").Distinct().Count())
			.DefaultIfEmpty(0).Max();
		var miss = use.Count(e => e.Status >= 400);
		if (paths <= 2 && maxQ >= 5 && use.Count >= 6) return "depth";
		if (paths >= 6 && miss / (double)use.Count >= 0.45) return "breadth";
		if (use.Count <= 6) return "probe";
		return "mixed";
	}

	static List<double> ToolGaps(List<Observation> items)
	{
		var gaps = new List<double>();
		for (var i = 1; i < items.Count; i++)
		{
			var g = (items[i].Utc - items[i - 1].Utc).TotalSeconds;
			if (g >= 0 && g < Timing.FastestThinkSec) gaps.Add(g);
		}
		return gaps;
	}

	static int PeakPerSecond(List<Observation> items)
	{
		var max = 1;
		for (var i = 0; i < items.Count; i++)
		{
			var c = 0;
			for (var j = i; j < items.Count; j++)
			{
				if ((items[j].Utc - items[i].Utc).TotalSeconds > 1) break;
				c++;
			}
			if (c > max) max = c;
		}
		return max;
	}

	static double Median(List<double> values)
	{
		if (values.Count == 0) return 0;
		var s = values.OrderBy(v => v).ToList();
		var mid = s.Count / 2;
		if (s.Count % 2 == 1) return s[mid];
		return (s[mid - 1] + s[mid]) / 2;
	}

	static string FormatGap(double sec)
	{
		if (sec >= 90) return Math.Round(sec / 60.0, 1) + " min";
		return Math.Round(sec) + "s";
	}

	static bool CanaryHit(string path, string canary)
	{
		if (string.IsNullOrWhiteSpace(canary)) return false;
		var a = path.TrimEnd('/');
		var b = canary.TrimEnd('/');
		if (a.Length == 0) a = "/";
		if (b.Length == 0) b = "/";
		return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}

	public static string UaFamily(string? ua)
	{
		var u = (ua ?? "").ToLowerInvariant();
		if (u.Contains("nuclei")) return "nuclei";
		if (u.Contains("sqlmap")) return "sqlmap";
		if (u.Contains("ffuf") || u.Contains("fuzz faster u fool")) return "ffuf";
		if (u.Contains("dalfox")) return "dalfox";
		if (u.Contains("katana")) return "katana";
		if (u.Contains("httpx")) return "httpx";
		if (u.Contains("nikto") || u.Contains("nmap") || u.Contains("masscan") || u.Contains("gobuster") || u.Contains("dirbuster") || u.Contains("wpscan") || u.Contains("zgrab"))
			return "scanner";
		if (u.Contains("go-http-client")) return "go";
		if (u.Contains("python-requests")) return "python";
		if (u.Contains("curl/")) return "curl";
		if (u.Contains("mozilla/")) return "browser";
		if (u.Length == 0) return "empty";
		return "other";
	}

	static bool IsScanner(string fam) =>
		fam is "nuclei" or "sqlmap" or "ffuf" or "dalfox" or "katana" or "httpx" or "scanner";

	static string HeaderKey(string[] headers) =>
		string.Join(",", headers.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().OrderBy(h => h));

	static Signal Soft(string name, int weight, string detail) =>
		new() { Name = name, Weight = weight, Detail = detail, Hard = false };

	public static string SelfCheck()
	{
		var failures = new List<string>();
		var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		var quiet = new List<Observation>();
		for (var i = 0; i < 12; i++)
			quiet.Add(Http(t0.AddSeconds(i * 0.6), "/"));
		var qv = Score("q", quiet, "");
		if (qv.Pace != "quiet") failures.Add("quiet pace got " + qv.Pace);
		if (qv.Cycle.Length != 0) failures.Add("quiet stream counted a model cycle");

		var slow = new List<Observation>
		{
			Http(t0, "/"),
			Http(t0.AddSeconds(90), "/a"),
			Http(t0.AddSeconds(200), "/b"),
			Http(t0.AddSeconds(400), "/c")
		};
		var sv = Score("s", slow, "");
		if (sv.Cycle != "slow") failures.Add("90s pauses got cycle " + sv.Cycle);
		if (!sv.Signals.Any(s => s.Name == "slow_cycle")) failures.Add("missing slow_cycle");

		var fast = new List<Observation>();
		for (var b = 0; b < 3; b++)
		{
			var start = t0.AddSeconds(b * 8);
			for (var i = 0; i < 8; i++)
				fast.Add(Http(start.AddSeconds(i * 0.05), "/p" + b));
		}
		var fv = Score("f", fast, "");
		if (fv.Cycle != "fast") failures.Add("3s-class pauses got cycle " + fv.Cycle + " median " + fv.ThinkMedianSec);
		if (fv.Pace != "aggressive") failures.Add("aggressive burst got pace " + fv.Pace);

		var idle = new List<Observation>
		{
			Http(t0, "/"),
			Http(t0.AddMinutes(20), "/"),
			Http(t0.AddMinutes(40), "/")
		};
		var iv = Score("i", idle, "");
		if (iv.Cycle.Length != 0) failures.Add("20 min idle counted as a model cycle");

		var scan = new List<Observation> { Http(t0, "/", ua: "sqlmap/1.8") };
		if (Score("x", scan, "").Band != "block") failures.Add("scanner ua was not a block");

		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	static Observation Http(DateTime t, string path, string ua = "Go-http-client/1.1") => new()
	{
		Utc = t,
		Src = "203.0.113.8",
		LocalPort = 80,
		Kind = ObsKind.Http,
		Path = path,
		Method = "GET",
		Status = 200,
		UserAgent = ua,
		Headers = ["accept", "user-agent"]
	};
}

namespace Guardex;

// Sessions live here. A host sensor fills them today. A gateway sensor can call Add later.
public sealed class SessionStore
{
	readonly object _gate = new();
	readonly Dictionary<string, List<Observation>> _events = new(StringComparer.Ordinal);
	readonly Dictionary<string, Verdict> _verdicts = new(StringComparer.Ordinal);
	readonly List<FeedItem> _feed = [];
	readonly List<int> _listenPorts = [];
	int _generation;

	public string TcpStatus { get; private set; } = "starting";
	public string HttpStatus { get; private set; } = "starting";
	public string ModelStatus { get; private set; } = "idle";

	public int Generation
	{
		get { lock (_gate) return _generation; }
	}

	public void SetModel(string status)
	{
		lock (_gate)
		{
			if (ModelStatus == status) return;
			ModelStatus = status;
			_generation++;
		}
	}

	public void SetStatus(bool tcp, string status)
	{
		lock (_gate)
		{
			if (tcp)
			{
				if (TcpStatus == status) return;
				TcpStatus = status;
			}
			else
			{
				if (HttpStatus == status) return;
				HttpStatus = status;
			}
			_generation++;
		}
	}

	public void SetListenPorts(IReadOnlyCollection<int> ports)
	{
		lock (_gate)
		{
			if (ports.Count == _listenPorts.Count && ports.All(p => _listenPorts.Contains(p))) return;
			_listenPorts.Clear();
			_listenPorts.AddRange(ports.OrderBy(p => p));
			_generation++;
		}
	}

	internal Verdict? Add(Observation obs)
	{
		lock (_gate)
		{
			if (!_events.TryGetValue(obs.Src, out var list))
			{
				list = [];
				_events[obs.Src] = list;
			}
			list.Add(obs);
			Trim(list);
			var verdict = Scorer.Score(obs.Src, list, Canary);
			_verdicts[obs.Src] = verdict;
			var where = obs.Kind == ObsKind.Http
				? $"{obs.Method} {obs.Path} {obs.Status}"
				: $"tcp :{obs.LocalPort} {obs.LocalProcess}".Trim();
			_feed.Add(new FeedItem { Utc = obs.Utc, Text = $"{obs.Src}  {where}" });
			if (_feed.Count > 80) _feed.RemoveRange(0, _feed.Count - 80);
			_generation++;
			return verdict;
		}
	}

	public void ApplyJudge(string src, int scoreAtAsk, string label, string reason, string band)
	{
		lock (_gate)
		{
			if (!_verdicts.TryGetValue(src, out var v)) return;
			if (v.Score != scoreAtAsk) return;
			v.Judge = label;
			v.Reason = reason;
			if (!string.IsNullOrEmpty(band)) v.Band = band;
			var (action, why) = RecommendFrom(v);
			v.Action = action;
			v.Why = why;
			_generation++;
		}
	}

	static (string Action, string Why) RecommendFrom(Verdict v)
	{
		if (v.Band == "allow") return ("none", "The session sits in the allow band.");
		if (v.Band == "watch") return ("watch", "Keep this source. The score is not high enough to act.");
		if (v.Band == "block") return ("block", "Drop this source on the host. Guardex does not create a firewall rule.");
		if (v.Signals.Any(s => s.Name is "slow_cycle" or "episodic" or "fast_cycle"))
			return ("tarpit", "A slow reply spends the pause while their model thinks. Guardex does not apply it.");
		if (v.Signals.Any(s => s.Name == "port_sweep"))
			return ("block", "A browser check does not stop a port sweep. Guardex does not create a firewall rule.");
		return ("challenge", "A browser check stops a bare HTTP client. A worker driving a real browser can pass it.");
	}

	public string Canary { get; set; } = "";

	public IReadOnlyList<(string Src, int Score, bool Gray)> GrayCandidates()
	{
		lock (_gate)
		{
			return _verdicts.Values
				.Where(v => v.Gray)
				.Select(v => (v.Src, v.Score, v.Gray))
				.ToList();
		}
	}

	public string Digest(string src)
	{
		lock (_gate)
		{
			if (!_verdicts.TryGetValue(src, out var v)) return "";
			var sig = string.Join(", ", v.Signals.Select(s => $"{s.Name}({s.Weight})"));
			if (sig.Length == 0) sig = "none";
			return $"""
				[[guardex-digest]]
				src={v.Src} n={v.Count} span_sec={Math.Round(v.SpanSec)} ports={string.Join(",", v.Ports)}
				pace={v.Pace} cycle={v.Cycle} think_median_sec={Math.Round(v.ThinkMedianSec)} think_gaps={v.ThinkGaps}
				window_min=60 fastest_think_sec=2.5 typical_think_sec=8
				rule_score={v.Score} rule_band={v.Band} gray={v.Gray} ua={v.Families}
				signals={sig}
				[[/guardex-digest]]
				""";
		}
	}

	static void Trim(List<Observation> list)
	{
		var cutoff = DateTime.UtcNow.AddSeconds(-Timing.WindowSec);
		list.RemoveAll(e => e.Utc < cutoff);
		if (list.Count > Timing.MaxEventsPerSource)
			list.RemoveRange(0, list.Count - Timing.MaxEventsPerSource);
	}

	public Dash Copy()
	{
		lock (_gate)
		{
			var sources = _verdicts.Values
				.OrderByDescending(v => v.Score)
				.ThenBy(v => v.Src)
				.Take(100)
				.Select(ToRow)
				.ToList();
			var feed = _feed.TakeLast(40)
				.Select(f => f.Utc.ToLocalTime().ToString("HH:mm:ss") + "  " + f.Text)
				.ToList();
			return new Dash
			{
				Summary = Timing.Summary,
				Tcp = TcpStatus,
				Http = HttpStatus,
				Model = ModelStatus,
				ListenCount = _listenPorts.Count,
				ListenText = string.Join(", ", _listenPorts.Take(24)),
				Sources = sources,
				Feed = feed
			};
		}
	}

	static SourceRow ToRow(Verdict v)
	{
		var lines = new List<string>
		{
			$"{v.Band}  {v.Score}",
			$"{v.Count} events over {Math.Round(v.SpanSec)}s. Ports {(v.Ports.Count == 0 ? "none" : string.Join(", ", v.Ports))}.",
			v.ThinkGaps > 0
				? $"Pauses {v.ThinkGaps}, median {Math.Round(v.ThinkMedianSec)}s. Pace {(v.Pace.Length == 0 ? "none" : v.Pace)}."
				: "No model-sized pause in the window.",
			$"Action {v.Action}. {v.Why}"
		};
		if (v.Judge.Length > 0) lines.Add("Model " + v.Judge + ". " + v.Reason);
		foreach (var s in v.Signals)
			lines.Add($"{s.Weight,4}  {s.Name}  {s.Detail}");
		return new SourceRow
		{
			Src = v.Src,
			Score = v.Score,
			Band = v.Band,
			Cycle = v.Cycle.Length > 0 ? v.Cycle : v.Pace,
			Count = v.Count,
			Detail = string.Join("\n", lines)
		};
	}
}

public sealed class Dash
{
	public string Summary { get; init; } = "";
	public string Tcp { get; init; } = "";
	public string Http { get; init; } = "";
	public string Model { get; init; } = "";
	public int ListenCount { get; init; }
	public string ListenText { get; init; } = "";
	public List<SourceRow> Sources { get; init; } = [];
	public List<string> Feed { get; init; } = [];
}

public sealed class SourceRow
{
	public string Src { get; init; } = "";
	public int Score { get; init; }
	public string Band { get; init; } = "";
	public string Cycle { get; init; } = "";
	public int Count { get; init; }
	public string Detail { get; init; } = "";
}

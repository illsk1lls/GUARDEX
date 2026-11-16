namespace Guardex;

enum ObsKind { Tcp, Http }

sealed class Observation
{
	public DateTime Utc { get; init; }
	public string Src { get; init; } = "";
	public int LocalPort { get; init; }
	public string LocalProcess { get; init; } = "";
	public ObsKind Kind { get; init; }
	public string Path { get; init; } = "";
	public string Query { get; init; } = "";
	public string Method { get; init; } = "";
	public int Status { get; init; }
	public string UserAgent { get; init; } = "";
	public string[] Headers { get; init; } = [];
}

sealed class Signal
{
	public string Name { get; init; } = "";
	public int Weight { get; init; }
	public string Detail { get; init; } = "";
	public bool Hard { get; init; }
}

sealed class Verdict
{
	public string Src { get; init; } = "";
	public int Score { get; set; }
	public string Band { get; set; } = "allow";
	public bool Gray { get; set; }
	public int Count { get; init; }
	public double SpanSec { get; init; }
	public string Pace { get; init; } = "";
	public string Cycle { get; init; } = "";
	public double ThinkMedianSec { get; init; }
	public int ThinkGaps { get; init; }
	public List<Signal> Signals { get; init; } = [];
	public string Action { get; set; } = "none";
	public string Why { get; set; } = "";
	public string Judge { get; set; } = "";
	public string Reason { get; set; } = "";
	public List<int> Ports { get; init; } = [];
	public string Families { get; init; } = "";
	public DateTime UpdatedUtc { get; set; }
}

sealed class FeedItem
{
	public DateTime Utc { get; init; }
	public string Text { get; init; } = "";
}

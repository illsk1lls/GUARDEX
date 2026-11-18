namespace Guardex;

// Speeds a person can actually run at home.
// The tool burst is the fast part. The quiet part is the model thinking.
public static class Timing
{
	// One hour holds a few slow turns. A 15-minute think still leaves a before and an after.
	public const double WindowSec = 60 * 60;

	// Quiet tool pace tops out near 0.75s. 2.5s is the fastest pause we still call a model turn.
	public const double FastestThinkSec = 2.5;

	// A typical home GPU or a small CPU model. At or above this, the pause is the common case.
	public const double TypicalThinkSec = 8;

	// Slower than this, the person stopped. A CPU turn of several minutes still counts.
	public const double SlowestThinkSec = 15 * 60;

	public const int MaxEventsPerSource = 800;

	public const string Summary =
		"Watching the last 60 minutes on this machine. " +
		"HTTP pacing and the pause between tries are scored for scripted clients. " +
		"A browser user-agent is left out of that. " +
		"A sweep is six or more listening ports within two minutes.";
}

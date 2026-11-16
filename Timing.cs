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
		"A home-PC model often goes quiet for 8 seconds up to 15 minutes between tries. " +
		"A pause of 2.5 seconds still counts. Faster than that is scored as the tool rate: about 2, 5, or 20 requests a second.";
}

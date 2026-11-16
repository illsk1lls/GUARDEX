namespace Guardex;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		if (args.Contains("--check"))
		{
			var result = Scorer.SelfCheck();
			Console.WriteLine(result);
			return result == "PASS" ? 0 : 1;
		}

		var app = new System.Windows.Application
		{
			ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
		};
		var shell = new Shell();
		shell.Run();
		app.Run();
		return 0;
	}
}

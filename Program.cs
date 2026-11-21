namespace Guardex;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		if (args.Contains("--check"))
		{
			var parts = new[] { Scorer.SelfCheck(), PostureRules.SelfCheck(), RemoteCatalog.SelfCheck(), Enforcer.SelfCheck(), LaunchRules.SelfCheck(), ClipRules.SelfCheck(), ProcList.SelfCheck(), ModelNames.SelfCheck(), Telemetry.SelfCheck() };
			if (parts.All(p => p == "PASS"))
			{
				Console.WriteLine("PASS");
				return 0;
			}
			Console.WriteLine(string.Join("; ", parts.Where(p => p != "PASS")));
			return 1;
		}

		if (args.Contains("--posture"))
		{
			foreach (var line in PostureWatch.Inspect())
				Console.WriteLine(line);
			return 0;
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

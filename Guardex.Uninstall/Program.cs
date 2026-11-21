namespace Guardex.Uninstall;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		if (args.Contains("--check"))
		{
			var failures = new List<string>();
			if (!UninstallWork.Tagged("other", "GUARDEX")) failures.Add("group");
			if (!UninstallWork.Tagged("GUARDEX 203.0.113.8", "")) failures.Add("address");
			if (!UninstallWork.Tagged("GUARDEX-Telemetry WerFault.exe", "")) failures.Add("telemetry");
			if (UninstallWork.Tagged("Windows Defender Firewall", "")) failures.Add("stranger");
			if (UninstallWork.Tagged("GUARDEX-Telemetry WerFault.exe", "Something else") != true) failures.Add("name still tagged");
			var plain = UninstallWork.Plan(false, false);
			var both = UninstallWork.Plan(true, true);
			var dataOnly = UninstallWork.Plan(true, false);
			var rulesOnly = UninstallWork.Plan(false, true);
			if (plain.Count != 6 || both.Count != 9 || dataOnly.Count != 8 || rulesOnly.Count != 7) failures.Add("plan size");
			if (!dataOnly.Contains("Putting diagnostic settings back.") || dataOnly.Contains("Removing firewall rules.")) failures.Add("data plan");
			if (!rulesOnly.Contains("Removing firewall rules.") || rulesOnly.Contains("Removing GUARDEX data for this account.")) failures.Add("rules plan");
			if (UninstallWork.Percent(0, 9) != 0 || UninstallWork.Percent(9, 9) != 100 || UninstallWork.Percent(3, 6) != 50) failures.Add("percent");
			if (failures.Count == 0)
			{
				Console.WriteLine("PASS");
				return 0;
			}
			Console.WriteLine(string.Join("; ", failures));
			return 1;
		}

		var app = new System.Windows.Application();
		app.Run(new UninstallWindow());
		return 0;
	}
}

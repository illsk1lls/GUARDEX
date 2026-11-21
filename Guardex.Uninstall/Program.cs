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

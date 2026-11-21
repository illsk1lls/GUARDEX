namespace Guardex.Setup;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		if (args.Contains("--check") || args.Contains("--check-logic"))
		{
			var failures = new List<string>();
			if (SetupWork.Relation("", "0.5.0") != "install") failures.Add("fresh");
			if (SetupWork.Relation("0.4.0", "0.5.0") != "upgrade") failures.Add("upgrade");
			if (SetupWork.Relation("0.5.0.0", "0.5.0") != "repair") failures.Add("repair");
			if (SetupWork.Relation("0.6.0", "0.5.0") != "replace") failures.Add("replace");
			if (args.Contains("--check-logic"))
			{
				if (failures.Count == 0)
				{
					Console.WriteLine("PASS");
					return 0;
				}
				Console.WriteLine(string.Join("; ", failures));
				return 1;
			}
			if (!SetupWork.Official(SetupWork.DesktopRuntimeUrl)) failures.Add("runtime url");
			if (!SetupWork.Official(SetupWork.DesktopRuntimeFallbackUrl)) failures.Add("runtime fallback");
			if (!SetupWork.Official(SetupWork.WebView2Url)) failures.Add("webview url");
			if (!SetupWork.HasPayload()) failures.Add("payload missing");
			if (failures.Count == 0)
			{
				Console.WriteLine("PASS");
				return 0;
			}
			Console.WriteLine(string.Join("; ", failures));
			return 1;
		}

		var app = new System.Windows.Application();
		app.Run(new SetupWindow());
		return 0;
	}
}

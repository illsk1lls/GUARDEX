namespace Guardex.Setup;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		if (args.Contains("--check"))
		{
			var failures = new List<string>();
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

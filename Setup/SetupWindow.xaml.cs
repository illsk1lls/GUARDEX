using System.IO;
using System.Windows;

namespace Guardex.Setup;

public partial class SetupWindow : Window
{
	bool _busy;
	string _relation = "install";

	public SetupWindow()
	{
		InitializeComponent();
		Loaded += (_, _) => Prepare();
	}

	void Prepare()
	{
		var installed = SetupWork.InstalledVersion();
		var mine = SetupWork.ThisVersion();
		_relation = SetupWork.Relation(installed, mine);
		if (_relation == "upgrade")
		{
			Intro.Text = "GUARDEX " + installed + " is installed. This setup is " + mine + ", so it can upgrade in place. Settings on this account stay. If the .NET 10 Desktop Runtime or the Edge WebView2 runtime is missing, they are downloaded from Microsoft.";
			InstallButton.Content = "Upgrade";
			StepText.Text = "Ready to upgrade.";
		}
		else if (_relation == "repair")
		{
			Intro.Text = "GUARDEX " + mine + " is already installed. Running setup again replaces the program files, shortcuts, and uninstall entry. Settings on this account stay.";
			InstallButton.Content = "Repair";
			StepText.Text = "Ready to repair.";
		}
		else if (_relation == "replace")
		{
			Intro.Text = "GUARDEX " + installed + " is already installed, and it is newer than this setup (" + mine + "). You can still replace those files with this copy. Settings on this account stay.";
			InstallButton.Content = "Replace";
			StepText.Text = "Ready to replace the newer copy.";
		}
	}

	void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

	void OnClose(object sender, RoutedEventArgs e)
	{
		if (_busy) return;
		Close();
	}

	async void OnInstall(object sender, RoutedEventArgs e)
	{
		if (_busy) return;
		_busy = true;
		InstallButton.IsEnabled = false;
		SignInBox.IsEnabled = false;
		try
		{
			await Install(SignInBox.IsChecked == true);
			StepText.Text = _relation switch
			{
				"upgrade" => "GUARDEX is upgraded.",
				"repair" => "GUARDEX is repaired.",
				"replace" => "GUARDEX was replaced with this copy.",
				_ => "GUARDEX is installed."
			};
			Bar.Value = 100;
			InstallButton.Content = "Close";
			InstallButton.IsEnabled = true;
			InstallButton.Click -= OnInstall;
			InstallButton.Click += (_, _) => Close();
		}
		catch (Exception ex)
		{
			StepText.Text = ex.Message;
			InstallButton.IsEnabled = true;
			SignInBox.IsEnabled = true;
		}
		finally
		{
			_busy = false;
		}
	}

	async Task Install(bool signIn)
	{
		var temp = Path.Combine(Path.GetTempPath(), "GuardexSetup");
		Directory.CreateDirectory(temp);
		var ct = CancellationToken.None;

		if (SetupWork.HasDesktopRuntime())
		{
			Report(15, "The .NET 10 Desktop Runtime is already installed.");
		}
		else
		{
			Report(2, "Downloading the .NET 10 Desktop Runtime from Microsoft.");
			var runtime = Path.Combine(temp, "windowsdesktop-runtime-win-x64.exe");
			try
			{
				await SetupWork.Download(SetupWork.DesktopRuntimeUrl, runtime, new Progress<double>(p => Report(2 + p * 20, "Downloading the .NET 10 Desktop Runtime.")), ct);
			}
			catch
			{
				await SetupWork.Download(SetupWork.DesktopRuntimeFallbackUrl, runtime, new Progress<double>(p => Report(2 + p * 20, "Downloading the .NET 10 Desktop Runtime.")), ct);
			}
			Report(24, "Installing the .NET 10 Desktop Runtime.");
			var code = await SetupWork.RunInstaller(runtime, "/install /quiet /norestart", ct);
			if (!SetupWork.InstallerOk(code))
				throw new InvalidOperationException("The .NET installer returned " + code + ".");
			Report(40, "The .NET 10 Desktop Runtime is installed.");
		}

		if (SetupWork.HasWebView2())
		{
			Report(55, "The WebView2 runtime is already installed.");
		}
		else
		{
			Report(42, "Downloading the WebView2 runtime from Microsoft.");
			var web = Path.Combine(temp, "MicrosoftEdgeWebview2Setup.exe");
			await SetupWork.Download(SetupWork.WebView2Url, web, new Progress<double>(p => Report(42 + p * 12, "Downloading the WebView2 runtime.")), ct);
			Report(56, "Installing the WebView2 runtime.");
			var code = await SetupWork.RunInstaller(web, "/silent /install", ct);
			if (!SetupWork.InstallerOk(code))
				throw new InvalidOperationException("The WebView2 installer returned " + code + ".");
			Report(68, "The WebView2 runtime is installed.");
		}

		if (!SetupWork.HasPayload())
			throw new InvalidOperationException("This setup file does not contain GUARDEX. Build it with build-release.ps1.");

		Report(72, "Closing a running copy, then copying GUARDEX into Program Files.");
		SetupWork.StopApps();
		var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GUARDEX");
		var exe = Path.Combine(dest, "Guardex.exe");
		try
		{
			SetupWork.ExtractPayload(dest);
		}
		catch (IOException)
		{
			throw new InvalidOperationException("GUARDEX is running. Close it, then run setup again.");
		}
		if (!File.Exists(exe))
			throw new InvalidOperationException("Guardex.exe was not in the package.");

		Report(88, "Adding the Start menu shortcut.");
		SetupWork.Shortcut(exe);
		var browser = Path.Combine(dest, "GuardexBrowser.exe");
		if (!File.Exists(browser))
			throw new InvalidOperationException("GuardexBrowser.exe was not in the package.");
		SetupWork.BrowserLinks(browser);
		Report(92, signIn ? "Setting GUARDEX to start at sign-in." : "Leaving startup unchanged.");
		SetupWork.SignInTask(exe, signIn);
		Report(95, "Signing the programs for this PC.");
		var thumb = SetupWork.SignInstalled(dest);
		Report(97, "Adding GUARDEX to the Windows uninstall list.");
		SetupWork.Register(dest, SetupWork.ThisVersion(), thumb);

		Report(98, "Starting GUARDEX.");
		if (System.Diagnostics.Process.GetProcessesByName("Guardex").Length == 0)
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
	}

	void Report(double value, string text)
	{
		Bar.Value = value;
		StepText.Text = text;
	}
}

using System.IO;
using System.Windows;

namespace Guardex.Setup;

public partial class SetupWindow : Window
{
	bool _busy;

	public SetupWindow()
	{
		InitializeComponent();
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
			StepText.Text = "GUARDEX is installed.";
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

		Report(72, "Copying GUARDEX into Program Files.");
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
		Report(94, signIn ? "Setting GUARDEX to start at sign-in." : "Leaving startup unchanged.");
		SetupWork.SignInTask(exe, signIn);

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

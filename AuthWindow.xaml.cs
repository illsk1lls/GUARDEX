using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Guardex;

public partial class AuthWindow : Window
{
	public SignIn? Creds { get; private set; }

	public AuthWindow(string endpoint)
	{
		InitializeComponent();
		Hint.Text = "Sign in to access:\n" + endpoint;
	}

	void OnMode(object sender, RoutedEventArgs e)
	{
		if (ApiPanel == null || NpmPanel == null) return;
		ApiPanel.Visibility = ModeApi.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
		NpmPanel.Visibility = ModeNpm.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
		ErrorLine.Text = "";
	}

	void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
	}

	void OnSignIn(object sender, RoutedEventArgs e)
	{
		var mode = ModeApi.IsChecked == true ? "api" : ModeNpm.IsChecked == true ? "npm" : "none";
		var key = KeyBox.Password.Trim();
		var user = UserBox.Text.Trim();
		var pass = PassBox.Password;
		if (mode == "api" && (key.Length == 0 || key.Equals("none", StringComparison.OrdinalIgnoreCase)))
		{
			ErrorLine.Text = "API mode needs a token.";
			return;
		}
		if (mode == "npm" && (user.Length == 0 || pass.Length == 0))
		{
			ErrorLine.Text = "Basic mode needs a username and password.";
			return;
		}
		Creds = new SignIn
		{
			Mode = mode,
			ApiKey = mode == "api" ? key : "",
			User = mode == "npm" ? user : "",
			Password = mode == "npm" ? pass : "",
			Remember = RememberBox.IsChecked == true
		};
		DialogResult = true;
	}

	void OnCancel(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
	}
}

public sealed class SignIn
{
	public string Mode { get; init; } = "none";
	public string User { get; init; } = "";
	public string Password { get; init; } = "";
	public string ApiKey { get; init; } = "";
	public bool Remember { get; init; }
}

static class EndpointAuth
{
	public static SignIn? Load(string url)
	{
		try
		{
			var path = File();
			if (!System.IO.File.Exists(path)) return null;
			var raw = ProtectedData.Unprotect(System.IO.File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
			using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(raw));
			if (!doc.RootElement.TryGetProperty(Key(url), out var row)) return null;
			var key = Text(row, "apiKey");
			var user = Text(row, "user");
			var mode = Text(row, "mode");
			if (mode is not ("none" or "api" or "npm"))
				mode = key.Length > 0 ? "api" : user.Length > 0 ? "npm" : "none";
			return new SignIn
			{
				Mode = mode,
				User = user,
				Password = Text(row, "password"),
				ApiKey = key
			};
		}
		catch
		{
			return null;
		}
	}

	public static void Save(string url, SignIn creds)
	{
		try
		{
			var map = ReadAll();
			map[Key(url)] = new Dictionary<string, string>
			{
				["mode"] = creds.Mode,
				["user"] = creds.User,
				["password"] = creds.Password,
				["apiKey"] = creds.ApiKey
			};
			var json = JsonSerializer.Serialize(map);
			var raw = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
			var path = File();
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			System.IO.File.WriteAllBytes(path, raw);
		}
		catch { }
	}

	static Dictionary<string, Dictionary<string, string>> ReadAll()
	{
		try
		{
			var path = File();
			if (!System.IO.File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
			var raw = ProtectedData.Unprotect(System.IO.File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
			return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(Encoding.UTF8.GetString(raw))
				?? new(StringComparer.OrdinalIgnoreCase);
		}
		catch
		{
			return new(StringComparer.OrdinalIgnoreCase);
		}
	}

	static string Text(JsonElement row, string name) =>
		row.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

	static string Key(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

	static string File() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex", "endpoint-auth.bin");
}

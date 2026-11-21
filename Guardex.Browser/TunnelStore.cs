using System.IO;
using Guardex.Tunnel;

namespace Guardex.Browser;

static class TunnelStore
{
	public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");
	public static string ConfigPath => Path.Combine(Root, "tunnel.conf");
	public static string ArmedPath => Path.Combine(Root, "tunnel-on.txt");

	public static bool SavedOn()
	{
		try { return File.Exists(ArmedPath) && File.ReadAllText(ArmedPath).Trim() == "1"; }
		catch { return false; }
	}

	public static void SaveArmed(bool on)
	{
		Directory.CreateDirectory(Root);
		File.WriteAllText(ArmedPath, on ? "1" : "0");
	}

	public static bool TryLoad(out string error)
	{
		error = "";
		if (!File.Exists(ConfigPath)) return false;
		var text = File.ReadAllText(ConfigPath);
		return VpnHub.LoadConfig(text, out error);
	}

	public static void SaveConfig(string text)
	{
		Directory.CreateDirectory(Root);
		File.WriteAllText(ConfigPath, text);
	}
}

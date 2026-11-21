using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Guardex.Browser;

static class TrayMark
{
	[DllImport("user32.dll", SetLastError = true)]
	static extern bool DestroyIcon(IntPtr handle);

	public static Icon Make(bool tunnel, bool armed)
	{
		using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		using (var g = Graphics.FromImage(bmp))
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.Clear(Color.Transparent);
			var fill = tunnel ? Color.FromArgb(143, 181, 115) : armed ? Color.FromArgb(224, 160, 96) : Color.FromArgb(110, 118, 106);
			using var brush = new SolidBrush(fill);
			g.FillPolygon(brush, new[]
			{
				new Point(16, 2),
				new Point(28, 7),
				new Point(26, 16),
				new Point(16, 30),
				new Point(6, 16),
				new Point(4, 7)
			});
			using var bar = new SolidBrush(Color.FromArgb(228, 236, 223));
			g.FillRectangle(bar, 9, 11, 14, 3);
		}
		var handle = bmp.GetHicon();
		try
		{
			using var tmp = Icon.FromHandle(handle);
			return (Icon)tmp.Clone();
		}
		finally { DestroyIcon(handle); }
	}
}

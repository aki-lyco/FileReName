using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Explore
{
	/// <summary>
	/// 画像のEXIF/基本情報をテキスト化して返す（AIへのヒントやヒューリスティック用）。
	/// </summary>
	public static class ImageInfoExtractor
	{
		public static bool LooksLikeImage(string path)
		{
			var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
			return ext is ".jpg" or ".jpeg" or ".jpe" or ".png" or ".tif" or ".tiff" or ".bmp" or ".gif" or ".webp" or ".heic" or ".heif";
		}

		public static Task<string> ExtractSummaryAsync(string path)
		{
			try
			{
				using var img = Image.FromFile(path);
				var sb = new StringBuilder();

				sb.AppendLine($"Image: {img.Width}x{img.Height}");

				var make = GetAscii(img, 0x010F);
				var model = GetAscii(img, 0x0110);
				if (!string.IsNullOrWhiteSpace(make) || !string.IsNullOrWhiteSpace(model))
					sb.AppendLine($"Camera: {($"{make} {model}").Trim()}");

				var lens = GetAscii(img, 0xA434);
				if (!string.IsNullOrWhiteSpace(lens))
					sb.AppendLine($"Lens: {lens}");

				var dt = GetAscii(img, 0x9003) ?? GetAscii(img, 0x0132);
				if (!string.IsNullOrWhiteSpace(dt) &&
					DateTime.TryParseExact(dt.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
											DateTimeStyles.AssumeLocal, out var taken))
				{
					sb.AppendLine($"DateTaken: {taken:yyyy-MM-dd HH:mm:ss}");
				}

				var gps = TryGps(img);
				if (gps is not null) sb.AppendLine($"GPS: {gps.Value.lat:F6},{gps.Value.lon:F6}");

				return Task.FromResult(sb.ToString().Trim());
			}
			catch
			{
				return Task.FromResult(string.Empty);
			}

			static string? GetAscii(Image img, int id)
			{
				try
				{
					var pi = img.GetPropertyItem(id);
					if (pi?.Value == null) return null;
					var s = Encoding.ASCII.GetString(pi.Value).Trim('\0').Trim();
					return string.IsNullOrWhiteSpace(s) ? null : s;
				}
				catch { return null; }
			}

			static (double lat, double lon)? TryGps(Image img)
			{
				try
				{
					string? latRef = GetAscii(img, 0x0001), lonRef = GetAscii(img, 0x0003);
					var lat = GetRationals(img, 0x0002);
					var lon = GetRationals(img, 0x0004);
					if (lat?.Length == 3 && lon?.Length == 3 && !string.IsNullOrWhiteSpace(latRef) && !string.IsNullOrWhiteSpace(lonRef))
					{
						double toDeg(double d, double m, double s) => d + m / 60.0 + s / 3600.0;
						var la = toDeg(lat[0], lat[1], lat[2]);
						var lo = toDeg(lon[0], lon[1], lon[2]);
						if (latRef!.StartsWith("S", StringComparison.OrdinalIgnoreCase)) la = -la;
						if (lonRef!.StartsWith("W", StringComparison.OrdinalIgnoreCase)) lo = -lo;
						return (la, lo);
					}
				}
				catch { }
				return null;

				static double[]? GetRationals(Image img, int id)
				{
					try
					{
						var pi = img.GetPropertyItem(id);
						if (pi?.Value == null || pi.Value.Length < 8) return null;
						var arr = new double[pi.Len / 8];
						for (int i = 0; i < arr.Length; i++)
						{
							int num = BitConverter.ToInt32(pi.Value, i * 8 + 0);
							int den = BitConverter.ToInt32(pi.Value, i * 8 + 4);
							if (den == 0) return null;
							arr[i] = num / (double)den;
						}
						return arr;
					}
					catch { return null; }
				}
			}
		}
	}
}

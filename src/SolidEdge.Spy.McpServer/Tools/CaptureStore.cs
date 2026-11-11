using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SolidEdge.Spy.McpServer.Tools;

internal static class CaptureStore
{
	// 契约常量:LRU 配额只允许改这里(2026-10-25 审计:此前逻辑处用字面量,常量被架空,改常量不生效;
	// "50 张/100MB/1500"同时活在常量/字面量/工具描述三处)
	public const int MaxFiles = 50;

	public const long MaxTotalBytes = 104857600L;

	public const int InlineMaxEdge = 1500;

	// 内嵌小图档位(2026-11-10 用户拍板:默认 ≤40KB JPEG,省 token;大图走落盘路径显式请求)
	public const int InlineJpegMaxEdge = 1280;

	public const long InlineJpegMaxBytes = 40960L;

	private static string _lastHashHex;

	private static bool _swept;

	public static string Dir
	{
		get
		{
			string text = Environment.GetEnvironmentVariable("SE_MCP_CAPTURE_DIR");
			if (string.IsNullOrWhiteSpace(text))
			{
				text = Path.Combine(Path.GetTempPath(), "se_mcp_captures");
			}
			return text;
		}
	}

	public static void StartupSweep()
	{
		if (!_swept)
		{
			_swept = true;
			try
			{
				Directory.CreateDirectory(Dir);
			}
			catch
			{
			}
			EnforceRetention();
		}
	}

	public static string NewFilePath(string docName, string label)
	{
		StartupSweep();
		string text = DateTime.Now.ToString("HHmmss");
		string text2 = Path.Combine(Dir, Sanitize(docName) + "_" + label + "_" + text + ".png");
		int num = 2;
		while (File.Exists(text2))
		{
			text2 = Path.Combine(Dir, Sanitize(docName) + "_" + label + "_" + text + "-" + num + ".png");
			num++;
		}
		return text2;
	}

	public static bool IsDuplicate(string path)
	{
		string text = HashFile(path);
		if (text == null)
		{
			return false;
		}
		bool result = text == _lastHashHex;
		_lastHashHex = text;
		return result;
	}

	public static void EnforceRetention()
	{
		try
		{
			DirectoryInfo directoryInfo = new DirectoryInfo(Dir);
			if (!directoryInfo.Exists)
			{
				return;
			}
			List<FileInfo> list = (from f in directoryInfo.GetFiles("*.png")
				orderby f.LastWriteTimeUtc descending
				select f).ToList();
			long num = list.Sum((FileInfo f) => f.Length);
			int num2 = 0;
			foreach (FileInfo item in list)
			{
				num2++;
				if (num2 > MaxFiles || num > MaxTotalBytes)
				{
					num -= item.Length;
					try
					{
						item.Delete();
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}

	public static byte[] DownscaleToPngBytes(string path, int maxEdge)
	{
		using Bitmap bitmap = new Bitmap(path);
		int num = Math.Max(bitmap.Width, bitmap.Height);
		if (num <= maxEdge)
		{
			using (MemoryStream memoryStream = new MemoryStream())
			{
				bitmap.Save(memoryStream, ImageFormat.Png);
				return memoryStream.ToArray();
			}
		}
		using (Bitmap bitmap2 = ScaleToMaxEdge(bitmap, maxEdge))
		{
			using MemoryStream memoryStream2 = new MemoryStream();
			bitmap2.Save(memoryStream2, ImageFormat.Png);
			return memoryStream2.ToArray();
		}
	}

	/// <summary>
	/// 内嵌小图 JPEG(2026-11-10):按 边长递减 × 质量递减 逐档压缩,首档命中 ≤maxBytes 即返回;
	/// 最小档仍超限就如实返回最小档结果(调用方在 note 披露实际大小)。CAD 视口是线条图,
	/// JPEG 高质量下体积可控,优先降边长再降质量。
	/// </summary>
	public static byte[] DownscaleToJpegBytes(string path, int maxEdge, long maxBytes)
	{
		int[] edges = new int[5] { maxEdge, 960, 720, 560, 480 };
		long[] qualities = new long[4] { 80L, 65L, 50L, 40L };
		byte[] last = null;
		using (Bitmap source = new Bitmap(path))
		{
			foreach (int edge in edges)
			{
				using (Bitmap scaled = ScaleToMaxEdge(source, edge))
				{
					foreach (long q in qualities)
					{
						byte[] bytes = EncodeJpeg(scaled, q);
						if (bytes.Length <= maxBytes)
						{
							return bytes;
						}
						last = bytes;
					}
				}
			}
		}
		return last;
	}

	private static Bitmap ScaleToMaxEdge(Bitmap source, int maxEdge)
	{
		int num = Math.Max(source.Width, source.Height);
		if (num <= maxEdge)
		{
			return new Bitmap(source);
		}
		double num2 = (double)maxEdge / (double)num;
		int width = Math.Max(1, (int)Math.Round((double)source.Width * num2));
		int height = Math.Max(1, (int)Math.Round((double)source.Height * num2));
		Bitmap bitmap = new Bitmap(width, height);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
			graphics.SmoothingMode = SmoothingMode.HighQuality;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			graphics.DrawImage(source, new Rectangle(0, 0, width, height));
		}
		return bitmap;
	}

	private static byte[] EncodeJpeg(Bitmap image, long quality)
	{
		ImageCodecInfo encoder = null;
		ImageCodecInfo[] imageEncoders = ImageCodecInfo.GetImageEncoders();
		foreach (ImageCodecInfo imageCodecInfo in imageEncoders)
		{
			if (imageCodecInfo.FormatID == ImageFormat.Jpeg.Guid)
			{
				encoder = imageCodecInfo;
				break;
			}
		}
		using EncoderParameters encoderParameters = new EncoderParameters(1);
		encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
		using MemoryStream memoryStream = new MemoryStream();
		image.Save(memoryStream, encoder, encoderParameters);
		return memoryStream.ToArray();
	}

	private static string HashFile(string path)
	{
		try
		{
			using FileStream inputStream = File.OpenRead(path);
			using SHA256 sHA = SHA256.Create();
			return BitConverter.ToString(sHA.ComputeHash(inputStream)).Replace("-", "");
		}
		catch
		{
			return null;
		}
	}

	private static string Sanitize(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "SE";
		}
		StringBuilder stringBuilder = new StringBuilder();
		string text = name.Trim();
		foreach (char c in text)
		{
			stringBuilder.Append((char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_');
		}
		string text2 = stringBuilder.ToString().TrimEnd('_');
		if (text2.Length == 0)
		{
			text2 = "SE";
		}
		if (text2.Length <= 40)
		{
			return text2;
		}
		return text2.Substring(0, 40);
	}
}

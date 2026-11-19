/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using GenOnlineService.Identity;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The QR encoder behind the authenticator setup page. The expected matrices are the output of the qrcode npm package
// (1.5.4, MIT) for the same text in byte mode; the vectors cover versions 1, 7, 8, 10-M and the 11-L fallback a long
// percent-encoded name needs. The decoder below is only a test instrument: it reads a symbol as the standard says,
// so the setup page's SVG can be turned back into the otpauth URI it was made from.
public class QrCodeTests
{
	// "01234567" and "HELLO WORLD" in byte mode, as the reference encoder lays them out
	[Theory]
	[InlineData("01234567", 'M', 1, 2, "f3e5362ba1db1944df066aa79ed538585eed00762d2c42ade85e0a9f6e03cb5f")]
	[InlineData("HELLO WORLD", 'M', 1, 4, "2d21897bf5a7ac606d02da07bdd5e7f02a8b90097f12404b1f7d764f18ef618c")]
	[InlineData("A", 'M', 1, 3, "bc9009ae87ca68f1d256b69eb26362086d73d9f0b95f1e9f3dccae752626c3b6")]
	[InlineData("otpauth://totp/Generals%20Online%3APlayer%230042?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Generals%20Online&digits=6", 'M', 7, 4, "a3d52f754bade86ab8302edd614b4146ec7b7a5e61d1eb18ae520d65002010bb")]
	[InlineData("otpauth://totp/Generals%20Online%3A%D0%98%D0%B3%D1%80%D0%BE%D0%BA%230001?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Generals%20Online&digits=6", 'M', 8, 5, "d4bcd51deae8e2c2d7d8c8f37edcc4844c4f54faeda17c0b5a1d9f3eb8f8d346")]
	public void TheMatrixMatchesTheReferenceEncoder(string text, char level, int version, int mask, string expectedHash)
	{
		QrCode.Symbol symbol = QrCode.Encode(text, null, level);

		Assert.Equal(version, symbol.Version);
		Assert.Equal(mask, symbol.Mask);
		Assert.Equal(level, symbol.ErrorCorrection);
		Assert.Equal(expectedHash, Hash(symbol));
	}

	// 200 bytes fills version 10-M; 480 bytes needs the L fallback, as an unusually long encoded name would
	[Theory]
	[InlineData(200, 'M', 10, 2, "64e9db0f7dcfa486be63739a1efef2c8324387cf233f7686c75d9b4e079a8873")]
	[InlineData(300, 'M', 13, 0, "a6c4c99a50e77d18b6ddfdcbd85fffbd3b4da81e4eabc96b030a3f559d2b7fbb")]
	[InlineData(340, 'M', 14, 0, "95c9a7c0cf85b5d756d6849130646a1de0e776db8a18c5f4d06005b3548dff04")]
	[InlineData(480, 'L', 15, 0, "b8b84b2470c1d8f341ecd2d277f199b28c9fa767fa401211700ba4c688d86da9")]
	public void ALongTextStillFits(int length, char level, int version, int mask, string expectedHash)
	{
		QrCode.Symbol symbol = QrCode.Encode(new string('x', length));

		Assert.Equal(level, symbol.ErrorCorrection);
		Assert.Equal(version, symbol.Version);
		Assert.Equal(mask, symbol.Mask);
		Assert.Equal(expectedHash, Hash(symbol));
	}

	// The decoder's own proof: a matrix produced by the reference encoder for "HELLO WORLD", byte mode, 1-M
	[Fact]
	public void TheDecoderReadsATrustedReferenceMatrix()
	{
		string[] rows =
		[
			"111111101100101111111",
			"100000100001001000001",
			"101110100101001011101",
			"101110101001001011101",
			"101110101110101011101",
			"100000101001001000001",
			"111111101010101111111",
			"000000001001100000000",
			"100010111111011111001",
			"000100001011100001111",
			"001111110011011010010",
			"111110001100010000000",
			"111110101010101100110",
			"000000001010111101011",
			"111111101110101011010",
			"100000100101110110011",
			"101110101101011000110",
			"101110100100100011011",
			"101110100111000111000",
			"100000100001010000000",
			"111111101111111110101",
		];

		QrDecoder.Decoded decoded = QrDecoder.Decode(QrDecoder.MatrixOf(rows));

		Assert.Equal("HELLO WORLD", decoded.Text);
		Assert.Equal(1, decoded.Version);
		Assert.Equal(4, decoded.Mask);
		Assert.Equal('M', decoded.Level);
	}

	// The SVG the setup page receives: a white backdrop with a quiet zone, black modules, and the URI in the modules
	[Fact]
	public void TheSvgCarriesTheTextInAQuietZone()
	{
		const string uri = "otpauth://totp/Generals%20Online%3APlayer%230042?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Generals%20Online&digits=6";

		string svg = QrCode.Svg(uri, "Authenticator setup QR code");
		(bool[,] modules, int view) = QrDecoder.MatrixFromSvg(svg);

		Assert.Equal(modules.GetLength(0) + 8, view);
		Assert.Equal(uri, QrDecoder.Decode(modules).Text);
		Assert.Contains("class=\"gp-qr\"", svg);
		Assert.Contains("viewBox=\"0 0 ", svg);
		Assert.Contains("<title>Authenticator setup QR code</title>", svg);
		Assert.Contains("aria-label=\"Authenticator setup QR code\"", svg);
		Assert.Contains("role=\"img\"", svg);
		Assert.Contains("fill=\"#FFFFFF\"", svg);
		Assert.Contains("fill=\"#000000\"", svg);
		Assert.Contains("shape-rendering=\"crispEdges\"", svg);
		Assert.DoesNotContain("<script", svg);
		Assert.DoesNotContain("href=", svg);
	}

	// A name that is expensive to percent-encode moves the symbol to the L fallback; the text still round-trips
	[Fact]
	public void ALongEncodedNameUsesTheFallbackAndStillReadsBack()
	{
		string text = "otpauth://totp/" + new string('%', 400) + "?secret=JBSWY3DPEHPK3PXP&issuer=Generals%20Online&digits=6";

		QrCode.Symbol symbol = QrCode.Encode(text);

		Assert.Equal('L', symbol.ErrorCorrection);
		Assert.Equal(text, QrDecoder.Decode(symbol.Modules).Text);
	}

	private static string Hash(QrCode.Symbol symbol)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(String.Join("\n", symbol.Rows())))).ToLowerInvariant();
}

// A QR Code Model 2 reader for the tests: format information, the function modules, the unmasking, the interleaved
// codewords and the byte-mode segment. It shares no code with QrCode.
internal static class QrDecoder
{
	internal sealed record Decoded(string Text, int Version, int Mask, char Level);

	private static readonly int[] s_totalCodewords = [0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346, 404, 466, 532, 581, 655];
	private static readonly int[] s_eccCodewordsM = [0, 10, 16, 26, 36, 48, 64, 72, 88, 110, 130, 150, 176, 198, 216, 240];
	private static readonly int[] s_eccCodewordsL = [0, 7, 10, 15, 20, 26, 36, 40, 48, 60, 72, 80, 96, 104, 120, 132];
	private static readonly int[] s_blocksM = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 8, 9, 9, 9, 10];
	private static readonly int[] s_blocksL = [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6];
	private static readonly int[][] s_alignment =
	[
		[], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50], [6, 30, 54], [6, 32, 58], [6, 34, 62], [6, 26, 46, 66], [6, 26, 48, 70],
	];

	internal static bool[,] MatrixOf(string[] rows)
	{
		var matrix = new bool[rows.Length, rows.Length];
		for (int y = 0; y < rows.Length; y++)
		{
			Assert.Equal(rows.Length, rows[y].Length);
			for (int x = 0; x < rows.Length; x++)
			{
				matrix[y, x] = rows[y][x] == '1';
			}
		}
		return matrix;
	}

	// The path of QrCode.Svg is a run per row: M x,y h length v1 h-length z, offset by the four-module quiet zone
	internal static (bool[,] Modules, int View) MatrixFromSvg(string svg)
	{
		System.Text.RegularExpressions.Match box = Regex.Match(svg, "viewBox=\"0 0 (\\d+) (\\d+)\"");
		Assert.True(box.Success, "no viewBox: " + svg);
		int view = Int32.Parse(box.Groups[1].Value);
		Assert.Equal(view, Int32.Parse(box.Groups[2].Value));

		System.Text.RegularExpressions.Match path = Regex.Match(svg, "<path[^>]* d=\"([^\"]*)\"");
		Assert.True(path.Success, "no path: " + svg);
		int size = view - 8;
		var modules = new bool[size, size];
		foreach (System.Text.RegularExpressions.Match run in Regex.Matches(path.Groups[1].Value, "M(\\d+),(\\d+)h(\\d+)v1h-\\d+z"))
		{
			int x = Int32.Parse(run.Groups[1].Value) - 4;
			int y = Int32.Parse(run.Groups[2].Value) - 4;
			int length = Int32.Parse(run.Groups[3].Value);
			Assert.InRange(x, 0, size - 1);
			Assert.InRange(y, 0, size - 1);
			Assert.InRange(x + length, 1, size);
			for (int i = 0; i < length; i++)
			{
				modules[y, x + i] = true;
			}
		}
		return (modules, view);
	}

	internal static Decoded Decode(bool[,] modules)
	{
		int size = modules.GetLength(0);
		Assert.True(size >= 21 && (size - 17) % 4 == 0, $"not a QR symbol size: {size}");
		int version = (size - 17) / 4;
		(char level, int mask) = ReadFormat(modules);
		bool[,] function = FunctionModules(size, version);

		var plain = (bool[,])modules.Clone();
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				if (!function[y, x] && MaskAt(mask, x, y))
				{
					plain[y, x] = !plain[y, x];
				}
			}
		}

		int total = s_totalCodewords[version];
		var bits = new List<bool>(total * 8);
		for (int right = size - 1; right >= 1; right -= 2)
		{
			if (right == 6)
			{
				right = 5;
			}
			for (int vertical = 0; vertical < size; vertical++)
			{
				for (int j = 0; j < 2; j++)
				{
					int x = right - j;
					bool upward = ((right + 1) & 2) == 0;
					int y = upward ? size - 1 - vertical : vertical;
					if (!function[y, x])
					{
						bits.Add(plain[y, x]);
					}
				}
			}
		}
		Assert.True(bits.Count >= total * 8, "too few data modules");

		var words = new byte[total];
		for (int i = 0; i < total; i++)
		{
			int value = 0;
			for (int j = 0; j < 8; j++)
			{
				value = (value << 1) | (bits[i * 8 + j] ? 1 : 0);
			}
			words[i] = (byte)value;
		}

		int ecc = level == 'M' ? s_eccCodewordsM[version] : s_eccCodewordsL[version];
		int blocks = level == 'M' ? s_blocksM[version] : s_blocksL[version];
		int dataTotal = total - ecc;
		int shortLength = dataTotal / blocks;
		int longCount = dataTotal % blocks;
		var lengths = new int[blocks];
		for (int block = 0; block < blocks; block++)
		{
			lengths[block] = shortLength + (block >= blocks - longCount ? 1 : 0);
		}

		var dataBlocks = new byte[blocks][];
		for (int block = 0; block < blocks; block++)
		{
			dataBlocks[block] = new byte[lengths[block]];
		}
		int index = 0;
		for (int i = 0; i <= shortLength; i++)
		{
			for (int block = 0; block < blocks; block++)
			{
				if (i < lengths[block])
				{
					dataBlocks[block][i] = words[index++];
				}
			}
		}
		Assert.Equal(dataTotal, index);

		var data = new byte[dataTotal];
		int offset = 0;
		foreach (byte[] block in dataBlocks)
		{
			Array.Copy(block, 0, data, offset, block.Length);
			offset += block.Length;
		}

		return new Decoded(ReadSegment(data, version), version, mask, level);
	}

	private static (char Level, int Mask) ReadFormat(bool[,] modules)
	{
		int raw = 0;
		for (int i = 0; i <= 5; i++)
		{
			raw |= (modules[i, 8] ? 1 : 0) << i;
		}
		raw |= (modules[7, 8] ? 1 : 0) << 6;
		raw |= (modules[8, 8] ? 1 : 0) << 7;
		raw |= (modules[8, 7] ? 1 : 0) << 8;
		for (int i = 9; i < 15; i++)
		{
			raw |= (modules[8, 14 - i] ? 1 : 0) << i;
		}

		int data = (raw ^ 0x5412) >> 10;
		char level = ((data >> 3) & 3) switch { 1 => 'L', 0 => 'M', 3 => 'Q', _ => 'H' };
		int mask = data & 7;
		Assert.Equal(raw, FormatBits(level, mask));
		return (level, mask);
	}

	private static int FormatBits(char level, int mask)
	{
		int data = ((level switch { 'L' => 1, 'M' => 0, 'Q' => 3, _ => 2 }) << 3) | mask;
		int remainder = data;
		for (int i = 0; i < 10; i++)
		{
			remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
		}
		return ((data << 10) | remainder) ^ 0x5412;
	}

	private static bool[,] FunctionModules(int size, int version)
	{
		var function = new bool[size, size];
		void Mark(int x, int y) => function[y, x] = true;

		for (int i = 0; i < size; i++)
		{
			Mark(6, i);
			Mark(i, 6);
		}
		foreach ((int cx, int cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
		{
			for (int dy = -4; dy <= 4; dy++)
			{
				for (int dx = -4; dx <= 4; dx++)
				{
					if (cx + dx >= 0 && cy + dy >= 0 && cx + dx < size && cy + dy < size)
					{
						Mark(cx + dx, cy + dy);
					}
				}
			}
		}
		foreach (int cy in s_alignment[version])
		{
			foreach (int cx in s_alignment[version])
			{
				if ((cx == 6 && cy == 6) || (cx == 6 && cy == size - 7) || (cx == size - 7 && cy == 6))
				{
					continue;
				}
				for (int dy = -2; dy <= 2; dy++)
				{
					for (int dx = -2; dx <= 2; dx++)
					{
						Mark(cx + dx, cy + dy);
					}
				}
			}
		}
		if (version >= 7)
		{
			for (int i = 0; i < 18; i++)
			{
				int a = size - 11 + i % 3, b = i / 3;
				Mark(a, b);
				Mark(b, a);
			}
		}
		for (int i = 0; i <= 5; i++)
		{
			Mark(8, i);
		}
		Mark(8, 7);
		Mark(8, 8);
		Mark(7, 8);
		for (int i = 9; i < 15; i++)
		{
			Mark(14 - i, 8);
		}
		for (int i = 0; i < 8; i++)
		{
			Mark(size - 1 - i, 8);
		}
		for (int i = 8; i < 15; i++)
		{
			Mark(8, size - 15 + i);
		}
		Mark(8, size - 8);
		return function;
	}

	private static string ReadSegment(byte[] data, int version)
	{
		int bit = 0;
		int Read(int count)
		{
			int value = 0;
			for (int i = 0; i < count; i++)
			{
				value = (value << 1) | ((data[bit >> 3] >> (7 - (bit & 7))) & 1);
				bit++;
			}
			return value;
		}

		Assert.Equal(0b0100, Read(4));
		int count = Read(version <= 9 ? 8 : 16);
		var bytes = new byte[count];
		for (int i = 0; i < count; i++)
		{
			bytes[i] = (byte)Read(8);
		}
		return Encoding.UTF8.GetString(bytes);
	}

	private static bool MaskAt(int mask, int x, int y) => mask switch
	{
		0 => (x + y) % 2 == 0,
		1 => y % 2 == 0,
		2 => x % 3 == 0,
		3 => (x + y) % 3 == 0,
		4 => (y / 2 + x / 3) % 2 == 0,
		5 => (x * y) % 2 + (x * y) % 3 == 0,
		6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
		_ => ((x * y) % 3 + (x + y) % 2) % 2 == 0,
	};
}

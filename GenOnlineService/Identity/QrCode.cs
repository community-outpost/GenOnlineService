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

using System.Text;

namespace GenOnlineService.Identity
{
	// A QR Code Model 2 encoder (byte mode) written here so the authenticator setup page can show an inline SVG without
	// a script, a second request or a package. Error correction M is tried first; L takes over for a long
	// percent-encoded name, which is the only way an otpauth URI outgrows version 12-M.
	// The matrices are checked against the qrcode npm package's output (the vectors in QrCodeTests).
	internal static class QrCode
	{
		internal sealed record Symbol(int Version, int Mask, char ErrorCorrection, int Size, bool[,] Modules)
		{
			internal string[] Rows()
			{
				var rows = new string[Size];
				for (int y = 0; y < Size; y++)
				{
					var row = new StringBuilder(Size);
					for (int x = 0; x < Size; x++)
					{
						row.Append(Modules[y, x] ? '1' : '0');
					}
					rows[y] = row.ToString();
				}
				return rows;
			}
		}

		// versions 1-15: the symbol's codewords, the error correction codewords and the number of blocks, M and L
		private static readonly int[] s_totalCodewords = [0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346, 404, 466, 532, 581, 655];
		private static readonly int[] s_eccCodewordsM = [0, 10, 16, 26, 36, 48, 64, 72, 88, 110, 130, 150, 176, 198, 216, 240];
		private static readonly int[] s_eccCodewordsL = [0, 7, 10, 15, 20, 26, 36, 40, 48, 60, 72, 80, 96, 104, 120, 132];
		private static readonly int[] s_blocksM = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 8, 9, 9, 9, 10];
		private static readonly int[] s_blocksL = [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6];

		// the centres of the alignment patterns, versions 1-15
		private static readonly int[][] s_alignment =
		[
			[], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50], [6, 30, 54], [6, 32, 58], [6, 34, 62], [6, 26, 46, 66], [6, 26, 48, 70],
		];

		internal static Symbol Encode(string text, int? mask = null, char? errorCorrection = null)
		{
			byte[] payload = Encoding.UTF8.GetBytes(text);
			(char level, int version) = ChooseVersion(payload.Length, errorCorrection);
			int eccTotal = level == 'M' ? s_eccCodewordsM[version] : s_eccCodewordsL[version];
			int blockCount = level == 'M' ? s_blocksM[version] : s_blocksL[version];
			int dataCodewords = s_totalCodewords[version] - eccTotal;

			byte[] data = DataCodewords(payload, version, dataCodewords);
			byte[] interleaved = Interleave(data, version, eccTotal, blockCount);

			int size = 17 + 4 * version;
			var modules = new bool[size, size];
			var function = new bool[size, size];
			DrawTiming(modules, function, size);
			DrawFinder(modules, function, size, 3, 3);
			DrawFinder(modules, function, size, size - 4, 3);
			DrawFinder(modules, function, size, 3, size - 4);
			DrawAlignment(modules, function, size, version);
			DrawVersion(modules, function, size, version);
			DrawFormat(modules, function, size, FormatBits(level, 0));
			DrawData(modules, function, size, interleaved);

			int chosen = mask is int forced ? forced : BestMask(modules, function, size, level);
			var result = (bool[,])modules.Clone();
			ApplyMask(result, function, size, chosen);
			DrawFormat(result, function, size, FormatBits(level, chosen));
			return new Symbol(version, chosen, level, size, result);
		}

		// The SVG of the symbol, quiet zone included and black on white so it scans in the dark theme too. label is
		// encoded by this method; the caller passes plain text.
		internal static string Svg(string text, string label, int pixels = 200)
		{
			Symbol symbol = Encode(text);
			const int quiet = 4;
			int view = symbol.Size + quiet * 2;
			var path = new StringBuilder();
			for (int y = 0; y < symbol.Size; y++)
			{
				int x = 0;
				while (x < symbol.Size)
				{
					if (!symbol.Modules[y, x])
					{
						x++;
						continue;
					}
					int start = x;
					while (x < symbol.Size && symbol.Modules[y, x])
					{
						x++;
					}
					int run = x - start;
					path.Append('M').Append((start + quiet).ToString(System.Globalization.CultureInfo.InvariantCulture))
						.Append(',').Append((y + quiet).ToString(System.Globalization.CultureInfo.InvariantCulture))
						.Append('h').Append(run.ToString(System.Globalization.CultureInfo.InvariantCulture))
						.Append("v1h-").Append(run.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('z');
				}
			}

			string escaped = System.Net.WebUtility.HtmlEncode(label);
			return $"<svg class=\"gp-qr\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {view} {view}\" width=\"{pixels}\" height=\"{pixels}\" role=\"img\" aria-label=\"{escaped}\" shape-rendering=\"crispEdges\">"
				+ $"<title>{escaped}</title><rect width=\"{view}\" height=\"{view}\" fill=\"#FFFFFF\"/><path fill=\"#000000\" d=\"{path}\"/></svg>";
		}

		private static (char Level, int Version) ChooseVersion(int byteCount, char? forced)
		{
			char level = forced ?? 'M';
			if (TryVersion(byteCount, level, out int version))
			{
				return (level, version);
			}
			if (forced != null || !TryVersion(byteCount, 'L', out version))
			{
				throw new ArgumentException("The text is too long for the QR versions this encoder makes.", nameof(byteCount));
			}
			return ('L', version);
		}

		private static bool TryVersion(int byteCount, char level, out int version)
		{
			for (version = 1; version <= 15; version++)
			{
				int ecc = level == 'M' ? s_eccCodewordsM[version] : s_eccCodewordsL[version];
				int data = s_totalCodewords[version] - ecc;
				int countBits = version <= 9 ? 8 : 16;
				if (byteCount <= (data * 8 - 4 - countBits) / 8)
				{
					return true;
				}
			}
			version = 0;
			return false;
		}

		private static byte[] DataCodewords(byte[] payload, int version, int dataCodewords)
		{
			int capacityBits = dataCodewords * 8;
			var bits = new List<bool>(capacityBits);
			AppendBits(bits, 0b0100, 4);
			AppendBits(bits, payload.Length, version <= 9 ? 8 : 16);
			foreach (byte value in payload)
			{
				AppendBits(bits, value, 8);
			}
			AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count));
			while (bits.Count % 8 != 0)
			{
				bits.Add(false);
			}

			var data = new byte[dataCodewords];
			for (int i = 0; i < bits.Count / 8; i++)
			{
				int value = 0;
				for (int j = 0; j < 8; j++)
				{
					value = (value << 1) | (bits[i * 8 + j] ? 1 : 0);
				}
				data[i] = (byte)value;
			}
			for (int i = bits.Count / 8, pad = 0; i < data.Length; i++, pad++)
			{
				data[i] = (byte)(pad % 2 == 0 ? 0xEC : 0x11);
			}
			return data;
		}

		private static void AppendBits(List<bool> bits, int value, int count)
		{
			for (int i = count - 1; i >= 0; i--)
			{
				bits.Add(((value >> i) & 1) != 0);
			}
		}

		private static byte[] Interleave(byte[] data, int version, int eccTotal, int blockCount)
		{
			int eccPerBlock = eccTotal / blockCount;
			int shortLength = data.Length / blockCount;
			int longCount = data.Length % blockCount;
			var dataBlocks = new byte[blockCount][];
			int offset = 0;
			for (int block = 0; block < blockCount; block++)
			{
				int length = shortLength + (block >= blockCount - longCount ? 1 : 0);
				dataBlocks[block] = data[offset..(offset + length)];
				offset += length;
			}

			var eccBlocks = new byte[blockCount][];
			for (int block = 0; block < blockCount; block++)
			{
				eccBlocks[block] = ReedSolomon(dataBlocks[block], eccPerBlock);
			}

			var result = new byte[s_totalCodewords[version]];
			int index = 0;
			for (int i = 0; i <= shortLength; i++)
			{
				for (int block = 0; block < blockCount; block++)
				{
					if (i < dataBlocks[block].Length)
					{
						result[index++] = dataBlocks[block][i];
					}
				}
			}
			for (int i = 0; i < eccPerBlock; i++)
			{
				for (int block = 0; block < blockCount; block++)
				{
					result[index++] = eccBlocks[block][i];
				}
			}
			return result;
		}

		// Reed-Solomon over GF(256) with the QR polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D)
		private static byte[] ReedSolomon(byte[] data, int degree)
		{
			byte[] divisor = Divisor(degree);
			var remainder = new byte[degree];
			foreach (byte value in data)
			{
				int factor = value ^ remainder[0];
				Array.Copy(remainder, 1, remainder, 0, degree - 1);
				remainder[degree - 1] = 0;
				for (int i = 0; i < degree; i++)
				{
					remainder[i] ^= Multiply(divisor[i], factor);
				}
			}
			return remainder;
		}

		private static byte[] Divisor(int degree)
		{
			var result = new byte[degree];
			result[degree - 1] = 1;
			int root = 1;
			for (int i = 0; i < degree; i++)
			{
				for (int j = 0; j < degree; j++)
				{
					result[j] = Multiply(result[j], root);
					if (j + 1 < degree)
					{
						result[j] ^= result[j + 1];
					}
				}
				root = Multiply(root, 0x02);
			}
			return result;
		}

		private static byte Multiply(int x, int y)
		{
			int z = 0;
			for (int i = 7; i >= 0; i--)
			{
				z = (z << 1) ^ ((z >> 7) * 0x11D);
				z ^= ((y >> i) & 1) * x;
			}
			return (byte)z;
		}

		private static void DrawTiming(bool[,] modules, bool[,] function, int size)
		{
			for (int i = 0; i < size; i++)
			{
				Set(modules, function, 6, i, i % 2 == 0);
				Set(modules, function, i, 6, i % 2 == 0);
			}
		}

		private static void DrawFinder(bool[,] modules, bool[,] function, int size, int cx, int cy)
		{
			for (int dy = -4; dy <= 4; dy++)
			{
				for (int dx = -4; dx <= 4; dx++)
				{
					int x = cx + dx, y = cy + dy;
					if (x < 0 || y < 0 || x >= size || y >= size)
					{
						continue;
					}
					int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
					Set(modules, function, x, y, distance != 2 && distance != 4);
				}
			}
		}

		private static void DrawAlignment(bool[,] modules, bool[,] function, int size, int version)
		{
			int[] centres = s_alignment[version];
			foreach (int cy in centres)
			{
				foreach (int cx in centres)
				{
					// the three corners hold finder patterns instead
					if ((cx == 6 && cy == 6) || (cx == 6 && cy == size - 7) || (cx == size - 7 && cy == 6))
					{
						continue;
					}
					for (int dy = -2; dy <= 2; dy++)
					{
						for (int dx = -2; dx <= 2; dx++)
						{
							Set(modules, function, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
						}
					}
				}
			}
		}

		private static void DrawVersion(bool[,] modules, bool[,] function, int size, int version)
		{
			if (version < 7)
			{
				return;
			}
			int remainder = version;
			for (int i = 0; i < 12; i++)
			{
				remainder = (remainder << 1) ^ (((int)((uint)remainder >> 11)) * 0x1F25);
			}
			int bits = (version << 12) | remainder;
			for (int i = 0; i < 18; i++)
			{
				bool bit = ((bits >> i) & 1) != 0;
				int a = size - 11 + i % 3, b = i / 3;
				Set(modules, function, a, b, bit);
				Set(modules, function, b, a, bit);
			}
		}

		private static void DrawFormat(bool[,] modules, bool[,] function, int size, int bits)
		{
			for (int i = 0; i <= 5; i++)
			{
				Set(modules, function, 8, i, Bit(bits, i));
			}
			Set(modules, function, 8, 7, Bit(bits, 6));
			Set(modules, function, 8, 8, Bit(bits, 7));
			Set(modules, function, 7, 8, Bit(bits, 8));
			for (int i = 9; i < 15; i++)
			{
				Set(modules, function, 14 - i, 8, Bit(bits, i));
			}
			for (int i = 0; i < 8; i++)
			{
				Set(modules, function, size - 1 - i, 8, Bit(bits, i));
			}
			for (int i = 8; i < 15; i++)
			{
				Set(modules, function, 8, size - 15 + i, Bit(bits, i));
			}
			Set(modules, function, 8, size - 8, true);
		}

		private static int FormatBits(char level, int mask)
		{
			int data = ((level switch { 'L' => 1, 'M' => 0, 'Q' => 3, _ => 2 }) << 3) | mask;
			int remainder = data;
			for (int i = 0; i < 10; i++)
			{
				remainder = (remainder << 1) ^ (((int)((uint)remainder >> 9)) * 0x537);
			}
			return ((data << 10) | remainder) ^ 0x5412;
		}

		private static void DrawData(bool[,] modules, bool[,] function, int size, byte[] data)
		{
			int index = 0;
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
						if (!function[y, x] && index < data.Length * 8)
						{
							modules[y, x] = Bit(data[index >> 3], 7 - (index & 7));
							index++;
						}
					}
				}
			}
		}

		private static int BestMask(bool[,] modules, bool[,] function, int size, char level)
		{
			int best = 0, lowest = Int32.MaxValue;
			for (int mask = 0; mask < 8; mask++)
			{
				var candidate = (bool[,])modules.Clone();
				ApplyMask(candidate, function, size, mask);
				DrawFormat(candidate, function, size, FormatBits(level, mask));
				int penalty = Penalty(candidate, size);
				if (penalty < lowest)
				{
					lowest = penalty;
					best = mask;
				}
			}
			return best;
		}

		private static void ApplyMask(bool[,] modules, bool[,] function, int size, int mask)
		{
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					if (!function[y, x] && MaskAt(mask, x, y))
					{
						modules[y, x] = !modules[y, x];
					}
				}
			}
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

		private static int Penalty(bool[,] modules, int size)
		{
			int points = 0;

			// N1: runs of five or more modules of one colour, in rows and in columns
			for (int a = 0; a < size; a++)
			{
				int runRow = 0, runColumn = 0;
				bool? lastRow = null, lastColumn = null;
				for (int b = 0; b < size; b++)
				{
					bool row = modules[a, b];
					if (lastRow == row)
					{
						runRow++;
					}
					else
					{
						if (runRow >= 5)
						{
							points += 3 + (runRow - 5);
						}
						lastRow = row;
						runRow = 1;
					}
					bool column = modules[b, a];
					if (lastColumn == column)
					{
						runColumn++;
					}
					else
					{
						if (runColumn >= 5)
						{
							points += 3 + (runColumn - 5);
						}
						lastColumn = column;
						runColumn = 1;
					}
				}
				if (runRow >= 5)
				{
					points += 3 + (runRow - 5);
				}
				if (runColumn >= 5)
				{
					points += 3 + (runColumn - 5);
				}
			}

			// N2: 2x2 blocks of one colour
			for (int y = 0; y < size - 1; y++)
			{
				for (int x = 0; x < size - 1; x++)
				{
					bool value = modules[y, x];
					if (modules[y, x + 1] == value && modules[y + 1, x] == value && modules[y + 1, x + 1] == value)
					{
						points += 3;
					}
				}
			}

			// N3: the finder look-alike 1:1:3:1:1 with four light modules on either side
			for (int a = 0; a < size; a++)
			{
				int rowBits = 0, columnBits = 0;
				for (int b = 0; b < size; b++)
				{
					rowBits = ((rowBits << 1) & 0x7FF) | (modules[a, b] ? 1 : 0);
					if (b >= 10 && (rowBits == 0x5D0 || rowBits == 0x05D))
					{
						points += 40;
					}
					columnBits = ((columnBits << 1) & 0x7FF) | (modules[b, a] ? 1 : 0);
					if (b >= 10 && (columnBits == 0x5D0 || columnBits == 0x05D))
					{
						points += 40;
					}
				}
			}

			// N4: the proportion of dark modules, in steps of five percent away from half
			int dark = 0;
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					if (modules[y, x])
					{
						dark++;
					}
				}
			}
			int total = size * size;
			points += Math.Abs((int)Math.Ceiling(dark * 100.0 / total / 5) - 10) * 10;

			return points;
		}

		private static void Set(bool[,] modules, bool[,] function, int x, int y, bool dark)
		{
			modules[y, x] = dark;
			function[y, x] = true;
		}

		private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;
	}
}

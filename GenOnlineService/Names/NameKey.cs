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

using System.Globalization;
using System.Reflection;
using System.Text;

namespace GenOnlineService.Names
{
	// The uniqueness key of a display name, the same as the Keycloak plugin's registry key (NameRules.normalize):
	// lower case, NFKC, the UTS #39 confusables skeleton, lower case again. Bob, BOB and B0b share one key.
	// Lower case follows Java's Locale.ROOT rules, NameKeyVectors.txt in the tests pins the result against the plugin's code.
	public static class NameKey
	{
		// The plugin's table, generated from ICU4J 78.1 (genonline-keycloak confusables.txt)
		public const string TableResource = "GenOnlineService.Resources.confusables.txt";

		private static readonly Lazy<IReadOnlyDictionary<int, string>> s_table = new(LoadTable);

		// The longest key a storable name can have: 32 characters of NFKC grow at most sevenfold (a Hangul syllable)
		public const int MaxLength = 255;

		public static string Of(string name)
		{
			string folded = LowerRoot(name).Normalize(NormalizationForm.FormKC);
			return LowerRoot(Skeleton(folded));
		}

		// Java's String.toLowerCase(Locale.ROOT): the simple mappings plus the two special cases Java applies in every locale
		public static string LowerRoot(string value)
		{
			int[] points = CodePoints(value);
			StringBuilder lower = new StringBuilder(value.Length + 1);
			for (int i = 0; i < points.Length; i++)
			{
				int point = points[i];
				if (point == CapitalIWithDot)
				{
					// SpecialCasing.txt maps it to i and a combining dot, the invariant culture keeps it
					lower.Append("i\u0307");
				}
				else if (point == CapitalSigma)
				{
					lower.Append(IsFinalSigma(points, i) ? '\u03C2' : '\u03C3');
				}
				else
				{
					lower.Append(Rune.ToLowerInvariant(new Rune(point)).ToString());
				}
			}
			return lower.ToString();
		}

		private const int CapitalIWithDot = 0x0130;
		private const int CapitalSigma = 0x03A3;

		// Java's ConditionalSpecialCasing.isFinalCased: a cased letter before the sigma and none after it, both within
		// one word of Java's word BreakIterator, which WordBoundaries reproduces (NameKeyVectors.txt)
		private static bool IsFinalSigma(int[] points, int index)
		{
			bool[] boundary = WordBoundaries(points);
			for (int i = index; !boundary[i]; i--)
			{
				if (IsJavaCased(points[i - 1]))
				{
					for (int j = index + 1; !boundary[j]; j++)
					{
						if (IsJavaCased(points[j]))
						{
							return false;
						}
					}
					return true;
				}
			}
			return false;
		}

		// The character sets of the JDK word break rules (sun.text.resources.BreakIteratorRules)
		[Flags]
		private enum BreakSet
		{
			None = 0,
			Ignore = 1,
			Letter = 2,
			Digit = 4,
			MidWord = 8,
			MidNum = 16,
			PreNum = 32,
			PostNum = 64,
			Space = 128,
			LineEnd = 256,
			Kanji = 512,
			Katakana = 1024,
			Hiragana = 2048,
			Supplementary = 4096,
			Format = 8192,
			Danda = 16384
		}

		private static BreakSet SetsOf(int point)
		{
			BreakSet sets = BreakSet.None;
			UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(point);
			if (point > 0xFFFF)
			{
				sets |= BreakSet.Supplementary;
			}
			if (point is 0x3005 or (>= 0x4E00 and <= 0x9FA5) or (>= 0xF900 and <= 0xFA2D))
			{
				sets |= BreakSet.Kanji;
			}
			if (point is >= 0x30A1 and <= 0x30FE)
			{
				sets |= BreakSet.Katakana;
			}
			if (point is (>= 0x3041 and <= 0x3094) or 0x309D or 0x309E or 0x30FC)
			{
				sets |= BreakSet.Hiragana;
			}
			bool kana = (sets & (BreakSet.Kanji | BreakSet.Katakana | BreakSet.Hiragana)) != 0;
			if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format && point is not 0x00AD and <= 0xFFFF)
			{
				sets |= category == UnicodeCategory.Format ? BreakSet.Ignore | BreakSet.Format : BreakSet.Ignore;
			}
			if (!kana && (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
				or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.SpacingCombiningMark))
			{
				sets |= BreakSet.Letter;
			}
			if (category is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
			{
				sets |= BreakSet.Digit;
			}
			if (category is UnicodeCategory.DashPunctuation or UnicodeCategory.ConnectorPunctuation && point <= 0xFFFF || point is 0x00AD or 0x2027 or '"' or '\'' or '.')
			{
				sets |= BreakSet.MidWord;
			}
			if (point is '"' or '\'' or ',' or '.' or 0x066B)
			{
				sets |= BreakSet.MidNum;
			}
			if (category == UnicodeCategory.CurrencySymbol || point is '#' or '.' or '^' or 0x00A2)
			{
				sets |= BreakSet.PreNum;
			}
			if (point is 0x0964 or 0x0965)
			{
				sets |= BreakSet.Danda;
			}
			if (point is '%' or '&' or 0x00A2 or 0x066A or 0x2030 or 0x2031)
			{
				sets |= BreakSet.PostNum;
			}
			if (category == UnicodeCategory.SpaceSeparator || point == '\t')
			{
				sets |= BreakSet.Space;
			}
			if (point is '\n' or '\f' or 0x2028 or 0x2029)
			{
				sets |= BreakSet.LineEnd;
			}
			return sets;
		}

		// boundary[i] is true when a word boundary sits before points[i]. Each token is the longest match of the JDK
		// rules: letters and numbers in turn with one middle character inside a word or number, a number after a prefix,
		// spaces, or a run of kanji, katakana or hiragana. A mark joins the character before it. The JDK ends a token
		// after a supplementary character, except one that starts the text.
		private static bool[] WordBoundaries(int[] points)
		{
			int length = points.Length;
			BreakSet[] sets = new BreakSet[length];
			for (int i = 0; i < length; i++)
			{
				sets[i] = SetsOf(points[i]);
			}

			bool[] boundary = new bool[length + 1];
			boundary[0] = true;
			boundary[length] = true;

			int start = 0;
			while (start < length && (sets[start] & BreakSet.Ignore) != 0)
			{
				// a mark that starts the text is a token of its own
				start++;
				boundary[start] = true;
			}

			while (start < length)
			{
				if ((sets[start] & BreakSet.Ignore) != 0 && EndsToken(sets, start - 1))
				{
					// a mark after a supplementary letter or digit counts as one in the next token
					sets[start] |= sets[start - 1] & (BreakSet.Letter | BreakSet.Digit);
				}
				int end = Math.Max(start + 1, Math.Max(MatchWordsAndNumbers(sets, start), MatchPrefixedNumber(sets, start)));
				end = Math.Max(end, MatchRun(sets, start, BreakSet.Space));
				end = Math.Max(end, MatchRun(sets, start, BreakSet.Kanji));
				end = Math.Max(end, MatchRun(sets, start, BreakSet.Katakana));
				end = Math.Max(end, MatchRun(sets, start, BreakSet.Hiragana));
				if (!KeepsMarksOut(sets, end - 1))
				{
					// marks belong to the character before them
					end = SkipIgnored(sets, end);
				}
				boundary[end] = true;
				start = end;
			}
			return boundary;
		}

		private static int SkipIgnored(BreakSet[] sets, int index)
		{
			while (index < sets.Length && (sets[index] & BreakSet.Ignore) != 0)
			{
				index++;
			}
			return index;
		}

		private static bool Is(BreakSet[] sets, int index, BreakSet set) => index >= 0 && index < sets.Length && (sets[index] & set) != 0;

		// Marks after a word's danda, a number's suffix, or a supplementary letter or digit that ends a token start the next token
		private static bool KeepsMarksOut(BreakSet[] sets, int index)
		{
			int last = LastBeforeFormat(sets, index);
			return IsWordDanda(sets, last) || IsNumberSuffix(sets, last) || EndsToken(sets, index) && Is(sets, index, BreakSet.Letter | BreakSet.Digit);
		}

		private static bool IsNumberSuffix(BreakSet[] sets, int index)
		{
			return Is(sets, index, BreakSet.PostNum) && Is(sets, LastBeforeIgnored(sets, index - 1), BreakSet.Digit);
		}

		// index, or the last character before the marks that end at index
		private static int LastBeforeIgnored(BreakSet[] sets, int index)
		{
			while (index >= 0 && (sets[index] & BreakSet.Ignore) != 0)
			{
				index--;
			}
			return index;
		}

		private static bool IsWordDanda(BreakSet[] sets, int index)
		{
			int before = LastBeforeIgnored(sets, index - 1);
			return index >= 0 && Is(sets, index, BreakSet.Danda) && before >= 0 && Is(sets, before, BreakSet.Letter);
		}

		// index, or the last character before the format characters that end at index
		private static int LastBeforeFormat(BreakSet[] sets, int index)
		{
			while (index >= 0 && (sets[index] & BreakSet.Format) != 0)
			{
				index--;
			}
			return index;
		}

		private static int SkipFormat(BreakSet[] sets, int index)
		{
			while (Is(sets, index, BreakSet.Format))
			{
				index++;
			}
			return index;
		}

		// A supplementary character ends the token, unless it starts the text
		private static bool EndsToken(BreakSet[] sets, int index) => index > 0 && (sets[index] & BreakSet.Supplementary) != 0;

		// Members of one set from index, a single middle character between two members included. Returns the end, and
		// ended is true when a supplementary character closed the token.
		private static int MatchRunWithMiddles(BreakSet[] sets, int index, BreakSet member, BreakSet middle, out bool ended)
		{
			ended = false;
			int i = index;
			while (Is(sets, i, member))
			{
				ended = EndsToken(sets, i);
				if (ended)
				{
					return i + 1;
				}
				i = SkipIgnored(sets, i + 1);
				if (!Is(sets, i, member) && Is(sets, i, middle))
				{
					// format characters after the middle character are skipped, a mark ends the token
					int after = SkipFormat(sets, i + 1);
					if (Is(sets, after, member))
					{
						i = after;
					}
				}
			}
			return i;
		}

		// {word}(number word)*{number{post_num}}, where word is letters with middle characters and an optional danda
		private static int MatchWordsAndNumbers(BreakSet[] sets, int start)
		{
			int i = start;
			bool lastNumber = false;
			bool lastWord = false;
			// words and numbers take turns
			while (!lastNumber && !lastWord ? Is(sets, i, BreakSet.Letter | BreakSet.Digit) : Is(sets, i, lastWord ? BreakSet.Digit : BreakSet.Letter))
			{
				bool letter = Is(sets, i, BreakSet.Letter) && !lastWord;
				i = letter
					? MatchRunWithMiddles(sets, i, BreakSet.Letter, BreakSet.MidWord, out bool ended)
					: MatchRunWithMiddles(sets, i, BreakSet.Digit, BreakSet.MidNum, out ended);
				lastNumber = !letter;
				lastWord = letter;
				if (ended)
				{
					return i;
				}
				if (letter && Is(sets, i, BreakSet.Danda))
				{
					// a word may end with a danda
					i = SkipFormat(sets, i + 1);
					if (Is(sets, i, BreakSet.Ignore))
					{
						return i;
					}
				}
			}
			if (lastNumber && Is(sets, i, BreakSet.PostNum))
			{
				i = SkipFormat(sets, i + 1);
			}
			return i;
		}

		// pre_num(number word)*{number{post_num}}
		private static int MatchPrefixedNumber(BreakSet[] sets, int start)
		{
			if (!Is(sets, start, BreakSet.PreNum))
			{
				return start;
			}
			// a mark after the prefix ends it
			int i = start + 1;
			while (Is(sets, i, BreakSet.Digit))
			{
				i = MatchRunWithMiddles(sets, i, BreakSet.Digit, BreakSet.MidNum, out bool ended);
				if (ended)
				{
					return i;
				}
				if (!Is(sets, i, BreakSet.Letter))
				{
					if (Is(sets, i, BreakSet.PostNum))
					{
						i = SkipFormat(sets, i + 1);
					}
					return i;
				}
				i = MatchRunWithMiddles(sets, i, BreakSet.Letter, BreakSet.MidWord, out ended);
				if (ended)
				{
					return i;
				}
				if (Is(sets, i, BreakSet.Danda))
				{
					i = SkipFormat(sets, i + 1);
					if (Is(sets, i, BreakSet.Ignore))
					{
						return i;
					}
				}
			}
			return i;
		}

		private static int MatchRun(BreakSet[] sets, int start, BreakSet set)
		{
			int i = start;
			while (Is(sets, i, set))
			{
				i = SkipIgnored(sets, i + 1);
			}
			return i;
		}

		// Java's ConditionalSpecialCasing.isCased, its Other_Lowercase and Other_Uppercase ranges included
		private static bool IsJavaCased(int point)
		{
			UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(point);
			return category is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter
				|| point is >= 0x02B0 and <= 0x02B8
				|| point is >= 0x02C0 and <= 0x02C1
				|| point is >= 0x02E0 and <= 0x02E4
				|| point is 0x0345 or 0x037A
				|| point is >= 0x1D2C and <= 0x1D61
				|| point is >= 0x2160 and <= 0x217F
				|| point is >= 0x24B6 and <= 0x24E9;
		}

		private static int[] CodePoints(string value)
		{
			List<int> points = new List<int>(value.Length);
			foreach (Rune rune in value.EnumerateRunes())
			{
				points.Add(rune.Value);
			}
			return points.ToArray();
		}

		// skeleton(X) = NFD(map(NFD(X))), one code point at a time
		public static string Skeleton(string value)
		{
			IReadOnlyDictionary<int, string> table = s_table.Value;
			string nfd = value.Normalize(NormalizationForm.FormD);
			StringBuilder mapped = new StringBuilder(nfd.Length);
			foreach (Rune rune in nfd.EnumerateRunes())
			{
				if (table.TryGetValue(rune.Value, out string? target))
				{
					mapped.Append(target);
				}
				else
				{
					mapped.Append(rune.ToString());
				}
			}
			return mapped.ToString().Normalize(NormalizationForm.FormD);
		}

		public static int TableSize => s_table.Value.Count;

		private static IReadOnlyDictionary<int, string> LoadTable()
		{
			using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(TableResource)
				?? throw new InvalidOperationException($"{TableResource} is missing from the assembly");
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8);

			Dictionary<int, string> table = new(16384);
			string? line;
			while ((line = reader.ReadLine()) != null)
			{
				line = line.Trim();
				if (line.Length == 0 || line.StartsWith('#'))
				{
					continue;
				}

				string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				StringBuilder target = new StringBuilder();
				for (int i = 1; i < parts.Length; i++)
				{
					target.Append(Char.ConvertFromUtf32(Int32.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
				}
				table[Int32.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = target.ToString();
			}
			return table;
		}
	}
}

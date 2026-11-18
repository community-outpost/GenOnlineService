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
using System.Text;

namespace GenOnlineService.TextRules
{
	// One folded character and the span of the source text it came from
	public readonly record struct FoldedChar(char C, int Start, int End);

	// A run of one repeated character: "fuuuck" reads f u(3) c k, and a listed "ass" needs its s doubled
	public readonly record struct TextRun(char C, int Count, int Start, int End, bool TokenStart, bool TokenEnd);

	// One way of reading a text, as runs over words; matching runs on these runs
	public sealed record TextReading(string Name, IReadOnlyList<TextRun> Runs)
	{
		// The runs as a plain string, words apart by one space, for the test page
		public string Text
		{
			get
			{
				var text = new StringBuilder(Runs.Count + 8);
				for (int i = 0; i < Runs.Count; i++)
				{
					if (i > 0 && Runs[i].TokenStart)
					{
						text.Append(' ');
					}
					text.Append(Runs[i].C, Math.Min(Runs[i].Count, 2));
				}
				return text.ToString();
			}
		}
	}

	// Folds text before matching: Unicode compatibility forms, case, diacritics and Arabic tashkeel, tatweel and
	// invisible format characters drop out, Arabic letter variants become one letter, Cyrillic and Greek look-alikes
	// become Latin, and leetspeak reads as letters. Every folded character keeps the span it came from, so a hit can be masked.
	public static class TextFolding
	{
		public const string LatinReading = "folded";
		public const string LatinAltReading = "folded_alt";
		public const string ArabiziReading = "arabizi";

		private static readonly Dictionary<char, char> s_lookAlikes = new()
		{
			// Cyrillic
			['\u0430'] = 'a', ['\u0432'] = 'b', ['\u0435'] = 'e', ['\u0451'] = 'e', ['\u043A'] = 'k', ['\u043C'] = 'm', ['\u043D'] = 'h',
			['\u043E'] = 'o', ['\u0440'] = 'p', ['\u0441'] = 'c', ['\u0442'] = 't', ['\u0443'] = 'y', ['\u0445'] = 'x', ['\u0456'] = 'i',
			['\u0458'] = 'j', ['\u0455'] = 's', ['\u0501'] = 'd', ['\u04BB'] = 'h', ['\u0461'] = 'w', ['\u0457'] = 'i', ['\u0491'] = 'r',
			// Greek
			['\u03B1'] = 'a', ['\u03B2'] = 'b', ['\u03B5'] = 'e', ['\u03B7'] = 'n', ['\u03B9'] = 'i', ['\u03BA'] = 'k', ['\u03BD'] = 'v',
			['\u03BF'] = 'o', ['\u03C1'] = 'p', ['\u03C4'] = 't', ['\u03C5'] = 'u', ['\u03C7'] = 'x', ['\u03C9'] = 'w', ['\u03C2'] = 's',
			['\u03C3'] = 's', ['\u03BC'] = 'u'
		};

		// Arabic letter variants that read as one letter for matching
		private static readonly Dictionary<char, char> s_arabic = new()
		{
			['\u0623'] = '\u0627', ['\u0625'] = '\u0627', ['\u0622'] = '\u0627', ['\u0671'] = '\u0627', ['\u0672'] = '\u0627', ['\u0673'] = '\u0627',
			['\u0649'] = '\u064A', ['\u06CC'] = '\u064A', ['\u06D2'] = '\u064A', ['\u0626'] = '\u064A',
			['\u0629'] = '\u0647', ['\u06C0'] = '\u0647', ['\u06C1'] = '\u0647', ['\u06D5'] = '\u0647',
			['\u0624'] = '\u0648',
			['\u06A9'] = '\u0643', ['\u06AA'] = '\u0643'
		};

		// Leetspeak: the reading of digits and symbols inside Latin words. 1 reads as i, the alternative reading as l
		private static readonly Dictionary<char, char> s_leet = new()
		{
			['0'] = 'o', ['1'] = 'i', ['3'] = 'e', ['4'] = 'a', ['5'] = 's', ['7'] = 't', ['@'] = 'a', ['$'] = 's', ['!'] = 'i', ['|'] = 'l'
		};

		// Arabizi digits: the Arabic letters chat written in Latin script uses them for
		private static readonly Dictionary<char, char> s_arabiziDigits = new()
		{
			['2'] = '\u0621', ['3'] = '\u0639', ['5'] = '\u062E', ['6'] = '\u0637', ['7'] = '\u062D', ['8'] = '\u0642', ['9'] = '\u0635'
		};

		// The Arabic prefixes a whole word match accepts in front of a listed Arabic word: the article and the conjunctions
		public static readonly IReadOnlyList<string> ArabicPrefixes = ["\u0627\u0644", "\u0648\u0627\u0644", "\u0628\u0627\u0644", "\u0641\u0627\u0644", "\u0644\u0644", "\u0648", "\u0641", "\u064A\u0627"];

		// The characters that take part in a word: letters, digits and the symbols leetspeak reads as letters
		private static bool IsWordChar(char c) => Char.IsLetterOrDigit(c) || s_leet.ContainsKey(c);

		private static bool IsArabic(char c) => c is >= '\u0600' and <= '\u06FF';

		public static bool IsArabicText(string text) => text.Any(IsArabic);

		// Compatibility folding, case, marks, tatweel, Arabic variants and look-alikes; separators stay
		public static List<FoldedChar> Fold(string text)
		{
			var folded = new List<FoldedChar>(text.Length);
			int index = 0;
			foreach (Rune rune in text.EnumerateRunes())
			{
				int start = index;
				int end = index + rune.Utf16SequenceLength;
				index = end;

				string decomposed = rune.ToString().Normalize(NormalizationForm.FormKD);
				foreach (char raw in decomposed)
				{
					UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(raw);
					// diacritics, Arabic tashkeel, zero-width joiners and direction marks
					if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format)
					{
						continue;
					}
					// tatweel stretches a word and means nothing
					if (raw == '\u0640')
					{
						continue;
					}

					char c = Char.ToLowerInvariant(raw);
					if (c is >= '\u0660' and <= '\u0669')
					{
						c = (char)('0' + (c - '\u0660'));
					}
					else if (c is >= '\u06F0' and <= '\u06F9')
					{
						c = (char)('0' + (c - '\u06F0'));
					}
					else if (s_lookAlikes.TryGetValue(c, out char latin))
					{
						c = latin;
					}
					else if (s_arabic.TryGetValue(c, out char arabic))
					{
						c = arabic;
					}
					folded.Add(new FoldedChar(c, start, end));
				}
			}
			return folded;
		}

		// The words of folded text. Names read as one word: every separator between letters drops out.
		// Elsewhere spaces and punctuation part words, but a row of single characters ("f u c k", "f.u.c.k") joins into one.
		public static List<List<FoldedChar>> Words(List<FoldedChar> folded, bool name)
		{
			var pieces = new List<List<FoldedChar>>();
			List<FoldedChar>? current = null;
			foreach (FoldedChar c in folded)
			{
				if (IsWordChar(c.C))
				{
					current ??= new List<FoldedChar>();
					current.Add(c);
				}
				else if (current != null)
				{
					pieces.Add(current);
					current = null;
				}
			}
			if (current != null)
			{
				pieces.Add(current);
			}

			if (name)
			{
				return pieces.Count <= 1 ? pieces : [pieces.SelectMany(p => p).ToList()];
			}

			var words = new List<List<FoldedChar>>(pieces.Count);
			int i = 0;
			while (i < pieces.Count)
			{
				int j = i;
				while (j < pieces.Count && pieces[j].Count == 1)
				{
					j++;
				}
				if (j - i >= 3)
				{
					words.Add(pieces.Skip(i).Take(j - i).SelectMany(p => p).ToList());
					i = j;
				}
				else
				{
					words.Add(pieces[i]);
					i++;
				}
			}
			return words;
		}

		// The Latin reading with leetspeak; the alternative reading reads 1 as l
		public static TextReading Latin(List<List<FoldedChar>> words, bool alternative)
		{
			var runs = new List<TextRun>();
			foreach (List<FoldedChar> word in words)
			{
				AppendRuns(runs, word.Select(c =>
				{
					char letter = alternative && c.C == '1' ? 'l' : s_leet.TryGetValue(c.C, out char leet) ? leet : c.C;
					return c with { C = letter };
				}).ToList());
			}
			return new TextReading(alternative ? LatinAltReading : LatinReading, runs);
		}

		public static bool NeedsAlternative(List<List<FoldedChar>> words) => words.Any(w => w.Any(c => c.C == '1'));

		// Arabizi: a Latin word with Arabizi digits (3ars, m3ras) read as an Arabic consonant skeleton. Only those words are
		// read, so plain English never reads as Arabic. Null when no word qualifies.
		public static TextReading? Arabizi(List<List<FoldedChar>> words)
		{
			var runs = new List<TextRun>();
			foreach (List<FoldedChar> word in words)
			{
				bool latinLetter = word.Any(c => c.C is >= 'a' and <= 'z');
				bool arabiziDigit = word.Any(c => s_arabiziDigits.ContainsKey(c.C));
				if (!latinLetter || !arabiziDigit || word.Any(c => IsArabic(c.C)))
				{
					continue;
				}
				AppendRuns(runs, LatinSkeleton(word));
			}
			return runs.Count == 0 ? null : new TextReading(ArabiziReading, runs);
		}

		// Arabic script folded to the consonant skeleton Arabizi can spell: long vowels, hamza and ta marbuta drop out,
		// and the letters Latin script cannot tell apart (s and its emphatic, t and its emphatic, ...) read as one
		public static string ArabicSkeleton(string folded)
		{
			var skeleton = new StringBuilder(folded.Length);
			foreach (char c in folded)
			{
				char? letter = c switch
				{
					'\u0627' or '\u0648' or '\u064A' or '\u0621' => null,
					'\u0635' or '\u062B' => '\u0633',
					'\u0637' => '\u062A',
					'\u0636' => '\u062F',
					'\u0630' or '\u0638' => '\u0632',
					'\u062D' => '\u0647',
					'\u0642' => '\u0643',
					_ when IsArabic(c) => c,
					_ => null
				};
				if (letter is char kept && (skeleton.Length == 0 || skeleton[^1] != kept))
				{
					skeleton.Append(kept);
				}
			}
			// ta marbuta, folded to ha, ends many words and Arabizi spells it as a vowel; a final ha from hah stays
			if (folded.EndsWith('\u0647') && skeleton.Length > 0 && skeleton[^1] == '\u0647')
			{
				skeleton.Length--;
			}
			return skeleton.ToString();
		}

		private static List<FoldedChar> LatinSkeleton(List<FoldedChar> word)
		{
			var skeleton = new List<FoldedChar>(word.Count);
			for (int i = 0; i < word.Count; i++)
			{
				char c = word[i].C;
				char next = i + 1 < word.Count ? word[i + 1].C : '\0';
				char? letter = null;
				bool pair = true;
				switch (c, next)
				{
					case ('s', 'h'): case ('c', 'h'): letter = '\u0634'; break;
					case ('k', 'h'): letter = '\u062E'; break;
					case ('g', 'h'): letter = '\u063A'; break;
					case ('t', 'h'): letter = '\u0633'; break;
					case ('d', 'h'): letter = '\u0632'; break;
					default:
						pair = false;
						letter = c switch
						{
							'b' or 'p' => '\u0628',
							't' or '6' => '\u062A',
							'j' or 'g' => '\u062C',
							'h' or '7' => '\u0647',
							'5' => '\u062E',
							'd' => '\u062F',
							'r' => '\u0631',
							'z' => '\u0632',
							's' or '9' => '\u0633',
							'3' => '\u0639',
							'f' or 'v' => '\u0641',
							'q' or 'k' or 'c' or '8' => '\u0643',
							'l' => '\u0644',
							'm' => '\u0645',
							'n' => '\u0646',
							_ => null
						};
						break;
				}
				if (letter is char kept)
				{
					int end = pair ? word[i + 1].End : word[i].End;
					if (skeleton.Count == 0 || skeleton[^1].C != kept)
					{
						skeleton.Add(new FoldedChar(kept, word[i].Start, end));
					}
					else
					{
						skeleton[^1] = skeleton[^1] with { End = end };
					}
				}
				if (pair)
				{
					i++;
				}
			}
			return skeleton;
		}

		private static void AppendRuns(List<TextRun> runs, List<FoldedChar> word)
		{
			int first = runs.Count;
			foreach (FoldedChar c in word)
			{
				if (runs.Count > first && runs[^1].C == c.C)
				{
					TextRun last = runs[^1];
					runs[^1] = last with { Count = last.Count + 1, End = c.End };
				}
				else
				{
					runs.Add(new TextRun(c.C, 1, c.Start, c.End, runs.Count == first, false));
				}
			}
			if (runs.Count > first)
			{
				runs[^1] = runs[^1] with { TokenEnd = true };
			}
		}
	}
}

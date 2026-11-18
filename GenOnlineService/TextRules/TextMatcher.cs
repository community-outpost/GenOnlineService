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

namespace GenOnlineService.TextRules
{
	// A listed word as the matcher holds it; Exception marks a word that keeps the words it contains allowed
	public sealed record TextTermInfo(Int64 ID, string Term, string Language, string Category, string Severity, string Match, bool Exception = false);

	// One match: the listed word, the reading that found it and the span of the source text it covers
	public sealed record TextHit(TextTermInfo Term, string Reading, int Start, int End);

	// Aho-Corasick over the runs of a reading, built once per word list. A key is the run characters of a listed word,
	// words apart by one space; every run of the key needs as many repeats in the text as the word has (at most two).
	public sealed class TextMatcher
	{
		private const char WordGap = ' ';

		private sealed class Entry
		{
			public required TextTermInfo Info { get; init; }
			public required string Key { get; init; }
			public required int[] Counts { get; init; }
			public required bool WholeWord { get; init; }
		}

		private readonly List<Dictionary<char, int>> m_next = [new()];
		private readonly List<int> m_fail = [0];
		private readonly List<List<int>> m_outputs = [[]];
		private readonly List<Entry> m_entries = [];

		public int Count => m_entries.Count;

		// keys are the readings of the words, folded as the text they will match is folded
		public void Add(TextTermInfo info, TextReading key, bool wholeWord)
		{
			if (key.Runs.Count == 0)
			{
				return;
			}
			var chars = new List<char>(key.Runs.Count + 4);
			var counts = new List<int>(key.Runs.Count + 4);
			foreach (TextRun run in key.Runs)
			{
				if (run.TokenStart && chars.Count > 0)
				{
					chars.Add(WordGap);
					counts.Add(0);
				}
				chars.Add(run.C);
				counts.Add(Math.Min(run.Count, 2));
			}

			int node = 0;
			foreach (char c in chars)
			{
				if (!m_next[node].TryGetValue(c, out int child))
				{
					child = m_next.Count;
					m_next.Add(new Dictionary<char, int>());
					m_fail.Add(0);
					m_outputs.Add([]);
					m_next[node][c] = child;
				}
				node = child;
			}
			m_outputs[node].Add(m_entries.Count);
			m_entries.Add(new Entry { Info = info, Key = new string(chars.ToArray()), Counts = counts.ToArray(), WholeWord = wholeWord });
		}

		// Links every node to its longest proper suffix in the trie, breadth first
		public void Build()
		{
			var queue = new Queue<int>();
			foreach (int child in m_next[0].Values)
			{
				m_fail[child] = 0;
				queue.Enqueue(child);
			}
			while (queue.Count > 0)
			{
				int node = queue.Dequeue();
				foreach ((char c, int child) in m_next[node])
				{
					int fail = m_fail[node];
					while (fail != 0 && !m_next[fail].ContainsKey(c))
					{
						fail = m_fail[fail];
					}
					m_fail[child] = m_next[fail].TryGetValue(c, out int target) && target != child ? target : 0;
					m_outputs[child].AddRange(m_outputs[m_fail[child]]);
					queue.Enqueue(child);
				}
			}
		}

		// Every listed word in the reading that no exception covers
		public List<TextHit> Find(TextReading reading)
		{
			IReadOnlyList<TextRun> runs = reading.Runs;
			var stream = new List<char>(runs.Count + 8);
			var runAt = new List<int>(runs.Count + 8);
			for (int i = 0; i < runs.Count; i++)
			{
				if (runs[i].TokenStart && stream.Count > 0)
				{
					stream.Add(WordGap);
					runAt.Add(-1);
				}
				stream.Add(runs[i].C);
				runAt.Add(i);
			}

			var found = new List<(Entry Entry, int First, int Last)>();
			int node = 0;
			for (int position = 0; position < stream.Count; position++)
			{
				char c = stream[position];
				while (node != 0 && !m_next[node].ContainsKey(c))
				{
					node = m_fail[node];
				}
				node = m_next[node].TryGetValue(c, out int next) ? next : 0;
				foreach (int index in m_outputs[node])
				{
					Entry entry = m_entries[index];
					int startPosition = position - entry.Key.Length + 1;
					if (Accepts(entry, runs, runAt, startPosition, position))
					{
						found.Add((entry, runAt[startPosition], runAt[position]));
					}
				}
			}

			var exceptions = found.Where(f => f.Entry.Info.Exception).ToList();
			var hits = new List<TextHit>();
			foreach ((Entry entry, int first, int last) in found)
			{
				if (entry.Info.Exception || exceptions.Any(x => x.First <= first && last <= x.Last))
				{
					continue;
				}
				hits.Add(new TextHit(entry.Info, reading.Name, runs[first].Start, runs[last].End));
			}
			return hits;
		}

		private static bool Accepts(Entry entry, IReadOnlyList<TextRun> runs, List<int> runAt, int startPosition, int endPosition)
		{
			for (int k = 0; k < entry.Counts.Length; k++)
			{
				int run = runAt[startPosition + k];
				if (run >= 0 && runs[run].Count < entry.Counts[k])
				{
					return false;
				}
			}
			if (!entry.WholeWord)
			{
				return true;
			}

			TextRun first = runs[runAt[startPosition]];
			TextRun last = runs[runAt[endPosition]];
			if (!last.TokenEnd)
			{
				return false;
			}
			if (first.TokenStart)
			{
				return true;
			}
			// an Arabic word with the article or a conjunction in front still counts as the whole word
			return entry.Info.Language == TextLanguages.Arabic && TextFolding.ArabicPrefixes.Contains(WordBefore(runs, runAt[startPosition]), StringComparer.Ordinal);
		}

		// The characters of the word in front of a run, repeats spelled out
		private static string WordBefore(IReadOnlyList<TextRun> runs, int run)
		{
			int start = run;
			while (start > 0 && !runs[start].TokenStart)
			{
				start--;
			}
			var prefix = new System.Text.StringBuilder();
			for (int i = start; i < run; i++)
			{
				prefix.Append(runs[i].C, Math.Min(runs[i].Count, 2));
			}
			return prefix.ToString();
		}
	}
}

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

using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace GenOnlineService.Realtime
{
	// Message.mentions (section 4): @name and @name#tag in the text as stored, resolved against the conversation's current members,
	// and in a channel an exact @name#tag also against a user outside it
	internal static class ChatMentions
	{
		// Ready.limits.mention_max, the most users one message mentions
		public const int Max = 10;

		// A tag longer than this is no tag, so the number always fits
		private const int MaxTagDigits = 9;

		public readonly record struct Named(long UserID, string Name, int Tag);

		// The ids in order of appearance without duplicates, at most Max. Names compare case insensitively after NFC (never on the
		// look-alike key), the longest name wins and must end at the end of the text, whitespace, punctuation or '#'. A written tag
		// must match, with or without leading zeros; an outsider resolves only with its tag.
		public static long[] Resolve(string content, IReadOnlyCollection<Named> members, IReadOnlyCollection<Named> outsiders)
		{
			string text = content.Normalize(NormalizationForm.FormC);
			List<(Named User, bool Member)> candidates = members.Select(m => (m with { Name = m.Name.Normalize(NormalizationForm.FormC) }, true))
				.Concat(outsiders.Select(o => (o with { Name = o.Name.Normalize(NormalizationForm.FormC) }, false)))
				.Where(c => c.Item1.Name.Length > 0)
				.ToList();

			List<long> mentioned = new();
			int position = 0;
			while (mentioned.Count < Max)
			{
				int at = text.IndexOf('@', position);
				if (at < 0)
				{
					break;
				}

				int start = at + 1;
				int longest = 0;
				int resumeAt = start;
				List<long> matched = new();
				foreach ((Named user, bool member) in candidates)
				{
					int nameLength = user.Name.Length;
					if (nameLength < longest || start + nameLength > text.Length || String.Compare(text, start, user.Name, 0, nameLength, StringComparison.OrdinalIgnoreCase) != 0)
					{
						continue;
					}

					int end = start + nameLength;
					if (!EndsName(text, end))
					{
						continue;
					}

					long tag = ReadTag(text, end, out int afterTag);
					if (tag >= 0 ? tag != user.Tag : !member)
					{
						continue;
					}

					if (nameLength > longest)
					{
						longest = nameLength;
						matched.Clear();
					}
					matched.Add(user.UserID);
					resumeAt = Math.Max(resumeAt, tag >= 0 ? afterTag : end);
				}

				foreach (long userID in matched)
				{
					if (mentioned.Count < Max && !mentioned.Contains(userID))
					{
						mentioned.Add(userID);
					}
				}
				position = resumeAt;
			}

			return mentioned.ToArray();
		}

		// The names written as @name#tag, which a channel looks up among every user; a name never holds '#', so it ends at the first one
		public static string[] OutsiderNames(string content)
		{
			string text = content.Normalize(NormalizationForm.FormC);
			List<string> names = new();
			int position = 0;
			while (true)
			{
				int at = text.IndexOf('@', position);
				if (at < 0)
				{
					break;
				}

				int start = at + 1;
				position = start;
				int hash = text.IndexOf('#', start, Math.Min(text.Length - start, User.UsernameMaxLength + 1));
				if (hash <= start || ReadTag(text, hash, out _) < 0)
				{
					continue;
				}

				string name = text.Substring(start, hash - start);
				if (!Char.IsWhiteSpace(name[0]) && !Char.IsWhiteSpace(name[^1]) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
				{
					names.Add(name);
				}
			}
			return names.ToArray();
		}

		// Resolves the text against the stored names of the members, and with outsidersByTag (channel: only) against every user
		// written as @name#tag. Reads nothing when the text holds no '@'.
		public static async Task<long[]> ResolveAsync(AppDbContext db, string content, IEnumerable<long> memberIDs, bool outsidersByTag)
		{
			if (!content.Contains('@'))
			{
				return [];
			}

			Dictionary<long, (string Name, int Tag)> names = await RealtimeHub.NamesAsync(db, memberIDs);
			List<Named> members = names.Select(n => new Named(n.Key, n.Value.Name, n.Value.Tag)).OrderBy(n => n.UserID).ToList();

			List<Named> outsiders = new();
			string[] written = outsidersByTag ? OutsiderNames(content) : [];
			if (written.Length > 0)
			{
				// the normalized column narrows the read, the exact NFC comparison in Resolve decides
				string[] keys = written.Select(NameNormalizer.Normalize).Distinct(StringComparer.Ordinal).ToArray();
				outsiders = (await db.Users.AsNoTracking()
					.Where(u => keys.Contains(u.UsernameNormalized))
					.Select(u => new { u.ID, u.Username, u.NameTag })
					.ToListAsync())
					.Select(u => new Named(u.ID, u.Username, u.NameTag))
					.ToList();
			}

			return Resolve(content, members, outsiders);
		}

		// Message.mentions is absent rather than empty
		public static IReadOnlyList<long>? OrNull(IReadOnlyList<long> mentions) => mentions.Count == 0 ? null : mentions;

		private static bool EndsName(string text, int end)
		{
			return end == text.Length || Char.IsWhiteSpace(text[end]) || Char.IsPunctuation(text[end]) || text[end] == '#';
		}

		// The number of "#0042" at position, -1 when none is written there
		private static long ReadTag(string text, int position, out int end)
		{
			end = position;
			if (position >= text.Length || text[position] != '#')
			{
				return -1;
			}

			int digits = position + 1;
			while (digits < text.Length && Char.IsAsciiDigit(text[digits]))
			{
				digits++;
			}

			int count = digits - position - 1;
			if (count == 0 || count > MaxTagDigits || (digits < text.Length && Char.IsLetterOrDigit(text[digits])))
			{
				return -1;
			}

			end = digits;
			return Int64.Parse(text.AsSpan(position + 1, count), NumberStyles.None, CultureInfo.InvariantCulture);
		}
	}
}

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

using GenOnlineService.Names;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService.Realtime
{
	// Finds the player a person typed into an add-a-friend or block-a-player form: a plain player id, or the whole name with its
	// tag ("Name#0042"). The name is the name key of the name rules (NameKey: case, look-alike characters), so a name and tag
	// is one player, as the unique index says. A bare name, part of a name or a name without its tag is nobody: names are not
	// unique, and a form must not guess who was meant. The answer says only whether a player has it; the actions that follow
	// (SocialActions) check the rest.
	public static class SocialPlayers
	{
		// longer than a name and its tag, an id or any text a person has a reason to type
		public const int MaxTextLength = 64;

		public static async Task<long?> FindAsync(AppDbContext db, string? text)
		{
			text = text?.Trim();
			if (String.IsNullOrEmpty(text) || text.Length > MaxTextLength)
			{
				return null;
			}

			if (text.All(Char.IsAsciiDigit))
			{
				return Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0 ? id : null;
			}

			int hash = text.LastIndexOf('#');
			if (hash <= 0 || hash == text.Length - 1)
			{
				return null;
			}

			ReadOnlySpan<char> digits = text.AsSpan(hash + 1);
			if (digits.Length > 4 || digits.ContainsAnyExceptInRange('0', '9'))
			{
				return null;
			}

			string name = text.Substring(0, hash).Trim();
			string key;
			try
			{
				key = NameKey.Of(name);
			}
			catch (ArgumentException)
			{
				// text that is no name at all (a lone surrogate)
				return null;
			}
			if (key.Length == 0 || key.Length > NameKey.MaxLength)
			{
				return null;
			}

			int tag = Int32.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
			long found = await db.Users.AsNoTracking().Where(u => u.NameKey == key && u.NameTag == tag).Select(u => u.ID).FirstOrDefaultAsync();
			return found > 0 ? found : null;
		}
	}
}

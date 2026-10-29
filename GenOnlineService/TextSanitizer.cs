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


// Client-supplied text that ends up in a database column
public static class TextSanitizer
{
	// PostgreSQL rejects NUL characters in text and an over-long value fails the whole statement, so strip NULs and
	// clamp to maxLength (never splitting a surrogate pair). Null and empty become an empty string.
	public static string Clean(string? value, int maxLength)
	{
		if (String.IsNullOrEmpty(value) || maxLength <= 0)
		{
			return String.Empty;
		}

		if (value.Contains('\0'))
		{
			value = value.Replace("\0", String.Empty);
		}

		if (value.Length > maxLength)
		{
			int length = Char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
			value = value.Substring(0, length);
		}

		return value;
	}
}

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
	public static class NameKey
	{
		// The plugin's table, generated from ICU4J 78.1 (genonline-keycloak confusables.txt)
		public const string TableResource = "GenOnlineService.Resources.confusables.txt";

		private static readonly Lazy<IReadOnlyDictionary<int, string>> s_table = new(LoadTable);

		public static string Of(string name)
		{
			string folded = name.ToLowerInvariant().Normalize(NormalizationForm.FormKC);
			return Skeleton(folded).ToLowerInvariant();
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

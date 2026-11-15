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
using GenOnlineService.Names;
using Xunit;

namespace GenOnlineService.Tests;

// NameKey against the Keycloak plugin's own key (NameRules.normalize), from vectors the plugin's code generated
public class NameKeyTests
{
	public static IEnumerable<object[]> Vectors()
	{
		foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "NameKeyVectors.txt")))
		{
			if (line.Length == 0 || line.StartsWith('#'))
			{
				continue;
			}
			string[] columns = line.Split('\t');
			yield return new object[] { columns[2], FromHex(columns[0]), FromHex(columns[1]) };
		}
	}

	private static string FromHex(string codePoints)
	{
		return String.Concat(codePoints.Split(' ').Select(hex => Char.ConvertFromUtf32(Int32.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
	}

	private static string Hex(string value)
	{
		return String.Join(' ', value.EnumerateRunes().Select(rune => rune.Value.ToString("X4", CultureInfo.InvariantCulture)));
	}

	[Theory]
	[MemberData(nameof(Vectors))]
	public void KeyMatchesThePlugin(string label, string input, string pluginKey)
	{
		Assert.True(Hex(pluginKey) == Hex(NameKey.Of(input)), $"{label}: plugin {Hex(pluginKey)}, NameKey {Hex(NameKey.Of(input))}");
	}

	[Fact]
	public void TheVectorsCoverTheCasingSpecialCases()
	{
		List<object[]> vectors = Vectors().ToList();
		Assert.True(vectors.Count >= 60);
		Assert.Contains(vectors, v => ((string)v[1]).Contains('\u0130'));
		Assert.Contains(vectors, v => ((string)v[2]).Contains('\u03C2') && !((string)v[1]).Contains('\u03C2'));
		Assert.Contains(vectors, v => ((string)v[1]).Contains('\u03A3') && !((string)v[2]).Contains('\u03C2'));
	}

	[Fact]
	public void LowerCaseFollowsJavaRootRules()
	{
		// the invariant culture keeps capital I with dot above and never writes a final sigma
		Assert.Equal("\u0130", "\u0130".ToLowerInvariant());
		Assert.Equal("i\u0307stanbul", NameKey.LowerRoot("\u0130stanbul"));
		Assert.Equal("\u03BF\u03B4\u03C5\u03C3\u03C3\u03B5\u03C5\u03C2", NameKey.LowerRoot("\u039F\u0394\u03A5\u03A3\u03A3\u0395\u03A5\u03A3"));
		Assert.Equal("\u03C3", NameKey.LowerRoot("\u03A3"));
		Assert.Equal("\u03B1\u03C3_\u03B2", NameKey.LowerRoot("\u0391\u03A3_\u0392"));
		Assert.Equal("\u03B1\u03C2 \u03B2", NameKey.LowerRoot("\u0391\u03A3 \u0392"));
	}

	// Documented difference (docs/identity.md): .NET takes casing and normalization from the host's ICU and the plugin from
	// its JDK, so a character one of them does not know yet keys differently. U+A7CB (Unicode 16.0) lower-cases to U+0264
	// on JDK 25, a host ICU before Unicode 16.0 keeps it. This pins both possible .NET results.
	[Fact]
	public void ACharacterNewerThanTheHostUnicodeKeysAsTheHostKnowsIt()
	{
		string key = Hex(NameKey.Of("\uA7CB"));
		Assert.Contains(key, new[] { "0264", "A7CB" });
	}
}

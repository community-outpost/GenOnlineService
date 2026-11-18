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

namespace GenOnlineService.Portal.Client
{
	// What the game does with one line of the message of the day
	public enum PortalMotdProblem
	{
		None,
		// The line starts with a backslash and more than 8 characters follow, but they are no colour code: the game still
		// cuts the backslash and the next 8 characters
		InvalidCode,
		// The line is a backslash and at most 8 characters: the game shows it as it is, backslash included
		CodeWithoutText
	}

	// One line as the game's list box shows it: the text, its colour as alpha, red, green and blue, and what went wrong
	public sealed record PortalMotdLine(string Text, byte Alpha, byte Red, byte Green, byte Blue, string? Code, PortalMotdProblem Problem)
	{
		public string Css => $"rgb({Red} {Green} {Blue} / {(Alpha / 255.0).ToString("0.###", CultureInfo.InvariantCulture)})";
	}

	// The message of the day as the game reads it, after WOLWelcomeMenu.cpp: the service fills {0} with the players online,
	// then each line is trimmed, an empty line is a blank row, a line starting with two backslashes shows one, and a line
	// starting with one backslash and more than 8 characters takes those 8 as its colour, alpha red green blue in hex
	public static class PortalMotd
	{
		// The game's default colour of a line, GameSpyColor[GSCOLOR_MOTD]
		public const string DefaultCode = "ffffffff";

		// String.Format as ConfigSources.FormatMotd runs it, the template kept when it is no format string
		public static string Fill(string template, int playersOnline)
		{
			try
			{
				return String.Format(CultureInfo.InvariantCulture, template, playersOnline);
			}
			catch (FormatException)
			{
				return template;
			}
		}

		public static IReadOnlyList<PortalMotdLine> Parse(string? motd, int playersOnline)
		{
			var lines = new List<PortalMotdLine>();
			if (String.IsNullOrEmpty(motd))
			{
				return lines;
			}
			foreach (string raw in Fill(motd, playersOnline).Split('\n'))
			{
				lines.Add(ParseLine(raw));
			}
			return lines;
		}

		public static PortalMotdLine ParseLine(string raw)
		{
			string line = raw.TrimEnd('\r').Trim();
			if (line.Length == 0)
			{
				return new PortalMotdLine(" ", 255, 255, 255, 255, null, PortalMotdProblem.None);
			}
			if (line.StartsWith("\\\\", StringComparison.Ordinal))
			{
				return new PortalMotdLine(line[1..], 255, 255, 255, 255, null, PortalMotdProblem.None);
			}
			if (line[0] != '\\')
			{
				return new PortalMotdLine(line, 255, 255, 255, 255, null, PortalMotdProblem.None);
			}
			if (line.Length <= 9)
			{
				return new PortalMotdLine(line, 255, 255, 255, 255, null, PortalMotdProblem.CodeWithoutText);
			}
			string code = line.Substring(1, 8);
			bool valid = IsCode(code);
			return new PortalMotdLine(line[9..], Byte(code, 0), Byte(code, 2), Byte(code, 4), Byte(code, 6), valid ? code.ToLowerInvariant() : null, valid ? PortalMotdProblem.None : PortalMotdProblem.InvalidCode);
		}

		public static bool IsCode(string code) => code.Length == 8 && code.All(Uri.IsHexDigit);

		// The colour code a line of the editor starts with, after its leading spaces, or null
		public static string? CodeOf(string line)
		{
			string trimmed = line.TrimStart();
			return trimmed.Length > 9 && trimmed[0] == '\\' && trimmed[1] != '\\' && IsCode(trimmed.Substring(1, 8)) ? trimmed.Substring(1, 8).ToLowerInvariant() : null;
		}

		// The line with the colour code set, or without one when the code is null. A line whose text starts with a
		// backslash keeps it visible: doubled without a code, single after one
		public static string WithCode(string line, string? code)
		{
			string text = line.Trim().TrimEnd('\r');
			if (CodeOf(text) != null)
			{
				text = text[9..];
			}
			else if (text.StartsWith("\\\\", StringComparison.Ordinal))
			{
				text = text[1..];
			}
			if (code == null)
			{
				return text.StartsWith('\\') ? "\\" + text : text;
			}
			return "\\" + code.ToLowerInvariant() + text;
		}

		// The distinct colour codes the message uses, in the order they first appear
		public static IReadOnlyList<string> CodesOf(string? motd)
			=> (motd ?? "").Split('\n').Select(CodeOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

		// strtol on "0x" and two characters, as the game's grabUByte reads them: the hex digits up to the first other one
		private static byte Byte(string code, int at)
		{
			int value = 0;
			for (int i = at; i < at + 2; i++)
			{
				int digit = HexValue(code[i]);
				if (digit < 0)
				{
					break;
				}
				value = value * 16 + digit;
			}
			return (byte)value;
		}

		private static int HexValue(char c) => c switch
		{
			>= '0' and <= '9' => c - '0',
			>= 'a' and <= 'f' => c - 'a' + 10,
			>= 'A' and <= 'F' => c - 'A' + 10,
			_ => -1
		};
	}
}

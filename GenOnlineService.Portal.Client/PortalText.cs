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
using System.Text.Json;

namespace GenOnlineService.Portal.Client
{
	// Words for the error codes the portal can meet, any other code shows as it is
	public static class PortalText
	{
		// Times show in UTC, as the service stores them
		public static string Date(DateTime? value) => value is DateTime time ? time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "never";

		// The day alone, for a column that only needs it
		public static string Day(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

		// The activity feed's short age: "just now", "5m ago", "3h ago", "2d ago", then the date
		public static string Ago(DateTime value, DateTime utcNow)
		{
			TimeSpan age = utcNow - value.ToUniversalTime();
			return age.TotalMinutes < 1 ? "just now"
				: age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
				: age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
				: age.TotalDays < 7 ? $"{(int)age.TotalDays}d ago"
				: Date(value);
		}

		// A table's short time: the age within a week, then the day alone, so the column keeps one line
		public static string Recent(DateTime value, DateTime utcNow) => (utcNow - value.ToUniversalTime()).TotalDays < 7 ? Ago(value, utcNow) : Day(value);

		// Counts on the dashboard with a thousands separator, the same in every browser
		public static string Count(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

		// Two letters for an avatar: the start of the display name
		public static string Initials(string name) => name.Length > 0 ? name.Substring(0, Math.Min(2, name.Length)).ToUpperInvariant() : "?";

		// 3725 seconds reads 1:02:05
		public static string Duration(long? seconds) => seconds is long s ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : "";

		// A lobby state of the hub's LobbyState in the words of the pages
		public static string LobbyState(string state) => state switch
		{
			"setup" => "setup",
			"countdown" => "countdown",
			"in_game" => "in a match",
			"complete" => "finished",
			_ => state
		};

		// AS64500 Example Transit, from the GeoIP ASN database; empty when it is not deployed
		public static string Network(long? asn, string? owner) => asn == null ? owner ?? "" : "AS" + asn.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + (owner == null ? "" : " " + owner);

		public static string Error(string? code) => code switch
		{
			null or "" => "The request failed.",
			"forbidden" => "Your account may not do this.",
			"unauthorized" => "You are signed out.",
			"csrf" => "The page expired. Reload it and try again.",
			"not_found" => "It no longer exists.",
			"name.taken" => "That name is taken.",
			"name.length" => "That name is too short or too long.",
			"name.invalid_characters" => "That name has characters that are not allowed.",
			"name.whitespace" => "A name cannot start or end with a space.",
			"name.reserved" => "That name uses a reserved word.",
			"name.managed" => "Names are managed by the sign-in provider.",
			"name.unavailable" => "Renaming is unavailable right now.",
			"invalid_argument" => "A field is missing or not valid.",
			"moderation.target_outranks" => "That player holds the same or a higher staff rank.",
			"moderation.target_offline" => "That player is not connected.",
			"moderation.reason_too_long" => "The reason is too long.",
			"roles.rank" => "That role ranks the same as or above your own.",
			"roles.upstream" => "That role comes from a sign-in provider and follows its sign-ins.",
			"conflict" => "That name is in use, or the token is already revoked, expired or rotated.",
			"token.limit_reached" => "This owner already holds the most active tokens allowed.",
			"account.no_password" => "This account signs in with a provider only and has no password.",
			"account.no_two_factor" => "Two-factor sign-in is not on for this account.",
			"conflict.stale" => "Someone changed this meanwhile. Reload the page and try again.",
			"flag.no_changes" => "The draft holds no change to publish.",
			"flag.in_use" => "The service itself reads this field, so it cannot be archived or moved out of the service.",
			"flag.exists" => "A field with this key exists already.",
			_ => "The service answered: " + code
		};

		// A refused game config change: invalid_argument names the field of the definition the host found wrong
		public static string FlagError(string? code, string? field) => code != "invalid_argument" || field == null ? Error(code) : field switch
		{
			"value" => "The value does not fit the type, the limits or the choices of this field.",
			"rules" => "A targeting rule is not valid: roles must be known roles, countries two-letter codes, a percentage from 1 to 99 and the last rule, and every rule value must fit the field.",
			"custom" => "The custom flagd targeting is not valid.",
			"comment" => "The note is too long.",
			"If-Match" => "The page expired. Reload it and try again.",
			_ => "This is not valid: " + field + "."
		};
	}

	// The game config fields in words, and the values the edit form types
	public static class PortalFlagText
	{
		// The type, the limits and the unit, as the edit form takes them
		public static string TypeText(PortalFlag flag)
		{
			string text = flag.Type switch
			{
				"bool" => "on or off",
				"int" => "whole number",
				"number" => "number",
				"choice" => "one of " + String.Join(", ", (flag.Choices ?? []).Select(c => c.Label)),
				_ => "text"
			};
			if (flag.Type == "string")
			{
				return flag.Max is double longest ? $"{text}, at most {Number(longest)} characters" : text;
			}
			if (flag.Min is double min && flag.Max is double max)
			{
				text += $" from {Number(min)} to {Number(max)}";
			}
			else if (flag.Min is double low)
			{
				text += $", at least {Number(low)}";
			}
			else if (flag.Max is double high)
			{
				text += $", at most {Number(high)}";
			}
			return flag.Unit == null ? text : $"{text}, in {flag.Unit}";
		}

		private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

		// A value in words: on or off, the label of a choice, a number with its unit
		public static string Display(PortalFlag flag, JsonElement value)
		{
			switch (value.ValueKind)
			{
				case JsonValueKind.True:
					return "on";
				case JsonValueKind.False:
					return "off";
				case JsonValueKind.Undefined:
				case JsonValueKind.Null:
					return "none";
			}
			if (flag.Type == "choice" && flag.Choices?.FirstOrDefault(c => c.Value.GetRawText() == value.GetRawText()) is PortalFlagChoice choice)
			{
				return choice.Label;
			}
			if (value.ValueKind == JsonValueKind.String)
			{
				return "\u201c" + value.GetString() + "\u201d";
			}
			return flag.Unit == null ? value.GetRawText() : value.GetRawText() + " " + flag.Unit;
		}

		public static string RuleText(PortalFlag flag, PortalFlagRule rule)
		{
			string who = rule.Kind switch
			{
				"role" => "role " + String.Join(", ", rule.Values ?? []),
				"client" => "client " + String.Join(", ", rule.Values ?? []),
				"country" => "from " + String.Join(", ", rule.Values ?? []),
				"percentage" => rule.Percent + "% of players",
				_ => rule.Kind
			};
			return who + " \u2192 " + Display(flag, rule.Value);
		}

		// The value as the input holds it: true or false, the number, the text, or the JSON of a choice
		public static string TextOf(PortalFlag flag, JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			JsonValueKind.String when flag.Type != "choice" => value.GetString() ?? "",
			JsonValueKind.Undefined => "",
			_ => value.GetRawText()
		};

		// The typed value as JSON of the field's type; the host checks the limits and the choices again
		public static bool TryParse(PortalFlag flag, string text, out JsonElement value, out string? problem)
		{
			problem = null;
			string json;
			switch (flag.Type)
			{
				case "bool":
					json = text == "true" ? "true" : "false";
					break;
				case "int":
					if (!long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole))
					{
						problem = "Enter a whole number.";
						value = default;
						return false;
					}
					json = whole.ToString(CultureInfo.InvariantCulture);
					break;
				case "number":
					if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
					{
						problem = "Enter a number, with a dot for decimals.";
						value = default;
						return false;
					}
					json = number.ToString("R", CultureInfo.InvariantCulture);
					break;
				case "choice":
					json = text;
					break;
				default:
					json = JsonSerializer.Serialize(text);
					break;
			}
			try
			{
				using JsonDocument document = JsonDocument.Parse(json);
				value = document.RootElement.Clone();
			}
			catch (JsonException)
			{
				problem = "Pick one of the choices.";
				value = default;
				return false;
			}
			if ((flag.Type == "int" || flag.Type == "number") && ((flag.Min is double min && value.GetDouble() < min) || (flag.Max is double max && value.GetDouble() > max)))
			{
				problem = "The value is outside its limits: " + TypeText(flag) + ".";
				return false;
			}
			return true;
		}
	}
}

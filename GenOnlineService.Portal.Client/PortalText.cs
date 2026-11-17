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
			_ => "The service answered: " + code
		};
	}
}

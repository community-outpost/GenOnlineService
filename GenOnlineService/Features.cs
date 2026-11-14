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

using GenOnlineService.Realtime;
using System.Text.RegularExpressions;

namespace GenOnlineService
{
	// Client features switch client capabilities on per player, computed from the user's roles through Features:roles in configuration
	public static class ClientFeatures
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ClientFeatures));

		public const string ConfigSection = "Features:roles";

		public const string StatsExporter = "stats_exporter";
		public const string Observer = "observer";

		// The lobby side of an observer seat, PLAYERTEMPLATE_OBSERVER in the game
		public const int ObserverSide = -2;

		private static readonly Regex s_name = new Regex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant);

		// Features the client and the service know, a name outside this set is a typo
		public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { StatsExporter, Observer };

		// Used when the environment has no Features:roles section, an environment that sets one replaces this map entirely
		public static readonly IReadOnlyDictionary<string, string[]> Defaults = new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			[StaffRoles.Streamer] = new[] { StatsExporter, Observer },
			[StaffRoles.Moderator] = new[] { StatsExporter, Observer },
			[StaffRoles.Admin] = new[] { StatsExporter, Observer }
		};

		private static volatile IReadOnlyDictionary<string, string[]> s_featuresByRole = new Dictionary<string, string[]>(StringComparer.Ordinal);

		public static bool IsValidName(string name) => s_name.IsMatch(name);

		// Reads role to feature lists, a bad feature name is an error and a role the code does not know is a warning
		public static IReadOnlyDictionary<string, string[]> Parse(IConfiguration config, List<string> errors, List<string> warnings)
		{
			Dictionary<string, string[]> result = new(StringComparer.Ordinal);

			IConfigurationSection section = config.GetSection(ConfigSection);
			if (!section.Exists())
			{
				return Defaults;
			}

			foreach (IConfigurationSection role in section.GetChildren())
			{
				// a single value counts as a list of one, an empty value as an empty list
				string[] names = role.Value != null
					? (role.Value.Length == 0 ? Array.Empty<string>() : new[] { role.Value })
					: role.GetChildren().Select(c => c.Value ?? String.Empty).ToArray();

				foreach (string name in names.Where(n => !IsValidName(n)))
				{
					errors.Add($"{ConfigSection}:{role.Key} lists feature \"{name}\", feature names are lowercase snake_case");
				}
				foreach (string name in names.Where(n => IsValidName(n) && !Known.Contains(n)))
				{
					errors.Add($"{ConfigSection}:{role.Key} lists unknown feature \"{name}\", known features are {String.Join(", ", Known.Order())}");
				}

				if (!StaffRoles.IsKnown(role.Key))
				{
					warnings.Add($"{ConfigSection} names unknown role \"{role.Key}\", it is ignored");
					continue;
				}

				result[role.Key] = names.Where(Known.Contains).Distinct(StringComparer.Ordinal).ToArray();
			}

			return result;
		}

		// Loads the map at startup, false after logging every bad entry so the service stops
		public static bool Configure(IConfiguration config)
		{
			List<string> errors = new();
			List<string> warnings = new();
			IReadOnlyDictionary<string, string[]> map = Parse(config, errors, warnings);

			foreach (string warning in warnings)
			{
				s_log.LogWarning("{Problem}", warning);
			}

			if (errors.Count > 0)
			{
				foreach (string error in errors)
				{
					s_log.LogCritical("{Problem}", error);
				}
				return false;
			}

			s_featuresByRole = map;
			return true;
		}

		// Sorted and distinct, so two lists compare by sequence
		public static IReadOnlyList<string> For(IReadOnlyDictionary<string, string[]> featuresByRole, IEnumerable<string> roles)
		{
			return roles
				.SelectMany(role => featuresByRole.TryGetValue(role, out string[]? features) ? features : Array.Empty<string>())
				.Distinct(StringComparer.Ordinal)
				.OrderBy(feature => feature, StringComparer.Ordinal)
				.ToArray();
		}

		public static IReadOnlyList<string> Of(Int64 userID) => For(s_featuresByRole, StaffRoles.RolesOf(userID));

		public static bool Has(Int64 userID, string feature) => Of(userID).Contains(feature, StringComparer.Ordinal);

		// Users whose feature list differs between two role snapshots, with their new list
		public static IReadOnlyList<(Int64 UserID, IReadOnlyList<string> Features)> Changes(IReadOnlyDictionary<string, string[]> featuresByRole,
			IReadOnlyDictionary<Int64, HashSet<string>> before, IReadOnlyDictionary<Int64, HashSet<string>> after)
		{
			List<(Int64, IReadOnlyList<string>)> result = new();

			foreach (Int64 userID in before.Keys.Union(after.Keys).Order())
			{
				IReadOnlyList<string> old = For(featuresByRole, before.TryGetValue(userID, out HashSet<string>? a) ? a : Enumerable.Empty<string>());
				IReadOnlyList<string> next = For(featuresByRole, after.TryGetValue(userID, out HashSet<string>? b) ? b : Enumerable.Empty<string>());
				if (!old.SequenceEqual(next, StringComparer.Ordinal))
				{
					result.Add((userID, next));
				}
			}

			return result;
		}

		// Every copy sees the same role change on its own reload, so each sends only to the session groups of its own connections and no connection hears it twice
		public static void PublishChanges(IReadOnlyDictionary<Int64, HashSet<string>> before, IReadOnlyDictionary<Int64, HashSet<string>> after)
		{
			foreach ((Int64 userID, IReadOnlyList<string> features) in Changes(s_featuresByRole, before, after))
			{
				// one user's failure never costs the others their update, the role maps are already swapped
				try
				{
					FeaturesUpdateEvent update = new FeaturesUpdateEvent { Features = features };
					foreach (UserSession session in WebSocketManager.GetAllDataFromUser(userID))
					{
						RealtimeOut.Fire(RealtimeOut.Session(session).FeaturesUpdate(update));
					}
				}
				catch (Exception ex)
				{
					s_log.LogWarning(ex, "FeaturesUpdate for user {UserID} failed", userID);
				}
			}
		}
	}
}

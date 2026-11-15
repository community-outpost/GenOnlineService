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
using System.Text.Json.Nodes;

namespace Database
{
	public sealed record KeycloakImportIdentity(string Provider, string UserID, string? UserName);

	// DisplayName becomes the display_name attribute; the GenOnline Keycloak plugin gives it a tag when it first sees the account
	public sealed record KeycloakImportUser(long PlayerID, IReadOnlyList<KeycloakImportIdentity> Identities, string? DisplayName = null);

	// The official deployment's Keycloak users (identity section 10): player_id is the old user id, and the plugin's counter starts above them
	public static class KeycloakImportFile
	{
		// Providers brokered inside Keycloak; their aliases in the realm must match these names
		public static readonly IReadOnlySet<string> BrokeredProviders = new HashSet<string>(StringComparer.Ordinal)
		{
			global::IdentityProviders.Steam, global::IdentityProviders.Discord, global::IdentityProviders.Google, global::IdentityProviders.GameReplays
		};

		// A Keycloak partial import; an existing Keycloak user is skipped, never overwritten
		public static JsonObject BuildUsers(IEnumerable<KeycloakImportUser> users)
		{
			var list = new JsonArray();
			foreach (KeycloakImportUser user in users)
			{
				var entry = new JsonObject
				{
					// players choose their own login name later; the import only needs a unique one
					["username"] = "player_" + user.PlayerID.ToString(CultureInfo.InvariantCulture),
					["enabled"] = true
				};

				// no email: player ids never match by email, and Keycloak's partial import fails outright on an address an existing account holds
				var attributes = new JsonObject { ["player_id"] = new JsonArray(user.PlayerID.ToString(CultureInfo.InvariantCulture)) };
				if (!String.IsNullOrEmpty(user.DisplayName))
				{
					// without it the plugin's required display_name would make every imported player choose a new name
					attributes["display_name"] = new JsonArray(user.DisplayName);
				}
				entry["attributes"] = attributes;

				var identities = new JsonArray();
				foreach (KeycloakImportIdentity identity in user.Identities.Where(i => BrokeredProviders.Contains(i.Provider)))
				{
					identities.Add(new JsonObject
					{
						["identityProvider"] = identity.Provider,
						["userId"] = identity.UserID,
						["userName"] = String.IsNullOrEmpty(identity.UserName) ? identity.UserID : identity.UserName
					});
				}

				entry["federatedIdentities"] = identities;
				list.Add(entry);
			}

			return new JsonObject { ["ifResourceExists"] = "SKIP", ["users"] = list };
		}

		// The realm attribute that starts the GenOnline Keycloak plugin's player_id counter; the value goes in unchanged
		public const string CounterStartAttribute = "genonlinePlayerIdStart";

		public static JsonObject BuildCounter(IEnumerable<KeycloakImportUser> users)
		{
			long highest = users.Select(u => u.PlayerID).DefaultIfEmpty(0).Max();
			return new JsonObject { [CounterStartAttribute] = highest + 1, ["highest_imported_id"] = highest };
		}

		// Writes <path> and <path>.counter.json; returns both paths
		public static (string UsersPath, string CounterPath) Write(string path, IReadOnlyCollection<KeycloakImportUser> users)
		{
			var options = new JsonSerializerOptions { WriteIndented = true };
			string counterPath = path + ".counter.json";
			File.WriteAllText(path, BuildUsers(users).ToJsonString(options));
			File.WriteAllText(counterPath, BuildCounter(users).ToJsonString(options));
			return (path, counterPath);
		}
	}
}

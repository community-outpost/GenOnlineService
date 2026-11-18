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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenOnlineService.Flags
{
	// The live revision as this copy holds it
	public sealed class FlagSnapshot
	{
		public static readonly FlagSnapshot Empty = new(0, new Dictionary<string, JsonObject>(StringComparer.Ordinal));

		public int Revision { get; }

		// flagd flags by key, every scope
		public IReadOnlyDictionary<string, JsonObject> Flags { get; }

		public FlagSnapshot(int revision, IReadOnlyDictionary<string, JsonObject> flags)
		{
			Revision = revision;
			Flags = flags;
		}

		public static FlagSnapshot Parse(int revision, string document)
		{
			var flags = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
			if (JsonNode.Parse(document)?["flags"] is JsonObject map)
			{
				foreach ((string key, JsonNode? node) in map)
				{
					if (node is JsonObject flag)
					{
						flags[key] = (JsonObject)flag.DeepClone();
					}
				}
			}
			return new FlagSnapshot(revision, flags);
		}

		public static string ScopeOf(JsonObject flag) => flag["metadata"]?["scope"]?.GetValueKind() == JsonValueKind.String ? flag["metadata"]!["scope"]!.GetValue<string>() : FlagScopes.Client;

		public static bool IsEnabled(JsonObject flag) => flag["state"]?.GetValueKind() != JsonValueKind.String || flag["state"]!.GetValue<string>() != "DISABLED";

		// Enabled client flags for this context, by key; a disabled flag is left out so the game keeps its own default
		public IReadOnlyList<(string Key, FlagResult Result)> EvaluateClient(JsonObject context)
		{
			var results = new List<(string, FlagResult)>();
			foreach ((string key, JsonObject flag) in Flags.OrderBy(f => f.Key, StringComparer.Ordinal))
			{
				if (ScopeOf(flag) == FlagScopes.Client && IsEnabled(flag))
				{
					results.Add((key, FlagEvaluator.Evaluate(key, flag, context)));
				}
			}
			return results;
		}

		// The client flags under client.<group>.<name> as the grouped object GET /api/v1/config sends; other client flags are left out
		public JsonObject ServiceConfigFor(JsonObject context)
		{
			const string prefix = "client.";
			var root = new JsonObject();
			foreach ((string key, FlagResult result) in EvaluateClient(context))
			{
				if (!key.StartsWith(prefix, StringComparison.Ordinal))
				{
					continue;
				}

				string[] parts = key[prefix.Length..].Split('.');
				JsonObject? node = root;
				for (int i = 0; i < parts.Length - 1 && node != null; i++)
				{
					if (node[parts[i]] == null)
					{
						node[parts[i]] = new JsonObject();
					}
					node = node[parts[i]] as JsonObject;
				}
				if (node != null && !node.ContainsKey(parts[^1]))
				{
					node[parts[^1]] = result.Value?.DeepClone();
				}
			}
			return root;
		}

		// Changes when the revision or anything in the context changes, so a client may cache the answer by it
		public string ETagFor(JsonObject context)
		{
			byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Revision.ToString(CultureInfo.InvariantCulture) + "\n" + context.ToJsonString()));
			return "\"" + Revision.ToString(CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + "\"";
		}
	}

	// Every copy keeps the live revision in memory: loaded at startup, again when this copy publishes,
	// when another copy says so over the backplane, and on the reconcile tick as a safety net
	public static class FlagCatalog
	{
		private static readonly ILogger s_log = AppLog.For(typeof(FlagCatalog));

		private static volatile FlagSnapshot s_current = FlagSnapshot.Empty;

		public static FlagSnapshot Current => s_current;

		// Raised after a new revision is loaded, with its number; the realtime layer tells the players
		public static event Action<int>? Changed;

		public static async Task Reload(AppDbContext db)
		{
			int latest = await db.FlagRevisions.AsNoTracking().Select(r => (int?)r.Number).MaxAsync() ?? 0;
			if (latest == s_current.Revision)
			{
				return;
			}

			FlagSnapshot next = FlagSnapshot.Empty;
			if (latest > 0)
			{
				string document = await db.FlagRevisions.AsNoTracking().Where(r => r.Number == latest).Select(r => r.Document).SingleAsync();
				next = FlagSnapshot.Parse(latest, document);
			}
			Install(next);
		}

		public static void Install(FlagSnapshot snapshot)
		{
			s_current = snapshot;
			s_log.LogInformation("Flags revision {Revision} is live with {Count} flags", snapshot.Revision, snapshot.Flags.Count);
			try
			{
				Changed?.Invoke(snapshot.Revision);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "A flags change handler failed");
			}
		}

		// The value of a service flag for code that has no player; fallback when the flag is missing, disabled or of another type
		public static T Get<T>(string key, T fallback)
		{
			ServiceFlags.Register(key);
			if (!s_current.Flags.TryGetValue(key, out JsonObject? flag) || !FlagSnapshot.IsEnabled(flag))
			{
				return fallback;
			}

			FlagResult result = FlagEvaluator.Evaluate(key, flag, new JsonObject());
			try
			{
				return result.Value == null ? fallback : result.Value.GetValue<T>();
			}
			catch (Exception ex) when (ex is InvalidOperationException or FormatException)
			{
				return fallback;
			}
		}
	}
}

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

using System.Text.Json;

namespace GenOnlineService.Identity
{
	// The fingerprinted address of a static file, as /portal/poster.m73fwoi15c.webp for /portal/poster.webp. MapStaticAssets
	// serves those with "max-age=31536000, immutable" and the plain address with a short cache that is checked again, so
	// the sign-in pages link the fingerprinted ones: a browser keeps them for a year, and a deploy that changes a file
	// changes its address. The addresses come from the same build manifest MapStaticAssets reads; without it (a host that
	// does not serve static assets) the plain address stays.
	public static class PortalAssets
	{
		public static readonly string ManifestPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(PortalAssets).Assembly.GetName().Name}.staticwebassets.endpoints.json");

		private static readonly Lazy<IReadOnlyDictionary<string, string>> s_routes = new(() => Load(ManifestPath));

		public static string Url(string path)
			=> s_routes.Value.TryGetValue(path.TrimStart('/'), out string? route) ? "/" + route : path;

		// label (the plain path) -> fingerprinted route, from the endpoints manifest; only the uncompressed endpoint of each,
		// since the compressed ones answer the same route by Accept-Encoding
		public static IReadOnlyDictionary<string, string> Load(string manifestPath)
		{
			var routes = new Dictionary<string, string>(StringComparer.Ordinal);
			if (!File.Exists(manifestPath))
			{
				return routes;
			}

			using FileStream stream = File.OpenRead(manifestPath);
			using JsonDocument manifest = JsonDocument.Parse(stream);
			foreach (JsonElement endpoint in manifest.RootElement.GetProperty("Endpoints").EnumerateArray())
			{
				if (endpoint.TryGetProperty("Selectors", out JsonElement selectors) && selectors.GetArrayLength() > 0)
				{
					continue;
				}
				string? label = null;
				bool fingerprinted = false;
				foreach (JsonElement property in endpoint.GetProperty("EndpointProperties").EnumerateArray())
				{
					switch (property.GetProperty("Name").GetString())
					{
						case "label":
							label = property.GetProperty("Value").GetString();
							break;
						case "fingerprint":
							fingerprinted = true;
							break;
					}
				}
				if (label != null && fingerprinted)
				{
					routes[label] = endpoint.GetProperty("Route").GetString()!;
				}
			}
			return routes;
		}
	}
}

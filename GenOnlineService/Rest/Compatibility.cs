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

using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RegexGroup = System.Text.RegularExpressions.Group;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace GenOnlineService.Rest
{
	// The patcher an outdated client downloads; the size is null when the service has no copy of it
	public sealed record CompatibilityUpdate
	{
		public string PatcherName { get; init; } = "";
		public string PatcherPath { get; init; } = "";
		public long? PatcherSize { get; init; }
	}

	[ApiFixture("get-compatibility.response")]
	public sealed record CompatibilityResponse
	{
		public bool Compatible { get; init; }
		public string? MinVersion { get; init; }
		public string ServiceVersion { get; init; } = "";
		public CompatibilityUpdate? Update { get; init; }
	}

	// Checks a client version against the min_version of its client in the registry
	public static class Compatibility
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Compatibility));

		// CalVer YYYY.MM.PATCH, nothing before or after it
		private static readonly Regex s_version = new(@"^(\d{4})\.(\d{1,2})\.(\d{1,9})\z", RegexOptions.CultureInvariant);

		// The registry stores min_version as three numbers of up to 9 digits
		private static readonly Regex s_minVersion = new(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})\z", RegexOptions.CultureInvariant);

		// Informational version stamped into the build from the git tag (GoResolveVersion in the csproj)
		public static string ServiceVersion { get; } = typeof(Compatibility).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

		public static Task<Result<CompatibilityResponse>> CheckAsync(string? clientID, string? version)
		{
			return EvaluateAsync(clientID, version, id => ClientRegistry.FindEnabled(id), ReadUpdateAsync);
		}

		// Arguments first, then the client; the update is only read for an incompatible version
		public static async Task<Result<CompatibilityResponse>> EvaluateAsync(string? clientID, string? version, Func<string, RegisteredClient?> findEnabled, Func<Task<CompatibilityUpdate?>> loadUpdate)
		{
			if (String.IsNullOrEmpty(clientID))
			{
				return Result.Invalid("client");
			}

			if (!TryParseVersion(version, out (int, int, int) parsed))
			{
				return Result.Invalid("version");
			}

			RegisteredClient? client = findEnabled(clientID);
			if (client == null)
			{
				return Result.NotFound("client");
			}

			bool compatible = IsCompatible(parsed, client.MinVersion);
			return Result.Success(new CompatibilityResponse
			{
				Compatible = compatible,
				MinVersion = client.MinVersion,
				ServiceVersion = ServiceVersion,
				Update = compatible ? null : await loadUpdate()
			});
		}

		public static bool TryParseVersion(string? version, out (int Year, int Month, int Patch) parsed)
		{
			parsed = default;
			RegexMatch match = version == null ? RegexMatch.Empty : s_version.Match(version);
			if (!match.Success)
			{
				return false;
			}

			int month = Number(match.Groups[2]);
			if (month < 1 || month > 12)
			{
				return false;
			}

			parsed = (Number(match.Groups[1]), month, Number(match.Groups[3]));
			return true;
		}

		// A client without a usable min_version accepts every version
		public static bool IsCompatible((int Year, int Month, int Patch) version, string? minVersion)
		{
			RegexMatch match = minVersion == null ? RegexMatch.Empty : s_minVersion.Match(minVersion);
			if (!match.Success)
			{
				return true;
			}

			(int, int, int) minimum = (Number(match.Groups[1]), Number(match.Groups[2]), Number(match.Groups[3]));
			return version.CompareTo(minimum) >= 0;
		}

		private static int Number(RegexGroup group) => int.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture);

		// data/patchdata.json names the patcher; its size is that of crcfiles/<patcher_name> when the file is there
		private static async Task<CompatibilityUpdate?> ReadUpdateAsync()
		{
			string path = Path.Combine("data", "patchdata.json");
			if (!File.Exists(path))
			{
				s_log.LogWarning("data/patchdata.json is missing, an outdated client gets no update");
				return null;
			}

			try
			{
				using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
				string? name = ReadString(document.RootElement, "patcher_name");
				string? url = ReadString(document.RootElement, "patcher_path");
				if (String.IsNullOrEmpty(name) || String.IsNullOrEmpty(url))
				{
					s_log.LogWarning("data/patchdata.json needs patcher_name and patcher_path");
					return null;
				}

				return new CompatibilityUpdate { PatcherName = name, PatcherPath = url, PatcherSize = PatcherSize(name) };
			}
			catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
			{
				s_log.LogWarning(ex, "data/patchdata.json could not be read");
				return null;
			}
		}

		// Only a plain file name inside crcfiles counts, never a path out of it
		private static long? PatcherSize(string name)
		{
			if (Path.GetFileName(name) != name)
			{
				return null;
			}

			FileInfo file = new FileInfo(Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", name));
			return file.Exists ? file.Length : null;
		}

		private static string? ReadString(JsonElement element, string property)
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
		}
	}
}

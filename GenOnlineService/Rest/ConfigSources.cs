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
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace GenOnlineService.Rest
{
	// The game's network tuning and the message of the day
	[ApiFixture("get-config.response")]
	public sealed record ConfigResponse
	{
		public JsonElement ServiceConfig { get; init; }
		public string? Motd { get; init; }
	}

	// Reads the sources of GET /api/v1/config: files under data/ and the motd application setting
	public static class ConfigSources
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ConfigSources));

		private static readonly string s_serviceConfigPath = Path.Combine("data", "serviceconfig.json");
		private static readonly string s_motdPath = Path.Combine("data", "motd.txt");

		// Null when the file is missing or is not a JSON object
		public static async Task<JsonElement?> ReadServiceConfigAsync()
		{
			string? text = await ReadFileAsync(s_serviceConfigPath);
			JsonElement? config = text == null ? null : ParseServiceConfig(text);
			if (text != null && config == null)
			{
				s_log.LogError("data/serviceconfig.json is not a JSON object");
			}
			return config;
		}

		public static JsonElement? ParseServiceConfig(string text)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(text);
				return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		// The motd setting wins over data/motd.txt; {0} is the number of players online
		public static async Task<string?> ReadMotdAsync(IDbContextFactory<AppDbContext> dbFactory)
		{
			string? stored;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				stored = await Database.Upstream.GetSetting(db, SettingKeys.Motd);
			}

			if (!TryReadMotdSetting(stored, out string? template))
			{
				if (stored != null)
				{
					s_log.LogWarning("The motd setting is not a JSON string, data/motd.txt is used instead");
				}
				template = await ReadFileAsync(s_motdPath);
			}

			return template == null ? null : FormatMotd(template, WebSocketManager.GetNumberOfUsersOnline());
		}

		// A setting is used when it holds a JSON string; null, another JSON value or no row falls back to the file
		public static bool TryReadMotdSetting(string? stored, out string? motd)
		{
			motd = null;
			if (stored == null)
			{
				return false;
			}

			try
			{
				using JsonDocument document = JsonDocument.Parse(stored);
				if (document.RootElement.ValueKind != JsonValueKind.String)
				{
					return false;
				}
				motd = document.RootElement.GetString();
				return true;
			}
			catch (JsonException)
			{
				return false;
			}
		}

		// An invalid format string is sent as written; an empty message is null
		public static string? FormatMotd(string template, int playersOnline)
		{
			if (template.Length == 0)
			{
				return null;
			}

			try
			{
				return String.Format(CultureInfo.InvariantCulture, template, playersOnline);
			}
			catch (FormatException)
			{
				return template;
			}
		}

		private static async Task<string?> ReadFileAsync(string path)
		{
			try
			{
				return await File.ReadAllTextAsync(path);
			}
			catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
			{
				return null;
			}
		}
	}
}

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
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenOnlineService.Rest
{
	// One field per key of SettingKeys.Editable; an omitted field keeps its value, null clears it
	[ApiFixture("put-application-settings.request")]
	public sealed record ApplicationSettingsRequest
	{
		public Optional<string?> Motd { get; init; }
	}

	[ApiFixture("get-application-settings.response")]
	public sealed record ApplicationSettingsResponse
	{
		public string? Motd { get; init; }
	}

	public static class ApplicationSettingsRules
	{
		public const int MaximumMotdLength = 2000;

		// Maps each editable key in the body to its new value, null to clear the row
		public static (IReadOnlyDictionary<string, string?>? Changes, Failure? Failure) Parse(JsonNode? body)
		{
			if (body is not JsonObject fields)
			{
				return (null, Result.Invalid("body"));
			}

			var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
			foreach ((string key, JsonNode? value) in fields)
			{
				if (!SettingKeys.Editable.Contains(key))
				{
					return (null, Result.Invalid(key));
				}

				if (value == null)
				{
					changes[key] = null;
					continue;
				}

				if (value is not JsonValue text || !text.TryGetValue(out string? motd) || motd.Length > MaximumMotdLength)
				{
					return (null, Result.Invalid(key));
				}
				changes[key] = motd;
			}

			return (changes, null);
		}
	}

	// Settings an admin may tune at runtime; every other application_settings row stays internal
	[ApiController]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.SettingsManage)]
	public sealed class ApplicationSettingsController : ControllerBase
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ApplicationSettingsController));

		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public ApplicationSettingsController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[HttpGet("~/api/v1/application/settings")]
		public async Task<IActionResult> Get()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			return RestResults.Ok(await Answer(db));
		}

		[HttpPut("~/api/v1/application/settings")]
		public async Task<IActionResult> Put([FromBody] JsonNode? body)
		{
			(IReadOnlyDictionary<string, string?>? changes, Failure? failure) = ApplicationSettingsRules.Parse(body);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (changes!.Count > 0)
			{
				Database.ModerationActor actor = CallerActors.Of(User);

				// the value and its audit row commit together
				await using (var tx = await db.Database.BeginTransactionAsync())
				{
					foreach ((string key, string? value) in changes)
					{
						if (value == null)
						{
							await Database.Upstream.DeleteSetting(db, key);
						}
						else
						{
							await Database.Upstream.SetSetting(db, key, JsonSerializer.Serialize(value), actor.Name);
						}
						await Database.Moderation.AddAuditEntry(db, actor, ModerationActions.SettingUpdate, AuditTargetKinds.Setting, null, null, new { key, value });
					}
					await tx.CommitAsync();
				}
			}

			return RestResults.Ok(await Answer(db));
		}

		private static async Task<ApplicationSettingsResponse> Answer(AppDbContext db)
		{
			return new ApplicationSettingsResponse { Motd = ReadString(SettingKeys.Motd, await Database.Upstream.GetSetting(db, SettingKeys.Motd)) };
		}

		// Values are stored as JSON; a row that is not a JSON string reads as unset
		private static string? ReadString(string key, string? json)
		{
			if (json == null)
			{
				return null;
			}

			try
			{
				return JsonNode.Parse(json) is JsonValue value && value.TryGetValue(out string? text) ? text : null;
			}
			catch (JsonException ex)
			{
				s_log.LogWarning(ex, "Application setting {Key} is not valid JSON", key);
				return null;
			}
		}
	}
}

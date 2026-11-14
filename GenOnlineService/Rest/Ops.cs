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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace GenOnlineService.Rest
{
	[ApiFixture("get-health.response")]
	public sealed record HealthResponse
	{
		public string Status { get; init; } = "ok";
		public string Version { get; init; } = "";
		public DateTime StartedAt { get; init; }
		public long UptimeS { get; init; }
	}

	// The body of /-/liveness and of a ready /-/readiness
	[ApiFixture("get-liveness.response")]
	public sealed record ProbeResponse
	{
		public string Status { get; init; } = "ok";
	}

	[ApiFixture("get-database.response")]
	public sealed record DatabaseStateResponse
	{
		public bool Ok { get; init; }
	}

	public sealed record ClientCheck
	{
		public string ClientId { get; init; } = "";
		public bool Ok { get; init; }
	}

	[ApiFixture("get-self_check-version.response")]
	public sealed record VersionSelfCheckResponse
	{
		public bool Ok { get; init; }
		public IReadOnlyList<ClientCheck> Clients { get; init; } = [];
	}

	// Operator routes under /-/ (GitLab names); the edge blocks /-/* for the public
	[ApiController]
	public sealed class OpsController : ControllerBase
	{
		private static readonly ILogger s_log = AppLog.For(typeof(OpsController));
		private static readonly TimeSpan s_databaseTimeout = TimeSpan.FromSeconds(2);

		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public OpsController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[AllowAnonymous]
		[HttpGet("~/-/health")]
		[HttpHead("~/-/health")]
		public IActionResult Health()
		{
			DateTime started = Program.g_LastStartTime;
			return RestResults.Ok(new HealthResponse
			{
				Version = Compatibility.ServiceVersion,
				StartedAt = started.ToUniversalTime(),
				UptimeS = (long)Math.Max(0, (DateTime.Now - started).TotalSeconds)
			});
		}

		[AllowAnonymous]
		[HttpGet("~/-/liveness")]
		public IActionResult Liveness() => RestResults.Ok(new ProbeResponse());

		// Ready while the database answers; the failover lease of docs/realtime-failover.md joins this check when it is built
		[AllowAnonymous]
		[HttpGet("~/-/readiness")]
		public async Task<IActionResult> Readiness()
		{
			return await DatabaseAnswers() ? RestResults.Ok(new ProbeResponse()) : RestResults.Fail(RestCodes.ServiceUnavailable, new { What = "database" });
		}

		[Authorize(Policy = PermissionPolicies.Prefix + Permissions.OpsDatabase)]
		[HttpGet("~/-/database")]
		public async Task<IActionResult> DatabaseState() => RestResults.Ok(new DatabaseStateResponse { Ok = await DatabaseAnswers() });

		// The compatibility check of every enabled game client against its own minimum version
		[Authorize(Policy = PermissionPolicies.Prefix + Permissions.ServerStats)]
		[HttpGet("~/-/self_check/version")]
		public async Task<IActionResult> VersionSelfCheck()
		{
			var clients = new List<ClientCheck>();
			foreach (RegisteredClient client in ClientRegistry.All.Where(c => c.Enabled && c.Kind == ClientKinds.Game).OrderBy(c => c.ClientID, StringComparer.Ordinal))
			{
				bool ok;
				try
				{
					// A client without a minimum accepts every version, the lowest CalVer stands for any
					var result = await Compatibility.CheckAsync(client.ClientID, client.MinVersion ?? "2000.1.0");
					ok = result.Ok && result.Data!.Compatible;
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Version self check failed for client {ClientId}", client.ClientID);
					ok = false;
				}
				clients.Add(new ClientCheck { ClientId = client.ClientID, Ok = ok });
			}

			// no game client to check is a failed check, not a pass
			return RestResults.Ok(new VersionSelfCheckResponse { Ok = clients.Count > 0 && clients.All(c => c.Ok), Clients = clients });
		}

		// One cheap query within two seconds
		private async Task<bool> DatabaseAnswers()
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
			timeout.CancelAfter(s_databaseTimeout);
			try
			{
				await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(timeout.Token);
				await db.ApplicationSettings.AsNoTracking().AnyAsync(timeout.Token);
				return true;
			}
			catch (Exception ex)
			{
				s_log.LogWarning(ex, "Database check failed");
				return false;
			}
		}
	}
}

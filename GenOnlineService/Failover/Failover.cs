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
using GenOnlineService.Rest;
using System.Security.Cryptography;

namespace GenOnlineService.Failover
{
	// Realtime:failover, see docs/realtime-failover.md, off by default, a copy without it serves as it always did
	public sealed class FailoverSettings
	{
		public bool Enabled { get; init; }
		public int RenewIntervalMs { get; init; } = 500;
		public int LeaseMs { get; init; } = 2500;
		public int HandoverLimitS { get; init; } = 120;
		public int BatchPercent { get; init; } = 5;
		public int BatchIntervalMs { get; init; } = 200;

		// The pause between the target confirming and the first batch, so the proxy health check (every second) already sends reconnects to the target
		public int SwitchDelayMs { get; init; } = 1000;

		// The delay a switched client waits before it reconnects, spread so the new copy builds snapshots gradually
		public int SwitchRetryMinMs { get; init; } = 250;
		public int SwitchRetryMaxMs { get; init; } = 1000;

		// The longest a handover runs: the drain limit, the bounded handover statement, the wait for the target and a take back, the switch delay and the last batch
		public int HandoverBudgetMs => HandoverLimitS * 1000 + LeaseMs * 3 + SwitchDelayMs + 5000;

		public const string PostgresOnly = "Realtime:failover:enabled is true, but more than one service copy (failover and handover) is supported on PostgreSQL only. Set Database:db_provider to postgres or Realtime:failover:enabled to false.";

		// Refuses values that would let a paused copy outlive its lease, checked only when failover is on
		public static FailoverSettings Read(IConfiguration configuration)
		{
			IConfigurationSection section = configuration.GetSection("Realtime:failover");
			FailoverSettings settings = new()
			{
				Enabled = section.GetValue("enabled", false),
				RenewIntervalMs = section.GetValue("renew_interval_ms", 500),
				LeaseMs = section.GetValue("lease_ms", 2500),
				HandoverLimitS = section.GetValue("handover_limit_s", 120),
				BatchPercent = section.GetValue("batch_percent", 5),
				BatchIntervalMs = section.GetValue("batch_interval_ms", 200),
				SwitchDelayMs = section.GetValue("switch_delay_ms", 1000),
			};

			if (!settings.Enabled)
			{
				return settings;
			}
			if (!Program.UsesPostgres(configuration))
			{
				throw new InvalidOperationException(PostgresOnly);
			}
			if (settings.RenewIntervalMs < 50 || settings.LeaseMs < settings.RenewIntervalMs * 3)
			{
				throw new InvalidOperationException("Realtime:failover:lease_ms must be at least three times renew_interval_ms, and renew_interval_ms at least 50.");
			}
			if (settings.HandoverLimitS < 1 || settings.BatchPercent < 1 || settings.BatchPercent > 100 || settings.BatchIntervalMs < 1 || settings.SwitchDelayMs < 0)
			{
				throw new InvalidOperationException("Realtime:failover:handover_limit_s, batch_percent (1 to 100) and batch_interval_ms must be positive, switch_delay_ms zero or more.");
			}

			return settings;
		}
	}

	// One id per process: the pod or host name plus a random suffix, so a restarted copy never reuses the lease of the old one
	public static class CopyIdentity
	{
		public static string ID { get; } = Create();

		private static string Create()
		{
			string host = Environment.GetEnvironmentVariable("POD_NAME")
				?? Environment.GetEnvironmentVariable("HOSTNAME")
				?? Environment.MachineName;
			host = host.Trim();
			if (host.Length > 100)
			{
				host = host.Substring(0, 100);
			}
			return (host.Length > 0 ? host : "copy") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
		}
	}

	// The failover state the request path reads, every read is a volatile field, never a database call
	public static class RealtimeFailover
	{
		private static FailoverCoordinator? s_current;

		public static bool Enabled => Volatile.Read(ref s_current) != null;

		// True while this copy serves players: always without failover, else on the lease holder and during the handover overlap
		public static bool Serving => Volatile.Read(ref s_current)?.IsReady ?? true;

		public static FailoverCoordinator? Current => Volatile.Read(ref s_current);

		// True while this copy must not act on its connections: never without failover
		public static bool Fenced => Volatile.Read(ref s_current)?.IsFenced ?? false;

		internal static void Install(FailoverCoordinator? coordinator) => Volatile.Write(ref s_current, coordinator);

		// A standby refuses the hub upgrade and game API calls with a retryable 503, the proxy sends that traffic to the ready copy
		public static IApplicationBuilder UseRealtimeStandbyGate(this IApplicationBuilder app, Func<bool>? serving = null)
		{
			Func<bool> isServing = serving ?? (() => Serving);
			return app.Use(async (context, next) =>
			{
				if (!isServing() && (context.Request.Path.StartsWithSegments(RealtimeHub.Path) || context.Request.Path.StartsWithSegments("/api")))
				{
					context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
					context.Response.Headers.RetryAfter = "1";
					await context.Response.WriteAsJsonAsync(ApiResults.BodyOf(Result.Fail(RestCodes.ServiceUnavailable, new { What = StandbyWhat })), ApiResults.Json);
					return;
				}

				await next(context);
			});
		}

		public const string StandbyWhat = "standby";
	}
}

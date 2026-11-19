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
using MaxMind.GeoIP2;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Realtime
{
	// HTTP checks on the hub upgrade that must answer with a status code before SignalR takes over
	public static class RealtimeGate
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeGate));

		private const string QueryTokenName = "access_token";

		private static bool IsRealtime(HttpContext context) => context.Request.Path.StartsWithSegments(RealtimeHub.Path);

		// Browsers cannot set headers on a websocket, so the web chat sends the token in the query; runs before authentication
		public static IApplicationBuilder UseRealtimeQueryToken(this IApplicationBuilder app)
		{
			return app.Use((context, next) =>
			{
				if (IsRealtime(context)
					&& !context.Request.Headers.ContainsKey("Authorization")
					&& context.Request.Query.TryGetValue(QueryTokenName, out var token)
					&& token.Count == 1
					&& !String.IsNullOrEmpty(token[0]))
				{
					context.Request.Headers.Authorization = "Bearer " + token[0];

					// later logging and telemetry never see the token
					QueryBuilder rest = new QueryBuilder(context.Request.Query.Where(q => !String.Equals(q.Key, QueryTokenName, StringComparison.OrdinalIgnoreCase)));
					context.Request.QueryString = rest.ToQueryString();
				}

				return next(context);
			});
		}

		// A game client connects only after registering this sign-in with POST /api/v1/auth/session; runs after authentication.
		// Answers with a status only on a /realtime request that is not an upgrade
		public static IApplicationBuilder UseRealtimeRegistrationGate(this IApplicationBuilder app)
		{
			return app.Use(async (context, next) =>
			{
				// the hub refuses an unregistered upgrade after the handshake with session.not_registered (RealtimeAdmission)
				if (IsRealtime(context) && !RealtimeAdmission.IsRealtimeUpgrade(context) && context.User.Identity?.IsAuthenticated == true)
				{
					Int64 userID = RealtimeClaims.UserID(context.User);
					EUserSessionType sessionType = RealtimeClaims.SessionType(context.User);
					if (sessionType == EUserSessionType.GameClient)
					{
						// the stored registration, so a client registered on another copy (a handover) or before a restart connects here too
						GameSessionRegistration? registration;
						try
						{
							await using AppDbContext db = await context.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(context.RequestAborted);
							registration = await GameSessions.LoadAsync(db, userID, sessionType, context.RequestAborted);
						}
						catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
						{
							s_log.LogError(ex, "Reading the game session of user {UserId} failed, using this copy's cached one", userID);
							registration = GameSessions.Get(userID, sessionType);
						}
						if (registration == null || registration.AuthorizationID != RealtimeClaims.AuthorizationID(context.User))
						{
							context.Response.StatusCode = StatusCodes.Status409Conflict;
							await context.Response.WriteAsJsonAsync(ApiResults.BodyOf(Result.Fail(ErrorCodes.SessionNotRegistered)), ApiResults.Json);
							return;
						}
					}
				}

				await next(context);
			});
		}
	}

	// Coarse location of a connecting client, used for lobby regions and latency estimates
	public readonly record struct GeoLocation(string Continent, string Country, double Latitude, double Longitude)
	{
		private static readonly ILogger s_log = AppLog.For(typeof(GeoLocation));

		// Reused because opening the database is expensive; it is absent in fresh clones, which falls back to defaults
		private static readonly DatabaseReader? s_reader = TryOpen();

		private static readonly GeoLocation s_default = new GeoLocation("NA", "US", 38.8977, -77.0365);

		private static DatabaseReader? TryOpen()
		{
			try
			{
				return new DatabaseReader("data/GeoLite2-City.mmdb");
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GeoIP DB unavailable; using fallback coordinates");
				return null;
			}
		}

		// The country GeoIP knows for the address, else the country the network is registered in; null when neither is known,
		// never the fallback. A sign-in records this, so staff never see the lobby fallback as the player's country.
		public static string? KnownCountry(string ipAddress)
		{
			if (s_reader == null)
			{
				return null;
			}

			try
			{
				var city = s_reader.City(ipAddress);
				return city.Country.IsoCode ?? city.RegisteredCountry.IsoCode;
			}
			catch
			{
				return null;
			}
		}

		public static GeoLocation Lookup(string ipAddress)
		{
			if (s_reader == null)
			{
				return s_default;
			}

			try
			{
				var city = s_reader.City(ipAddress);
				return new GeoLocation(
					city.Continent.Code ?? s_default.Continent,
					city.Country.IsoCode ?? s_default.Country,
					city.Location.Latitude ?? s_default.Latitude,
					city.Location.Longitude ?? s_default.Longitude);
			}
			catch
			{
				return s_default;
			}
		}
	}
}

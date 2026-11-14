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
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;

namespace GenOnlineService.Identity
{
	// A sign-in ends when its authorization or its refresh token is revoked, by the user, by reuse detection or by an operator
	public static class SignInRevocation
	{
		private static readonly ILogger s_log = AppLog.For(typeof(SignInRevocation));

		// Ended sign-ins are remembered for longer than an access token lives, so a connect racing the revocation is refused
		private static readonly TimeSpan s_rememberEnded = TimeSpan.FromHours(1);
		private static readonly ConcurrentDictionary<string, DateTime> s_ended = new(StringComparer.Ordinal);

		public static bool HasEnded(string authorizationID) => s_ended.TryGetValue(authorizationID, out DateTime endedAt) && DateTime.UtcNow - endedAt < s_rememberEnded;

		// Drops the registration and closes only the realtime connections of that sign-in
		public static async Task EndAsync(string? authorizationID)
		{
			if (String.IsNullOrEmpty(authorizationID))
			{
				return;
			}

			DateTime now = DateTime.UtcNow;
			s_ended[authorizationID] = now;
			foreach (var entry in s_ended.Where(e => now - e.Value >= s_rememberEnded))
			{
				s_ended.TryRemove(entry);
			}

			try
			{
				GameSessions.RemoveSignIn(authorizationID);
				await Realtime.RealtimeControl.RequestClose(Realtime.CloseRequest.ForSignIn(authorizationID, CloseCodes.AuthRevoked));
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Closing the realtime connections of sign-in {AuthorizationId} failed", authorizationID);
			}
		}
	}

	public sealed class SignInTokenManager : OpenIddictTokenManager<OpenIddictEntityFrameworkCoreToken>
	{
		public SignInTokenManager(
			IOpenIddictTokenCache<OpenIddictEntityFrameworkCoreToken> cache,
			ILogger<OpenIddictTokenManager<OpenIddictEntityFrameworkCoreToken>> logger,
			IOptionsMonitor<OpenIddictCoreOptions> options,
			IOpenIddictTokenStore<OpenIddictEntityFrameworkCoreToken> store)
			: base(cache, logger, options, store)
		{
		}

		// Revoking a refresh token ends its sign-in, an access token alone does not
		public override async ValueTask<bool> TryRevokeAsync(OpenIddictEntityFrameworkCoreToken token, CancellationToken cancellationToken = default)
		{
			bool revoked = await base.TryRevokeAsync(token, cancellationToken);
			if (revoked && await GetTypeAsync(token, cancellationToken) == OpenIddictConstants.TokenTypeIdentifiers.RefreshToken)
			{
				await SignInRevocation.EndAsync(await GetAuthorizationIdAsync(token, cancellationToken));
			}
			return revoked;
		}

		public override async ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken = default)
		{
			long revoked = await base.RevokeByAuthorizationIdAsync(identifier, cancellationToken);
			await SignInRevocation.EndAsync(identifier);
			return revoked;
		}
	}

	public sealed class SignInAuthorizationManager : OpenIddictAuthorizationManager<OpenIddictEntityFrameworkCoreAuthorization>
	{
		public SignInAuthorizationManager(
			IOpenIddictAuthorizationCache<OpenIddictEntityFrameworkCoreAuthorization> cache,
			ILogger<OpenIddictAuthorizationManager<OpenIddictEntityFrameworkCoreAuthorization>> logger,
			IOptionsMonitor<OpenIddictCoreOptions> options,
			IOpenIddictAuthorizationStore<OpenIddictEntityFrameworkCoreAuthorization> store)
			: base(cache, logger, options, store)
		{
		}

		public override async ValueTask<bool> TryRevokeAsync(OpenIddictEntityFrameworkCoreAuthorization authorization, CancellationToken cancellationToken = default)
		{
			bool revoked = await base.TryRevokeAsync(authorization, cancellationToken);
			if (revoked)
			{
				await SignInRevocation.EndAsync(await GetIdAsync(authorization, cancellationToken));
			}
			return revoked;
		}
	}
}

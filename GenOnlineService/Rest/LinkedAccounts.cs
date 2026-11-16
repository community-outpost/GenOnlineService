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

namespace GenOnlineService.Rest
{
	public sealed record SignInToken(string Token, int ExpiresIn);

	// A platform the game client links an account at; the routes of section 10.4 name it by Name
	public interface ILinkedAccountProvider
	{
		string Name { get; }

		// A short-lived token the platform plugin signs the player in with
		(SignInToken? Token, Failure? Failure) IssueSignInToken(Int64 userID, string displayName);

		// Validates the provider's proof of the caller's account and links it as their middleware ID
		Task<Failure?> LinkAsync(Int64 userID, EUserSessionType sessionType, string proof);
	}

	// The providers the service supports, matched by name as an opaque, case-sensitive string
	public sealed class LinkedAccountProviders
	{
		private readonly Dictionary<string, ILinkedAccountProvider> m_byName;

		public LinkedAccountProviders(IEnumerable<ILinkedAccountProvider> providers)
		{
			m_byName = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
		}

		public ILinkedAccountProvider? Find(string name) => m_byName.GetValueOrDefault(name);
	}

	// The codes of the linked-account routes, each naming its provider
	public static class LinkedAccounts
	{
		// A missing body or an empty proof is invalid_argument {field: proof}
		public static Failure? ValidateRequest(LinkAccountRequest? body)
		{
			return String.IsNullOrEmpty(body?.Proof) ? Result.Invalid("proof") : null;
		}

		public static Failure InvalidProof(string provider) => Result.Fail(RestCodes.LinkedAccountInvalidProof, new { Provider = provider });

		public static Failure Mismatch(string provider) => Result.Fail(RestCodes.LinkedAccountMismatch, new { Provider = provider });

		public static Failure InUse(string provider) => Result.Fail(RestCodes.LinkedAccountInUse, new { Provider = provider });

		public static Failure Locked(string provider) => Result.Fail(RestCodes.LinkedAccountLocked, new { Provider = provider });

		public static Failure Unavailable(string provider) => Result.Fail(RestCodes.ServiceUnavailable, new { What = "linked_account", Provider = provider });
	}

	// EOS: the sign-in token is the service's own EOS ID token, the proof is the Epic Connect ID token
	public sealed class EosLinkedAccountProvider : ILinkedAccountProvider
	{
		private readonly LobbyManager m_lobbyManager;
		private readonly Func<EosTokenIssuer.Settings?> m_tokenSettings;

		public EosLinkedAccountProvider(LobbyManager lobbyManager) : this(lobbyManager, EosTokenIssuer.GetSettings)
		{
		}

		public EosLinkedAccountProvider(LobbyManager lobbyManager, Func<EosTokenIssuer.Settings?> tokenSettings)
		{
			m_lobbyManager = lobbyManager;
			m_tokenSettings = tokenSettings;
		}

		public string Name => EosLink.Provider;

		public (SignInToken? Token, Failure? Failure) IssueSignInToken(Int64 userID, string displayName)
		{
			EosTokenIssuer.Settings? settings = m_tokenSettings();
			if (settings == null)
			{
				return (null, LinkedAccounts.Unavailable(Name));
			}

			string token = EosTokenIssuer.Issue(settings, userID, displayName, out _);
			return (new SignInToken(token, (int)settings.Lifetime.TotalSeconds), null);
		}

		public Task<Failure?> LinkAsync(Int64 userID, EUserSessionType sessionType, string proof)
		{
			return EosLink.LinkAsync(m_lobbyManager, userID, sessionType, proof);
		}
	}
}

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
using System.Text.Json.Serialization;

namespace GenOnlineService.Rest
{
	// The caller: staff roles and the permissions they grant, both sorted
	[ApiFixture("get-me.response")]
	public sealed record MeResponse
	{
		public long UserId { get; init; }
		public string DisplayName { get; init; } = "";
		// "0042": Name#tag is unique, the name alone is not (section 3.1)
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Tag { get; init; }
		public IReadOnlyList<string> Roles { get; init; } = [];
		public IReadOnlyList<string> Permissions { get; init; } = [];
	}

	public enum SessionKind { Game, Launcher, Chat }

	// A session registered through POST /api/v1/auth/session
	public sealed record MeSession
	{
		public SessionKind Type { get; init; }
		public string ClientId { get; init; } = "";
		public DateTime RegisteredAt { get; init; }
	}

	[ApiFixture("get-me-sessions.response")]
	public sealed record MeSessionsResponse
	{
		public IReadOnlyList<MeSession> Sessions { get; init; } = [];
	}

	// A short-lived token the platform plugin signs the player in with; expires_in in seconds
	[ApiFixture("post-me-linked_accounts-sign_in_token.response")]
	public sealed record SignInTokenResponse
	{
		public string Token { get; init; } = "";
		public int ExpiresIn { get; init; }
	}

	// The provider's own proof of the account to link; for eos the Epic Connect ID token
	[ApiFixture("put-me-linked_accounts.request")]
	public sealed record LinkAccountRequest
	{
		public string? Proof { get; init; }
	}
}

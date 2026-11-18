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
	// A token as its owner sees it; never the secret or its hash. revoked_at in the future is the end of a rotation overlap;
	// allowed_ips is empty when any address may use the token
	[ApiFixture("get-personal_access_tokens-self.response")]
	public sealed record PersonalAccessTokenInfo
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public IReadOnlyList<string> Scopes { get; init; } = [];
		public DateTime CreatedAt { get; init; }
		public DateTime ExpiresAt { get; init; }
		public DateTime? LastUsedAt { get; init; }
		public string? LastUsedIp { get; init; }
		public DateTime? RevokedAt { get; init; }
		public string TokenLast4 { get; init; } = "";
		public IReadOnlyList<string> AllowedIps { get; init; } = [];
	}

	[ApiFixture("get-personal_access_tokens.response")]
	public sealed record PersonalAccessTokensResponse
	{
		public IReadOnlyList<PersonalAccessTokenInfo> PersonalAccessTokens { get; init; } = [];
	}

	// The create and rotate answers, the only place the full gopat- secret is ever shown
	[ApiFixture("post-personal_access_tokens.response")]
	[ApiFixture("post-personal_access_tokens-rotate.response")]
	public sealed record CreatedPersonalAccessToken
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public IReadOnlyList<string> Scopes { get; init; } = [];
		public DateTime CreatedAt { get; init; }
		public DateTime ExpiresAt { get; init; }
		public DateTime? LastUsedAt { get; init; }
		public string? LastUsedIp { get; init; }
		public DateTime? RevokedAt { get; init; }
		public string TokenLast4 { get; init; } = "";
		public IReadOnlyList<string> AllowedIps { get; init; } = [];
		public string Token { get; init; } = "";
	}

	// expires_at is an ISO 8601 date (midnight UTC) or a UTC timestamp; allowed_ips holds IP addresses or CIDR ranges
	[ApiFixture("post-personal_access_tokens.request")]
	public sealed record CreatePersonalAccessTokenRequest
	{
		public string? Name { get; init; }
		public IReadOnlyList<string>? Scopes { get; init; }
		public string? ExpiresAt { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public IReadOnlyList<string>? AllowedIps { get; init; }
	}
}

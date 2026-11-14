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
	// A service account with the permissions granted to it; disabled_at is null while it is active
	[ApiFixture("post-service_accounts.response")]
	public sealed record ServiceAccountInfo
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Description { get; init; }
		public DateTime CreatedAt { get; init; }
		public DateTime? DisabledAt { get; init; }
		public IReadOnlyList<string> Permissions { get; init; } = [];
	}

	[ApiFixture("get-service_accounts.response")]
	public sealed record ServiceAccountsResponse
	{
		public IReadOnlyList<ServiceAccountInfo> ServiceAccounts { get; init; } = [];
	}

	[ApiFixture("post-service_accounts.request")]
	public sealed record CreateServiceAccountRequest
	{
		public string? Name { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Description { get; init; }
	}

	public sealed record ServiceAccountGrantChanges
	{
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public IReadOnlyList<string>? Add { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public IReadOnlyList<string>? Remove { get; init; }
	}

	// Each part is optional; privileged permissions are never granted here, only through the CLI
	[ApiFixture("patch-service_accounts.request")]
	public sealed record UpdateServiceAccountRequest
	{
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public ServiceAccountGrantChanges? Grants { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public bool? Disabled { get; init; }
	}
}

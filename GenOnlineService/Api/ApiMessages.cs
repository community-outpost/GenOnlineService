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

using System.Text.Json.Serialization;

namespace GenOnlineService.Api
{
	using GenOnlineService.Realtime;

	// Names the docs/api-fixtures bodies a type reads or writes, the contract test round-trips each of them
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
	public sealed class ApiFixtureAttribute : Attribute
	{
		public ApiFixtureAttribute(string name)
		{
			Name = name;
		}

		public string Name { get; }
	}

	public enum SanctionType { Kick, Mute, Ban }

	// Every REST failure: a code and params from section 7 of the protocol
	[ApiFixture("error.response")]
	public sealed record ApiError
	{
		public string Error { get; init; } = "";
		public object Params { get; init; } = Empty.Instance;
	}

	// 10.1 Social and groups

	[ApiFixture("post-me-friend_requests.request")]
	public sealed record FriendRequestRequest
	{
		public long UserId { get; init; }
	}

	[ApiFixture("get-me-friends.response")]
	public sealed record FriendsResponse
	{
		public IReadOnlyList<FriendMember> Friends { get; init; } = [];
		public IReadOnlyList<Member> Incoming { get; init; } = [];
		public IReadOnlyList<Member> Outgoing { get; init; } = [];
	}

	[ApiFixture("get-me-blocks.response")]
	public sealed record BlocksResponse
	{
		public IReadOnlyList<Member> Blocked { get; init; } = [];
	}

	[ApiFixture("post-groups.request")]
	public sealed record CreateGroupRequest
	{
		public IReadOnlyList<long> UserIds { get; init; } = [];
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Name { get; init; }
	}

	[ApiFixture("post-groups.response")]
	public sealed record CreateGroupResponse
	{
		public long GroupId { get; init; }
	}

	[ApiFixture("post-groups-members.request")]
	public sealed record AddGroupMembersRequest
	{
		public IReadOnlyList<long> UserIds { get; init; } = [];
	}

	// The name is required, null clears it
	[ApiFixture("patch-groups.request")]
	public sealed record RenameGroupRequest
	{
		public Optional<string?> Name { get; init; }
	}

	// 10.2 Moderation and reports

	[ApiFixture("post-players-sanctions.request")]
	public sealed record CreateSanctionRequest
	{
		public SanctionType? Type { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public long? DurationS { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
	}

	[ApiFixture("post-players-sanctions.response")]
	public sealed record CreateSanctionResponse
	{
		public long SanctionId { get; init; }
	}

	// A sanction as staff see it, created_by is null when a tool or a sign-in provider created it
	public sealed record SanctionInfo
	{
		public long Id { get; init; }
		public SanctionType Type { get; init; }
		public string? Reason { get; init; }
		public DateTime CreatedAt { get; init; }
		public DateTime? ExpiresAt { get; init; }
		public DateTime? LiftedAt { get; init; }
		public Member? CreatedBy { get; init; }
	}

	[ApiFixture("get-players-sanctions.response")]
	public sealed record SanctionsResponse
	{
		public IReadOnlyList<SanctionInfo> Sanctions { get; init; } = [];
	}

	// The optional body of DELETE /api/v1/sanctions/:id and DELETE /api/v1/messages/:id
	[ApiFixture("delete-sanctions.request")]
	[ApiFixture("delete-messages.request")]
	public sealed record ReasonRequest
	{
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
	}

	// Exactly one of message_id and user_id
	[ApiFixture("post-reports.request")]
	public sealed record CreateReportRequest
	{
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? MessageId { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public long? UserId { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
	}

	[ApiFixture("post-reports.response")]
	public sealed record CreateReportResponse
	{
		public long ReportId { get; init; }
	}

	// One report in the staff queue, message_id is null for a report of a player; status is open, actioned or dismissed
	public sealed record ReportInfo
	{
		public long Id { get; init; }
		public Member Reporter { get; init; } = new Member();
		public Member Reported { get; init; } = new Member();
		public string? MessageId { get; init; }
		public string? Reason { get; init; }
		public string Status { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public DateTime? ResolvedAt { get; init; }
		public string? ResolutionNote { get; init; }
		// The text of the reported message as the report stored it, in GET /api/v1/reports/search only
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? MessageContent { get; init; }
	}

	[ApiFixture("get-reports.response")]
	public sealed record ReportsResponse
	{
		public IReadOnlyList<ReportInfo> Reports { get; init; } = [];
	}

	// Status is actioned or dismissed
	[ApiFixture("patch-reports.request")]
	public sealed record ResolveReportRequest
	{
		public string? Status { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? ResolutionNote { get; init; }
	}

	// POST /api/v1/players/:id/password_reset and /two_factor_reset; the reason is required
	[ApiFixture("post-players-account_support.request")]
	public sealed record AccountSupportRequest
	{
		public string? Reason { get; init; }
	}

	// The new password, in this answer only
	[ApiFixture("post-players-password_reset.response")]
	public sealed record PasswordResetResponse
	{
		public string Password { get; init; } = String.Empty;
	}

	// POST /api/v1/players/:id/reset_name
	public sealed record ResetNameRequest
	{
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
	}

	[ApiFixture("patch-players.request")]
	public sealed record RenamePlayerRequest
	{
		public string? Name { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }
	}

	// 10.3 Profile, matches and playlists

	[ApiFixture("patch-me.request")]
	public sealed record UpdateMeRequest
	{
		public string? Name { get; init; }
	}

	[ApiFixture("put-matches-results-me.request")]
	public sealed record MatchResultRequest
	{
		public bool Won { get; init; }
		public bool Desynced { get; init; }
		public int Side { get; init; }
		public int DurationS { get; init; }
		public MatchStats? Stats { get; init; }
	}

	[ApiFixture("put-matches-results-me.response")]
	public sealed record MatchResultResponse
	{
		public string ScreenshotUrl { get; init; } = "";
		public string ReplayUrl { get; init; } = "";
	}

	[ApiFixture("get-matchmaking-playlists.response")]
	public sealed record PlaylistsResponse
	{
		public IReadOnlyList<Playlist> Playlists { get; init; } = [];
	}

	// One state change of a connection attempt, t_ms counts from the start of the attempt
	public sealed record ConnectionReportEvent
	{
		public string? State { get; init; }
		public long? TMs { get; init; }
	}

	// The final result of one connection attempt to one peer
	[ApiFixture("post-lobbies-connection_reports.request")]
	public sealed record ConnectionReportRequest
	{
		public long? TargetUserId { get; init; }
		public string? Outcome { get; init; }
		public string? Transport { get; init; }
		public int? IpVersion { get; init; }
		public int? Attempt { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public IReadOnlyList<ConnectionReportEvent>? Events { get; init; }
	}
}

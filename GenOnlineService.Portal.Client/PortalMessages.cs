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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenOnlineService.Portal.Client
{
	// The pages read /api/v1, which answers in snake_case JSON
	public static class PortalJson
	{
		public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};
	}

	// The /api/v1 paths the browser calls with the sign-in cookie (the routes marked [AllowSignInCookie]), and the pages the host gates
	public static class PortalRoutes
	{
		public const string Reports = "api/v1/reports";
		public const string ReportSearch = "api/v1/reports/search";
		public const string Players = "api/v1/players";
		public const string PlayerSearch = "api/v1/players/search";
		public const string Sanctions = "api/v1/sanctions";
		public const string Matches = "api/v1/matches";
		public const string AntiCheat = "api/v1/anticheat_reports";
		public const string SignIn = "users/sign_in";
		public const string SignOut = "users/sign_out";

		public const string AccountPage = "/profile";
		// the logo of the sign-in pages (PortalLook.LogoPath on the host)
		public const string LogoPath = "/portal/logo.png";
		public const string AdminPages = "/admin";
		public const string ReportsPage = "/admin/reports";
		public const string PlayersPage = "/admin/players";
		public const string MatchesPage = "/admin/matches";
		public const string AntiCheatPage = "/admin/anticheat";

		public static string PlayerPage(long id) => PlayersPage + "/" + id;

		public static string MatchPage(long id) => MatchesPage + "/" + id;

		public static string AntiCheatList(string kind) => AntiCheatPage + "/" + kind;
	}

	// The permission names the pages show or hide parts by, the host checks each one again
	public static class PortalPermissions
	{
		public const string Kick = "moderation.kick";
		public const string Mute = "moderation.mute";
		public const string Ban = "moderation.ban";
		public const string Rename = "moderation.rename";
		public const string AnticheatReview = "anticheat.review";
		public const string PlayersInspect = "players.inspect";
	}

	// The review lists, in sidebar order
	public sealed record PortalReviewList(string Kind, string Title, string Description)
	{
		public static readonly IReadOnlyList<PortalReviewList> All =
		[
			new PortalReviewList("new_accounts", "New accounts in quickmatch", "New accounts that played a quickmatch game."),
			new PortalReviewList("modules", "Unrecognized modules", "Modules a game client loaded that are not on the allow list."),
			new PortalReviewList("probes", "Suspicious probes", "Missing or malformed answers to an anti-cheat probe inside a match."),
			new PortalReviewList("devices", "Suspicious device ids", "Sign-ins with a device value that lacks the marker of a genuine client."),
			new PortalReviewList("quickmatch_rules", "Illegal quick match actions", "Quick match requests a genuine client never sends, such as choosing an army in a random army playlist.")
		];

		public static PortalReviewList? Find(string? kind) => All.FirstOrDefault(list => list.Kind == kind);
	}

	// Who is signed in, what they may do, and the anti-forgery token every change must carry, all from the host page
	public sealed record PortalSessionInfo
	{
		public long UserId { get; init; }
		public string DisplayName { get; init; } = "";
		public string? Tag { get; init; }
		public IReadOnlyList<string> Roles { get; init; } = [];
		public IReadOnlyList<string> Permissions { get; init; } = [];
		public bool Staff { get; init; }
		// from the page's own csrf meta tags, not from the session JSON
		[JsonIgnore]
		public string CsrfHeader { get; init; } = "";
		[JsonIgnore]
		public string CsrfToken { get; init; } = "";

		[JsonIgnore]
		public string FullName => Tag == null ? DisplayName : DisplayName + "#" + Tag;

		public bool Can(string permission) => Permissions.Contains(permission);
	}

	// The reporter or the reported player of a report, as /api/v1/reports names them
	public sealed record PortalMember
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }

		[JsonIgnore]
		public string FullName => Tag == null ? Name : Name + "#" + Tag;
	}

	// One row of the staff queue, the fields of /api/v1/reports
	public sealed record PortalReport
	{
		public long Id { get; init; }
		public PortalMember Reporter { get; init; } = new PortalMember();
		public PortalMember Reported { get; init; } = new PortalMember();
		public string? MessageId { get; init; }
		public string? Reason { get; init; }
		public string Status { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public DateTime? ResolvedAt { get; init; }
		public string? ResolutionNote { get; init; }

		// Flat values for the grid columns
		[JsonIgnore]
		public string ReporterName => Reporter.FullName;
		[JsonIgnore]
		public string ReportedName => Reported.FullName;
	}

	// One page of the queue and the size of the whole filtered queue
	public sealed record PortalReportPage
	{
		public IReadOnlyList<PortalReport> Reports { get; init; } = [];
		public int Total { get; init; }
	}

	public sealed record PortalResolveRequest
	{
		public string? Status { get; init; }
		public string? ResolutionNote { get; init; }
	}

	// One account of the staff search, the fields of /api/v1/players/search
	public sealed record PortalPlayerRow
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }
		public string State { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public DateTime? LastSignInAt { get; init; }

		[JsonIgnore]
		public string FullName => Tag == null ? Name : Name + "#" + Tag;
	}

	public sealed record PortalPlayerPage
	{
		public IReadOnlyList<PortalPlayerRow> Players { get; init; } = [];
		public int Total { get; init; }
	}

	public sealed record PortalPlayerSession
	{
		public string Kind { get; init; } = "";
		public string ClientId { get; init; } = "";
		public DateTime RegisteredAt { get; init; }
	}

	// A kick, mute or ban, Active is false for a kick and for a lifted or expired sanction
	public sealed record PortalSanction
	{
		public long Id { get; init; }
		public string Type { get; init; } = "";
		public string? Reason { get; init; }
		public DateTime CreatedAt { get; init; }
		public DateTime? ExpiresAt { get; init; }
		public DateTime? LiftedAt { get; init; }
		public PortalMember? CreatedBy { get; init; }
		public bool Active { get; init; }
	}

	// The player page every staff member sees: the account, its sessions, ratings and sanctions
	public sealed record PortalPlayer
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }
		public string State { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public DateTime? LastSignInAt { get; init; }
		public IReadOnlyList<string> Roles { get; init; } = [];
		public bool Online { get; init; }
		public long? LobbyId { get; init; }
		public IReadOnlyList<PortalPlayerSession> Sessions { get; init; } = [];
		public int EloRating { get; init; }
		public int MonthlyEloRating { get; init; }
		public int EloMatches { get; init; }
		public IReadOnlyList<PortalSanction> Sanctions { get; init; } = [];

		// False where the sign-in provider owns the names
		public bool CanRename { get; init; }

		[JsonIgnore]
		public string FullName => Tag == null ? Name : Name + "#" + Tag;
	}

	public sealed record PortalIdentity
	{
		public string Provider { get; init; } = "";
		public string ExternalId { get; init; } = "";
		public DateTime LinkedAt { get; init; }
		public DateTime? LastUsedAt { get; init; }

		// The public profile at the provider, null where it has none
		public string? ProfileUrl { get; init; }
	}

	public sealed record PortalDevice
	{
		public string Kind { get; init; } = "";
		public string Fingerprint { get; init; } = "";
		public DateTime FirstSeenAt { get; init; }
		public DateTime LastSeenAt { get; init; }
		public string? LastIp { get; init; }
	}

	public sealed record PortalAddress
	{
		public string Ip { get; init; } = "";
		public string? Country { get; init; }
		public DateTime FirstSeenAt { get; init; }
		public DateTime LastSeenAt { get; init; }
		public int Events { get; init; }
	}

	// Another account on the same device ("device") or only on the same address ("ip")
	public sealed record PortalDuplicate
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }
		public string State { get; init; } = "";
		public string Match { get; init; } = "";
		public int Shared { get; init; }
		public DateTime LastSeenAt { get; init; }

		[JsonIgnore]
		public string FullName => Tag == null ? Name : Name + "#" + Tag;
	}

	// The personal data of a player, read with players.inspect and audited
	public sealed record PortalInspection
	{
		public string? LastIp { get; init; }
		public string? LastCountry { get; init; }
		public DateTime? LastSeenAt { get; init; }
		public IReadOnlyList<PortalIdentity> Identities { get; init; } = [];
		public IReadOnlyList<PortalDevice> Devices { get; init; } = [];
		public IReadOnlyList<PortalAddress> Addresses { get; init; } = [];
		public IReadOnlyList<PortalDuplicate> Duplicates { get; init; } = [];
	}

	public sealed record PortalSanctionRequest
	{
		public string? Type { get; init; }
		public long? DurationS { get; init; }
		public string? Reason { get; init; }
	}

	public sealed record PortalReasonRequest
	{
		public string? Reason { get; init; }
	}

	public sealed record PortalRenameRequest
	{
		public string? Name { get; init; }
		public string? Reason { get; init; }
	}

	public sealed record PortalMatchFile
	{
		public string Name { get; init; } = "";
		public string Type { get; init; } = "";
	}

	// One slot of a match, the fields of /api/v1/matches/:id the page shows
	public sealed record PortalMatchSlot
	{
		public string State { get; init; } = "";
		public long? UserId { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }
		public int Side { get; init; }
		public int? Team { get; init; }
		public bool Won { get; init; }
		public bool Desynced { get; init; }
		public long? DurationS { get; init; }
		public IReadOnlyList<PortalMatchFile> Files { get; init; } = [];

		[JsonIgnore]
		public string FullName => Tag == null ? Name : Name + "#" + Tag;
	}

	public sealed record PortalMatchMap
	{
		public string Name { get; init; } = "";
		public bool Official { get; init; }
	}

	public sealed record PortalMatchBuild
	{
		public long ExeCrc { get; init; }
		public long IniCrc { get; init; }
	}

	public sealed record PortalMatch
	{
		public long Id { get; init; }
		public long? OwnerId { get; init; }
		public string Name { get; init; } = "";
		public string Type { get; init; } = "";
		public string RosterType { get; init; } = "";
		public PortalMatchMap Map { get; init; } = new PortalMatchMap();
		public PortalMatchBuild Build { get; init; } = new PortalMatchBuild();
		public DateTime StartedAt { get; init; }
		public DateTime? FinishedAt { get; init; }
		public IReadOnlyList<PortalMatchSlot> Slots { get; init; } = [];
	}

	// One row of a review list, the fields of /api/v1/anticheat_reports/:kind
	public sealed record PortalAntiCheatReport
	{
		public long Id { get; init; }
		public PortalMember Player { get; init; } = new PortalMember();
		public long? MatchId { get; init; }
		public string? ModuleName { get; init; }
		public string? ModulePath { get; init; }
		public long? ModuleSize { get; init; }
		public string? Reason { get; init; }
		public long? LobbyId { get; init; }
		public int? PlaylistId { get; init; }
		public string? Method { get; init; }
		public IReadOnlyList<int>? Sides { get; init; }
		public DateTime? ReportedAt { get; init; }
		public DateTime? ReviewedAt { get; init; }

		public string SideList => Sides == null ? "" : String.Join(", ", Sides);
	}

	public sealed record PortalAntiCheatPage
	{
		public IReadOnlyList<PortalAntiCheatReport> Reports { get; init; } = [];
		public int Total { get; init; }
	}

	public sealed record PortalReviewRequest
	{
		public bool Reviewed { get; init; }
	}

	// The {error, params} body every refused call carries
	public sealed record PortalError
	{
		public string Error { get; init; } = "";
	}
}

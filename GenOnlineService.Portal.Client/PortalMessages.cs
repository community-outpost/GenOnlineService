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
		public const string Overview = "api/v1/staff/overview";
		public const string Reports = "api/v1/reports";
		public const string ReportSearch = "api/v1/reports/search";
		public const string Players = "api/v1/players";
		public const string PlayerSearch = "api/v1/players/search";
		public const string Sanctions = "api/v1/sanctions";
		public const string Matches = "api/v1/matches";
		public const string AntiCheat = "api/v1/anticheat_reports";
		public const string AuditEvents = "api/v1/audit_events";
		public const string ServiceAccounts = "api/v1/service_accounts";
		public const string MatchSearch = "api/v1/matches/search";
		public const string Settings = "api/v1/application/settings";
		public const string Announcements = "api/v1/announcements";
		public const string Messages = "api/v1/messages";
		public const string StaffRealtime = "api/v1/staff/realtime";
		public const string StaffLobbies = "api/v1/staff/lobbies";
		public const string Lobbies = "api/v1/lobbies";
		public const string TextRules = "api/v1/staff/text_rules";
		public const string SignIn = "users/sign_in";
		public const string SignOut = "users/sign_out";

		public const string AccountPage = "/profile";
		// the logo of the sign-in pages (PortalLook.LogoPath on the host)
		public const string LogoPath = "/portal/logo.png";
		public const string AdminPages = "/admin";
		// the dashboard is the start page of the staff area
		public const string OverviewPage = AdminPages;
		public const string ReportsPage = "/admin/reports";
		public const string PlayersPage = "/admin/players";
		public const string MatchesPage = "/admin/matches";
		public const string AntiCheatPage = "/admin/anticheat";
		public const string AuditPage = "/admin/audit";
		public const string PartnersPage = "/admin/partners";
		// The interactive API docs the service serves outside the staff pages, for partners to read (OpenApi:enabled)
		public const string ApiDocs = "/api/docs";
		public const string SettingsPage = "/admin/settings";
		public const string AnnouncementsPage = "/admin/announcements";
		public const string LivePage = "/admin/live";
		public const string LobbiesPage = "/admin/lobbies";
		public const string TextRulesPage = "/admin/text-rules";

		public static string PlayerPage(long id) => PlayersPage + "/" + id;

		// the player search with a name or id already typed in
		public static string PlayersSearch(string term) => PlayersPage + "?q=" + Uri.EscapeDataString(term);

		public static string MatchPage(long id) => MatchesPage + "/" + id;

		public static string PartnerPage(long id) => PartnersPage + "/" + id;

		// a lobby's connection reports, and its members while it is still open
		public static string LobbyPage(long id) => LobbiesPage + "/" + id;

		// the match list with one player's matches
		public static string MatchesOf(long player) => MatchesPage + "?player=" + player.ToString(System.Globalization.CultureInfo.InvariantCulture);

		public static string AntiCheatList(string kind) => AntiCheatPage + "/" + kind;

		// the audit log with the actions on one player
		public static string AuditOf(long target) => AuditPage + "?target=" + target;
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
		public const string RolesManage = "roles.manage";
		public const string AuditRead = "audit.read";
		public const string TokensManage = "tokens.manage";
		public const string ChatDeleteAny = "chat.delete_any";
		public const string AnnouncementsSend = "announcements.send";
		public const string SettingsManage = "settings.manage";
		public const string ServerStats = "server.stats";
		public const string AccountsSupport = "accounts.support";
		public const string TextRulesManage = "textrules.manage";
	}

	// The permissions of partner service accounts: the machine permissions the staff pages grant, and the privileged ones
	// only the CLI on the host grants (Permissions.Machine and Permissions.Privileged on the host)
	public static class PortalPartnerPermissions
	{
		public static readonly IReadOnlyList<string> Grantable = ["lobbies.read", "match_history.read"];

		public static readonly IReadOnlyList<string> Privileged = ["ops.database", "users.revoke"];

		public static bool IsPrivileged(string permission) => Privileged.Contains(permission);
	}

	// The audit actions the log's filter offers, in the words of the host's audit_log.action
	public static class PortalAuditActions
	{
		public static readonly IReadOnlyList<string> All =
		[
			"role_grant", "role_revoke", "kick", "mute", "unmute", "ban", "unban", "rename", "name_reset",
			"message_delete", "message_lookup", "staff_message", "report_view", "report_close", "player_inspect",
			"connection_lookup", "anticheat_review", "announcement", "setting_update", "token_mint", "token_rotate",
			"token_revoke", "sa_create", "sa_update", "sa_grant", "sa_revoke", "sa_disable", "sa_enable", "password_reset",
			"two_factor_reset", "revoke_sessions", "revoke_client_sessions", "client_add", "text_term_add", "text_term_update",
			"text_term_remove", "text_exception_add", "text_exception_remove", "text_workflow_update", "text_rules_seed"
		];
	}

	// The review lists, in sidebar order; the short title fits the sidebar beside its counter
	public sealed record PortalReviewList(string Kind, string Title, string ShortTitle, string Description)
	{
		public static readonly IReadOnlyList<PortalReviewList> All =
		[
			new PortalReviewList("new_accounts", "New accounts in quickmatch", "New accounts", "New accounts that played a quickmatch game."),
			new PortalReviewList("modules", "Unrecognized modules", "Modules", "Modules a game client loaded that are not on the allow list."),
			new PortalReviewList("probes", "Suspicious probes", "Probes", "Missing or malformed answers to an anti-cheat probe inside a match."),
			new PortalReviewList("devices", "Suspicious device ids", "Device ids", "Sign-ins with a device value that lacks the marker of a genuine client."),
			new PortalReviewList("quickmatch_rules", "Illegal quick match actions", "Quick match", "Quick match requests a genuine client never sends, such as choosing an army in a random army playlist.")
		];

		public static PortalReviewList? Find(string? kind) => All.FirstOrDefault(list => list.Kind == kind);

		// The Lucide icon of a list, in the sidebar and on the dashboard
		public static string IconOf(string kind) => kind switch
		{
			"new_accounts" => "user-plus",
			"modules" => "puzzle",
			"probes" => "radar",
			_ => "fingerprint-pattern"
		};
	}

	// The unreviewed rows of one review list on the dashboard
	public sealed record PortalQueue
	{
		public string Kind { get; init; } = "";
		public int Unreviewed { get; init; }
	}

	// A sanction of the dashboard's activity feed, with the player it hit
	public sealed record PortalOverviewSanction
	{
		public long Id { get; init; }
		public string Type { get; init; } = "";
		public PortalMember Player { get; init; } = new PortalMember();
		public string? Reason { get; init; }
		public DateTime CreatedAt { get; init; }
		public DateTime? ExpiresAt { get; init; }
		public DateTime? LiftedAt { get; init; }
		public PortalMember? CreatedBy { get; init; }
		public bool Active { get; init; }
	}

	// The staff dashboard: live counts, the queues, and the newest reports and sanctions. The anti-cheat numbers are
	// null for staff without anticheat.review
	public sealed record PortalOverview
	{
		public int PlayersOnline { get; init; }
		public int LobbiesOpen { get; init; }
		public int MatchesToday { get; init; }
		public int OpenReports { get; init; }
		public int? UnreviewedAnticheat { get; init; }
		public IReadOnlyList<PortalQueue>? Anticheat { get; init; }
		public IReadOnlyList<PortalReport> RecentReports { get; init; } = [];
		public IReadOnlyList<PortalOverviewSanction> RecentSanctions { get; init; } = [];
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
		// The reported message's text as the report stored it, null for a report of a player
		public string? MessageContent { get; init; }

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
		public bool Online { get; init; }
		public bool Banned { get; init; }
		public bool Muted { get; init; }
		public IReadOnlyList<string> Roles { get; init; } = [];
		public bool Flagged { get; init; }
		public int Matches { get; init; }
		public int? Rating { get; init; }

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

		// Every role row with where it comes from: "local", or "upstream:<provider>" for a role that follows the sign-ins
		// of that provider and cannot be changed here
		public IReadOnlyList<PortalRoleGrant> RoleGrants { get; init; } = [];

		// The roles the caller may grant or revoke on this player: roles.manage and the rank rule, empty otherwise
		public IReadOnlyList<string> AssignableRoles { get; init; } = [];

		// False where the sign-in provider owns the names
		public bool CanRename { get; init; }

		[JsonIgnore]
		public string FullName => Tag == null ? Name : Name + "#" + Tag;
	}

	public sealed record PortalRoleGrant
	{
		public string Role { get; init; } = "";
		public string Source { get; init; } = "";

		[JsonIgnore]
		public bool Local => Source == "local";

		// "upstream:discord" reads discord
		[JsonIgnore]
		public string Provider => Source.StartsWith("upstream:", StringComparison.Ordinal) ? Source.Substring("upstream:".Length) : Source;
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

	// The new password of a staff password reset, answered once
	public sealed record PortalPasswordReset
	{
		public string Password { get; init; } = "";
	}

	public sealed record PortalRenameRequest
	{
		public string? Name { get; init; }
		public string? Reason { get; init; }
	}

	// The settings staff may change at runtime, GET /api/v1/application/settings
	public sealed record PortalSettings
	{
		// the message of the day, null when none is set
		public string? Motd { get; init; }
	}

	public sealed record PortalAnnouncementRequest
	{
		public string? Message { get; init; }
	}

	// The users online the announcement went to
	public sealed record PortalAnnouncementResult
	{
		public int DeliveredTo { get; init; }
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

	public sealed record PortalAuditActor
	{
		public long Id { get; init; }
		public string? DisplayName { get; init; }
	}

	// One row of GET /api/v1/audit_events
	public sealed record PortalAuditEvent
	{
		public long Id { get; init; }
		public string Action { get; init; } = "";
		public string ActorKind { get; init; } = "";
		public string ActorName { get; init; } = "";
		public PortalAuditActor? Actor { get; init; }
		public string TargetKind { get; init; } = "";
		public long? TargetId { get; init; }
		public string? Reason { get; init; }
		public string Source { get; init; } = "";
		public JsonElement? Details { get; init; }
		public DateTime CreatedAt { get; init; }
	}

	public sealed record PortalAuditPage
	{
		public IReadOnlyList<PortalAuditEvent> AuditEvents { get; init; } = [];
	}

	// The {error, params} body every refused call carries
	public sealed record PortalError
	{
		public string Error { get; init; } = "";
	}

	// What the player list shows (T17f), in the address of the page (?q=&filter=&signed_in_within=&order_by=&sort=) and,
	// with query for q, in the arguments of /api/v1/players/search
	public sealed record PortalPlayerQuery
	{
		public const string Online = "online";
		public const string Banned = "banned";
		public const string Muted = "muted";
		public const string Staff = "staff";
		public const string Flagged = "flagged";

		public static readonly string[] AllFilters = [Online, Banned, Muted, Staff, Flagged];

		public string? Search { get; init; }
		public IReadOnlyList<string> Filters { get; init; } = [];
		// 24h, 7d or 30d
		public string? SignedInWithin { get; init; }
		// last_sign_in_at, created_at, name or matches; null is the host's order
		public string? OrderBy { get; init; }
		// asc or desc; null is the column's own direction
		public string? Sort { get; init; }

		public bool IsFiltered => Filters.Count > 0 || SignedInWithin != null;

		// Unknown values in a typed or old address are dropped, so the list still opens
		// A direction with a search but no order is left out: the host keeps a search's own order and refuses that pair
		public static PortalPlayerQuery FromAddress(string? q, string? filter, string? signedInWithin, string? orderBy, string? sort) => new PortalPlayerQuery
		{
			Search = String.IsNullOrWhiteSpace(q) ? null : q.Trim(),
			Filters = (filter ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(AllFilters.Contains).Distinct().ToList(),
			SignedInWithin = signedInWithin is "24h" or "7d" or "30d" ? signedInWithin : null,
			OrderBy = orderBy is "last_sign_in_at" or "created_at" or "name" or "matches" ? orderBy : null,
			Sort = sort is "asc" or "desc" && (String.IsNullOrWhiteSpace(q) || orderBy is "last_sign_in_at" or "created_at" or "name" or "matches") ? sort : null
		};

		// The page's address arguments; null leaves one out
		public Dictionary<string, object?> ToAddress() => new Dictionary<string, object?>
		{
			["q"] = Search,
			["filter"] = Filters.Count == 0 ? null : String.Join(',', Filters),
			["signed_in_within"] = SignedInWithin,
			["order_by"] = OrderBy,
			["sort"] = Search != null && OrderBy == null ? null : Sort
		};

		public string ToApiQuery() => PortalQueries.Join(new Dictionary<string, object?>(ToAddress().Where(a => a.Key != "q")) { ["query"] = Search });
	}

	// One player of a match of the staff match list
	public sealed record PortalMatchRowPlayer
	{
		public long UserId { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }
		public int? Team { get; init; }
		public bool Won { get; init; }
		public bool Desynced { get; init; }

		[JsonIgnore]
		public string FullName => Tag == null ? Name : Name + "#" + Tag;
	}

	// One match of /api/v1/matches/search
	public sealed record PortalMatchRow
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Type { get; init; }
		public PortalMatchMap Map { get; init; } = new PortalMatchMap();
		public DateTime StartedAt { get; init; }
		public DateTime? FinishedAt { get; init; }
		public long? DurationS { get; init; }
		public IReadOnlyList<PortalMatchRowPlayer> Players { get; init; } = [];
		public IReadOnlyList<string> Flags { get; init; } = [];

		// What the match came to for one of its players: won, lost when another player won, no result when nobody did,
		// in progress while it runs
		public string ResultFor(long userID) =>
			FinishedAt == null ? "in progress"
			: Players.Any(p => p.UserId == userID && p.Won) ? "won"
			: Players.Any(p => p.Won) ? "lost"
			: "no result";
	}

	// Total is null when the host stopped counting (past 10,000 rows); HasNext says whether another page follows
	public sealed record PortalMatchPage
	{
		public IReadOnlyList<PortalMatchRow> Matches { get; init; } = [];
		public int? Total { get; init; }
		public bool HasNext { get; init; }
	}

	// What the match list shows (T17f), in the address of the page and the arguments of /api/v1/matches/search alike
	public sealed record PortalMatchQuery
	{
		// quick_match or custom
		public string? Type { get; init; }
		public string? Map { get; init; }
		// a player id, Name#tag or a whole name
		public string? Player { get; init; }
		// yyyy-MM-dd, UTC; To includes its day
		public string? From { get; init; }
		public string? To { get; init; }
		public bool Flagged { get; init; }

		public bool IsFiltered => Type != null || Map != null || Player != null || From != null || To != null || Flagged;

		public static PortalMatchQuery FromAddress(string? type, string? map, string? player, string? from, string? to, string? flagged) => new PortalMatchQuery
		{
			Type = type is "quick_match" or "custom" ? type : null,
			Map = String.IsNullOrWhiteSpace(map) ? null : map.Trim(),
			Player = String.IsNullOrWhiteSpace(player) ? null : player.Trim(),
			From = Day(from),
			To = Day(to),
			Flagged = flagged == "true"
		};

		public Dictionary<string, object?> ToAddress() => new Dictionary<string, object?>
		{
			["type"] = Type,
			["map"] = Map,
			["player"] = Player,
			["from"] = From,
			["to"] = To,
			["flagged"] = Flagged ? "true" : null
		};

		public string ToApiQuery() => PortalQueries.Join(ToAddress());

		// One player's matches by id, as the player page lists them
		public static PortalMatchQuery OfPlayer(long id) => new PortalMatchQuery { Player = id.ToString(System.Globalization.CultureInfo.InvariantCulture) };

		private static string? Day(string? text) =>
			DateTime.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) ? text : null;
	}

	// A partner: a service account with the permissions granted to it and what its tokens add up to, as
	// /api/v1/service_accounts answers
	public sealed record PortalPartner
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Description { get; init; }
		public DateTime CreatedAt { get; init; }
		public DateTime? DisabledAt { get; init; }
		public IReadOnlyList<string> Permissions { get; init; } = [];
		public int ActiveTokens { get; init; }
		public DateTime? LastTokenUsedAt { get; init; }
	}

	public sealed record PortalPartnerPage
	{
		public IReadOnlyList<PortalPartner> ServiceAccounts { get; init; } = [];
	}

	// A token of a partner, never its secret; revoked_at in the future is the end of a rotation overlap
	public sealed record PortalPartnerToken
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
		// empty when any address may use the token
		public IReadOnlyList<string> AllowedIps { get; init; } = [];

		// Revoked, rotated past its overlap, or expired
		public bool IsActive(DateTime utcNow) => ExpiresAt > utcNow && (RevokedAt == null || RevokedAt > utcNow);
	}

	public sealed record PortalPartnerTokenPage
	{
		public IReadOnlyList<PortalPartnerToken> PersonalAccessTokens { get; init; } = [];
	}

	// The create and rotate answers, the only place the gopat- secret is ever shown
	public sealed record PortalCreatedToken
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public DateTime ExpiresAt { get; init; }
		public string Token { get; init; } = "";
	}

	public sealed record PortalCreatePartnerRequest
	{
		public string? Name { get; init; }
		public string? Description { get; init; }
	}

	public sealed record PortalGrantChanges
	{
		public IReadOnlyList<string>? Add { get; init; }
		public IReadOnlyList<string>? Remove { get; init; }
	}

	// Each part is optional; the null ones are left out of the body, an empty description clears it
	public sealed record PortalUpdatePartnerRequest
	{
		public string? Description { get; init; }
		public PortalGrantChanges? Grants { get; init; }
		public bool? Disabled { get; init; }
	}

	// expires_at is a day (midnight UTC); allowed_ips holds addresses or CIDR ranges
	public sealed record PortalCreateTokenRequest
	{
		public string Name { get; init; } = "";
		public IReadOnlyList<string> Scopes { get; init; } = [];
		public string ExpiresAt { get; init; } = "";
		public IReadOnlyList<string>? AllowedIps { get; init; }
	}

	public static class PortalQueries
	{
		// name=value pairs without the null ones, each value escaped
		public static string Join(IEnumerable<KeyValuePair<string, object?>> arguments) =>
			String.Join('&', arguments.Where(a => a.Value != null).Select(a => a.Key + "=" + Uri.EscapeDataString(a.Value!.ToString()!)));
	}

	// GET /api/v1/staff/realtime: the failover lease as "realtime status" prints it (null without PostgreSQL), and the
	// players, lobbies and queues of the service copy that answered
	public sealed record PortalRealtime
	{
		public PortalRealtimeCopy Copy { get; init; } = new PortalRealtimeCopy();
		public PortalRealtimeLease? Lease { get; init; }
		public int PlayersOnline { get; init; }
		public int LobbiesOpen { get; init; }
		public IReadOnlyList<PortalRealtimeQueue> Queues { get; init; } = [];
	}

	public sealed record PortalRealtimeCopy
	{
		public string Id { get; init; } = "";
		public bool Serving { get; init; }
		public bool Failover { get; init; }
	}

	// Holder and standby are null for none, expires_in_ms is null once the lease expired
	public sealed record PortalRealtimeLease
	{
		public string? Holder { get; init; }
		public long Epoch { get; init; }
		public long? ExpiresInMs { get; init; }
		public string? Standby { get; init; }
		public bool HandoverRequested { get; init; }
	}

	public sealed record PortalRealtimeQueue
	{
		public int PlaylistId { get; init; }
		public string Name { get; init; } = "";
		public int Queued { get; init; }
	}

	// GET /api/v1/staff/lobbies: every live lobby in the fields of the hub's LobbyState
	public sealed record PortalLobbyList
	{
		public IReadOnlyList<PortalLobby> Lobbies { get; init; } = [];
	}

	public sealed record PortalLobby
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public long HostId { get; init; }
		public string Type { get; init; } = "";
		public string State { get; init; } = "";
		public string Visibility { get; init; } = "";
		public bool HasPassword { get; init; }
		public PortalMatchMap Map { get; init; } = new PortalMatchMap();
		public string Region { get; init; } = "";
		public long? MatchId { get; init; }
		public DateTime CreatedAt { get; init; }
		public IReadOnlyList<PortalSlot> Slots { get; init; } = [];

		[JsonIgnore]
		public IEnumerable<PortalSlot> Players => Slots.Where(s => s.Member != null);

		[JsonIgnore]
		public int Seats => Slots.Count(s => s.State != "closed");
	}

	public sealed record PortalSlot
	{
		public int Index { get; init; }
		public string State { get; init; } = "";
		public PortalMember? Member { get; init; }
		public bool Ready { get; init; }
	}

	// One connection report as "connections player|lobby" prints it, a report never carries an address
	public sealed record PortalConnectionReport
	{
		public long Id { get; init; }
		public long LobbyId { get; init; }
		public PortalMember Reporter { get; init; } = new PortalMember();
		public PortalMember Target { get; init; } = new PortalMember();
		public int Attempt { get; init; }
		public string Outcome { get; init; } = "";
		public string Transport { get; init; } = "";
		public int IpVersion { get; init; }
		public IReadOnlyList<PortalConnectionEvent> Events { get; init; } = [];
		public DateTime CreatedAt { get; init; }
		public DateTime UpdatedAt { get; init; }

		// the timeline as the CLI prints it: connecting@0ms, failed@5000ms
		[JsonIgnore]
		public string Timeline => String.Join(", ", Events.Select(e => $"{e.State}@{e.TMs}ms"));
	}

	public sealed record PortalConnectionEvent
	{
		public string State { get; init; } = "";
		public long TMs { get; init; }
	}

	public sealed record PortalConnectionReports
	{
		public IReadOnlyList<PortalConnectionReport> Reports { get; init; } = [];
	}

	// The text rules page (/admin/text-rules) of GET /api/v1/staff/text_rules/*: words, exceptions, rule workflows, the test
	public sealed record PortalTextTerm
	{
		public long Id { get; init; }
		public string Term { get; init; } = "";
		public string Language { get; init; } = "";
		public string Category { get; init; } = "";
		public string Severity { get; init; } = "";
		public string Match { get; init; } = "";
		public string Source { get; init; } = "";
		public string UpdatedBy { get; init; } = "";
		public DateTime UpdatedAt { get; init; }
	}

	public sealed record PortalTextTerms
	{
		public int Total { get; init; }
		public IReadOnlyList<PortalTextTerm> Terms { get; init; } = [];
	}

	// A new word sends every field, an edit only the ones that change
	public sealed record PortalTextTermRequest
	{
		public string? Term { get; init; }
		public string? Language { get; init; }
		public string? Category { get; init; }
		public string? Severity { get; init; }
		public string? Match { get; init; }
	}

	public sealed record PortalTextException
	{
		public long Id { get; init; }
		public string Term { get; init; } = "";
		public string Language { get; init; } = "";
		public string Source { get; init; } = "";
		public string UpdatedBy { get; init; } = "";
		public DateTime UpdatedAt { get; init; }
	}

	public sealed record PortalTextExceptions
	{
		public IReadOnlyList<PortalTextException> Exceptions { get; init; } = [];
	}

	public sealed record PortalTextRule
	{
		public string Name { get; init; } = "";
		public string Expression { get; init; } = "";
		public string Outcome { get; init; } = "";
		public bool Enabled { get; init; } = true;
	}

	public sealed record PortalTextWorkflowDefinition
	{
		public IReadOnlyList<PortalTextRule> Rules { get; init; } = [];
	}

	public sealed record PortalTextWorkflow
	{
		public string Kind { get; init; } = "";
		public PortalTextWorkflowDefinition Definition { get; init; } = new PortalTextWorkflowDefinition();
		public IReadOnlyList<string> Outcomes { get; init; } = [];
		public IReadOnlyList<string> Fields { get; init; } = [];
		public string UpdatedBy { get; init; } = "";
		public DateTime? UpdatedAt { get; init; }
	}

	public sealed record PortalTextWorkflows
	{
		public IReadOnlyList<PortalTextWorkflow> Workflows { get; init; } = [];
	}

	public sealed record PortalTextTestReading
	{
		public string Name { get; init; } = "";
		public string Text { get; init; } = "";
	}

	public sealed record PortalTextTestHit
	{
		public string Term { get; init; } = "";
		public string Language { get; init; } = "";
		public string Category { get; init; } = "";
		public string Severity { get; init; } = "";
		public string Reading { get; init; } = "";
		public string Text { get; init; } = "";
	}

	public sealed record PortalTextTest
	{
		public string Kind { get; init; } = "";
		public string Outcome { get; init; } = "";
		public string? Rule { get; init; }
		public string? Masked { get; init; }
		public IReadOnlyList<PortalTextTestReading> Readings { get; init; } = [];
		public IReadOnlyList<PortalTextTestHit> Hits { get; init; } = [];
	}

	// The choices of the text rules, as the host's TextKinds, TextLanguages, TextCategories, TextSeverities and TextMatchModes
	public static class PortalTextChoices
	{
		public static readonly IReadOnlyList<(string Value, string Title)> Kinds = [("display_name", "Display name"), ("lobby_name", "Lobby name"), ("chat", "Chat message")];
		public static readonly IReadOnlyList<(string Value, string Title)> Languages = [("en", "English"), ("ar", "Arabic")];
		public static readonly IReadOnlyList<string> Categories = ["slur", "sexual", "profanity", "insult", "impersonation", "reserved"];
		public static readonly IReadOnlyList<string> Severities = ["block", "flag"];
		public static readonly IReadOnlyList<string> Matches = ["word", "anywhere", "literal"];

		public static string KindTitle(string kind) => Kinds.FirstOrDefault(k => k.Value == kind).Title ?? kind;
	}

	// An error answer with its reason, as a refused rule workflow carries it in params.reason
	public sealed record PortalReasonError
	{
		public string Error { get; init; } = "";
		public PortalReasonParams? Params { get; init; }
	}

	public sealed record PortalReasonParams
	{
		public string? Reason { get; init; }
	}
}

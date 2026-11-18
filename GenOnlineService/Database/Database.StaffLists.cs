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

using GenOnlineService;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace Database
{
	// The sort columns of the staff player list
	public static class StaffPlayerOrders
	{
		public const string LastSignInAt = "last_sign_in_at";
		public const string CreatedAt = "created_at";
		public const string Name = "name";
		public const string Matches = "matches";

		public static readonly string[] All = [LastSignInAt, CreatedAt, Name, Matches];

		// Names read A to Z, dates and counts newest or most first
		public static bool DescendingByDefault(string order) => order != Name;
	}

	// Which accounts the staff player list keeps; every condition given must hold
	public sealed record StaffPlayerQuery
	{
		// A name part, Name#tag or a player id; null lists every account
		public string? Term { get; init; }
		// Only these accounts (the online filter); null keeps every account
		public IReadOnlyCollection<long>? OnlyIDs { get; init; }
		public bool Banned { get; init; }
		public bool Muted { get; init; }
		public bool Staff { get; init; }
		public bool Flagged { get; init; }
		// Unreviewed anti-cheat rows flag a player too, for a caller who may review them
		public bool AntiCheat { get; init; }
		public DateTime? SignedInSince { get; init; }
		// null: the exact id first and then by name for a term, the most recent sign-in first without one
		public string? OrderBy { get; init; }
		// null: the column's own direction
		public bool? Descending { get; init; }
	}

	// What the list shows beside the account row: active sanctions, staff roles, review flags, matches and the rating
	public sealed record StaffPlayerFacts(bool Banned, bool Muted, bool Flagged, int Matches, int? Rating, IReadOnlyList<string> Roles);

	// Total is null past the count cap; HasNext says whether another page follows
	public sealed record StaffPlayerList(int? Total, bool HasNext, IReadOnlyList<StaffPlayerRow> Players, IReadOnlyDictionary<long, StaffPlayerFacts> Facts);

	// The staff list of every account (T17f), filtered, sorted and paged in the database
	public static class StaffPlayerLists
	{
		private static readonly Regex s_tagged = new Regex(@"^(?<name>.+)#(?<tag>[0-9]{1,4})$", RegexOptions.CultureInvariant);

		// Counting stops here, as for the match list, so opening the list never counts every account
		public const int TotalCap = 10_000;

		public static async Task<StaffPlayerList> ListAsync(AppDbContext db, StaffPlayerQuery query, DateTime utcNow, int skip, int take, int totalCap = TotalCap, CancellationToken cancellationToken = default)
		{
			IQueryable<User> users = db.Users.AsNoTracking();
			long termID = 0;
			if (query.Term != null)
			{
				(users, termID) = Matching(users, query.Term);
			}
			if (query.OnlyIDs != null)
			{
				long[] ids = query.OnlyIDs.ToArray();
				users = users.Where(u => ids.Contains(u.ID));
			}
			if (query.Banned)
			{
				users = users.Where(HasActive(db, SanctionTypes.Ban, utcNow));
			}
			if (query.Muted)
			{
				users = users.Where(HasActive(db, SanctionTypes.Mute, utcNow));
			}
			if (query.Staff)
			{
				string[] staffRoles = StaffRoles.All.Where(StaffRoles.IsStaff).ToArray();
				users = users.Where(u => db.UserRoles.Any(r => r.UserID == u.ID && staffRoles.Contains(r.Role)));
			}
			if (query.Flagged)
			{
				// the unreviewed anti-cheat rows are few and read on their reviewed_at index, two of the tables have no user index
				long[] reviewable = query.AntiCheat ? (await UnreviewedPlayers(db, null, cancellationToken)).ToArray() : [];
				users = users.Where(u => db.PlayerReports.Any(r => r.ReportedUserID == u.ID && r.Status == PlayerReportStatuses.Open) || reviewable.Contains(u.ID));
			}
			if (query.SignedInSince is DateTime since)
			{
				DateTime from = DateTime.SpecifyKind(since, DateTimeKind.Utc);
				users = users.Where(u => u.LastSignInAt != null && u.LastSignInAt >= from);
			}

			int counted = await users.Take(totalCap + 1).CountAsync(cancellationToken);
			string? order = query.OrderBy ?? (query.Term == null ? StaffPlayerOrders.LastSignInAt : null);
			List<StaffPlayerRow> page = order == StaffPlayerOrders.LastSignInAt
				? await BySignInAsync(users, query.Descending ?? true, skip, take + 1, cancellationToken)
				: await Rows(Ordered(db, users, query, termID).Skip(skip).Take(take + 1), cancellationToken);
			// one row past the page says whether another follows
			bool hasNext = page.Count > take;
			if (hasNext)
			{
				page.RemoveAt(page.Count - 1);
			}
			return new StaffPlayerList(counted > totalCap ? null : counted, hasNext, page, await FactsOf(db, page.Select(p => p.ID).ToArray(), query.AntiCheat, utcNow, cancellationToken));
		}

		private static Task<List<StaffPlayerRow>> Rows(IQueryable<User> users, CancellationToken cancellationToken) =>
			users.Select(u => new StaffPlayerRow(u.ID, u.Username, u.NameTag, u.State, u.CreatedAt, u.LastSignInAt)).ToListAsync(cancellationToken);

		// Last seen in two parts, so ix_users_last_sign_in_at (last_sign_in_at DESC, id DESC) serves each page on both databases:
		// the accounts that signed in by time and id, then those that never did by id, last both ways. One ORDER BY with the
		// nulls last would need NULLS LAST on PostgreSQL and an expression on MariaDB, which no plain index serves.
		private static async Task<List<StaffPlayerRow>> BySignInAsync(IQueryable<User> users, bool descending, int skip, int take, CancellationToken cancellationToken)
		{
			IQueryable<User> signedIn = users.Where(u => u.LastSignInAt != null);
			IQueryable<User> bySignIn = descending
				? signedIn.OrderByDescending(u => u.LastSignInAt).ThenByDescending(u => u.ID)
				: signedIn.OrderBy(u => u.LastSignInAt).ThenBy(u => u.ID);
			List<StaffPlayerRow> page = await Rows(bySignIn.Skip(skip).Take(take), cancellationToken);
			if (page.Count < take)
			{
				// the signed-in part ends on this page, so its length is known without counting it, unless the page starts past it
				int signedInCount = page.Count > 0 || skip == 0 ? skip + page.Count : await signedIn.CountAsync(cancellationToken);
				IQueryable<User> never = users.Where(u => u.LastSignInAt == null);
				never = descending ? never.OrderByDescending(u => u.ID) : never.OrderBy(u => u.ID);
				page.AddRange(await Rows(never.Skip(Math.Max(0, skip - signedInCount)).Take(take - page.Count), cancellationToken));
			}
			return page;
		}

		// The term anywhere in the name ignoring case or the exact player id, as the staff search always matched; Name#tag
		// narrows the name part to that tag
		private static (IQueryable<User> Users, long ID) Matching(IQueryable<User> users, string term)
		{
			System.Text.RegularExpressions.Match tagged = s_tagged.Match(term);
			if (tagged.Success)
			{
				string name = NameNormalizer.Normalize(tagged.Groups["name"].Value.Trim());
				int tag = Int32.Parse(tagged.Groups["tag"].Value, CultureInfo.InvariantCulture);
				return (users.Where(u => u.UsernameNormalized.Contains(name) && u.NameTag == tag), 0);
			}

			string normalized = NameNormalizer.Normalize(term);
			long id = long.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
			return (users.Where(u => u.UsernameNormalized.Contains(normalized) || u.ID == id), id);
		}

		private static Expression<Func<User, bool>> HasActive(AppDbContext db, string type, DateTime utcNow)
		{
			return u => db.Sanctions.Any(s => s.UserID == u.ID && s.Type == type && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > utcNow));
		}

		// Ties go by the account id in the same direction, so a page boundary never repeats or skips an account
		private static IQueryable<User> Ordered(AppDbContext db, IQueryable<User> users, StaffPlayerQuery query, long termID)
		{
			string? order = query.OrderBy;
			if (order == null && query.Term != null)
			{
				return users.OrderByDescending(u => u.ID == termID).ThenBy(u => u.UsernameNormalized).ThenBy(u => u.ID);
			}

			order ??= StaffPlayerOrders.LastSignInAt;
			bool descending = query.Descending ?? StaffPlayerOrders.DescendingByDefault(order);
			switch (order)
			{
				// (name, tag) is unique, so ux_users_username_normalized_name_tag orders the whole list
				case StaffPlayerOrders.Name:
					return descending
						? users.OrderByDescending(u => u.UsernameNormalized).ThenByDescending(u => u.NameTag)
						: users.OrderBy(u => u.UsernameNormalized).ThenBy(u => u.NameTag);
				case StaffPlayerOrders.CreatedAt:
					return descending
						? users.OrderByDescending(u => u.CreatedAt).ThenByDescending(u => u.ID)
						: users.OrderBy(u => u.CreatedAt).ThenBy(u => u.ID);
				case StaffPlayerOrders.Matches:
					return descending
						? users.OrderByDescending(u => db.MatchPlayers.Count(p => p.UserId == u.ID)).ThenByDescending(u => u.ID)
						: users.OrderBy(u => db.MatchPlayers.Count(p => p.UserId == u.ID)).ThenBy(u => u.ID);
				default:
					throw new ArgumentOutOfRangeException(nameof(query), order, "Last seen pages through BySignInAsync");
			}
		}

		// The players with an unreviewed anti-cheat row, of these ids or of all
		private static async Task<HashSet<long>> UnreviewedPlayers(AppDbContext db, long[]? ids, CancellationToken cancellationToken)
		{
			IQueryable<long> modules = db.AnticheatModuleReports.AsNoTracking().Where(r => r.ReviewedAt == null).Select(r => r.UserId);
			IQueryable<long> newAccounts = db.AnticheatNewAccountGames.AsNoTracking().Where(r => r.ReviewedAt == null).Select(r => r.UserId);
			IQueryable<long> probes = db.AnticheatProbeReports.AsNoTracking().Where(r => r.ReviewedAt == null).Select(r => r.UserId);
			var found = new HashSet<long>();
			foreach (IQueryable<long> source in new[] { modules, newAccounts, probes })
			{
				found.UnionWith(await (ids == null ? source : source.Where(id => ids.Contains(id))).Distinct().ToListAsync(cancellationToken));
			}
			return found;
		}

		// One query per kind of fact for the page's accounts, each on an index that starts with the user id
		private static async Task<Dictionary<long, StaffPlayerFacts>> FactsOf(AppDbContext db, long[] ids, bool antiCheat, DateTime utcNow, CancellationToken cancellationToken)
		{
			if (ids.Length == 0)
			{
				return new Dictionary<long, StaffPlayerFacts>();
			}

			var roles = await db.UserRoles.AsNoTracking().Where(r => ids.Contains(r.UserID)).Select(r => new { r.UserID, r.Role }).ToListAsync(cancellationToken);
			var sanctions = await db.Sanctions.AsNoTracking()
				.Where(s => ids.Contains(s.UserID) && (s.Type == SanctionTypes.Ban || s.Type == SanctionTypes.Mute) && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > utcNow))
				.Select(s => new { s.UserID, s.Type })
				.ToListAsync(cancellationToken);

			var flagged = (await db.PlayerReports.AsNoTracking().Where(r => ids.Contains(r.ReportedUserID) && r.Status == PlayerReportStatuses.Open).Select(r => r.ReportedUserID).Distinct().ToListAsync(cancellationToken)).ToHashSet();
			if (antiCheat)
			{
				flagged.UnionWith(await UnreviewedPlayers(db, ids, cancellationToken));
			}

			Dictionary<long, int> matches = await db.MatchPlayers.AsNoTracking()
				.Where(p => p.UserId != null && ids.Contains(p.UserId.Value))
				.GroupBy(p => p.UserId!.Value)
				.Select(g => new { UserID = g.Key, Count = g.Count() })
				.ToDictionaryAsync(g => g.UserID, g => g.Count, cancellationToken);

			// a rating counts once the player has a rated match
			Dictionary<long, int> ratings = await db.PlayerRatings.AsNoTracking()
				.Where(r => ids.Contains(r.UserID) && r.Kind == RatingKinds.AllTime && r.MatchesPlayed > 0)
				.ToDictionaryAsync(r => r.UserID, r => r.Rating, cancellationToken);

			return ids.ToDictionary(id => id, id => new StaffPlayerFacts(
				Banned: sanctions.Any(s => s.UserID == id && s.Type == SanctionTypes.Ban),
				Muted: sanctions.Any(s => s.UserID == id && s.Type == SanctionTypes.Mute),
				Flagged: flagged.Contains(id),
				Matches: matches.GetValueOrDefault(id),
				Rating: ratings.TryGetValue(id, out int rating) ? rating : null,
				// known roles only, so an unknown role never shows a badge (StaffRoles.RolesOf)
				Roles: roles.Where(r => r.UserID == id && StaffRoles.IsKnown(r.Role)).Select(r => r.Role).Distinct().Order(StringComparer.Ordinal).ToList()));
		}
	}

	// The flags the staff match list marks a match with
	public static class StaffMatchFlags
	{
		public const string Desync = "desync";
		public const string NoResult = "no_result";
		public const string AntiCheat = "anticheat";
	}

	// Which matches the staff match list keeps; every condition given must hold
	public sealed record StaffMatchQuery
	{
		public ELobbyType? Type { get; init; }
		// A part of the map name, any case
		public string? Map { get; init; }
		// Matches with any of these accounts; null keeps every match
		public IReadOnlyCollection<long>? PlayerIDs { get; init; }
		// Started at or after From and before To
		public DateTime? From { get; init; }
		public DateTime? To { get; init; }
		public bool Flagged { get; init; }
		// Unreviewed anti-cheat rows flag a match too, for a caller who may review them
		public bool AntiCheat { get; init; }
	}

	// One player of a listed match: the name the player had in the match and the account's current tag
	public sealed record StaffMatchPlayer(long UserId, string Name, int? Tag, int? Team, int? Side, bool Won, bool Desynced);

	public sealed record StaffMatchRow(long Id, string Name, ELobbyType? Type, string Map, string? MapPath, bool OfficialMap, DateTime StartedAt, DateTime? FinishedAt, IReadOnlyList<StaffMatchPlayer> Players, IReadOnlyList<string> Flags);

	// Total is null past the count cap; HasNext says whether another page follows
	public sealed record StaffMatchList(int? Total, bool HasNext, IReadOnlyList<StaffMatchRow> Matches);

	// The staff list of matches (T17f), newest first on the primary key, filtered and paged in the database
	public static class StaffMatchLists
	{
		// Counting stops here, as GitLab stops counting a long list, so a page of a long history never counts it to the end
		public const int TotalCap = 10_000;

		// A desynced player's details contain this, asked by JSON containment on every provider: MySQL 8 stores its json
		// column normalized ("desynced": true, with a space), so the text MariaDB keeps verbatim cannot be matched as text
		private const string DesyncedJson = "{\"desynced\":true}";

		public static async Task<StaffMatchList> ListAsync(AppDbContext db, StaffMatchQuery query, int skip, int take, int totalCap = TotalCap, CancellationToken cancellationToken = default)
		{
			IQueryable<global::Match> matches = db.Matches.AsNoTracking();
			Expression<Func<MatchPlayer, bool>> desynced = db.Database.IsNpgsql()
				? p => p.Details != null && NpgsqlJsonDbFunctionsExtensions.JsonContains(EF.Functions, p.Details, DesyncedJson)
				: p => p.Details != null && MySqlJsonDbFunctionsExtensions.JsonContains(EF.Functions, p.Details, DesyncedJson);
			if (query.Type is ELobbyType type)
			{
				matches = matches.Where(m => m.LobbyType == type);
			}
			if (!String.IsNullOrEmpty(query.Map))
			{
				string map = query.Map.ToLowerInvariant();
				matches = matches.Where(m => m.MapName.ToLower().Contains(map));
			}
			if (query.PlayerIDs != null)
			{
				long[] ids = query.PlayerIDs.ToArray();
				matches = matches.Where(m => m.Players.Any(p => p.UserId != null && ids.Contains(p.UserId.Value)));
			}
			if (query.From is DateTime from)
			{
				DateTime start = DateTime.SpecifyKind(from, DateTimeKind.Utc);
				matches = matches.Where(m => m.StartedAt >= start);
			}
			if (query.To is DateTime to)
			{
				DateTime end = DateTime.SpecifyKind(to, DateTimeKind.Utc);
				matches = matches.Where(m => m.StartedAt < end);
			}
			if (query.Flagged)
			{
				// the unreviewed rows are few and read on their reviewed_at index; match_id has no index of its own
				long[] reviewable = query.AntiCheat ? (await UnreviewedMatches(db, null, cancellationToken)).ToArray() : [];
				matches = matches.Where(m => m.Players.AsQueryable().Any(desynced)
					|| (m.FinishedAt != null && !m.Players.Any(p => p.Result == MatchResults.Win))
					|| reviewable.Contains(m.Id));
			}

			int counted = await matches.Take(totalCap + 1).CountAsync(cancellationToken);
			List<global::Match> page = await matches.OrderByDescending(m => m.Id).Skip(skip).Take(take + 1).ToListAsync(cancellationToken);
			bool hasNext = page.Count > take;
			if (hasNext)
			{
				page.RemoveAt(page.Count - 1);
			}
			return new StaffMatchList(counted > totalCap ? null : counted, hasNext, await RowsOf(db, page, query.AntiCheat, cancellationToken));
		}

		// The accounts a player filter names: a player id, Name#tag, or a whole name with any tag, in any case
		public static async Task<List<long>> PlayersNamed(AppDbContext db, string text, CancellationToken cancellationToken = default)
		{
			if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
			{
				return [id];
			}

			int hash = text.LastIndexOf('#');
			if (hash > 0 && Int32.TryParse(text.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int tag) && text.Length - hash - 1 <= 4)
			{
				string tagged = NameNormalizer.Normalize(text.Substring(0, hash).Trim());
				return await db.Users.AsNoTracking().Where(u => u.UsernameNormalized == tagged && u.NameTag == tag).Select(u => u.ID).ToListAsync(cancellationToken);
			}

			string name = NameNormalizer.Normalize(text);
			return await db.Users.AsNoTracking().Where(u => u.UsernameNormalized == name).Select(u => u.ID).ToListAsync(cancellationToken);
		}

		// The matches with an unreviewed anti-cheat row (a sign-in row has match id 0), of these ids or of all
		private static async Task<HashSet<long>> UnreviewedMatches(AppDbContext db, long[]? ids, CancellationToken cancellationToken)
		{
			IQueryable<long> modules = db.AnticheatModuleReports.AsNoTracking().Where(r => r.ReviewedAt == null).Select(r => r.MatchId);
			IQueryable<long> newAccounts = db.AnticheatNewAccountGames.AsNoTracking().Where(r => r.ReviewedAt == null).Select(r => r.MatchId);
			IQueryable<long> probes = db.AnticheatProbeReports.AsNoTracking().Where(r => r.ReviewedAt == null).Select(r => r.MatchId);
			var found = new HashSet<long>();
			foreach (IQueryable<long> source in new[] { modules, newAccounts, probes })
			{
				IQueryable<long> matchIDs = ids == null ? source.Where(id => id != 0) : source.Where(id => ids.Contains(id));
				found.UnionWith(await matchIDs.Distinct().ToListAsync(cancellationToken));
			}
			return found;
		}

		private static async Task<List<StaffMatchRow>> RowsOf(AppDbContext db, List<global::Match> page, bool antiCheat, CancellationToken cancellationToken)
		{
			long[] ids = page.Select(m => m.Id).ToArray();
			if (ids.Length == 0)
			{
				return new List<StaffMatchRow>();
			}

			List<MatchPlayer> players = await db.MatchPlayers.AsNoTracking()
				.Where(p => ids.Contains(p.MatchId))
				.OrderBy(p => p.MatchId).ThenBy(p => p.Slot)
				.ToListAsync(cancellationToken);
			Dictionary<long, (string Name, int Tag)> names = await DisplayNames.GetBulk(db, players.Where(p => p.UserId != null).Select(p => p.UserId!.Value).ToHashSet());

			HashSet<long> reviewable = antiCheat ? await UnreviewedMatches(db, ids, cancellationToken) : new HashSet<long>();

			var rows = new List<StaffMatchRow>(page.Count);
			foreach (global::Match match in page)
			{
				// every slot decides the flags, the list names the slots with an account
				List<MatchPlayer> all = players.Where(p => p.MatchId == match.Id).ToList();
				List<StaffMatchPlayer> slots = all.Where(p => p.UserId != null).Select(p =>
				{
					MatchPlayerDetails details = MatchHistory.ReadDetails(p.Details);
					long userID = p.UserId!.Value;
					return new StaffMatchPlayer(userID,
						details.display_name.Length > 0 ? details.display_name : names.TryGetValue(userID, out var current) ? current.Name : "",
						names.TryGetValue(userID, out var named) ? named.Tag : null,
						p.Team, p.Side, p.Result == MatchResults.Win, details.desynced);
				}).ToList();

				var flags = new List<string>();
				if (all.Any(p => MatchHistory.ReadDetails(p.Details).desynced))
				{
					flags.Add(StaffMatchFlags.Desync);
				}
				if (match.FinishedAt != null && !all.Any(p => p.Result == MatchResults.Win))
				{
					flags.Add(StaffMatchFlags.NoResult);
				}
				if (reviewable.Contains(match.Id))
				{
					flags.Add(StaffMatchFlags.AntiCheat);
				}

				rows.Add(new StaffMatchRow(match.Id, match.Name, match.LobbyType, match.MapName, match.MapPath, match.IsOfficialMap, match.StartedAt, match.FinishedAt, slots, flags));
			}
			return rows;
		}
	}
}

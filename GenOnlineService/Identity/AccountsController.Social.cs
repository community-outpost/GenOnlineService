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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net;
using System.Text;

namespace GenOnlineService.Identity
{
	// The Friends and Blocked players pages of the account area (the design's Friends and Blocked boards). They call the
	// same SocialActions as the game's hub and GET/POST /api/v1/me, so every rule (the friends limit, who can be blocked,
	// duplicates, crossed requests) and every push to the game is the one the game has. A form posts and redirects back to
	// its page with one of a fixed set of answers in the address (post-redirect-get); no text a person typed travels there.
	// No script: every control is a form with its own antiforgery token.
	public sealed partial class AccountsController
	{
		public const string FriendsPath = "/profile/friends";
		public const string BlockedPath = "/profile/blocked";
		private const string PlayerField = "player";
		private const string UserField = "user_id";
		private const string AnswerField = "r";

		// The answers a form ends with: the text and whether it is good news. A refusal of an add never says why (an unknown
		// player, one who blocked you and one who cannot be added are all "neutral"), as the form must not tell who plays here.
		private static readonly Dictionary<string, (string Key, bool Success)> s_socialAnswers = new(StringComparer.Ordinal)
		{
			["sent"] = ("Social.Done.Sent", true),
			["accepted"] = ("Social.Done.Accepted", true),
			["declined"] = ("Social.Done.Declined", true),
			["cancelled"] = ("Social.Done.Cancelled", true),
			["removed"] = ("Social.Done.Removed", true),
			["blocked"] = ("Social.Done.Blocked", true),
			["unblocked"] = ("Social.Done.Unblocked", true),
			["neutral"] = ("Social.Error.Neutral", false),
			["full"] = ("Social.Error.Full", false),
			["self"] = ("Social.Error.Self", false),
			["gone"] = ("Social.Error.Gone", false),
			["toomany"] = ("Social.Error.TooMany", false)
		};

		// The answers each page can show: one that belongs to the other page is not shown
		private static readonly HashSet<string> s_friendsAnswers = new(StringComparer.Ordinal) { "sent", "accepted", "declined", "cancelled", "removed", "blocked", "neutral", "full", "self", "gone", "toomany" };
		private static readonly HashSet<string> s_blockedAnswers = new(StringComparer.Ordinal) { "blocked", "unblocked", "neutral", "self", "gone", "toomany" };

		private IDbContextFactory<AppDbContext> Db => HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();

		// The signed-in player of a social page, or where to send the browser: the sign-in page, or the name the account owes
		private async Task<(User? User, IActionResult? Redirect)> SocialUser(string page)
		{
			User? user = await CookieUser();
			if (user == null)
			{
				return (null, Challenge(new AuthenticationProperties { RedirectUri = page }, IdentityConstants.ApplicationScheme));
			}
			return user.NameRequiredAt != null ? (null, LocalRedirect(WelcomeUrl(page))) : (user, null);
		}

		private (string? Message, bool Success) SocialAnswer(string? code, HashSet<string> allowed)
			=> code != null && allowed.Contains(code) && s_socialAnswers.TryGetValue(code, out (string Key, bool Success) answer) ? (T(answer.Key, SocialActions.FriendsLimit), answer.Success) : (null, false);

		// A form's answer: the antiforgery token first, then the player, then what act returns (one of s_socialAnswers) goes
		// back to the page. Too many tries answers 429 with the page itself.
		private async Task<IActionResult> SocialPost(string page, Func<User, Task<string>> act)
		{
			if (!await ValidForm())
			{
				return Expired();
			}
			(User? user, IActionResult? redirect) = await SocialUser(page);
			if (user == null)
			{
				return redirect!;
			}

			string answer = await act(user);
			if (answer == "toomany")
			{
				return page == BlockedPath ? await BlockedView(429, user, answer) : await FriendsView(429, user, answer);
			}
			return LocalRedirect($"{page}?{AnswerField}={answer}");
		}

		// Adding a friend and blocking look a player up by what a person typed, so they share one limit for each account: a
		// name that is not found costs a try like one that is
		private bool SocialTryConsume(User user) => m_socialLimiter.TryConsume(("social:user:" + user.ID.ToString(CultureInfo.InvariantCulture), AccountSocialLimiter.PerAccount));

		private async Task<long?> FindPlayer(string? text)
		{
			await using AppDbContext db = await Db.CreateDbContextAsync();
			return await SocialPlayers.FindAsync(db, text);
		}

		// An action on a player a page listed, by the id its row's form posts. What the action refuses is told as a page that
		// changed (a request that is no longer there), except a friends list that is full, which is told as it is.
		private static async Task<string> SocialRow(string? id, Func<long, Task<Result<Empty>>> act, string done, string refused = "gone")
		{
			if (!Int64.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long target) || target <= 0)
			{
				return "gone";
			}
			Result<Empty> result = await act(target);
			return result.Ok ? done : result.Code == ErrorCodes.SocialFriendListFull ? "full" : refused;
		}

		// A button in a list row: a form that posts the player's id to the action
		private string RowForm(string action, long id, string button)
			=> $"{PostForm(action, "gp-inline")}{Hidden(UserField, id.ToString(CultureInfo.InvariantCulture))}{button}</form>";

		// The line under a name in a request: what it is, and the day it was made when that is known
		private string RequestMeta(string textKey, DateTime? at)
			=> WebUtility.HtmlEncode(T(textKey)) + (at is DateTime day && day > DateTime.UnixEpoch ? " · " + WebUtility.HtmlEncode(Day(day)) : String.Empty);

		// ===== Friends =====

		[HttpGet("~" + FriendsPath)]
		public async Task<IActionResult> FriendsPage([FromQuery(Name = AnswerField)] string? answer)
		{
			(User? user, IActionResult? redirect) = await SocialUser(FriendsPath);
			return user == null ? redirect! : await FriendsView(200, user, answer);
		}

		[HttpPost("~" + FriendsPath + "/add")]
		public Task<IActionResult> FriendsAdd([FromForm(Name = PlayerField)] string? player) => SocialPost(FriendsPath, async user =>
		{
			if (!SocialTryConsume(user))
			{
				return "toomany";
			}
			long? target = await FindPlayer(player);
			if (target == null)
			{
				return "neutral";
			}
			if (target == user.ID)
			{
				return "self";
			}
			Result<Empty> result = await SocialActions.SendFriendRequestAsync(Db, user.ID, target.Value);
			return result.Ok ? "sent" : result.Code == ErrorCodes.SocialFriendListFull ? "full" : "neutral";
		});

		[HttpPost("~" + FriendsPath + "/accept")]
		public Task<IActionResult> FriendsAccept([FromForm(Name = UserField)] string? id) => SocialPost(FriendsPath, user => SocialRow(id, target => SocialActions.AcceptFriendRequestAsync(Db, user.ID, target), "accepted"));

		[HttpPost("~" + FriendsPath + "/decline")]
		public Task<IActionResult> FriendsDecline([FromForm(Name = UserField)] string? id) => SocialPost(FriendsPath, user => SocialRow(id, target => SocialActions.DeleteFriendRequestAsync(Db, user.ID, target), "declined"));

		[HttpPost("~" + FriendsPath + "/cancel")]
		public Task<IActionResult> FriendsCancel([FromForm(Name = UserField)] string? id) => SocialPost(FriendsPath, user => SocialRow(id, target => SocialActions.DeleteFriendRequestAsync(Db, user.ID, target), "cancelled"));

		[HttpPost("~" + FriendsPath + "/remove")]
		public Task<IActionResult> FriendsRemove([FromForm(Name = UserField)] string? id) => SocialPost(FriendsPath, user => SocialRow(id, target => SocialActions.RemoveFriendAsync(Db, user.ID, target), "removed"));

		// The block in a friend's menu, by the id its form posts; what SocialActions refuses (an admin) is the neutral answer
		[HttpPost("~" + FriendsPath + "/block")]
		public Task<IActionResult> FriendsBlock([FromForm(Name = UserField)] string? id) => SocialPost(FriendsPath, user => SocialRow(id, target => SocialActions.BlockAsync(Db, user.ID, target), "blocked", refused: "neutral"));

		private string PresenceOf(FriendMember friend)
		{
			if (!friend.Online)
			{
				return PortalLook.Presence(T("Social.Presence.Offline"), online: false);
			}
			string key = friend.Activity switch
			{
				PresenceActivity.Lobby => "Social.Presence.Lobby",
				PresenceActivity.Matchmaking => "Social.Presence.Matchmaking",
				PresenceActivity.InGame => "Social.Presence.InGame",
				_ => "Social.Presence.Online"
			};
			return PortalLook.Presence(T(key), online: true);
		}

		private async Task<ContentResult> FriendsView(int status, User user, string? code)
		{
			(string? message, bool success) = SocialAnswer(code, s_friendsAnswers);
			SocialLists lists;
			Dictionary<long, (string Name, int Tag)> names;
			Dictionary<long, DateTime> incomingAt;
			Dictionary<long, DateTime> outgoingAt;
			await using (AppDbContext db = await Db.CreateDbContextAsync())
			{
				lists = await SocialActions.ListsAsync(db, user.ID);
				// the names shown are the stored ones, Name#Tag (the friends of the Friends event carry no tag)
				List<long> everyone = lists.Friends.Select(f => f.Id).Concat(lists.Incoming.Select(m => m.Id)).Concat(lists.Outgoing.Select(m => m.Id)).Distinct().ToList();
				names = await Database.DisplayNames.GetBulk(db, everyone);
				List<FriendRequest> requests = await db.FriendRequests.AsNoTracking().Where(r => r.ToUserId == user.ID || r.FromUserId == user.ID).ToListAsync();
				incomingAt = requests.Where(r => r.ToUserId == user.ID).ToDictionary(r => r.FromUserId, r => r.CreatedAt);
				outgoingAt = requests.Where(r => r.FromUserId == user.ID).ToDictionary(r => r.ToUserId, r => r.CreatedAt);
			}

			var body = new StringBuilder();
			body.Append(PortalLook.Intro(T("Social.Friends.Intro")));
			if (message != null)
			{
				body.Append(PortalLook.Alert(message, success));
			}
			body.Append(PortalLook.PlayerForm(PostForm(FriendsPath + "/add", "gp-add"), "add", PlayerField, T("Social.Add.Title"), T("Social.Add.Placeholder"), T("Social.Add.Hint"), PortalLook.ActionButton(T("Social.Add.Submit"))));

			// Requests: the ones waiting for an answer, then the ones sent
			List<string> requestRows = new();
			List<Member> incoming = lists.Incoming.Where(m => names.ContainsKey(m.Id)).OrderByDescending(m => incomingAt.GetValueOrDefault(m.Id)).ToList();
			foreach (Member from in incoming)
			{
				(string name, int tag) = names[from.Id];
				string actions = RowForm(FriendsPath + "/accept", from.Id, PortalLook.ActionButton(T("Social.Accept"), "primary"))
					+ RowForm(FriendsPath + "/decline", from.Id, PortalLook.ActionButton(T("Social.Decline"), "ghost"));
				requestRows.Add(PortalLook.Person(name, "#" + Database.DisplayNames.FormatTag(tag), RequestMeta("Social.Request.Incoming", incomingAt.GetValueOrDefault(from.Id)), actions));
			}
			List<Member> outgoing = lists.Outgoing.Where(m => names.ContainsKey(m.Id)).OrderByDescending(m => outgoingAt.GetValueOrDefault(m.Id)).ToList();
			foreach (Member to in outgoing)
			{
				(string name, int tag) = names[to.Id];
				string actions = RowForm(FriendsPath + "/cancel", to.Id, PortalLook.ActionButton(T("Social.Cancel"), "ghost"));
				requestRows.Add(PortalLook.Person(name, "#" + Database.DisplayNames.FormatTag(tag), RequestMeta("Social.Request.Outgoing", outgoingAt.GetValueOrDefault(to.Id)), actions));
			}
			body.Append(PortalLook.Card(T("Social.Requests.Title"), T("Social.Requests.Description", incoming.Count, outgoing.Count),
				requestRows.Count == 0 ? PortalLook.Empty("inbox", T("Social.Requests.EmptyTitle"), T("Social.Requests.EmptyBody"), small: true) : PortalLook.People(requestRows)));

			// Friends: online first, then by name
			List<string> friendRows = new();
			foreach (FriendMember friend in lists.Friends.Where(f => names.ContainsKey(f.Id)).OrderByDescending(f => f.Online).ThenBy(f => names[f.Id].Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id))
			{
				(string name, int tag) = names[friend.Id];
				string menu = RowForm(FriendsPath + "/remove", friend.Id, PortalLook.MenuItem(T("Social.Friend.Remove"), "user-x"))
					+ RowForm(FriendsPath + "/block", friend.Id, PortalLook.MenuItem(T("Social.Friend.Block"), "ban"));
				friendRows.Add(PortalLook.Person(name, "#" + Database.DisplayNames.FormatTag(tag), PresenceOf(friend), PortalLook.MoreMenu(T("Social.Friend.More", Database.DisplayNames.Format(name, tag)), menu)));
			}
			string friendContent = friendRows.Count == 0
				? PortalLook.Empty("inbox", T("Social.Friends.EmptyTitle"), T("Social.Friends.EmptyBody"))
				: PortalLook.People(friendRows) + $"<p class=\"gp-hint gp-note\">{WebUtility.HtmlEncode(T("Social.Friends.MenuNote"))}</p>";
			body.Append(PortalLook.Card(T("Social.Friends.List.Title"), T("Social.Friends.List.Description", friendRows.Count, SocialActions.FriendsLimit), friendContent));

			return await AccountDocument(status, user, T("Social.Friends.Title"), FriendsPath, body.ToString());
		}

		// ===== Blocked players =====

		[HttpGet("~" + BlockedPath)]
		public async Task<IActionResult> BlockedPage([FromQuery(Name = AnswerField)] string? answer)
		{
			(User? user, IActionResult? redirect) = await SocialUser(BlockedPath);
			return user == null ? redirect! : await BlockedView(200, user, answer);
		}

		// Blocking by name or id. What SocialActions refuses (an unknown player, an admin, a player who cannot be blocked) is the
		// one neutral answer, as for adding a friend.
		[HttpPost("~" + BlockedPath + "/add")]
		public Task<IActionResult> BlockedAdd([FromForm(Name = PlayerField)] string? player) => SocialPost(BlockedPath, async user =>
		{
			if (!SocialTryConsume(user))
			{
				return "toomany";
			}
			long? target = await FindPlayer(player);
			if (target == null)
			{
				return "neutral";
			}
			if (target == user.ID)
			{
				return "self";
			}
			return (await SocialActions.BlockAsync(Db, user.ID, target.Value)).Ok ? "blocked" : "neutral";
		});

		[HttpPost("~" + BlockedPath + "/remove")]
		public Task<IActionResult> BlockedRemove([FromForm(Name = UserField)] string? id) => SocialPost(BlockedPath, user => SocialRow(id, target => SocialActions.UnblockAsync(Db, user.ID, target), "unblocked"));

		private async Task<ContentResult> BlockedView(int status, User user, string? code)
		{
			(string? message, bool success) = SocialAnswer(code, s_blockedAnswers);
			SocialLists lists;
			Dictionary<long, (string Name, int Tag)> names;
			Dictionary<long, DateTime> blockedAt;
			await using (AppDbContext db = await Db.CreateDbContextAsync())
			{
				lists = await SocialActions.ListsAsync(db, user.ID);
				names = await Database.DisplayNames.GetBulk(db, lists.Blocked.Select(m => m.Id).ToList());
				blockedAt = await db.Blocks.AsNoTracking().Where(b => b.UserId == user.ID).ToDictionaryAsync(b => b.BlockedUserId, b => b.CreatedAt);
			}

			var body = new StringBuilder();
			body.Append(PortalLook.Intro(T("Social.Blocked.Intro")));
			if (message != null)
			{
				body.Append(PortalLook.Alert(message, success));
			}
			body.Append(PortalLook.PlayerForm(PostForm(BlockedPath + "/add", "gp-add"), "blk", PlayerField, T("Social.Block.Title"), T("Social.Add.Placeholder"), T("Social.Block.Hint"), PortalLook.ActionButton(T("Social.Block.Submit"), "secondary")));

			List<string> rows = new();
			foreach (Member blocked in lists.Blocked.Where(m => names.ContainsKey(m.Id)).OrderByDescending(m => blockedAt.GetValueOrDefault(m.Id)).ThenBy(m => m.Id))
			{
				(string name, int tag) = names[blocked.Id];
				rows.Add(PortalLook.Person(name, "#" + Database.DisplayNames.FormatTag(tag), RequestMeta("Social.Blocked.Meta", blockedAt.GetValueOrDefault(blocked.Id)),
					RowForm(BlockedPath + "/remove", blocked.Id, PortalLook.ActionButton(T("Social.Unblock"), "outline"))));
			}
			body.Append(rows.Count == 0
				? PortalLook.Empty("inbox", T("Social.Blocked.EmptyTitle"), T("Social.Blocked.EmptyBody"))
				: PortalLook.People(rows));
			body.Append($"<p class=\"gp-hint gp-note\">{WebUtility.HtmlEncode(T("Social.Blocked.ReportNote"))}</p>");

			return await AccountDocument(status, user, T("Social.Blocked.Title"), BlockedPath, body.ToString());
		}
	}
}

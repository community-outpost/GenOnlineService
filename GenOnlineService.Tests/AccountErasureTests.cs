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

using System.Text.Json.Nodes;
using GenOnlineService.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

// Erasure on request (Privacy Policy, "Your rights"): personal data goes, the account id stays so matches, ratings and bans
// keep it, the name leaves the matches, and an active ban keeps the device ids and provider links until it ends
public class AccountErasureTests
{
	private const long MatchID = 700001;
	private static readonly Database.ModerationActor Operator = new() { Kind = AuditActorKinds.Cli, Name = "cli:operator", Source = ModerationSources.Cli };

	private sealed record Seeded(long Erased, long Other);

	private static async Task<Seeded> Seed(ProbeDatabase probe, bool banned)
	{
		await using AppDbContext db = probe.Open();
		DateTime now = DateTime.UtcNow;
		var user = new User
		{
			Username = "Hunter", UsernameNormalized = NameNormalizer.Normalize("Hunter"), NameTag = 1234,
			Email = "hunter@example.org", EmailNormalized = NameNormalizer.Normalize("hunter@example.org"), EmailConfirmedAt = now,
			PasswordHash = "hash", SecurityStamp = "old-stamp", TwoFactorEnabled = true, AuthenticatorKey = "protected-key",
			LockoutEndAt = now.AddMinutes(5), FailedSignInCount = 2, LastSignInAt = now, RenamedAt = now, CreatedAt = now.AddYears(-1), UpdatedAt = now
		};
		var other = new User { Username = "Friend", UsernameNormalized = NameNormalizer.Normalize("Friend"), NameTag = 4321, Email = "friend@example.org", EmailNormalized = NameNormalizer.Normalize("friend@example.org"), CreatedAt = now, UpdatedAt = now };
		db.Users.AddRange(user, other);
		await db.SaveChangesAsync();
		long id = user.ID, otherID = other.ID;

		db.UserUsernameHistory.Add(new UserUsernameHistory { UserID = id, Username = "OldName", NameTag = 1, ChangedAt = now, ChangedBy = "user:1" });
		db.UserRecoveryCodes.Add(new UserRecoveryCode { UserID = id, CodeHash = new string('a', 64), CreatedAt = now });
		db.UserAgreements.Add(new UserAgreement { UserID = id, Document = "terms", Version = "1", AgreedAt = now });
		db.Identities.Add(new UserIdentity { UserID = id, Provider = IdentityProviders.Steam, ExternUid = "76561190000000001", CreatedAt = now });
		db.Identities.Add(new UserIdentity { UserID = otherID, Provider = IdentityProviders.Steam, ExternUid = "76561190000000002", CreatedAt = now });
		db.Friendships.Add(new Friendship { UserIdLow = Math.Min(id, otherID), UserIdHigh = Math.Max(id, otherID), CreatedAt = now });
		db.FriendRequests.Add(new FriendRequest { FromUserId = otherID, ToUserId = id, CreatedAt = now });
		db.Blocks.Add(new UserBlock { UserId = otherID, BlockedUserId = id, CreatedAt = now });
		db.PlayerPreferences.Add(new PlayerPreference { UserID = id, Color = 3 });
		db.UserRoles.Add(new UserRole { UserID = id, Role = "moderator", Source = AuthoritySources.Local, GrantedAt = now });
		db.SessionEvents.Add(new SessionEvent { UserID = id, Kind = "start", Result = SessionEventResults.Ok, Ip = "192.0.2.7", CreatedAt = now });
		db.Devices.Add(new Device { UserID = id, Kind = "mac", ValueHmac = Enumerable.Repeat((byte)9, Device.ValueHmacLength).ToArray(), ValueRaw = "AA-BB", FirstSeenAt = now, LastSeenAt = now, LastIp = "192.0.2.7" });
		db.ChatMessages.Add(new ChatMessage { Kind = ChatMessageKinds.Channel, ChannelID = 1, SenderID = id, Type = ChatMessageTypes.Chat, Content = "hello", CreatedAt = now });
		db.ChatMessages.Add(new ChatMessage { Kind = ChatMessageKinds.Channel, ChannelID = 1, SenderID = otherID, Type = ChatMessageTypes.Chat, Content = "hi", CreatedAt = now });
		db.PlayerRatings.Add(new PlayerRating { UserID = id, Rating = 1600, MatchesPlayed = 10, UpdatedAt = now });
		db.Matches.Add(new Match { Id = MatchID, Name = "lobby", MapName = "Tournament Desert", RosterType = "1v1", StartedAt = now });
		db.MatchPlayers.AddRange(
			new MatchPlayer { MatchId = MatchID, Slot = 0, UserId = id, Details = "{\"display_name\":\"Hunter\",\"units_built\":12,\"metadata\":[{\"file_name\":\"a.jpg\"}]}" },
			new MatchPlayer { MatchId = MatchID, Slot = 1, UserId = otherID, Details = "{\"display_name\":\"Friend\",\"units_built\":3}" });
		if (banned)
		{
			db.Sanctions.Add(new Sanction { UserID = id, Type = SanctionTypes.Ban, Reason = "cheating", CreatedAt = now });
		}
		await db.SaveChangesAsync();
		return new Seeded(id, otherID);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ErasureRemovesPersonalDataAndKeepsTheAccountIdForMatchesAndRatings(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe, banned: false);

		await using (AppDbContext db = probe.Open())
		{
			ErasureResult result = await AccountErasure.Erase(db, s.Erased, Operator, "request by email", live: false);
			Assert.Equal(new ErasureResult(true, false, false, 1), result);
		}

		await using (AppDbContext db = probe.Open())
		{
			User row = await db.Users.SingleAsync(u => u.ID == s.Erased);
			Assert.StartsWith("Deleted", row.Username);
			Assert.True(AccountErasure.TombstoneName().Length <= User.UsernameMaxLength);
			Assert.Null(row.Email);
			Assert.Null(row.EmailNormalized);
			Assert.Null(row.EmailConfirmedAt);
			Assert.Null(row.PasswordHash);
			Assert.Null(row.AuthenticatorKey);
			Assert.False(row.TwoFactorEnabled);
			Assert.Null(row.LockoutEndAt);
			Assert.Equal(0, row.FailedSignInCount);
			Assert.Null(row.LastSignInAt);
			Assert.Null(row.RenamedAt);
			Assert.NotEqual("old-stamp", row.SecurityStamp);
			Assert.Equal(UserStates.Deactivated, row.State);
			Assert.True(await db.ErasedAccounts.AnyAsync(e => e.UserID == s.Erased));
			Assert.False(await db.ErasedAccounts.AnyAsync(e => e.UserID == s.Other));

			Assert.False(await db.UserUsernameHistory.AnyAsync(h => h.UserID == s.Erased));
			Assert.False(await db.NameReservations.AnyAsync(r => r.UserID == s.Erased));
			Assert.False(await db.UserRecoveryCodes.AnyAsync(c => c.UserID == s.Erased));
			Assert.False(await db.UserAgreements.AnyAsync(a => a.UserID == s.Erased));
			Assert.False(await db.Identities.AnyAsync(i => i.UserID == s.Erased));
			Assert.False(await db.Friendships.AnyAsync());
			Assert.False(await db.FriendRequests.AnyAsync());
			Assert.False(await db.Blocks.AnyAsync());
			Assert.False(await db.PlayerPreferences.AnyAsync(p => p.UserID == s.Erased));
			Assert.False(await db.UserRoles.AnyAsync(r => r.UserID == s.Erased));
			Assert.False(await db.SessionEvents.AnyAsync(e => e.UserID == s.Erased));
			Assert.False(await db.Devices.AnyAsync(d => d.UserID == s.Erased));
			Assert.False(await db.ChatMessages.AnyAsync(m => m.SenderID == s.Erased));

			// the match keeps its rows and statistics without the name; the other player and the rating are untouched
			JsonObject mine = JsonNode.Parse((await db.MatchPlayers.SingleAsync(p => p.UserId == s.Erased)).Details!)!.AsObject();
			Assert.Equal("", mine["display_name"]!.GetValue<string>());
			Assert.Equal(12, mine["units_built"]!.GetValue<int>());
			Assert.Equal("a.jpg", mine["metadata"]![0]!["file_name"]!.GetValue<string>());
			Assert.Contains("Friend", (await db.MatchPlayers.SingleAsync(p => p.UserId == s.Other)).Details);
			Assert.Equal(1600, (await db.PlayerRatings.SingleAsync(r => r.UserID == s.Erased)).Rating);

			// the other account keeps all of its own data
			Assert.True(await db.Identities.AnyAsync(i => i.UserID == s.Other));
			Assert.True(await db.ChatMessages.AnyAsync(m => m.SenderID == s.Other));
			Assert.Equal("friend@example.org", (await db.Users.SingleAsync(u => u.ID == s.Other)).Email);

			// audited without the name or the email
			AuditLogEntry audit = await db.AuditLog.SingleAsync(a => a.Action == AccountErasure.Action);
			Assert.Equal(s.Erased, audit.TargetID);
			Assert.Equal("request by email", audit.Reason);
			Assert.DoesNotContain("Hunter", audit.Details ?? "");
			Assert.DoesNotContain("hunter@", audit.Details ?? "");
		}

		// a second request changes nothing
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(new ErasureResult(true, true, false, 0), await AccountErasure.Erase(db, s.Erased, Operator, "again", live: false));
			Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == AccountErasure.Action));
			Assert.Equal(new ErasureResult(false, false, false, 0), await AccountErasure.Erase(db, 987654321, Operator, "nobody", live: false));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnActiveBanKeepsHashesAndDeviceRowsUntilItEnds(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe, banned: true);

		await using (AppDbContext db = probe.Open())
		{
			ErasureResult result = await AccountErasure.Erase(db, s.Erased, Operator, "request", live: false);
			Assert.True(result.BanHeld);
		}

		await using (AppDbContext db = probe.Open())
		{
			// the provider link goes, its hash and the mailbox hash stay; the device row stays without its raw value or address
			Assert.False(await db.Identities.AnyAsync(i => i.UserID == s.Erased));
			Assert.True(await IdentifierHashes.ProviderHeld(db, IdentityProviders.Steam, "76561190000000001"));
			Assert.True(await db.IdentifierHashes.AnyAsync(h => h.UserID == s.Erased && h.Kind == HashedIdentifierKinds.Email && h.SanctionID != null));
			Device device = await db.Devices.SingleAsync(d => d.UserID == s.Erased);
			Assert.Null(device.ValueRaw);
			Assert.Null(device.LastIp);
			Assert.Single(await db.Sanctions.Where(x => x.UserID == s.Erased).ToListAsync());
			// the rest of the personal data goes as without a ban
			Assert.Null((await db.Users.SingleAsync(u => u.ID == s.Erased)).Email);
			Assert.False(await db.SessionEvents.AnyAsync(e => e.UserID == s.Erased));

			// while the ban lasts the purge keeps the hashes
			Assert.Equal(0, await IdentifierHashes.PurgeEndedHolds(db, DateTime.UtcNow));
			await db.Sanctions.Where(x => x.UserID == s.Erased).ExecuteUpdateAsync(x => x.SetProperty(b => b.LiftedAt, DateTime.UtcNow));
		}

		// once the ban is lifted the hashes go; an account that was never erased keeps its own link
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(2, await IdentifierHashes.PurgeEndedHolds(db, DateTime.UtcNow));
			Assert.False(await IdentifierHashes.ProviderHeld(db, IdentityProviders.Steam, "76561190000000001"));
			Assert.True(await db.Identities.AnyAsync(i => i.UserID == s.Other));
		}
	}

	// two erasures of one account at the same time: one erases and audits, the other finds it already erased
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TwoErasuresAtOnceWriteOneAuditRow(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe, banned: false);

		async Task<ErasureResult> EraseOnce()
		{
			await using AppDbContext db = probe.Open();
			return await AccountErasure.Erase(db, s.Erased, Operator, "request", live: false);
		}
		ErasureResult[] results = await Task.WhenAll(EraseOnce(), EraseOnce());

		Assert.Single(results, r => !r.AlreadyErased);
		Assert.Single(results, r => r.AlreadyErased);
		await using AppDbContext check = probe.Open();
		Assert.Equal(1, await check.AuditLog.CountAsync(a => a.Action == AccountErasure.Action));
		Assert.Equal(1, await check.ErasedAccounts.CountAsync());
		Assert.Null((await check.Users.SingleAsync(u => u.ID == s.Erased)).Email);
	}

	// a live player cannot take a name that reads as an erased account's, look-alikes included
	[Theory]
	[InlineData("Deleted123456", true)]
	[InlineData("deleted000001", true)]
	[InlineData("DELETED999999", true)]
	[InlineData("De1eted123456", true)]
	[InlineData("DeletedOOOOOO", true)]
	[InlineData("Deleted12345", false)]
	[InlineData("Deleted1234567", false)]
	[InlineData("Deletedabcdef", false)]
	[InlineData("Delete1234567", false)]
	public void TombstoneShapedNamesAreRecognised(string name, bool shaped)
	{
		Assert.Equal(shaped, AccountErasure.IsTombstoneShaped(name));
	}

	[Fact]
	public void ATombstoneShapedNameIsAReservedWord()
	{
		Assert.Equal("deleted", GenOnlineService.Realtime.ProfileActions.ReservedWordIn(AccountErasure.TombstoneName()));
	}
}

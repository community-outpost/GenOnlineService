using Database;
using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;
using System.Security.Claims;
using Xunit;

namespace GenOnlineService.Tests;

// Game session registrations live in the database, so the copy a client reconnects to after a handover, or a restarted copy,
// accepts its realtime upgrade (docs/realtime-failover.md). Runs on the probe databases.
[Collection(GameSessionCacheCollection.Name)]
public class GameSessionProbeTests
{
	private const Int64 UserID = 4242;

	private sealed class ProbeFactory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;
		public ProbeFactory(ProbeDatabase probe) => m_probe = probe;
		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	private static async Task AddSignIn(AppDbContext db, string authorizationID)
	{
		db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Add(new OpenIddictEntityFrameworkCoreAuthorization
		{
			Id = authorizationID,
			Subject = UserID.ToString(),
			Status = "valid",
			Type = "permanent",
			CreationDate = DateTime.UtcNow
		});
		await db.SaveChangesAsync();
	}

	private static GameSessionRegistration Registration(string authorizationID) =>
		new(authorizationID, "genonline-game", "zerohour", DateTime.UtcNow, 0xDEADBEEF, 0x01020304, 7);

	// The hub upgrade of a game client signed in with authorizationID, through the registration gate; the status it answers
	private static async Task<int> Upgrade(ProbeDatabase probe, string authorizationID)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IDbContextFactory<AppDbContext>>(new ProbeFactory(probe));
		ServiceProvider provider = services.BuildServiceProvider();
		ApplicationBuilder app = new(provider);
		app.UseRealtimeRegistrationGate();
		app.Run(context =>
		{
			context.Response.StatusCode = StatusCodes.Status200OK;
			return Task.CompletedTask;
		});

		DefaultHttpContext context = new() { RequestServices = provider };
		context.Request.Path = RealtimeHub.Path;
		context.Response.Body = new MemoryStream();
		context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
		{
			new Claim(ClaimTypes.NameIdentifier, UserID.ToString()),
			new Claim("session_type", ((int)EUserSessionType.GameClient).ToString()),
			new Claim(SessionTokenHandler.AuthorizationIDClaim, authorizationID)
		}, "test"));
		await app.Build()(context);
		return context.Response.StatusCode;
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AClientRegisteredOnAnotherCopyIsAcceptedHere(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using (AppDbContext db = probe.Open())
		{
			await AddSignIn(db, "auth-a");
			await GameSessions.RegisterAsync(db, UserID, EUserSessionType.GameClient, Registration("auth-a"));
		}

		// this copy never saw the registration: a handover target or a restarted copy
		GameSessions.ForgetCached();

		Assert.Equal(StatusCodes.Status200OK, await Upgrade(probe, "auth-a"));
		GameSessionRegistration? cached = GameSessions.Get(UserID, EUserSessionType.GameClient);
		Assert.NotNull(cached);
		Assert.Equal(("auth-a", "genonline-game", "zerohour", (uint?)0xDEADBEEF, (uint?)0x01020304, 7),
			(cached!.AuthorizationID, cached.ClientID, cached.Game, cached.ExeCrc, cached.IniCrc, cached.AnticheatID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ANewerRegistrationOnAnotherCopyReplacesTheOneCachedHere(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using (AppDbContext db = probe.Open())
		{
			await AddSignIn(db, "auth-old");
			await AddSignIn(db, "auth-new");
			await GameSessions.RegisterAsync(db, UserID, EUserSessionType.GameClient, Registration("auth-old"));
		}
		GameSessions.ForgetCached();
		Assert.Equal(StatusCodes.Status200OK, await Upgrade(probe, "auth-old"));

		// the user signs in again and registers on the other copy
		await using (AppDbContext db = probe.Open())
		{
			await GameSessionStore.Save(db, UserID, EUserSessionType.GameClient, Registration("auth-new"));
			Assert.Equal(1, await db.GameSessions.CountAsync());
		}

		Assert.Equal(StatusCodes.Status200OK, await Upgrade(probe, "auth-new"));
		Assert.Equal(StatusCodes.Status409Conflict, await Upgrade(probe, "auth-old"));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AClientThatNeverRegisteredIsStillRefusedWith409(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		GameSessions.ForgetCached();

		Assert.Equal(StatusCodes.Status409Conflict, await Upgrade(probe, "auth-none"));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EndingOrPruningTheSignInRemovesItsStoredRegistration(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		await AddSignIn(db, "auth-ended");
		await AddSignIn(db, "auth-pruned");
		await GameSessionStore.Save(db, UserID, EUserSessionType.GameClient, Registration("auth-ended"));
		await GameSessionStore.Save(db, UserID, EUserSessionType.ChatClient, Registration("auth-pruned"));

		await GameSessionStore.DeleteSignIn(db, "auth-ended");
		Assert.Null(await GameSessionStore.Load(db, UserID, EUserSessionType.GameClient));

		// a pruned sign-in takes its registration with it
		await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Where(a => a.Id == "auth-pruned").ExecuteDeleteAsync();
		Assert.Null(await GameSessionStore.Load(db, UserID, EUserSessionType.ChatClient));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task RevokingAUsersGameSignInsRemovesOnlyThoseRegistrations(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		await AddSignIn(db, "auth-game");
		await AddSignIn(db, "auth-chat");
		await GameSessionStore.Save(db, UserID, EUserSessionType.GameClient, Registration("auth-game"));
		await GameSessionStore.Save(db, UserID, EUserSessionType.ChatClient, Registration("auth-chat"));

		await GameSessionStore.DeleteUser(db, UserID, new[] { "auth-game" });
		Assert.Null(await GameSessionStore.Load(db, UserID, EUserSessionType.GameClient));
		Assert.NotNull(await GameSessionStore.Load(db, UserID, EUserSessionType.ChatClient));

		await GameSessionStore.DeleteUser(db, UserID, null);
		Assert.Null(await GameSessionStore.Load(db, UserID, EUserSessionType.ChatClient));
	}
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameSessionCacheCollection
{
	public const string Name = "game session cache";
}

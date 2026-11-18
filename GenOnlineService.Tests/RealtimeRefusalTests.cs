using GenOnlineService.Api;
using GenOnlineService.Failover;
using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using System.Security.Claims;
using Xunit;

namespace GenOnlineService.Tests;

// Behind the Cloudflare tunnel an HTTP status on the realtime upgrade reaches the client only as a bare 502, so the
// service accepts every upgrade and refuses after the handshake with a close code (docs/realtime-protocol.md section 3)
public class RealtimeRefusalTests
{
	private sealed class WebSocketUpgrade : IHttpWebSocketFeature
	{
		public bool IsWebSocketRequest => true;
		public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
	}

	// Answers every challenge with 401 and every forbid with 403, as the session scheme does for a request that is not an upgrade
	private sealed class StatusAuthentication : IAuthenticationService
	{
		public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
		public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) { context.Response.StatusCode = 401; return Task.CompletedTask; }
		public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) { context.Response.StatusCode = 403; return Task.CompletedTask; }
		public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
		public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
	}

	private static DefaultHttpContext Request(string path, bool upgrade)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IAuthenticationService, StatusAuthentication>();
		DefaultHttpContext context = new() { RequestServices = services.BuildServiceProvider() };
		context.Request.Path = path;
		context.Response.Body = new MemoryStream();
		if (upgrade)
		{
			context.Features.Set<IHttpWebSocketFeature>(new WebSocketUpgrade());
		}
		return context;
	}

	private static async Task<int> ThroughAuthorization(string path, bool upgrade, PolicyAuthorizationResult result)
	{
		DefaultHttpContext context = Request(path, upgrade);
		bool passed = false;
		await new ApiAuthorizationResultHandler().HandleAsync(_ => { passed = true; context.Response.StatusCode = 200; return Task.CompletedTask; },
			context, new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(), result);
		return passed ? 200 : context.Response.StatusCode;
	}

	[Fact]
	public async Task AnUpgradeWithoutAValidTokenReachesTheHubWhichClosesIt()
	{
		Assert.Equal(200, await ThroughAuthorization(RealtimeHub.Path, upgrade: true, PolicyAuthorizationResult.Challenge()));
		Assert.Equal(200, await ThroughAuthorization(RealtimeHub.Path, upgrade: true, PolicyAuthorizationResult.Forbid()));
	}

	[Fact]
	public async Task RequestsThatAreNotARealtimeUpgradeKeepTheirHttpAnswers()
	{
		Assert.Equal(401, await ThroughAuthorization(RealtimeHub.Path, upgrade: false, PolicyAuthorizationResult.Challenge()));
		Assert.Equal(401, await ThroughAuthorization("/api/v1/me", upgrade: true, PolicyAuthorizationResult.Challenge()));
		Assert.Equal(401, await ThroughAuthorization("/api/v1/me", upgrade: false, PolicyAuthorizationResult.Challenge()));
	}

	private static async Task<int> ThroughStandbyGate(string path, bool upgrade)
	{
		DefaultHttpContext context = Request(path, upgrade);
		ApplicationBuilder app = new(context.RequestServices);
		app.UseRealtimeStandbyGate(() => false);
		app.Run(c => { c.Response.StatusCode = 200; return Task.CompletedTask; });
		await app.Build()(context);
		return context.Response.StatusCode;
	}

	[Fact]
	public async Task AStandbyLetsTheRealtimeUpgradeThroughAndStillRefusesOtherCallsWith503()
	{
		Assert.Equal(200, await ThroughStandbyGate(RealtimeHub.Path, upgrade: true));
		Assert.Equal(503, await ThroughStandbyGate(RealtimeHub.Path, upgrade: false));
		Assert.Equal(503, await ThroughStandbyGate("/api/v1/me", upgrade: false));
	}

	[Fact]
	public async Task TheRegistrationGateLeavesTheUpgradeToTheHub()
	{
		DefaultHttpContext context = Request(RealtimeHub.Path, upgrade: true);
		context.User = GameClient("auth-unregistered");
		ApplicationBuilder app = new(context.RequestServices);
		app.UseRealtimeRegistrationGate();
		app.Run(c => { c.Response.StatusCode = 200; return Task.CompletedTask; });
		await app.Build()(context);
		Assert.Equal(200, context.Response.StatusCode);
	}

	private static ClaimsPrincipal GameClient(string authorizationID, EUserSessionType sessionType = EUserSessionType.GameClient) =>
		new(new ClaimsIdentity(new[]
		{
			new Claim(ClaimTypes.NameIdentifier, "77"),
			new Claim("session_type", ((int)sessionType).ToString()),
			new Claim(ClaimTypes.Role, sessionType.ToString()),
			new Claim(SessionTokenHandler.AuthorizationIDClaim, authorizationID)
		}, "test"));

	private static GameSessionRegistration Registered(string authorizationID) => new(authorizationID, "genonline-game", "zerohour", DateTime.UtcNow, 1, 2, 3);

	private static Task<RealtimeAdmission.Refusal?> Check(bool serving, ClaimsPrincipal user, PlayBan? ban = null, GameSessionRegistration? registration = null) =>
		RealtimeAdmission.CheckAsync(serving, user, ban, () => Task.FromResult(registration));

	[Fact]
	public async Task ARegisteredGameClientAndAChatClientAreServed()
	{
		Assert.Null(await Check(true, GameClient("auth-1"), registration: Registered("auth-1")));
		Assert.Null(await Check(true, GameClient("auth-2", EUserSessionType.ChatClient)));
	}

	[Fact]
	public async Task AnUnregisteredGameClientIsClosedWithSessionNotRegistered()
	{
		foreach (GameSessionRegistration? registration in new[] { null, Registered("another-sign-in") })
		{
			RealtimeAdmission.Refusal? refusal = await Check(true, GameClient("auth-1"), registration: registration);
			Assert.NotNull(refusal);
			Assert.Equal(("session.not_registered", false), (refusal!.Code, refusal.AllowReconnect));
			Assert.Null(refusal.Before);
		}
	}

	[Fact]
	public async Task ACallerWithoutAValidGameTokenIsClosedWithUnauthorized()
	{
		ClaimsPrincipal anonymous = new(new ClaimsIdentity());
		ClaimsPrincipal website = new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "77") }, "test"));
		foreach (ClaimsPrincipal user in new[] { anonymous, website })
		{
			RealtimeAdmission.Refusal? refusal = await Check(true, user);
			Assert.NotNull(refusal);
			Assert.Equal(("unauthorized", false), (refusal!.Code, refusal.AllowReconnect));
			Assert.Null(refusal.Before);
		}
	}

	[Fact]
	public async Task ABannedCallerGetsTheNoticeWithReasonAndExpiryThenModerationBanned()
	{
		DateTime expires = new(2026, 12, 1, 18, 0, 0, DateTimeKind.Utc);
		RealtimeAdmission.Refusal? refusal = await Check(true, new ClaimsPrincipal(new ClaimsIdentity()), new PlayBan("cheating", expires));
		Assert.NotNull(refusal);
		Assert.Equal(("moderation.banned", false), (refusal!.Code, refusal.AllowReconnect));
		Assert.Equal(nameof(IRealtimeClient.ModerationNotice), refusal.Before!.Target);
		ModerationNoticeEvent notice = Assert.IsType<ModerationNoticeEvent>(Assert.Single(refusal.Before.Arguments));
		Assert.Equal((ModerationAction.Ban, "cheating", (DateTime?)expires), (notice.Action, notice.Reason, notice.ExpiresAt));
	}

	[Fact]
	public async Task ACopyThatIsNotServingSendsServerSwitchingThenServiceUnavailable()
	{
		// standby comes first: a standby reads nothing about the caller
		RealtimeAdmission.Refusal? refusal = await Check(false, new ClaimsPrincipal(new ClaimsIdentity()), new PlayBan("cheating", null));
		Assert.NotNull(refusal);
		Assert.Equal(("service.unavailable", true), (refusal!.Code, refusal.AllowReconnect));
		Assert.Equal(nameof(IRealtimeClient.ServerSwitching), refusal.Before!.Target);
		ServerSwitchingEvent switching = Assert.IsType<ServerSwitchingEvent>(Assert.Single(refusal.Before.Arguments));
		Assert.Equal(1000, switching.RetryAfterMs);
	}

	[Fact]
	public void TheRefusalCloseCodesAreTheProtocolNames()
	{
		Assert.Equal(("unauthorized", "session.not_registered", "service.unavailable", "moderation.banned"),
			(CloseCodes.Unauthorized, CloseCodes.SessionNotRegistered, CloseCodes.ServiceUnavailable, CloseCodes.ModerationBanned));
		Assert.IsType<InvocationMessage>(RealtimeAdmission.Standby().Before);
	}
}

public class RealtimeAdmittedFilterTests
{
	private sealed class Caller : Microsoft.AspNetCore.SignalR.HubCallerContext
	{
		public bool Aborted;
		public override string ConnectionId => "c1";
		public override string? UserIdentifier => null;
		public override ClaimsPrincipal? User => null;
		public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
		public override IFeatureCollection Features { get; } = new FeatureCollection();
		public override CancellationToken ConnectionAborted => CancellationToken.None;
		public override void Abort() => Aborted = true;
	}

	private static async Task<(bool Ran, bool Aborted)> Invoke(bool admitted)
	{
		Caller caller = new();
		if (admitted)
		{
			caller.Items[RealtimeHub.ConnectionItem] = new object();
		}
		bool ran = false;
		var context = new Microsoft.AspNetCore.SignalR.HubInvocationContext(caller, new ServiceCollection().BuildServiceProvider(), null!, typeof(RealtimeHub).GetMethod(nameof(RealtimeHub.OnConnectedAsync))!, Array.Empty<object?>());
		await new RealtimeAdmittedFilter().InvokeMethodAsync(context, _ => { ran = true; return ValueTask.FromResult<object?>(null); });
		return (ran, caller.Aborted);
	}

	[Fact]
	public async Task AMethodCallFromAConnectionTheHubDidNotAdmitNeverRuns()
	{
		Assert.Equal((false, true), await Invoke(admitted: false));
		Assert.Equal((true, false), await Invoke(admitted: true));
	}
}

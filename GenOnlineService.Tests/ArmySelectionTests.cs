using GenOnlineService.Failover;
using GenOnlineService.Realtime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests;

// A quick match lobby whose playlist has allow_army_selection off keeps every side random
public class ArmySelectionTests
{
	private static UserSession Session(long userID) => new(userID, EUserSessionType.GameClient, "genonline_zerohour", "EU", "NL", 0, 0);

	private static Lobby NewLobby(ELobbyType type, bool allowArmySelection)
	{
		Lobby lobby = new(9201, Session(1), "Army lobby", ELobbyState.GAME_SETUP, "Tournament Desert", "maps/tournament desert/tournament desert",
			false, 10000, false, true, false, String.Empty, true, 1, -1, false, 310, new GameBuild("zerohour", 0, 0, EKnownAnticheatID.NONE), 2, type);
		lobby.SetAllowArmySelection(allowArmySelection);
		return lobby;
	}

	private static Task<LobbyLive> Capture(Lobby lobby) => lobby.RunExclusiveAsync(() => Task.FromResult(lobby.CaptureLiveLocked()));

	[Theory]
	[InlineData(0)]
	[InlineData(2)]
	[InlineData(ClientFeatures.ObserverSide)]
	public void ALockedQuickMatchLobbyRefusesAnySideButRandom(int side)
	{
		Failure? failure = RealtimeLobbies.RefuseArmySelection(NewLobby(ELobbyType.QuickMatch, allowArmySelection: false), [side]);

		Assert.NotNull(failure);
		Assert.Equal("lobby.army_selection_disabled", failure!.Value.Code);
	}

	[Fact]
	public void ALockedQuickMatchLobbyTakesRandomOrNoSide()
	{
		Lobby lobby = NewLobby(ELobbyType.QuickMatch, allowArmySelection: false);

		Assert.Null(RealtimeLobbies.RefuseArmySelection(lobby, [-1]));
		Assert.Null(RealtimeLobbies.RefuseArmySelection(lobby, [null]));
		Assert.Null(RealtimeLobbies.RefuseArmySelection(lobby, []));
	}

	[Fact]
	public void OneChosenSideAmongSlotPatchesRefusesTheWholeUpdate()
	{
		Lobby lobby = NewLobby(ELobbyType.QuickMatch, allowArmySelection: false);

		Assert.NotNull(RealtimeLobbies.RefuseArmySelection(lobby, [-1, null, 3]));
	}

	[Fact]
	public void AQuickMatchLobbyThatAllowsArmySelectionTakesAnySide()
	{
		Assert.Null(RealtimeLobbies.RefuseArmySelection(NewLobby(ELobbyType.QuickMatch, allowArmySelection: true), [2]));
	}

	[Fact]
	public void ACustomLobbyAlwaysTakesAnySide()
	{
		Assert.Null(RealtimeLobbies.RefuseArmySelection(NewLobby(ELobbyType.CustomGame, allowArmySelection: false), [2]));
	}

	[Fact]
	public async Task TheLockSurvivesAFailoverRestore()
	{
		LobbyLive stored = await Capture(NewLobby(ELobbyType.QuickMatch, allowArmySelection: false));
		Assert.False(stored.AllowArmySelection);

		Lobby restored = Lobby.FromLive(JsonSerializer.Deserialize<LobbyLive>(JsonSerializer.Serialize(stored, LiveJson.Options), LiveJson.Options)!);

		Assert.False(restored.AllowArmySelection);
		Assert.NotNull(RealtimeLobbies.RefuseArmySelection(restored, [2]));
	}

	[Fact]
	public async Task ACaptureOfAnOlderCopyRestoresWithArmySelectionAllowed()
	{
		JsonObject older = JsonSerializer.SerializeToNode(await Capture(NewLobby(ELobbyType.QuickMatch, allowArmySelection: false)), LiveJson.Options)!.AsObject();
		Assert.True(older.Remove(older.Select(entry => entry.Key).Single(key => key.Equals("allowArmySelection", StringComparison.OrdinalIgnoreCase) || key == "allow_army_selection")));

		Lobby restored = Lobby.FromLive(older.Deserialize<LobbyLive>(LiveJson.Options)!);

		Assert.True(restored.AllowArmySelection);
	}
}

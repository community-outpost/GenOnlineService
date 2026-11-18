using GenOnlineService.Realtime;
using Xunit;

namespace GenOnlineService.Tests;

// The playlist sets the map, the starting cash and the superweapon limit of a quick match lobby, so nobody changes them
public class QuickMatchLobbySettingsTests
{
	private static UserSession Session(long userID) => new(userID, EUserSessionType.GameClient, "genonline_zerohour", "EU", "NL", 0, 0);

	private static Lobby NewLobby(ELobbyType type)
	{
		Lobby lobby = new(9301, Session(1), "Settings lobby", ELobbyState.GAME_SETUP, "Tournament Desert", "maps/tournament desert/tournament desert",
			false, 10000, false, true, false, String.Empty, true, 1, -1, false, 310, new GameBuild("zerohour", 0, 0, EKnownAnticheatID.NONE), 2, type);
		lobby.SetQuickMatchPlaylist(type == ELobbyType.QuickMatch ? 3 : 0, false);
		return lobby;
	}

	private static readonly UpdateLobbyMap s_map = new() { Name = "Winter Wolf", Path = "maps/winter wolf/winter wolf", Official = true, MaxPlayers = 2 };

	public static TheoryData<string> LockedChanges() => ["map", "starting_cash", "limit_superweapons"];

	private static UpdateLobbyRequest RequestOf(string change) => change switch
	{
		"map" => new UpdateLobbyRequest { Map = s_map },
		"starting_cash" => new UpdateLobbyRequest { Settings = new UpdateLobbySettings { StartingCash = 20000 } },
		_ => new UpdateLobbyRequest { Settings = new UpdateLobbySettings { LimitSuperweapons = true } },
	};

	[Theory]
	[MemberData(nameof(LockedChanges))]
	public void AQuickMatchLobbyRefusesThePlaylistSettings(string change)
	{
		Failure? failure = RealtimeLobbies.RefuseQuickMatchSettings(NewLobby(ELobbyType.QuickMatch), RequestOf(change));

		Assert.NotNull(failure);
		Assert.Equal("lobby.not_host", failure!.Value.Code);
	}

	[Theory]
	[MemberData(nameof(LockedChanges))]
	public void ACustomLobbyTakesThePlaylistSettings(string change)
	{
		Assert.Null(RealtimeLobbies.RefuseQuickMatchSettings(NewLobby(ELobbyType.CustomGame), RequestOf(change)));
	}

	[Fact]
	public void AQuickMatchLobbyKeepsTheOtherSettings()
	{
		Lobby lobby = NewLobby(ELobbyType.QuickMatch);

		Assert.Null(RealtimeLobbies.RefuseQuickMatchSettings(lobby, new UpdateLobbyRequest { Settings = new UpdateLobbySettings { MaxCameraHeight = 400 } }));
		Assert.Null(RealtimeLobbies.RefuseQuickMatchSettings(lobby, new UpdateLobbyRequest { Visibility = LobbyVisibility.Friends }));
		Assert.Null(RealtimeLobbies.RefuseQuickMatchSettings(lobby, new UpdateLobbyRequest()));
	}

	[Fact]
	public void TheReviewRowNamesTheRefusedMethodWithoutSides()
	{
		AnticheatQuickMatchReport row = RealtimeLobbies.QuickMatchRuleReportOf(NewLobby(ELobbyType.QuickMatch), 7, "UpdateLobby", [], DateTime.UnixEpoch);

		Assert.Equal("UpdateLobby", row.Method);
		Assert.Equal("", row.Sides);
		Assert.Equal(3, row.PlaylistId);
	}
}

using GenOnlineService.Realtime;
using Xunit;

namespace GenOnlineService.Tests;

// A lobby shows names alone; while two members share a name both show Name#tag, and the tag goes when one leaves
public class LobbyNameProbeTests
{
	// joining a lobby leaves the chat room, which needs the room catalog the service loads at startup
	static LobbyNameProbeTests()
	{
		string root = AppContext.BaseDirectory;
		while (!File.Exists(Path.Combine(root, "GenServices.sln")))
		{
			root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found");
		}
		RoomCatalog.Initialize(Path.Combine(root, "GenOnlineService", "data", "rooms.json"));
	}

	private static UserSession Session(long userID) => new(userID, EUserSessionType.GameClient, "genonline_zerohour", "EU", "NL", 0, 0);

	private static Lobby NewLobby(UserSession owner) => new(9001, owner, "Probe lobby", ELobbyState.GAME_SETUP, "Tournament Desert", "maps/tournament desert/tournament desert",
		false, 10000, false, true, false, String.Empty, true, 1, -1, false, 310, new GameBuild("zerohour", 0, 0, EKnownAnticheatID.NONE), 4, ELobbyType.CustomGame);

	private static string? ShownName(Lobby lobby, long userID)
	{
		LobbyMember member = lobby.Members.Single(m => m.IsHuman() && m.UserID == userID);
		string? slotName = RealtimeLobbies.SlotOf(member).Name;
		Assert.Equal(member.DisplayName, slotName);
		return slotName;
	}

	[Fact]
	public async Task BothMembersWithOneNameShowTheirTagsUntilOneLeaves()
	{
		UserSession bob = Session(1), otherBob = Session(2), ann = Session(3);
		Lobby lobby = NewLobby(bob);

		await lobby.AddCreatorAsync(bob, "Bob", 42, 8088, new UserLobbyPreferences());
		await lobby.AddCreatorAsync(ann, "Ann", 5, 8088, new UserLobbyPreferences());
		Assert.Equal("Bob", ShownName(lobby, 1));
		Assert.Equal("Ann", ShownName(lobby, 3));

		// the same name in other case: both get their tag, the member already there included
		await lobby.AddCreatorAsync(otherBob, "BOB", 7, 8088, new UserLobbyPreferences());
		Assert.Equal("Bob#0042", ShownName(lobby, 1));
		Assert.Equal("BOB#0007", ShownName(lobby, 2));
		Assert.Equal("Ann", ShownName(lobby, 3));

		await lobby.RemoveMemberAsync(2);
		Assert.Equal("Bob", ShownName(lobby, 1));
		Assert.Equal("Ann", ShownName(lobby, 3));
	}

	[Fact]
	public async Task AiSlotsNeverTagAHumanName()
	{
		UserSession bob = Session(11);
		Lobby lobby = NewLobby(bob);
		await lobby.AddCreatorAsync(bob, "Bob", 42, 8088, new UserLobbyPreferences());

		Assert.Equal("Bob", ShownName(lobby, 11));
		Assert.All(lobby.Members.Where(m => !m.IsHuman()), m => Assert.Null(RealtimeLobbies.SlotOf(m).Name));
	}
}

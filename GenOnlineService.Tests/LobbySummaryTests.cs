using GenOnlineService.Realtime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests;

// The lobby list carries the map path LobbyState sends, so a joiner can tell JoinLobby whether it has the map
public class LobbySummaryTests
{
	private static readonly JsonSerializerOptions s_options = RealtimeJson.Create();

	private static UserSession Session(long userID) => new(userID, EUserSessionType.GameClient, "genonline_zerohour", "EU", "NL", 0, 0);

	private static Lobby NewLobby(string mapPath) => new(9101, Session(1), "Summary lobby", ELobbyState.GAME_SETUP, "Whiteout", mapPath,
		false, 10000, false, true, false, String.Empty, false, 1, -1, false, 310, new GameBuild("zerohour", 0, 0, EKnownAnticheatID.NONE), 4, ELobbyType.CustomGame);

	[Fact]
	public void TheSummaryMapPathIsTheLobbyStateMapPath()
	{
		Lobby lobby = NewLobby("whiteout.map");

		LobbySummary summary = RealtimeLobbies.SummaryOf(lobby, null);

		Assert.Equal("whiteout.map", summary.Map.Path);
		Assert.Equal(RealtimeLobbies.StateOf(lobby).Map.Path, summary.Map.Path);
	}

	[Fact]
	public void TheSummarySendsThePathInItsMap()
	{
		Lobby lobby = NewLobby("maps/tournament desert/tournament desert");

		JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(RealtimeLobbies.SummaryOf(lobby, null), s_options))!;

		Assert.Equal("maps/tournament desert/tournament desert", (string?)json["map"]!["path"]);
		Assert.Equal("Whiteout", (string?)json["map"]!["name"]);
		Assert.False((bool)json["map"]!["official"]!);
	}

	[Fact]
	public void ASummaryWithoutAPathStillReadsAndWritesWithoutOne()
	{
		const string older = "{\"name\":\"Tournament Desert\",\"official\":true}";

		LobbySummaryMap map = JsonSerializer.Deserialize<LobbySummaryMap>(older, s_options)!;

		Assert.Null(map.Path);
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse(older), JsonNode.Parse(JsonSerializer.Serialize(map, s_options))));
	}
}

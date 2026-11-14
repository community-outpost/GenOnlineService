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

namespace GenOnlineService.Realtime
{
	// Every server event, named as on the wire
	public interface IRealtimeClient
	{
		Task Ready(ReadyEvent e);
		Task BadgesUpdate(BadgesUpdateEvent e);
		Task Notice(NoticeEvent e);
		Task ModerationNotice(ModerationNoticeEvent e);

		Task ChannelMembers(ChannelMembersEvent e);
		Task MemberAdd(MemberAddEvent e);
		Task MemberRemove(MemberRemoveEvent e);
		Task MemberUpdate(MemberUpdateEvent e);
		Task ChannelHistory(ChannelHistoryEvent e);
		Task MessageCreate(Message e);
		Task MessageDelete(MessageRefEvent e);
		Task Conversations(ConversationsEvent e);
		Task ReadMarker(MessageRefEvent e);
		Task GroupCreate(GroupEvent e);
		Task GroupUpdate(GroupEvent e);
		Task GroupRemoved(GroupRemovedEvent e);

		Task LobbyListUpsert(LobbyListUpsertEvent e);
		Task LobbyListRemove(LobbyIdEvent e);
		Task LobbyState(LobbyStateEvent e);
		Task LobbyLeft(LobbyLeftEvent e);
		Task CountdownStarted(CountdownStartedEvent e);
		Task CountdownCancelled(CountdownCancelledEvent e);
		Task GameStart(GameStartEvent e);

		Task MatchmakingStatus(MatchmakingStatusEvent e);
		Task MatchFound(MatchFoundEvent e);
		Task MatchSetupProgress(MatchSetupProgressEvent e);
		Task MatchStart(LobbyIdEvent e);
		Task MatchRequeue(MatchRequeueEvent e);

		Task Friends(FriendsEvent e);
		Task FriendRequestReceived(FriendRequestReceivedEvent e);
		Task FriendRequestAccepted(FriendRequestAcceptedEvent e);
		Task FriendRemoved(UserIdEvent e);
		Task FriendPresence(FriendPresenceEvent e);

		Task StartSignalling(StartSignallingEvent e);
		Task DisconnectPeer(DisconnectPeerEvent e);
		Task Signal(RelayEvent e);
		Task MeshCheckRequest(MeshCheckRequestEvent e);
		Task MeshCheckResult(MeshCheckResultEvent e);
		Task AnticheatRegister(AnticheatPeerEvent e);
		Task AnticheatDeregister(AnticheatPeerEvent e);
		Task AnticheatMessage(RelayEvent e);
		Task ProbeRequest(ProbeRequestEvent e);
		Task ModuleScanRequest(ModuleScanRequestEvent e);
	}
}

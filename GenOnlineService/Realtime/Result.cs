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

using System.Text.Json.Serialization;

namespace GenOnlineService.Realtime
{
	// The answer to every hub method: {ok, data} or {ok:false, code, params}
	public sealed class Result<T> where T : class
	{
		public bool Ok { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public T? Data { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Code { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public object? Params { get; init; }

		public static implicit operator Result<T>(Failure failure) => new Result<T> { Ok = false, Code = failure.Code, Params = failure.Params };
	}

	public readonly record struct Failure(string Code, object Params);

	public static class Result
	{
		public static Result<Empty> Success() => new Result<Empty> { Ok = true, Data = Empty.Instance };

		public static Result<T> Success<T>(T data) where T : class => new Result<T> { Ok = true, Data = data };

		public static Failure Fail(string code, object? parameters = null) => new Failure(code, parameters ?? Empty.Instance);

		public static Failure Forbidden(string permission) => Fail(ErrorCodes.Forbidden, new { Permission = permission });

		public static Failure Invalid(string field) => Fail(ErrorCodes.InvalidArgument, new { Field = field });

		public static Failure NotFound(string what) => Fail(ErrorCodes.NotFound, new { What = what });
	}

	// Stable codes from section 7 of the protocol
	public static class ErrorCodes
	{
		public const string Forbidden = "forbidden";
		public const string SessionNotRegistered = "session.not_registered";
		public const string ModerationBanned = "moderation.banned";
		public const string InvalidArgument = "invalid_argument";
		public const string NotFound = "not_found";
		public const string RateLimited = "rate_limited";
		public const string ChatRateLimited = "chat.rate_limited";
		public const string ChatTooLong = "chat.too_long";
		public const string ChatMuted = "chat.muted";
		public const string ChatNotFriends = "chat.not_friends";
		public const string ReportDuplicate = "report.duplicate";
		public const string MatchResultClosed = "match.result_closed";
		public const string GroupFull = "group.full";
		public const string GroupNotOwner = "group.not_owner";
		public const string GroupNotMember = "group.not_member";
		public const string NameLength = "name.length";
		public const string NameInvalidCharacters = "name.invalid_characters";
		public const string NameWhitespace = "name.whitespace";
		public const string NameReserved = "name.reserved";
		public const string NameTaken = "name.taken";
		public const string NameChanged = "name.changed";
		public const string ServerAnnouncement = "server.announcement";
		public const string ChannelUnavailable = "channel.unavailable";
		public const string LobbyFull = "lobby.full";
		public const string LobbyWrongPassword = "lobby.wrong_password";
		public const string LobbyNotHost = "lobby.not_host";
		public const string LobbyNotMember = "lobby.not_member";
		public const string LobbyAlreadyStarted = "lobby.already_started";
		public const string LobbyNotInSetup = "lobby.not_in_setup";
		public const string LobbyAlreadyMember = "lobby.already_member";
		public const string LobbyBlocked = "lobby.blocked";
		public const string LobbyFriendsOnly = "lobby.friends_only";
		public const string LobbyNotMatched = "lobby.not_matched";
		public const string LobbyAnticheatMismatch = "lobby.anticheat_mismatch";
		public const string LobbyBuildMismatch = "lobby.build_mismatch";
		public const string LobbySlotOccupied = "lobby.slot_occupied";
		public const string LobbyNotReady = "lobby.not_ready";
		public const string LobbyMeshIncomplete = "lobby.mesh_incomplete";
		public const string MatchmakingNotQueued = "matchmaking.not_queued";
		public const string MatchmakingAlreadyQueued = "matchmaking.already_queued";
		public const string MatchmakingInvalidPlaylist = "matchmaking.invalid_playlist";
		public const string MatchmakingTooFewMaps = "matchmaking.too_few_maps";
		public const string MatchmakingStatsUnavailable = "matchmaking.stats_unavailable";
		public const string SocialFriendListFull = "social.friend_list_full";
		public const string SocialBlocked = "social.blocked";
		public const string ModerationTargetOffline = "moderation.target_offline";
		public const string ModerationReasonTooLong = "moderation.reason_too_long";
		public const string ModerationTargetOutranks = "moderation.target_outranks";
	}

	// Close codes from section 3 of the protocol
	public static class CloseCodes
	{
		public const string SessionSuperseded = "session.superseded";
		public const string AuthRevoked = "auth.revoked";
		public const string ModerationKicked = "moderation.kicked";
		public const string ModerationBanned = "moderation.banned";
		public const string ServerRestarting = "server.restarting";
		public const string ServerSwitching = "server.switching";
		public const string ProtocolError = "protocol.error";
	}
}

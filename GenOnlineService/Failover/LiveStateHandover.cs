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

namespace GenOnlineService.Failover
{
	// The players a copy hands to another copy: ServerSwitching, a server.switching close and the end of the session here
	public interface ISessionSwitcher
	{
		// kept: the target keeps a seat or a ticket for them, so they keep their relay credentials and friends see no change.
		// keepRelay alone: another copy holds their entity, so its running match keeps its relay credentials, presence is published as usual.
		Task SwitchSessionsAsync(IReadOnlyCollection<UserSession> sessions, bool kept, bool keepRelay = false);
	}

	// Build step 4 on the copy that owns the state: the planned moves of the queue and the lobbies, and what happens to an entity another copy takes
	public sealed class LiveStateHandover : IHandoverMover
	{
		private static readonly ILogger s_log = AppLog.For(typeof(LiveStateHandover));

		// How often the move loop looks for a quiet lobby
		public const int QuietPollMs = 200;

		private readonly LiveStateWriter m_writer;
		private readonly ILiveStateStore m_store;
		private readonly LobbyManager m_lobbies;
		private readonly ISessionSwitcher m_switcher;
		private readonly int m_adoptTimeoutMs;
		private IReadOnlyDictionary<string, string>? m_takenBack;
		private volatile string? m_target;

		// Leaves of members of lobbies that are moving away, applied here if the lobby stays or comes back
		private readonly System.Collections.Concurrent.ConcurrentDictionary<long, System.Collections.Concurrent.ConcurrentDictionary<long, byte>> m_localLeaves = new();

		// Leaves noted for lobbies not taken yet on this copy, applied when the lobby arrives
		private readonly System.Collections.Concurrent.ConcurrentDictionary<long, System.Collections.Concurrent.ConcurrentDictionary<long, byte>> m_pendingLeaves = new();
		private string? m_takenBackFrom;

		public LiveStateHandover(LiveStateWriter writer, ILiveStateStore store, LobbyManager lobbies, ISessionSwitcher switcher, int adoptTimeoutMs)
		{
			m_writer = writer;
			m_store = store;
			m_lobbies = lobbies;
			m_switcher = switcher;
			m_adoptTimeoutMs = adoptTimeoutMs;
		}

		// The target took the entity: it rewrote it under its own name, or deleted it because it does not keep that kind
		private enum Taken { No, Kept, Dropped }

		private async Task<bool> WaitForTargetAsync(string key, string target) => await WaitForTakenAsync(key, target) != Taken.No;

		private async Task<Taken> WaitForTakenAsync(string key, string target)
		{
			long until = Environment.TickCount64 + m_adoptTimeoutMs;
			while (Environment.TickCount64 < until)
			{
				try
				{
					string? stored = await m_store.ReadAsync(key);
					if (stored == null)
					{
						return Taken.Dropped;
					}
					if (LiveJson.ReadEnvelope(stored)?.WrittenBy == target)
					{
						return Taken.Kept;
					}
				}
				catch (Exception ex)
				{
					s_log.LogWarning(ex, "Reading {Key} during the handover failed", key);
				}
				await Task.Delay(50);
			}
			return Taken.No;
		}

		// The queue first: no new matches here, the queue goes to the target with the next owner epoch, then the queued players follow it.
		// Then the lobbies one by one when quiet, after the limit all of them a few at a time, each with its members together, the doc's order before the batches.
		// A player queued here after the queue left (a requeue or a raced JoinQueue) follows with a ticket of its own. True when this copy owns nothing more.
		public async Task<bool> MoveAsync(string target, long limitAtMs, Func<long> clockMs, Func<Task<bool>> targetAlive)
		{
			m_target = target;
			try
			{
				return await MoveAllAsync(target, limitAtMs, clockMs, targetAlive);
			}
			finally
			{
				m_target = null;
			}
		}

		// A member of a lobby that is moving leaves here (a kick, ban, logout, supersede or expiry): the frozen lobby stays as captured
		// and the leave goes to the target as a note, which removes the seat there
		public void OnMovingLeave(long lobbyID, long userID)
		{
			m_localLeaves.GetOrAdd(lobbyID, _ => new())[userID] = 0;
			string? target = m_target;
			if (target == null)
			{
				s_log.LogInformation("User {UserId} left frozen lobby {LobbyId}, the leave applies when the lobby is back", userID, lobbyID);
				return;
			}
			s_log.LogInformation("User {UserId} left moving lobby {LobbyId}, the leave goes to {Target}", userID, lobbyID, target);
			_ = m_writer.TransferAsync(LiveKeys.Leave(lobbyID, userID), LiveJson.Data(new LobbyLeaveLive { LobbyId = lobbyID, UserId = userID }), target);
		}

		// A lobby that stayed here or came back applies the leaves noted while it was frozen, through the normal membership removal
		private void ApplyLocalLeaves(long lobbyID)
		{
			if (!m_localLeaves.TryRemove(lobbyID, out var leaves))
			{
				return;
			}
			_ = Task.Run(async () =>
			{
				foreach (long userID in leaves.Keys)
				{
					try
					{
						await m_lobbies.LeaveAsync(new LeaveLobbyCommand(lobbyID, userID, EUserSessionType.GameClient));
						s_log.LogInformation("User {UserId} left lobby {LobbyId} after its move did not happen", userID, lobbyID);
					}
					catch (Exception ex)
					{
						s_log.LogError(ex, "Applying the leave of user {UserId} to lobby {LobbyId} failed", userID, lobbyID);
					}
				}
			});
		}

		// The test seam for the leaves noted here
		internal bool HasLocalLeaves(long lobbyID) => m_localLeaves.ContainsKey(lobbyID);

		private async Task<bool> MoveAllAsync(string target, long limitAtMs, Func<long> clockMs, Func<Task<bool>> targetAlive)
		{
			long started = clockMs();
			if (m_writer.Owns(LiveKeys.Queue))
			{
				MatchmakingManager.HoldFormation = true;
				string queue = LiveJson.Data(MatchmakingManager.CaptureQueue());
				if (!await m_writer.TransferAsync(LiveKeys.Queue, queue, target) || !await WaitForTargetAsync(LiveKeys.Queue, target))
				{
					s_log.LogWarning("The matchmaking queue did not move to {Target}", target);
					return false;
				}
				IReadOnlyCollection<(UserSession Session, QueueTicketLive Ticket)> queued = await MatchmakingManager.ReleaseQueueAsync();
				s_log.LogInformation("The matchmaking queue moved to {Target}, {Count} queued players follow it", target, queued.Count);
				await m_switcher.SwitchSessionsAsync(queued.Select(entry => entry.Session).ToArray(), kept: true);
			}

			int moved = 0;
			while (true)
			{
				if (!await targetAlive() || !await MoveLateTicketsAsync(target))
				{
					return false;
				}

				bool late = clockMs() >= limitAtMs;
				List<Lobby> left = m_lobbies.GetLobbies().Where(lobby => !lobby.IsMovingAway && lobby.State != ELobbyState.COMPLETE).ToList();
				if (left.Count == 0)
				{
					s_log.LogInformation("{Count} lobbies moved to {Target} in {ElapsedMs} ms", moved, target, clockMs() - started);
					return await MoveLateTicketsAsync(target);
				}

				if (late)
				{
					// past the limit every lobby moves, a bounded number at once so the tail fits the handover budget
					using SemaphoreSlim slots = new(FailoverSettings.ForcedMoveConcurrency);
					bool[] results = await Task.WhenAll(left.Select(async lobby =>
					{
						await slots.WaitAsync();
						try
						{
							LobbyLive? live = await lobby.BeginMoveAsync(force: true);
							return live == null || await MoveLobbyAsync(lobby, LiveJson.Data(live), target);
						}
						finally
						{
							slots.Release();
						}
					}));
					moved += left.Count;
					if (results.Any(ok => !ok))
					{
						return false;
					}
					await Task.Delay(50);
					continue;
				}

				bool any = false;
				foreach (Lobby lobby in left)
				{
					LobbyLive? live = await lobby.BeginMoveAsync(force: false);
					if (live == null)
					{
						continue;
					}
					if (!await MoveLobbyAsync(lobby, LiveJson.Data(live), target))
					{
						return false;
					}
					any = true;
					++moved;
				}
				if (!any)
				{
					await Task.Delay(QuietPollMs);
				}
			}
		}

		// Each player queued here after the queue left gets a ticket entity handed to the target, and switches once the target took it
		private async Task<bool> MoveLateTicketsAsync(string target)
		{
			IReadOnlyCollection<(UserSession Session, QueueTicketLive Ticket)> late = await MatchmakingManager.ReleaseQueueAsync();
			foreach ((UserSession session, QueueTicketLive ticket) in late)
			{
				string key = LiveKeys.Ticket(ticket.UserId);
				if (!await m_writer.TransferAsync(key, LiveJson.Data(ticket), target) || !await WaitForTargetAsync(key, target))
				{
					s_log.LogWarning("The ticket of user {UserId} did not move to {Target}", ticket.UserId, target);
					return false;
				}
				await m_switcher.SwitchSessionsAsync(new[] { session }, kept: true);
			}
			return true;
		}

		// The final state goes to the target, the target takes it from its mirror, then the members switch together
		private async Task<bool> MoveLobbyAsync(Lobby lobby, string json, string target)
		{
			string key = LiveKeys.Lobby(lobby.LobbyID);
			if (!await m_writer.TransferAsync(key, json, target))
			{
				lobby.CancelMove();
				ApplyLocalLeaves(lobby.LobbyID);
				s_log.LogWarning("Lobby {LobbyId} could not be handed to {Target}", lobby.LobbyID, target);
				return false;
			}
			Taken taken = await WaitForTakenAsync(key, target);
			if (taken == Taken.No)
			{
				// the transfer stands, a cancel takes the lobby back with the next owner epoch and keeps the members here
				s_log.LogWarning("Lobby {LobbyId} was handed to {Target}, which did not take it", lobby.LobbyID, target);
				return false;
			}

			// a lobby the target did not keep, a quick match setup, leaves its members without a seat there, they publish offline as usual
			IReadOnlyCollection<UserSession> members = lobby.MemberSessions();
			m_lobbies.RemoveMovedLobby(lobby);
			m_localLeaves.TryRemove(lobby.LobbyID, out _);
			s_log.LogInformation("Lobby {LobbyId} moved to {Target} in state {State}, {Count} members follow it, kept {Kept}", lobby.LobbyID, target, lobby.State, members.Count, taken == Taken.Kept);
			AppMetrics.RecordFailoverLobbiesMoved(1);
			await m_switcher.SwitchSessionsAsync(members, kept: taken == Taken.Kept);
			return true;
		}

		// Before a cancel, the entities the target owns now, read from Redis since this copy follows no stream while it holds the lease
		public async Task PrepareTakeBackAsync(string target)
		{
			LiveSnapshot snapshot = await m_store.LoadAsync();
			m_takenBack = snapshot.Entities
				.Where(entry => LiveJson.ReadEnvelope(entry.Value)?.Owner == target)
				.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
			m_takenBackFrom = target;
		}

		// The activation of this copy, with what a cancelled handover takes back
		public void OnActivating(FailoverActivation activation, long epoch)
		{
			IReadOnlyDictionary<string, string>? takenBack = activation == FailoverActivation.TakenBack ? m_takenBack : null;
			string? from = m_takenBackFrom;
			m_takenBack = null;
			m_takenBackFrom = null;
			LiveStateTakeover.OnActivating(activation, epoch, m_lobbies, m_writer, takenBack, from);

			// the handover is over: which lobbies left no longer matters, notes for lobbies that never came are dropped,
			// and a lobby that stayed or came back applies the leaves noted while it was frozen
			m_lobbies.ClearMovedAway();
			m_pendingLeaves.Clear();
			foreach (long lobbyID in m_localLeaves.Keys.ToArray())
			{
				Lobby? lobby = m_lobbies.GetLobby(lobbyID);
				if (lobby == null)
				{
					m_localLeaves.TryRemove(lobbyID, out _);
				}
				else if (!lobby.IsMovingAway)
				{
					ApplyLocalLeaves(lobbyID);
				}
			}
		}

		// An entity whose write Redis refused belongs to another copy: this copy drops it and closes the connections of its members
		public void OnRejected(string key)
		{
			_ = Task.Run(async () =>
			{
				IReadOnlyCollection<UserSession> sessions;
				if (key == LiveKeys.Queue)
				{
					MatchmakingManager.HoldFormation = true;
					sessions = (await MatchmakingManager.ReleaseQueueAsync()).Select(entry => entry.Session).ToArray();
				}
				else if (LiveKeys.IsLobby(key) && Int64.TryParse(key.AsSpan(LiveKeys.LobbyPrefix.Length), out long lobbyID))
				{
					sessions = m_lobbies.DropLostLobby(lobbyID);
				}
				else
				{
					return;
				}
				s_log.LogWarning("Copy {CopyId} dropped {Key}, a newer owner holds it, {Count} players reconnect", m_writer.Copy, key, sessions.Count);
				await m_switcher.SwitchSessionsAsync(sessions, kept: false, keepRelay: true);
			});
		}

		// A write naming this copy as the owner: a planned handover gave it the entity, it takes it from the mirror and writes it under its own name
		public void OnOwned(string key, string body, long epoch)
		{
			if (m_writer.Owns(key))
			{
				return;
			}
			LiveEnvelope? envelope = LiveJson.ReadEnvelope(body);
			if (envelope == null)
			{
				AppMetrics.RecordFailoverLiveState("handover_unreadable");
				return;
			}

			// a leave from a lobby during its move: the seat goes now, or when the lobby arrives
			if (LiveKeys.IsLeave(key))
			{
				LobbyLeaveLive? leave = LiveJson.ReadData<LobbyLeaveLive>(envelope);
				m_writer.Adopt(key, epoch);
				if (leave != null)
				{
					Lobby? arrived = m_lobbies.GetLobby(leave.LobbyId);
					if (arrived != null)
					{
						_ = arrived.DropRestoredMemberAsync(leave.UserId);
					}
					else
					{
						m_pendingLeaves.GetOrAdd(leave.LobbyId, _ => new())[leave.UserId] = 0;
					}
				}
				m_writer.Delete(key);
				return;
			}

			// a ticket of a player queued on the old copy after the queue left: it waits here like a restored ticket, the delete tells the old copy
			if (LiveKeys.IsTicket(key))
			{
				QueueTicketLive? ticket = LiveJson.ReadData<QueueTicketLive>(envelope);
				m_writer.Adopt(key, epoch);
				if (ticket != null)
				{
					MatchmakingManager.AddRestoredTicket(ticket, UserSessionSettings.ReconnectGracePeriodMS);
				}
				m_writer.Delete(key);
				return;
			}

			if (key == LiveKeys.Queue)
			{
				QueueLive? queue = LiveJson.ReadData<QueueLive>(envelope);
				m_writer.Adopt(key, epoch);
				int tickets = queue == null ? 0 : MatchmakingManager.RestoreQueue(queue, UserSessionSettings.ReconnectGracePeriodMS);
				MatchmakingManager.HoldFormation = false;
				MatchmakingManager.ForceLivePublish();

				// the handed state written under this copy's name tells the old owner the queue arrived
				m_writer.Set(key, LiveJson.Data(queue ?? new QueueLive()));
				s_log.LogInformation("Copy {CopyId} took the matchmaking queue from {From} with epoch {Epoch}, {Tickets} tickets wait for their players", m_writer.Copy, envelope.WrittenBy, epoch, tickets);
				return;
			}

			if (!LiveKeys.IsLobby(key))
			{
				return;
			}
			m_writer.Adopt(key, epoch);
			LobbyLive? live = LiveJson.ReadData<LobbyLive>(envelope);
			string? skip = live == null ? "unreadable" : Lobby.SkipReasonOf(live);
			try
			{
				Lobby? handed = skip == null ? Lobby.FromLive(live!) : null;
				if (handed != null && m_pendingLeaves.TryRemove(handed.LobbyID, out var leaves))
				{
					foreach (long userID in leaves.Keys)
					{
						handed.RemoveRestoredSeat(userID);
					}
				}
				if (handed != null && m_lobbies.AdoptRestoredLobby(handed, UserSessionSettings.ReconnectGracePeriodMS))
				{
					// the handed state written under this copy's name tells the old owner the lobby arrived, its tick writes changes after that
					m_writer.Set(key, LiveJson.Data(live!));
					s_log.LogInformation("Copy {CopyId} took lobby {LobbyId} from {From} with epoch {Epoch}", m_writer.Copy, live!.Id, envelope.WrittenBy, epoch);
					return;
				}
			}
			catch (Exception ex)
			{
				skip = ex.Message;
			}

			// a lobby this copy does not keep is deleted, which also tells the old owner it was taken, its noted leaves go with it
			if (live != null)
			{
				m_pendingLeaves.TryRemove(live.Id, out _);
			}
			s_log.LogInformation("Copy {CopyId} does not keep handed lobby {Key}: {Reason}", m_writer.Copy, key, skip);
			m_writer.Delete(key);
		}

		// A copy that stops owning anything drops its lobbies and its queue without finishing them, the new owner carries on
		public void OnFencing()
		{
			m_pendingLeaves.Clear();
			m_localLeaves.Clear();
			m_writer.ReleaseAll();
			m_lobbies.DropAllLocal();
			MatchmakingManager.DropAllLocal();
		}
	}
}

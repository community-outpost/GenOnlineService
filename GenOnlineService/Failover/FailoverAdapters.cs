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

using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Failover
{
	// Database.Lease with a fresh context per statement
	public sealed class DatabaseLeaseStore : ILeaseStore
	{
		private readonly IDbContextFactory<AppDbContext> m_factory;

		public DatabaseLeaseStore(IDbContextFactory<AppDbContext> factory)
		{
			m_factory = factory;
		}

		private async Task<T> With<T>(Func<AppDbContext, Task<T>> statement, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_factory.CreateDbContextAsync(cancellationToken);
			return await statement(db);
		}

		public Task<RealtimeLease?> ReadAsync(CancellationToken cancellationToken) =>
			With(db => Database.Lease.Read(db, cancellationToken), cancellationToken);

		public Task<long?> TryTakeAsync(string copyID, int leaseMs, CancellationToken cancellationToken) =>
			With(db => Database.Lease.TryTake(db, copyID, leaseMs, cancellationToken), cancellationToken);

		public Task<bool> RenewAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken) =>
			With(db => Database.Lease.Renew(db, copyID, epoch, leaseMs, cancellationToken), cancellationToken);

		public Task AnnounceStandbyAsync(string copyID, int leaseMs, CancellationToken cancellationToken) =>
			With(db => Database.Lease.AnnounceStandby(db, copyID, leaseMs, cancellationToken), cancellationToken);

		public Task<Database.Lease.Handover?> HandOverAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken) =>
			With(db => Database.Lease.HandOver(db, copyID, epoch, leaseMs, cancellationToken), cancellationToken);

		public Task<long?> ReclaimAsync(string copyID, Database.Lease.Handover handover, int leaseMs, CancellationToken cancellationToken) =>
			With(db => Database.Lease.Reclaim(db, copyID, handover, leaseMs, cancellationToken), cancellationToken);

		public Task ReleaseAsync(string copyID, long epoch, CancellationToken cancellationToken) =>
			With(db => Database.Lease.Release(db, copyID, epoch, cancellationToken), cancellationToken);

		public Task ClearHandoverRequestAsync(string copyID, long epoch, CancellationToken cancellationToken) =>
			With(async db => { await Database.Lease.ClearHandoverRequest(db, copyID, epoch, cancellationToken); return true; }, cancellationToken);
	}

	// The sessions of this copy, each close destroys the session as a kick does, so lobbies and presence end here
	public sealed class RealtimeConnectionDrain : IConnectionDrain
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeConnectionDrain));

		private static List<UserSession> Sessions() => WebSocketManager.GetUserDataCache().Values.SelectMany(byUser => byUser.Values).ToList();

		public int OpenConnections => Sessions().Count(s => WebSocketManager.GetWebSocketForSession(s)?.IsOpen == true);

		public async Task<int> CloseAllAsync(string closeCode)
		{
			List<UserSession> sessions = Sessions();
			await Task.WhenAll(sessions.Select(s => EndAsync(s, connection => connection.CloseAsync(closeCode, allowReconnect: true))));
			return sessions.Count;
		}

		public async Task<int> SwitchAsync(int max, Func<int> retryAfterMs)
		{
			List<(UserSession Session, RealtimeConnection Connection)> batch = Sessions()
				.Select(s => (Session: s, Connection: WebSocketManager.GetWebSocketForSession(s)))
				.Where(e => e.Connection?.IsOpen == true)
				.Take(max)
				.Select(e => (e.Session, e.Connection!))
				.ToList();

			await Task.WhenAll(batch.Select(e => EndAsync(e.Session, connection => connection.SwitchAsync(retryAfterMs()))));
			return batch.Count;
		}

		public async Task<int> EndIdleSessionsAsync()
		{
			List<UserSession> idle = Sessions().Where(s => WebSocketManager.GetWebSocketForSession(s)?.IsOpen != true).ToList();
			await Task.WhenAll(idle.Select(s => EndAsync(s, connection => connection.CloseAsync(CloseCodes.ServerSwitching, allowReconnect: true))));
			return idle.Count;
		}

		private static async Task EndAsync(UserSession session, Func<RealtimeConnection, Task> close)
		{
			try
			{
				RealtimeConnection? connection = WebSocketManager.GetWebSocketForSession(session);
				if (connection != null)
				{
					await close(connection);
				}
				await WebSocketManager.DeleteSession(session.m_UserID, session.GetSessionType(), connection, true, session);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Ending the session of user {UserId} ({SessionType}) failed", session.m_UserID, session.GetSessionType());
			}
		}
	}
}

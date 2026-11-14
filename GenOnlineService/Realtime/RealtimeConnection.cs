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
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using System.Collections.Concurrent;

namespace GenOnlineService
{
	// One hub connection bound to a user session; the session outlives it across reconnects
	public sealed class RealtimeConnection
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeConnection));

		private readonly HubConnectionContext m_context;
		private int m_closed;

		public RealtimeConnection(HubConnectionContext context, EUserSessionType sessionType, Int64 userID, UserSession ownerSession)
		{
			m_context = context;
			m_SessionType = sessionType;
			m_UserID = userID;
			OwnerSession = ownerSession;
		}

		public EUserSessionType m_SessionType { get; }
		public Int64 m_UserID { get; }
		internal UserSession OwnerSession { get; }
		public string ConnectionId => m_context.ConnectionId;
		internal bool IsOpen => Volatile.Read(ref m_closed) == 0 && !m_context.ConnectionAborted.IsCancellationRequested;

		// Events sent here reach only this connection
		public IRealtimeClient Client => RealtimeOut.Connection(ConnectionId);

		// Sends a close record with a protocol close code, then drops the transport
		public async Task CloseAsync(string code, bool allowReconnect)
		{
			if (Interlocked.Exchange(ref m_closed, 1) != 0)
			{
				return;
			}

			try
			{
				using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				await m_context.WriteAsync(new CloseMessage(code, allowReconnect), timeout.Token);
			}
			catch (Exception ex)
			{
				s_log.LogDebug(ex, "Close record not delivered to user {UserId}", m_UserID);
			}

			m_context.Abort();
		}

		public bool AbortIfOpen()
		{
			if (!IsOpen)
			{
				return false;
			}

			Interlocked.Exchange(ref m_closed, 1);
			m_context.Abort();
			return true;
		}
	}

	// Keeps the transport context of every live hub connection so the service can write close records
	public sealed class RealtimeLifetimeManager : DefaultHubLifetimeManager<RealtimeHub>
	{
		private static readonly ConcurrentDictionary<string, HubConnectionContext> s_connections = new(StringComparer.Ordinal);

		public RealtimeLifetimeManager(ILogger<DefaultHubLifetimeManager<RealtimeHub>> logger) : base(logger)
		{
		}

		public static HubConnectionContext? Get(string connectionId) => s_connections.TryGetValue(connectionId, out HubConnectionContext? context) ? context : null;

		public override Task OnConnectedAsync(HubConnectionContext connection)
		{
			s_connections[connection.ConnectionId] = connection;
			return base.OnConnectedAsync(connection);
		}

		public override Task OnDisconnectedAsync(HubConnectionContext connection)
		{
			s_connections.TryRemove(connection.ConnectionId, out _);
			return base.OnDisconnectedAsync(connection);
		}
	}
}

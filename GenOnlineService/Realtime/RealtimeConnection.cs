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

		// Sends the optional moderation notice and a close record with a protocol close code on this transport, then drops it
		public Task CloseAsync(string code, bool allowReconnect, ModerationNoticeEvent? notice = null) =>
			CloseAfterAsync(code, allowReconnect, notice == null ? null : new InvocationMessage(nameof(IRealtimeClient.ModerationNotice), new object?[] { notice }));

		// A planned handover: ServerSwitching with the reconnect delay, then the server.switching close record
		public Task SwitchAsync(int retryAfterMs) =>
			CloseAfterAsync(CloseCodes.ServerSwitching, allowReconnect: true, new InvocationMessage(nameof(IRealtimeClient.ServerSwitching), new object?[] { new ServerSwitchingEvent { RetryAfterMs = retryAfterMs } }));

		private async Task CloseAfterAsync(string code, bool allowReconnect, InvocationMessage? before)
		{
			if (Interlocked.Exchange(ref m_closed, 1) != 0)
			{
				return;
			}

			try
			{
				using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				if (before != null)
				{
					// written to the transport directly so the event always precedes the close record
					await m_context.WriteAsync(before, timeout.Token);
				}
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

	// Keeps the transport context of every live hub connection on this copy so the service can write close records, delivery goes to the wrapped manager (in memory or the Redis backplane)
	public sealed class RealtimeLifetimeManager : HubLifetimeManager<RealtimeHub>, IDisposable
	{
		private static readonly ConcurrentDictionary<string, HubConnectionContext> s_connections = new(StringComparer.Ordinal);

		private readonly HubLifetimeManager<RealtimeHub> m_inner;

		public RealtimeLifetimeManager(HubLifetimeManager<RealtimeHub> inner)
		{
			m_inner = inner;
		}

		public static HubConnectionContext? Get(string connectionId) => s_connections.TryGetValue(connectionId, out HubConnectionContext? context) ? context : null;

		public override Task OnConnectedAsync(HubConnectionContext connection)
		{
			s_connections[connection.ConnectionId] = connection;
			return m_inner.OnConnectedAsync(connection);
		}

		public override Task OnDisconnectedAsync(HubConnectionContext connection)
		{
			s_connections.TryRemove(connection.ConnectionId, out _);
			return m_inner.OnDisconnectedAsync(connection);
		}

		public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) => m_inner.SendAllAsync(methodName, args, cancellationToken);
		public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) => m_inner.SendAllExceptAsync(methodName, args, excludedConnectionIds, cancellationToken);
		public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default) => m_inner.SendConnectionAsync(connectionId, methodName, args, cancellationToken);
		public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default) => m_inner.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);
		public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) => m_inner.SendGroupAsync(groupName, methodName, args, cancellationToken);
		public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default) => m_inner.SendGroupsAsync(groupNames, methodName, args, cancellationToken);
		public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) => m_inner.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, cancellationToken);
		public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) => m_inner.SendUserAsync(userId, methodName, args, cancellationToken);
		public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default) => m_inner.SendUsersAsync(userIds, methodName, args, cancellationToken);
		public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => m_inner.AddToGroupAsync(connectionId, groupName, cancellationToken);
		public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => m_inner.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
		public override Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken) => m_inner.InvokeConnectionAsync<T>(connectionId, methodName, args, cancellationToken);
		public override Task SetConnectionResultAsync(string connectionId, CompletionMessage result) => m_inner.SetConnectionResultAsync(connectionId, result);
		public override bool TryGetReturnType(string invocationId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? type) => m_inner.TryGetReturnType(invocationId, out type);

		public void Dispose() => (m_inner as IDisposable)?.Dispose();
	}
}

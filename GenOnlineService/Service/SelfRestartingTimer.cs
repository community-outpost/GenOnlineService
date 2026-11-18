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

using System.Timers;

namespace GenOnlineService.Service
{
	// A background timer that runs one tick at a time and starts the next interval when a tick ends, until the stopping token fires.
	// Once it fires no tick starts and the timer is stopped and disposed. A tick already running may finish; an ObjectDisposedException
	// it hits then (the service provider is gone) is logged at Debug, any other failure at Error with failureMessage.
	public sealed class SelfRestartingTimer : IDisposable
	{
		private readonly System.Timers.Timer m_timer;
		private readonly CancellationToken m_stopping;
		private readonly ILogger m_logger;
		private readonly string m_failureMessage;
		private readonly Func<Task> m_tick;
		private CancellationTokenRegistration m_registration;
		private int m_stopped;

		private SelfRestartingTimer(double intervalMs, CancellationToken stopping, ILogger logger, string failureMessage, Func<Task> tick)
		{
			m_timer = new System.Timers.Timer(intervalMs) { AutoReset = false };
			m_stopping = stopping;
			m_logger = logger;
			m_failureMessage = failureMessage;
			m_tick = tick;
			m_timer.Elapsed += OnElapsed;
		}

		public static SelfRestartingTimer Start(double intervalMs, CancellationToken stopping, ILogger logger, string failureMessage, Func<Task> tick)
		{
			var timer = new SelfRestartingTimer(intervalMs, stopping, logger, failureMessage, tick);
			// runs at once when the token already fired
			timer.m_registration = stopping.Register(timer.Stop);
			timer.Restart();
			return timer;
		}

		public bool IsStopped => Volatile.Read(ref m_stopped) != 0;

		// An ObjectDisposedException after the service began stopping is the expected end of a tick, not a failure
		public static bool IsShutdownFault(Exception ex, CancellationToken stopping) => ex is ObjectDisposedException && stopping.IsCancellationRequested;

		private async void OnElapsed(object? sender, ElapsedEventArgs e)
		{
			if (m_stopping.IsCancellationRequested)
			{
				return;
			}

			try
			{
				await m_tick();
			}
			catch (Exception ex)
			{
				if (IsShutdownFault(ex, m_stopping))
				{
					m_logger.LogDebug(ex, m_failureMessage);
				}
				else
				{
					m_logger.LogError(ex, m_failureMessage);
				}
			}
			finally
			{
				Restart();
			}
		}

		private void Restart()
		{
			if (m_stopping.IsCancellationRequested || IsStopped)
			{
				return;
			}

			try
			{
				m_timer.Start();
			}
			catch (ObjectDisposedException)
			{
				// stopped between the check and the start
			}
		}

		private void Stop()
		{
			if (Interlocked.Exchange(ref m_stopped, 1) != 0)
			{
				return;
			}
			m_timer.Stop();
			m_timer.Dispose();
		}

		public void Dispose()
		{
			m_registration.Dispose();
			Stop();
		}
	}
}

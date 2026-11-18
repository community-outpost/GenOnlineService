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

using GenOnlineService.Service;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GenOnlineService.Tests;

// The background timers of Program.cs restart themselves after each tick until the service stops
public class SelfRestartingTimerTests
{
	private const double c_IntervalMs = 10;

	private static async Task WaitUntil(Func<bool> condition)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "the condition was not met in time");
			await Task.Delay(5);
		}
	}

	[Fact]
	public async Task AfterCancellationTheCallbackNoLongerRunsAndTheTimerIsNotRestarted()
	{
		ListLogger logger = new();
		using CancellationTokenSource stopping = new();
		int ticks = 0;
		using SelfRestartingTimer timer = SelfRestartingTimer.Start(c_IntervalMs, stopping.Token, logger, "Test tick timer failed", () =>
		{
			Interlocked.Increment(ref ticks);
			return Task.CompletedTask;
		});

		await WaitUntil(() => Volatile.Read(ref ticks) >= 3);
		stopping.Cancel();
		Assert.True(timer.IsStopped);

		// a tick that was already running may still finish, nothing starts after it
		await Task.Delay(100);
		int afterStop = Volatile.Read(ref ticks);
		await Task.Delay(300);
		Assert.Equal(afterStop, Volatile.Read(ref ticks));
		Assert.Empty(logger.Entries);
	}

	[Fact]
	public async Task ATickRunningWhenTheServiceStopsIsNotRestarted()
	{
		ListLogger logger = new();
		using CancellationTokenSource stopping = new();
		int ticks = 0;
		TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using SelfRestartingTimer timer = SelfRestartingTimer.Start(c_IntervalMs, stopping.Token, logger, "Test tick timer failed", () =>
		{
			Interlocked.Increment(ref ticks);
			stopping.Cancel();
			finished.TrySetResult();
			return Task.CompletedTask;
		});

		await finished.Task;
		await Task.Delay(300);
		Assert.Equal(1, Volatile.Read(ref ticks));
		Assert.True(timer.IsStopped);
		Assert.Empty(logger.Entries);
	}

	[Fact]
	public async Task AnObjectDisposedExceptionAfterCancellationIsNotLoggedAtError()
	{
		ListLogger logger = new();
		using CancellationTokenSource stopping = new();
		int ticks = 0;
		using SelfRestartingTimer timer = SelfRestartingTimer.Start(c_IntervalMs, stopping.Token, logger, "Lobby tick timer failed", () =>
		{
			Interlocked.Increment(ref ticks);
			stopping.Cancel();
			throw new ObjectDisposedException("IServiceProvider");
		});

		await WaitUntil(() =>
		{
			lock (logger.Entries)
			{
				return logger.Entries.Count > 0;
			}
		});
		await Task.Delay(300);
		Assert.Equal(1, Volatile.Read(ref ticks));
		var entry = Assert.Single(logger.Entries);
		Assert.Equal(LogLevel.Debug, entry.Level);
		Assert.IsType<ObjectDisposedException>(entry.Exception);
		Assert.Equal("Lobby tick timer failed", entry.Message);
	}

	[Fact]
	public async Task ABackgroundJobThatHitsAnObjectDisposedExceptionWhileStoppingIsNotLoggedAtError()
	{
		ListLogger logger = new();
		using CancellationTokenSource stopping = new();
		stopping.Cancel();
		await Program.RunObservedBackgroundJobAsync("service_cleanup", logger, () => throw new ObjectDisposedException("IServiceProvider"), stopping.Token);
		var entry = Assert.Single(logger.Entries);
		Assert.Equal(LogLevel.Debug, entry.Level);
		Assert.Equal("Background job service_cleanup failed", entry.Message);

		// outside a shutdown the same exception is a real failure
		logger.Entries.Clear();
		using CancellationTokenSource running = new();
		await Program.RunObservedBackgroundJobAsync("service_cleanup", logger, () => throw new ObjectDisposedException("IServiceProvider"), running.Token);
		Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
	}

	[Fact]
	public async Task AnExceptionBeforeCancellationIsLoggedAtErrorAndTheTimerKeepsRunning()
	{
		ListLogger logger = new();
		using CancellationTokenSource stopping = new();
		int ticks = 0;
		using SelfRestartingTimer timer = SelfRestartingTimer.Start(c_IntervalMs, stopping.Token, logger, "Lobby tick timer failed", () =>
		{
			// an ObjectDisposedException outside a shutdown is an ordinary failure too
			throw Interlocked.Increment(ref ticks) % 2 == 0 ? new ObjectDisposedException("IServiceProvider") : new InvalidOperationException("tick failed");
		});

		await WaitUntil(() =>
		{
			lock (logger.Entries)
			{
				return logger.Entries.Count >= 4;
			}
		});
		Assert.False(timer.IsStopped);
		stopping.Cancel();
		await Task.Delay(100);

		List<(LogLevel Level, string Message, Exception? Exception)> entries;
		lock (logger.Entries)
		{
			entries = logger.Entries.ToList();
		}
		Assert.True(entries.Count >= 4);
		Assert.All(entries.Take(4), entry =>
		{
			Assert.Equal(LogLevel.Error, entry.Level);
			Assert.Equal("Lobby tick timer failed", entry.Message);
		});
		Assert.Contains(entries, entry => entry.Exception is InvalidOperationException);
		Assert.Contains(entries, entry => entry.Exception is ObjectDisposedException);
	}
}

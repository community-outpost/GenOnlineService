/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GenOnlineService
{
    public static class AppMetrics
    {
        public const string MeterName = "GenOnlineService";
        public const string ActivitySourceName = MeterName;
        public const string MySqlActivitySourceName = "MySqlConnector";
        public const string MySqlMeterName = "MySqlConnector";
        public const string NpgsqlActivitySourceName = "Npgsql";
        public const string NpgsqlMeterName = "Npgsql";

        private static readonly Meter s_meter = new(MeterName);
        private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

        private static int s_onlinePlayers;
        private static int s_activeLobbies;
        private static int s_webSocketSendQueueDepth;
        private static double s_externalPublicationBacklogAgeSeconds;
        private static readonly ConcurrentDictionary<EUserSessionType, int> s_webSocketConnections = new();
        private static readonly ConcurrentDictionary<string, int> s_matchmakingQueuedPlayers = new();
        private static readonly ConcurrentDictionary<string, long> s_backgroundJobLastSuccess = new();

        private static readonly Counter<long> s_authenticationAttempts =
            s_meter.CreateCounter<long>("genonline.authentication.attempts", "{attempt}", "Authentication attempts by flow and outcome");
        private static readonly Counter<long> s_webSocketFailures =
            s_meter.CreateCounter<long>("genonline.websocket.failures", "{message}", "Inbound or outbound WebSocket messages that failed");
        private static readonly Histogram<double> s_matchmakingWaitDuration =
            s_meter.CreateHistogram<double>("genonline.matchmaking.wait.duration", "s", "Time in the matchmaking queue by exit outcome");
        private static readonly Counter<long> s_matchmakingMatches =
            s_meter.CreateCounter<long>("genonline.matchmaking.matches", "{match}", "Matchmaking matches started or aborted during setup");
        private static readonly Counter<long> s_backgroundJobFailures =
            s_meter.CreateCounter<long>("genonline.background_job.failures", "{run}", "Failed background job runs");
        private static readonly Counter<long> s_externalPublicationFailures =
            s_meter.CreateCounter<long>("genonline.external_publication.failures", "{attempt}", "Failed external leaderboard publication attempts");
        private static readonly Counter<long> s_connectionOutcomes =
            s_meter.CreateCounter<long>("genonline.connection.outcomes", "{connection}", "Peer connection reports sent by clients, by outcome, transport and IP version");
        private static readonly Counter<long> s_securityActions =
            s_meter.CreateCounter<long>("genonline.security.actions", "{action}", "Rate limit rejections, moderation and anti-cheat actions");
        private static readonly Counter<long> s_apiTokenAuthFailures =
            s_meter.CreateCounter<long>("genonline.api_token.auth_failures", "{request}", "gopat- requests refused, by reason");
        private static readonly Counter<long> s_apiTokenRequests =
            s_meter.CreateCounter<long>("genonline.api_token.requests", "{request}", "gopat- requests accepted, by token owner");

        static AppMetrics()
        {
            s_meter.CreateObservableGauge("genonline.players.online", () => Volatile.Read(ref s_onlinePlayers), "{player}", "Players currently online");
            s_meter.CreateObservableGauge("genonline.lobbies.active", () => Volatile.Read(ref s_activeLobbies), "{lobby}", "Lobbies currently active");
            s_meter.CreateObservableGauge("genonline.websocket.connections.active",
                () => s_webSocketConnections.Select(entry => new Measurement<int>(entry.Value, new KeyValuePair<string, object?>("session.type", entry.Key.ToMetricLabel()))),
                "{connection}", "Open WebSocket connections by session type");
            s_meter.CreateObservableGauge("genonline.websocket.send_queue.depth", () => Volatile.Read(ref s_webSocketSendQueueDepth), "{message}", "Messages waiting for WebSocket delivery");
            s_meter.CreateObservableGauge("genonline.matchmaking.queue.players",
                () => s_matchmakingQueuedPlayers.Select(entry => new Measurement<int>(entry.Value, new KeyValuePair<string, object?>("playlist", entry.Key))),
                "{player}", "Players in the matchmaking queue by playlist");
            s_meter.CreateObservableGauge("genonline.background_job.last_success.time",
                () => s_backgroundJobLastSuccess.Select(entry => new Measurement<long>(entry.Value, new KeyValuePair<string, object?>("job", entry.Key))),
                "s", "Unix time of the last successful run by job");
            s_meter.CreateObservableGauge("genonline.external_publication.backlog.age", () => Volatile.Read(ref s_externalPublicationBacklogAgeSeconds), "s", "Age of the oldest unpublished match");
            s_meter.CreateObservableGauge("genonline.api_token.expires_in",
                () => ApiTokenCache.ActiveTokens.Select(t => new Measurement<double>((t.ExpiresAt - DateTime.UtcNow).TotalSeconds,
                    new KeyValuePair<string, object?>("token_id", t.ID), new KeyValuePair<string, object?>("name", t.Name), new KeyValuePair<string, object?>("owner", t.OwnerLabel))),
                "s", "Seconds until each active API token expires");
        }

        public static void RecordServiceSnapshot(int activeLobbies, int onlinePlayers)
        {
            Volatile.Write(ref s_activeLobbies, activeLobbies);
            Volatile.Write(ref s_onlinePlayers, onlinePlayers);
        }

        public static void RecordApiTokenAuthFailure(string reason)
        {
            s_apiTokenAuthFailures.Add(1, new TagList { { "reason", reason } });
        }

        public static void RecordApiTokenRequest(string owner)
        {
            s_apiTokenRequests.Add(1, new TagList { { "owner", owner } });
        }

        public static void RecordAuthentication(string flow, string source, string outcome)
        {
            s_authenticationAttempts.Add(1, new TagList { { "flow", flow }, { "source", source }, { "outcome", outcome } });
        }

        // records one attempt on dispose, with the outcome known at that point
        public static IDisposable TrackAuthentication(string flow, string source, Func<string> getOutcome)
        {
            return new DeferredRecord(() => RecordAuthentication(flow, source, getOutcome()));
        }

        public static void RecordWebSocketConnections(EUserSessionType sessionType, int connections)
        {
            s_webSocketConnections[sessionType] = connections;
        }

        public static void RecordWebSocketFailure(string direction, string reason)
        {
            s_webSocketFailures.Add(1, new TagList { { "direction", direction }, { "reason", reason } });
        }

        public static void AdjustWebSocketSendQueueDepth(int delta)
        {
            if (Interlocked.Add(ref s_webSocketSendQueueDepth, delta) < 0)
            {
                Interlocked.Exchange(ref s_webSocketSendQueueDepth, 0);
            }
        }

        public static void RecordMatchmakingQueue(string playlist, int players)
        {
            s_matchmakingQueuedPlayers[playlist] = players;
        }

        public static void RecordMatchmakingQueueExit(string playlist, string outcome, TimeSpan waited)
        {
            s_matchmakingWaitDuration.Record(Math.Max(0, waited.TotalSeconds), new TagList { { "playlist", playlist }, { "outcome", outcome } });
        }

        public static void RecordMatchmakingMatch(string playlist, string outcome)
        {
            s_matchmakingMatches.Add(1, new TagList { { "playlist", playlist }, { "outcome", outcome } });
        }

        public static void RecordBackgroundJob(string job, bool succeeded)
        {
            if (succeeded)
            {
                s_backgroundJobLastSuccess[job] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else
            {
                s_backgroundJobFailures.Add(1, new TagList { { "job", job } });
            }
        }

        public static void RecordExternalPublicationFailure(string outcome)
        {
            s_externalPublicationFailures.Add(1, new TagList { { "outcome", outcome } });
        }

        public static void RecordExternalPublicationBacklogAge(TimeSpan age)
        {
            Volatile.Write(ref s_externalPublicationBacklogAgeSeconds, Math.Max(0, age.TotalSeconds));
        }

        // One count per accepted connection report, the labels are the validated wire values
        public static void RecordConnectionOutcome(string outcome, string transport, int ipVersion)
        {
            s_connectionOutcomes.Add(1, new TagList { { "outcome", outcome }, { "transport", transport }, { "ip_version", ipVersion == 6 ? "6" : "4" } });
        }

        public static void RecordSecurityAction(string action, string outcome)
        {
            s_securityActions.Add(1, new TagList { { "action", action }, { "outcome", outcome } });
        }

        public static Activity? StartActivity(
            string name,
            ActivityKind kind = ActivityKind.Internal,
            params KeyValuePair<string, object?>[] attributes)
        {
            Activity? activity = s_activitySource.StartActivity(name, kind);
            if (activity != null)
            {
                foreach (KeyValuePair<string, object?> attribute in attributes)
                {
                    activity.SetTag(attribute.Key, attribute.Value);
                }
            }
            return activity;
        }

        public static void RecordException(Activity? activity, Exception exception)
        {
            if (activity == null)
            {
                return;
            }

            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddEvent(new ActivityEvent(
                "exception",
                tags: new ActivityTagsCollection
                {
                    { "exception.type", exception.GetType().FullName },
                    { "exception.message", exception.Message },
                    { "exception.stacktrace", exception.ToString() }
                }));
        }

        private sealed class DeferredRecord : IDisposable
        {
            private Action? _record;

            public DeferredRecord(Action record)
            {
                _record = record;
            }

            public void Dispose()
            {
                try
                {
                    Interlocked.Exchange(ref _record, null)?.Invoke();
                }
                catch
                {
                    // Telemetry is fail-open.
                }
            }
        }
    }
    // Lower-case enum labels for metrics and spans, cached to avoid per-call allocations.
    public static class MetricLabels
    {
        private static class Cache<T> where T : struct, Enum
        {
            public static readonly ConcurrentDictionary<T, string> Names = new();
        }

        public static string ToMetricLabel<T>(this T value) where T : struct, Enum
        {
            return Cache<T>.Names.GetOrAdd(value, static v => v.ToString().ToLowerInvariant());
        }
    }
}

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

#define USE_DISCORD_IN_DEBUG

using Amazon.S3.Model;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using GenOnlineService;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Collections.Concurrent;

public enum EDiscordChannelIDs
{
	Any = -1,
	NetworkRoomChat = 1,
	AdminCommands = 2,
	DirectMessage = 0
}

public enum EDiscordUserTypeRequirements
{
	Player,
	Staff
}

public enum DiscordCommandParsingFlags
{
	Default = 0,
	GreedyArgs = 1
}

public static class Helpers
{
	public static string ComputeMD5Hash(string input)
	{
		using (MD5 md5 = MD5.Create())
		{
			byte[] inputBytes = Encoding.UTF8.GetBytes(input);
			byte[] hashBytes = md5.ComputeHash(inputBytes);

			// Convert byte array to hexadecimal string
			StringBuilder sb = new StringBuilder();
			foreach (byte b in hashBytes)
			{
				sb.Append(b.ToString("x2"));
			}
			return sb.ToString();
		}
	}
	public static Int64 GetUnixTimestamp(bool toUTC = false)
	{
		DateTime now = DateTime.Now;

		if (toUTC)
		{
			now = now.ToUniversalTime();
		}

		return (Int64)now.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
	}

	public static string FormatString(string strFormat, params object[] strParams)
	{
		return String.Format(new System.Globalization.CultureInfo("en-US"), strFormat, strParams);
	}
}

public class DiscordBot
{
	private static readonly Microsoft.Extensions.Logging.ILogger s_log = AppLog.For(typeof(DiscordBot));
	enum EBotAction
	{
		PushScriptedMessage
	}

	DiscordSocketClient? discord = null;

	private Dictionary<EDiscordChannelIDs, ulong> g_dictChannelIDs = new Dictionary<EDiscordChannelIDs, ulong>();
	private Dictionary<EDiscordChannelIDs, ISocketMessageChannel> g_dictChannels = new Dictionary<EDiscordChannelIDs, ISocketMessageChannel>();

	public DiscordBot()
	{
#if !DEBUG
		_ = InitAsync().ContinueWith(t =>
		{
			if (t.IsFaulted)
				s_log.LogError(t.Exception, "Discord initialization failed");
		}, TaskContinuationOptions.OnlyOnFaulted);
#endif
	}

	~DiscordBot()
	{
		if (discord != null)
		{
			discord.LogoutAsync();
		}
	}

	public async Task SendNetworkRoomChat(int roomID, Int64 userID, string strDisplayName, string strMessage)
	{
		try
		{
            if (Program.g_Config == null)
            {
                return;
            }

            IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

            if (discordSettings == null)
            {
                return;
            }

            bool discord_send_room_chat_to_discord = discordSettings.GetValue<bool>("send_room_chat_to_discord");

            if (discord_send_room_chat_to_discord == null)
            {
                return;
            }

			if (discord_send_room_chat_to_discord)
			{
                string strFormattedChatMsg = String.Format("[{0} - UID {1}] {2}", strDisplayName, userID, strMessage);

                ISocketMessageChannel? channel = GetChannel(EDiscordChannelIDs.NetworkRoomChat);
                if (channel != null)
                {
                    string strDiscordMsg = String.Format("[NETWORK ROOM CHAT ID #{0}] {1}", roomID, strFormattedChatMsg);
                    await channel.SendMessageAsync(strDiscordMsg, allowedMentions: AllowedMentions.None).ConfigureAwait(true);
                }
            }
		}
		catch
		{

		}
	}

	public string GetDiscordUsernameFromID(UInt64 discordID)
	{
		if (discord != null)
		{
			var user = discord.GetUser(discordID);
			if (user != null)
			{
				return user.Username;
			}
		}

		return String.Empty;
	}

	public void UpdateBotStatus(string strStatus)
	{
		if (discord != null)
		{
			Game game = new Game(strStatus, ActivityType.Playing);
			discord.SetStatusAsync(UserStatus.Online);
			discord.SetActivityAsync(game);
		}
	}

	private Task OnReady()
	{
		if (Program.g_Config == null)
		{
			return Task.CompletedTask;
		}

		IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

		if (discordSettings == null)
		{
			return Task.CompletedTask;
		}

		UInt64? discord_network_room_chat_channel = discordSettings.GetValue<UInt64>("discord_network_room_chat_channel");
		UInt64? discord_admin_commands_channel = discordSettings.GetValue<UInt64>("discord_admin_commands_channel");

		if (discord_network_room_chat_channel == null || discord_admin_commands_channel == null)
		{
			return Task.CompletedTask;
		}

		// cache our channels
		g_dictChannelIDs[EDiscordChannelIDs.NetworkRoomChat] = (ulong)discord_network_room_chat_channel;
		g_dictChannelIDs[EDiscordChannelIDs.AdminCommands] = (ulong)discord_admin_commands_channel;

		ISocketMessageChannel? channel = GetChannel(EDiscordChannelIDs.NetworkRoomChat);
		if (channel != null)
		{
			//await channel.SendMessageAsync("Bot Started").ConfigureAwait(true);
		}

		return Task.CompletedTask;
	}

	private bool IsChannelAnAdminChannel(ulong channelID)
	{
		EDiscordChannelIDs discordChannelID = EDiscordChannelIDs.NetworkRoomChat;
		foreach (var channel in g_dictChannelIDs)
		{
			if (channel.Value == channelID)
			{
				discordChannelID = channel.Key;
				break;
			}
		}

		if (discordChannelID == EDiscordChannelIDs.NetworkRoomChat || discordChannelID == EDiscordChannelIDs.AdminCommands)
		{
			return true;
		}

		return false;
	}

	public bool IsReady()
	{
		return discord != null && discord.ConnectionState == ConnectionState.Connected;
	}

	public bool GetChannelID(out ulong channelID, EDiscordChannelIDs discordChannelID)
	{
		if (g_dictChannelIDs.ContainsKey(discordChannelID))
		{
			channelID = g_dictChannelIDs[discordChannelID];
		}

		channelID = 999999;
		return false;
	}

	public bool IsChannelIDDefined(ulong channelID, EDiscordChannelIDs discordChannelID)
	{
		if (g_dictChannelIDs.ContainsKey(discordChannelID))
		{
			return g_dictChannelIDs[discordChannelID] == channelID;
		}

		return false;
	}

	private uint g_cooldownLengthSeconds = 20;
	private Dictionary<ulong, double> m_dictCooldowns = new Dictionary<ulong, double>();

	private bool DoesDiscordClientHaveCooldown(ulong channelID, SocketUser user)
	{
		// Never have a cooldown for admin channels
		if (IsChannelAnAdminChannel(channelID))
		{
			return false;
		}

		ExpireCooldowns();
		return m_dictCooldowns.ContainsKey(user.Id);
	}

	private void ExpireCooldowns()
	{
		Int64 unixTimestamp = Helpers.GetUnixTimestamp();

		List<ulong> m_lstToRemove = new List<ulong>();
		foreach (var kvPair in m_dictCooldowns)
		{
			if ((kvPair.Value + g_cooldownLengthSeconds) <= unixTimestamp)
			{
				m_lstToRemove.Add(kvPair.Key);
			}
		}

		foreach (ulong key in m_lstToRemove)
		{
			m_dictCooldowns.Remove(key);
		}
	}

	private void CreateCooldown(ulong channelID, SocketUser user)
	{
		if (!IsChannelAnAdminChannel(channelID))
		{
			Int64 unixTimestamp = Helpers.GetUnixTimestamp();
			m_dictCooldowns[user.Id] = unixTimestamp;
		}
	}

	bool HasRole(IGuildUser user, string roleName)
	{
		return user.RoleIds
				   .Select(roleId => user.Guild.GetRole(roleId))
				   .Any(role => role.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
	}

	// Slash commands need no privileged intent, so the bot never reads message content
	private static ApplicationCommandProperties[] BuildCommands()
	{
		// staff commands stay visible to everyone, the service decides who may run them
		static SlashCommandBuilder Staff(string name, string description) => new SlashCommandBuilder()
			.WithName(name)
			.WithDescription(description);

		return new ApplicationCommandProperties[]
		{
			new SlashCommandBuilder().WithName("players").WithDescription("Shows how many players are online").Build(),
			new SlashCommandBuilder().WithName("lobbies").WithDescription("Shows how many lobbies exist").Build(),
			Staff("uptime", "Shows when the service started").Build(),
			Staff("peak", "Shows the highest player count since the last restart").Build(),
			Staff("whois", "Finds the user id of an online player")
				.AddOption("name", ApplicationCommandOptionType.String, "Display name, or Name#tag", isRequired: true, maxLength: 37)
				.Build(),
			Staff("kick", "Kicks an online player from the server")
				.AddOption("user_id", ApplicationCommandOptionType.Integer, "User id of the player", isRequired: true, minValue: 1)
				.AddOption("reason", ApplicationCommandOptionType.String, "Reason shown to the player", isRequired: false, maxLength: ModerationManager.MaximumReasonLength)
				.Build(),
			Staff("rename", "Replaces an offensive display name, the tag stays")
				.AddOption("user_id", ApplicationCommandOptionType.Integer, "User id of the player", isRequired: true, minValue: 1)
				.AddOption("name", ApplicationCommandOptionType.String, "The new display name", isRequired: true, maxLength: GenOnlineService.Realtime.RealtimeHub.NameMaxLength)
				.AddOption("reason", ApplicationCommandOptionType.String, "Reason kept in the audit log", isRequired: false, maxLength: ModerationManager.MaximumReasonLength)
				.Build(),
			Staff("announce", "Sends an announcement to every online player")
				.AddOption("message", ApplicationCommandOptionType.String, "Announcement text", isRequired: true, maxLength: 512)
				.Build()
		};
	}

	private bool m_bCommandsRegistered = false;

	// Guild commands appear immediately, global commands can take up to an hour
	private async Task OnReadyAsync()
	{
		if (m_bCommandsRegistered || discord == null || Program.g_Config == null)
		{
			return;
		}

		ulong guildID = Program.g_Config.GetSection("Discord").GetValue<ulong>("discord_guild_id");
		if (guildID == 0)
		{
			s_log.LogWarning("Discord:discord_guild_id is not set, slash commands are not registered");
			return;
		}

		SocketGuild? guild = discord.GetGuild(guildID);
		if (guild == null)
		{
			s_log.LogWarning("The Discord bot is not a member of guild {GuildId}, slash commands are not registered", guildID);
			return;
		}

		await guild.BulkOverwriteApplicationCommandAsync(BuildCommands()).ConfigureAwait(false);
		m_bCommandsRegistered = true;
	}

	private static string? RequiredPermission(string commandName) => commandName switch
	{
		"kick" => Permissions.ModerationKick,
		"rename" => Permissions.ModerationRename,
		"whois" => Permissions.ModerationKick,
		"uptime" => Permissions.ServerStats,
		"peak" => Permissions.ServerStats,
		"announce" => Permissions.AnnouncementsSend,
		_ => null
	};

	// A linked GenOnline account with the permission comes first, the legacy discord_admins list is the fallback until staff accounts are linked
	private static async Task<Database.ModerationActor?> ResolveStaff(ulong discordUserID, string permission)
	{
		string name = $"discord:{discordUserID}";

		using (var scope = ServiceLocator.Services.CreateScope())
		{
			var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
			await using var db = await factory.CreateDbContextAsync();
			string externUid = discordUserID.ToString();
			List<Int64> linkedUserIDs = await db.Identities.AsNoTracking()
				.Where(i => i.Provider == IdentityProviders.Discord && i.ExternUid == externUid)
				.Select(i => i.UserID)
				.ToListAsync();

			foreach (Int64 userID in linkedUserIDs)
			{
				if (StaffRoles.HasPermission(userID, permission))
				{
					return new Database.ModerationActor { Kind = AuditActorKinds.Discord, UserID = userID, Name = name, Source = ModerationSources.Discord };
				}
			}
		}

		List<UInt64>? legacyAdmins = Program.g_Config?.GetSection("Discord").GetSection("discord_admins").Get<List<UInt64>>();
		if (legacyAdmins?.Contains(discordUserID) == true)
		{
			s_log.LogInformation("Discord user {DiscordUserId} used {Permission} through the discord_admins list", discordUserID, permission);
			return new Database.ModerationActor { Kind = AuditActorKinds.Discord, Name = name, Source = ModerationSources.Discord };
		}

		return null;
	}

	private static T? GetOption<T>(SocketSlashCommand command, string name)
	{
		object? value = command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value;
		return value is T typed ? typed : default;
	}

	private async Task OnSlashCommandAsync(SocketSlashCommand command)
	{
		try
		{
			if (command.CommandName == "players" || command.CommandName == "lobbies")
			{
				if (DoesDiscordClientHaveCooldown(command.ChannelId ?? 0, command.User))
				{
					await command.RespondAsync("Too many commands. Please wait.", ephemeral: true);
					return;
				}

				CreateCooldown(command.ChannelId ?? 0, command.User);

				string publicReply = command.CommandName == "players"
					? $"There are currently {GenOnlineService.WebSocketManager.GetNumberOfUsersOnline()} players online."
					: $"There are currently {ServiceLocator.Services.GetRequiredService<LobbyManager>().GetNumLobbies()} lobbies.";
				await command.RespondAsync(publicReply, allowedMentions: AllowedMentions.None);
				return;
			}

			string? permission = RequiredPermission(command.CommandName);
			if (permission == null)
			{
				await command.RespondAsync("Unknown command.", ephemeral: true);
				return;
			}

			if (!IsChannelIDDefined(command.ChannelId ?? 0, EDiscordChannelIDs.AdminCommands))
			{
				await command.RespondAsync("Staff commands only work in the admin commands channel.", ephemeral: true);
				return;
			}

			Database.ModerationActor? actor = await ResolveStaff(command.User.Id, permission);
			if (actor == null)
			{
				await command.RespondAsync("You don't have access to this command.", ephemeral: true);
				return;
			}

			// kicks and announcements can take longer than the 3 second reply window
			await command.DeferAsync();
			string reply = await RunStaffCommand(command, actor);
			await command.FollowupAsync(reply, allowedMentions: AllowedMentions.None);
		}
		catch (Exception ex)
		{
			s_log.LogError(ex, "Discord slash command {Command} failed", command.CommandName);

			// a deferred interaction keeps showing "thinking" until it gets a follow-up
			if (command.HasResponded)
			{
				await command.FollowupAsync("The command failed, check the service logs.", ephemeral: true).ConfigureAwait(false);
			}
		}
	}

	private static async Task<string> RunStaffCommand(SocketSlashCommand command, Database.ModerationActor actor)
	{
		switch (command.CommandName)
		{
			case "uptime":
			{
				string startTime = Program.g_LastStartTime.ToString("yyyy-MM-dd HH:mm:ss");
				TimeSpan difference = DateTime.Now.Subtract(Program.g_LastStartTime);
				return $"The server was last started at {startTime} and the current uptime is Days: {difference.Days}, Hours: {difference.Hours}, Minutes: {difference.Minutes}";
			}
			case "peak":
				return $"The highest player peak seen (since last server restart) is {GenOnlineService.WebSocketManager.g_PeakConnectionCount}";
			case "whois":
			{
				string name = GetOption<string>(command, "name") ?? String.Empty;
				SharedUserData? userDataFound = GenOnlineService.WebSocketManager.GetSharedDataForUser(name);
				return userDataFound != null
					? $"User {Database.DisplayNames.Format(userDataFound.m_strDisplayName, userDataFound.m_NameTag)} is user ID {userDataFound.m_UserID}."
					: $"User {name} is not active on the server, or more than one active user has that name: use Name#tag.";
			}
			case "kick":
			{
				Int64 targetUserID = GetOption<Int64>(command, "user_id");
				string reason = GetOption<string>(command, "reason") ?? String.Empty;

				// a discord_admins entry has no account and keeps admin standing
				bool bMayModerate = actor.UserID is Int64 actorUserID
					? StaffRoles.CanModerate(actorUserID, targetUserID)
					: StaffRoles.Outranks(StaffRoles.AdminRank, targetUserID);
				if (!bMayModerate)
				{
					return "You cannot moderate a member of equal or higher staff rank.";
				}

				ModerationResult kickResult = await ModerationManager.KickUser(targetUserID, reason, actor);

				return kickResult.Result switch
				{
					EModerationResult.ReasonTooLong => $"Kick reason must be {ModerationManager.MaximumReasonLength} characters or fewer.",
					EModerationResult.TargetNotOnline => $"User {targetUserID} is not active on the server.",
					_ => String.IsNullOrWhiteSpace(reason)
						? $"User {targetUserID} ({kickResult.TargetDisplayName}) has been kicked from the server."
						: $"User {targetUserID} ({kickResult.TargetDisplayName}) has been kicked from the server. Reason: {reason}"
				};
			}
			case "rename":
			{
				// the same path as PATCH /api/v1/players/:id, the plugin sees the actor discord:<id>
				Int64 targetUserID = GetOption<Int64>(command, "user_id");
				string name = GetOption<string>(command, "name") ?? String.Empty;
				string? reason = GetOption<string>(command, "reason");
				GenOnlineService.Realtime.Result<GenOnlineService.Realtime.Empty> renamed = await ServiceLocator.Services.GetRequiredService<GenOnlineService.Names.PlayerNames>().RenameByStaffAsync(actor, targetUserID, name, reason);
				return renamed.Ok
					? $"User {targetUserID} is now named {name}."
					: $"User {targetUserID} was not renamed: {renamed.Code}.";
			}
			case "announce":
			{
				string message = GetOption<string>(command, "message") ?? String.Empty;
				return $"Announcement '{message}' was delivered to {DeliverAnnouncement(message)} users";
			}
			default:
				return "Unknown command.";
		}
	}

	// Staff text travels as a notice parameter, clients render it as an announcement
	private static int DeliverAnnouncement(string strMessage)
	{
		int numDelivered = GenOnlineService.WebSocketManager.GetNumberOfUsersOnline();
		GenOnlineService.Realtime.RealtimeOut.Fire(GenOnlineService.Realtime.RealtimeOut.All.Notice(new GenOnlineService.Realtime.NoticeEvent
		{
			Code = GenOnlineService.Realtime.ErrorCodes.ServerAnnouncement,
			Params = new { Message = strMessage }
		}));
		return numDelivered;
	}

	private static Task LogAsync(LogMessage log)
	{
		s_log.LogDebug("Discord: {DiscordLogMessage}", log.ToString());
		return Task.CompletedTask;
	}

	private async Task InitAsync()
	{
#if !DEBUG || USE_DISCORD_IN_DEBUG
		DiscordSocketConfig conf = new();
		// slash commands only need the guild intent, so no privileged intent is requested
		conf.GatewayIntents = GatewayIntents.Guilds;
		discord = new DiscordSocketClient(conf);

		// event handlers
		discord.Connected += OnReady;
		discord.Log += LogAsync;
		discord.Ready += OnReadyAsync;
		discord.SlashCommandExecuted += OnSlashCommandAsync;

		//1354979004507226294

		IConfigurationSection? discordSettings = Program.g_Config.GetSection("Discord");

		if (discordSettings == null)
		{
			throw new Exception("Discord section missing in config");
		}

		string? discordToken = discordSettings.GetValue<string>("token");

		if (discordToken == null)
		{
			throw new Exception("Discord Token missing in config");
		}

		await discord.LoginAsync(TokenType.Bot, discordToken).ConfigureAwait(true);
		await discord.StartAsync().ConfigureAwait(true);
#else
		await Task.Delay(1).ConfigureAwait(true);
#endif
	}

	public void PushDM(SocketUser user, string strMessage)
	{
		try
		{
			if (user != null)
			{
				user.SendMessageAsync(strMessage, allowedMentions: AllowedMentions.None).ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
			}
		}
		catch
		{
			// User probably has some privacy settings that do not allow us to send DMs
		}
	}

	public SocketUser? GetDiscordUserFromDiscordID(ulong DiscordUserID)
	{
		if (discord != null)
		{
			return discord.GetUser(DiscordUserID);
		}

		return null;
	}

	private ISocketMessageChannel? GetChannel(EDiscordChannelIDs channelID)
	{
		ISocketMessageChannel? channel = null;
		if (discord != null)
		{
			if (g_dictChannels.ContainsKey(channelID) && g_dictChannels[channelID] != null)
			{
				channel = g_dictChannels[channelID];
			}
			else
			{
				if (g_dictChannelIDs.ContainsKey(channelID))
				{
					channel = (ISocketMessageChannel)discord.GetChannel(g_dictChannelIDs[channelID]);
					g_dictChannels[channelID] = channel;
				}
			}
		}

		return channel;
	}

	public void PushChannelMessage(EDiscordChannelIDs channelID, string strMessage)
	{
		try
		{
			ISocketMessageChannel? channel = GetChannel(channelID);
			if (channel != null)
			{
				channel.SendMessageAsync(strMessage, allowedMentions: AllowedMentions.None).ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
			}
		}
		catch
		{

		}
	}

	public async Task PushMessage(SocketUser user, ulong channelToUse, string strMessage)
	{
		try
		{
			if (discord != null)
			{
				if (channelToUse == (ulong)EDiscordChannelIDs.DirectMessage)
				{
					PushDM(user, strMessage);
				}
				else
				{
					ISocketMessageChannel channel = (ISocketMessageChannel)discord.GetChannel(channelToUse);
					if (channel != null)
					{
						RestUserMessage msg = await channel.SendMessageAsync(strMessage, allowedMentions: AllowedMentions.None).ConfigureAwait(true);
					}
				}
			}
		}
		catch
		{

		}
	}
}

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

using Microsoft.AspNetCore.Mvc;
using System;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public enum EVersionCheckResult
	{
		OK = 0,
		Failed = 1,
		NeedsUpdate = 2
	};

	public abstract class APIResult
	{
		public string Serialize()
		{
			string strRetVal = JsonSerializer.Serialize(Convert.ChangeType(this, GetReturnType()));
			return strRetVal;
		}

		public abstract Type GetReturnType();
	}

	public class POST_VersionCheck_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_VersionCheck_Result);
		}

		public EVersionCheckResult result { get; set; } = EVersionCheckResult.Failed;
		public string patcher_name { get; set; } = String.Empty;
		public string patcher_path { get; set; } = String.Empty;
		public Int64 patcher_size { get; set; } = 0;
	}

	public class GET_VersionManifest_Result : APIResult
	{
		public override Type GetReturnType() { return typeof(GET_VersionManifest_Result); }
		public UInt32 execrc_30 { get; set; } = 0;
		public UInt32 execrc_60 { get; set; } = 0;
	}

	// legacy version checker
	[ApiController]
	[Route("/cloud/env:prod/VersionCheck")]
	public class VersionCheckLegacyController : ControllerBase
	{
		private readonly ILogger<VersionCheckLegacyController> _logger;

		public VersionCheckLegacyController(ILogger<VersionCheckLegacyController> logger)
		{
			_logger = logger;
		}

		[HttpPost(Name = "PostVersionCheckLegacy")]
		public async Task<APIResult> Post()
		{
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();

#if !DEBUG
				return await VersionHelper.Post_InternalHandler(jsonData);
#else
				return await VersionHelper.Post_InternalHandler(jsonData);
#endif
			}
		}
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class VersionCheckController : ControllerBase
	{

		public VersionCheckController()
		{

		}

		[HttpPost(Name = "PostVersionCheck")]
		public async Task<APIResult> Post()
		{		
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();

#if !DEBUG
				return await VersionHelper.Post_InternalHandler(jsonData);
#else
				return await VersionHelper.Post_InternalHandler(jsonData);
#endif
			}
		}
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class VersionManifestController : ControllerBase
	{
		[HttpGet(Name = "GetVersionManifest")]
		public async Task<APIResult> Get()
		{
			return await Task.FromResult(VersionHelper.Get_ManifestHandler());
		}
	}

	class VersionHelper
	{
		private static readonly ILogger s_log = AppLog.For(typeof(VersionHelper));
		public static APIResult Get_ManifestHandler()
		{
			GET_VersionManifest_Result manifest = new GET_VersionManifest_Result();
#if DEBUG
			manifest.execrc_30 = 0;
			manifest.execrc_60 = 0;
#else
			try
			{
				manifest.execrc_30 = CRC32Calculator.CalculateCRC32(Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", "GeneralsOnlineZH_30.exe"));
				manifest.execrc_60 = CRC32Calculator.CalculateCRC32(Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", "GeneralsOnlineZH_60.exe"));
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Failed to calculate executable CRCs for the version manifest");
				// Keep default 0 values on failure
			}
#endif
			return manifest;
		}

		// Fills the patcher download details that tell an outdated client to update.
		private static async Task ApplyPatcherResult(POST_VersionCheck_Result result, JsonSerializerOptions options)
		{
			var jsonPatchData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await System.IO.File.ReadAllTextAsync(Path.Combine("data", "patchdata.json")), options);

			if (jsonPatchData != null)
			{
				result.patcher_name = jsonPatchData["patcher_name"].ToString();
				result.patcher_path = jsonPatchData["patcher_path"].ToString();

#if !DEBUG
				string strPatcherPath = Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", result.patcher_name);
				//string strPatchPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "public_html", "updater", "v1.gopatch");

				result.patcher_size = (UInt32)new FileInfo(strPatcherPath).Length;
#else
			// large patch test
#if LARGE_PATCH_TEST
			result.patcher_path = "http://ipv4.download.thinkbroadband.com/100MB.zip";

			result.patcher_size = 100 * 1048576;
#else

			// TODO: Client should probably just make HEAD requests later
			// debug hack... since we dont have local data

			async Task<long> GetHTTPSize(string url)
			{
				using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
				{
					try
					{
						HttpRequestMessage request = new HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
						HttpResponseMessage response = await client.SendAsync(request);

						if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
						{
							return response.Content.Headers.ContentLength.Value;
						}
					}
					catch (HttpRequestException ex)
					{
						s_log.LogWarning(ex, "Failed to determine patch download size");
					}

					return -1; // Return -1 if the size could not be determined
				}
			}

			result.patcher_size = await GetHTTPSize(result.patcher_path);
#endif

#endif

				result.result = EVersionCheckResult.NeedsUpdate;
			}
			else
			{
				result.result = EVersionCheckResult.Failed;
			}
		}

		private static readonly string[] s_KnownGames = { "generals", "generalsmd" };

		private const string DefaultMinClientVersion = "0.0.0";

		// Reads the leading YYYY.MM.PATCH numbers from a CalVer or git describe version string.
		private static bool TryParseClientVersion(string? strVersion, out (int, int, int) version)
		{
			version = (0, 0, 0);

			if (string.IsNullOrWhiteSpace(strVersion))
			{
				return false;
			}

			string strCore = strVersion.Trim().Split('-', '+')[0];
			string[] parts = strCore.Split('.');
			if (parts.Length != 3
				|| !int.TryParse(parts[0], out int year)
				|| !int.TryParse(parts[1], out int month)
				|| !int.TryParse(parts[2], out int patch))
			{
				return false;
			}

			version = (year, month, patch);
			return true;
		}

		// Returns the configured minimum client version for a game or the default when it is missing or invalid.
		private static (int, int, int) GetMinClientVersion(string strGame)
		{
			string? strMin = Program.g_Config?.GetSection("VersionCheck").GetSection("MinClientVersion").GetValue<string>(strGame);
			if (TryParseClientVersion(strMin, out (int, int, int) minVersion))
			{
				return minVersion;
			}

			TryParseClientVersion(DefaultMinClientVersion, out minVersion);
			return minVersion;
		}

		// Returns the CRC of every executable under crcfiles/<game>.
		private static HashSet<UInt32> GetExeCRCs(string strGame)
		{
			HashSet<UInt32> crcs = new HashSet<UInt32>();
#if DEBUG
			crcs.Add(0);
#else
			string strDir = Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", strGame);
			if (Directory.Exists(strDir))
			{
				foreach (string strExe in Directory.GetFiles(strDir, "*.exe"))
				{
					crcs.Add(CRC32Calculator.CalculateCRC32(strExe));
				}
			}
#endif
			return crcs;
		}

		private static async Task<APIResult> Post_ClientVersionHandler(Dictionary<string, JsonElement> data, JsonSerializerOptions options)
		{
			POST_VersionCheck_Result result = new POST_VersionCheck_Result();

			string? strClientVersion = data["client_version"].ValueKind == JsonValueKind.String ? data["client_version"].GetString() : null;
			string? strGame = data.TryGetValue("game", out JsonElement gameElement) && gameElement.ValueKind == JsonValueKind.String ? gameElement.GetString() : null;

			if (!TryParseClientVersion(strClientVersion, out (int, int, int) clientVersion)
				|| strGame == null
				|| !s_KnownGames.Contains(strGame)
				|| !data.TryGetValue("exe_crc", out JsonElement exeCRCElement)
				|| !UInt32.TryParse(exeCRCElement.ToString(), out UInt32 exeCRC))
			{
				result.result = EVersionCheckResult.Failed;
				return result;
			}

#if DEBUG
#if LARGE_PATCH_TEST
			const bool bDoCRCChecks = true;
#else
			const bool bDoCRCChecks = false;
#endif
#else
			const bool bDoCRCChecks = true;
#endif

			bool bVersionAllowed = clientVersion.CompareTo(GetMinClientVersion(strGame)) >= 0;
			bool bExeCRCMatches = GetExeCRCs(strGame).Contains(exeCRC);

			if (!bDoCRCChecks || (bVersionAllowed && bExeCRCMatches))
			{
				result.result = EVersionCheckResult.OK;
			}
			else
			{
				await ApplyPatcherResult(result, options);
			}

			return result;
		}

#if !DEBUG
		public static async Task<APIResult> Post_InternalHandler(string jsonData)
#else
		public static async Task<APIResult> Post_InternalHandler(string jsonData)
#endif
		{
			POST_VersionCheck_Result result = new POST_VersionCheck_Result();

			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};

			try
			{
				var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

				// Clients that send client_version use the release version check.
				if (data != null && data.ContainsKey("client_version"))
				{
					return await Post_ClientVersionHandler(data, options);
				}

				if (data != null && data.ContainsKey("ver") && data.ContainsKey("netver") && data.ContainsKey("servicesver"))
				{
					if (UInt32.TryParse(data["execrc"].ToString(), out UInt32 execrc)
						&& Int32.TryParse(data["ver"].ToString(), out int ver)
						&& Int32.TryParse(data["netver"].ToString(), out int netver)
						&& Int32.TryParse(data["servicesver"].ToString(), out int servicesver))
					{
						// TODO: Check this exists on startup for safety
						// TODO: maybe cache it?
#if DEBUG
						UInt32 calculatedCRC_Exe_30 = 0;
						UInt32 calculatedCRC_Exe_60 = 0;
#else
						UInt32 calculatedCRC_Exe_30 = CRC32Calculator.CalculateCRC32(Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", "GeneralsOnlineZH_30.exe"));
						UInt32 calculatedCRC_Exe_60 = CRC32Calculator.CalculateCRC32(Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", "GeneralsOnlineZH_60.exe"));
#endif

#if DEBUG
#if LARGE_PATCH_TEST
						const bool bDoCRCChecks = true;
#else
						const bool bDoCRCChecks = false;
#endif
#else
						const bool bDoCRCChecks = true;
#endif

						// TODO: Service should have pulses on lobby from members and remove if they dont do within X seconds

						bool bVersionMatches = ver == Constants.GENERALS_ONLINE_VERSION;
						bool bNetVersionMatches = netver == Constants.GENERALS_ONLINE_NET_VERSION;
						bool bServicesVersionMatches = servicesver == Constants.GENERALS_ONLINE_SERVICE_VERSION;
						bool bExeCRCMatches = execrc == calculatedCRC_Exe_30 || execrc == calculatedCRC_Exe_60;

						if (!bDoCRCChecks || (bVersionMatches && bNetVersionMatches && bServicesVersionMatches && bExeCRCMatches))
						{
							result.result = EVersionCheckResult.OK;
						}
						else
						{
							await ApplyPatcherResult(result, options);
						}
					}
					else
					{
						result.result = EVersionCheckResult.Failed;
					}
				}
				else
				{
					return result;
				}
			}
			catch
			{
				return result;
			}

			return result;
		}
	}
}

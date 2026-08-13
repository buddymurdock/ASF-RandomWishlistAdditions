using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomWishlistAdditions;

#pragma warning disable CA1812 // ASF uses this class during runtime
#pragma warning disable CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning disable CA5394 // Randomness here only picks an arbitrary candidate/delay, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed class RandomWishlistAdditions : IASF, IBotConnection, IGitHubPluginUpdates {
	private const ushort DefaultCandidatePoolCacheHours = 6;
	private const ushort DefaultMaxDelayInMinutes = 1440;
	private const ushort DefaultMinDelayInMinutes = 360;
	private const byte DefaultTargetMaxCount = 10;
	private const byte DefaultTargetMinCount = 3;

	// Steam's public storefront widgets - the same pool every bot picks from, so it's cached once for the whole process rather than per bot
	private static readonly Uri FeaturedCategoriesRequest = new("https://store.steampowered.com/api/featuredcategories?cc=us&l=english");

	private readonly ConcurrentDictionary<string, int> BotAddedCount = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, CancellationTokenSource> BotLoops = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, bool> BotNoEligibleCandidatesWarned = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, int> BotTargets = new(StringComparer.Ordinal);

	// AppIDs the store confirmed aren't actual games (DLC/soundtrack/tool that slipped into New Releases or Specials); shared across bots, skipped for the rest of the process
	private readonly ConcurrentDictionary<uint, byte> RejectedAppIDs = new();

	// Serializes concurrent refreshes of CandidatePoolCache so multiple bots hitting an expired cache at once don't all fetch it in parallel
	private readonly SemaphoreSlim CandidatePoolLock = new(1, 1);

	private (DateTime FetchedAt, uint[] AppIDs) CandidatePoolCache;
	private ushort CandidatePoolCacheHours = DefaultCandidatePoolCacheHours;
	private bool Enabled;
	private ushort MaxDelayInMinutes = DefaultMaxDelayInMinutes;
	private ushort MinDelayInMinutes = DefaultMinDelayInMinutes;
	private byte TargetMaxCount = DefaultTargetMaxCount;
	private byte TargetMinCount = DefaultTargetMinCount;

	public string Name => nameof(RandomWishlistAdditions);
	public string RepositoryName => "buddymurdock/ASF-RandomWishlistAdditions";
	public Version Version => typeof(RandomWishlistAdditions).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomWishlistAdditionsEnabled / RandomWishlistAdditionsMinDelayMinutes / RandomWishlistAdditionsMaxDelayMinutes /
	// RandomWishlistAdditionsTargetMinCount / RandomWishlistAdditionsTargetMaxCount / RandomWishlistAdditionsCandidatePoolCacheHours from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomWishlistAdditions)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomWishlistAdditions)}MinDelayMinutes" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort minDelay) && (minDelay > 0):
						MinDelayInMinutes = minDelay;

						break;
					case $"{nameof(RandomWishlistAdditions)}MaxDelayMinutes" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort maxDelay) && (maxDelay > 0):
						MaxDelayInMinutes = maxDelay;

						break;
					case $"{nameof(RandomWishlistAdditions)}TargetMinCount" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte targetMin):
						TargetMinCount = targetMin;

						break;
					case $"{nameof(RandomWishlistAdditions)}TargetMaxCount" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte targetMax):
						TargetMaxCount = targetMax;

						break;
					case $"{nameof(RandomWishlistAdditions)}CandidatePoolCacheHours" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort cacheHours) && (cacheHours > 0):
						CandidatePoolCacheHours = cacheHours;

						break;
				}
			}
		}

		if (MinDelayInMinutes > MaxDelayInMinutes) {
			(MinDelayInMinutes, MaxDelayInMinutes) = (MaxDelayInMinutes, MinDelayInMinutes);
		}

		if (TargetMinCount > TargetMaxCount) {
			(TargetMinCount, TargetMaxCount) = (TargetMaxCount, TargetMinCount);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomWishlistAdditions)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, every {MinDelayInMinutes}-{MaxDelayInMinutes} minutes each bot tries to wishlist one random game from Steam's New Releases/Specials, up to a random target of {TargetMinCount}-{TargetMaxCount} additions per bot.");

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	public async Task OnBotDisconnected(Bot bot, EResult reason) {
		if (BotLoops.TryRemove(bot.BotName, out CancellationTokenSource? cts)) {
			await cts.CancelAsync().ConfigureAwait(false);
			cts.Dispose();
		}
	}

	public Task OnBotLoggedOn(Bot bot) {
		if (!Enabled) {
			return Task.CompletedTask;
		}

		CancellationTokenSource cts = new();

		if (!BotLoops.TryAdd(bot.BotName, cts)) {
			// A loop for this bot is already running, nothing to do
			cts.Dispose();

			return Task.CompletedTask;
		}

		Utilities.InBackground(() => BotWishlistLoopAsync(bot, cts.Token), true);

		return Task.CompletedTask;
	}

	private async Task BotWishlistLoopAsync(Bot bot, CancellationToken cancellationToken) {
		int target = BotTargets.GetOrAdd(bot.BotName, _ => TargetMinCount == TargetMaxCount ? TargetMinCount : Random.Shared.Next(TargetMinCount, TargetMaxCount + 1));

		while (!cancellationToken.IsCancellationRequested) {
			if (BotAddedCount.GetOrAdd(bot.BotName, 0) >= target) {
				// This bot reached its randomly assigned target for the process' lifetime; nothing left to do
				return;
			}

			int delayMinutes = MinDelayInMinutes == MaxDelayInMinutes ? MinDelayInMinutes : Random.Shared.Next(MinDelayInMinutes, MaxDelayInMinutes + 1);

			try {
				await Task.Delay(TimeSpan.FromMinutes(delayMinutes), cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (cancellationToken.IsCancellationRequested || !bot.IsConnectedAndLoggedOn) {
				break;
			}

			try {
				await TryAddRandomWishlistEntryAsync(bot, target).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	private async Task TryAddRandomWishlistEntryAsync(Bot bot, int target) {
		int addedSoFar = BotAddedCount.GetOrAdd(bot.BotName, 0);

		if (addedSoFar >= target) {
			return;
		}

		uint[] pool = await GetCandidatePoolAsync(bot).ConfigureAwait(false);

		if (pool.Length == 0) {
			return;
		}

		Dictionary<uint, string>? ownedGames = await bot.ArchiHandler.GetOwnedGames(bot.SteamID).ConfigureAwait(false);
		HashSet<uint> currentWishlist = await GetCurrentWishlistAsync(bot).ConfigureAwait(false);

		List<uint> eligible = [
			.. pool.Where(
				appID => !RejectedAppIDs.ContainsKey(appID) &&
					((ownedGames == null) || !ownedGames.ContainsKey(appID)) &&
					!currentWishlist.Contains(appID)
			)
		];

		if (eligible.Count == 0) {
			if (BotNoEligibleCandidatesWarned.TryAdd(bot.BotName, true)) {
				bot.ArchiLogger.LogGenericInfo($"{Name}: no eligible wishlist candidates left in the current pool for this bot (all owned, already wishlisted, or rejected); will keep retrying as the pool and wishlist change.");
			}

			return;
		}

		BotNoEligibleCandidatesWarned.TryRemove(bot.BotName, out _);

		uint appID = eligible[Random.Shared.Next(eligible.Count)];

		if (!await IsRealGameAsync(bot, appID).ConfigureAwait(false)) {
			RejectedAppIDs.TryAdd(appID, 0);

			return;
		}

		bool success = await AddToWishlistAsync(bot, appID).ConfigureAwait(false);

		if (success) {
			BotAddedCount[bot.BotName] = addedSoFar + 1;

			bot.ArchiLogger.LogGenericInfo($"Randomly added app {appID} to the wishlist ({addedSoFar + 1}/{target}).");
		} else {
			bot.ArchiLogger.LogGenericWarning($"Failed to add app {appID} to the wishlist.");
		}
	}

	private async Task<uint[]> GetCandidatePoolAsync(Bot bot) {
		(DateTime FetchedAt, uint[] AppIDs) cached = CandidatePoolCache;

		if ((cached.AppIDs.Length > 0) && ((DateTime.UtcNow - cached.FetchedAt) < TimeSpan.FromHours(CandidatePoolCacheHours))) {
			return cached.AppIDs;
		}

		await CandidatePoolLock.WaitAsync().ConfigureAwait(false);

		try {
			// Re-check after acquiring the lock - another bot may have already refreshed it while we were waiting
			cached = CandidatePoolCache;

			if ((cached.AppIDs.Length > 0) && ((DateTime.UtcNow - cached.FetchedAt) < TimeSpan.FromHours(CandidatePoolCacheHours))) {
				return cached.AppIDs;
			}

			ObjectResponse<FeaturedCategoriesResponse>? response = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<FeaturedCategoriesResponse>(FeaturedCategoriesRequest).ConfigureAwait(false);

			HashSet<uint> appIDs = [];

			foreach (uint appID in (response?.Content?.NewReleases?.Items ?? []).Select(static item => item.ID)) {
				appIDs.Add(appID);
			}

			foreach (uint appID in (response?.Content?.Specials?.Items ?? []).Select(static item => item.ID)) {
				appIDs.Add(appID);
			}

			if (appIDs.Count == 0) {
				// The fetch failed or returned nothing useful - keep serving whatever we had before (possibly empty) rather than blocking every bot on a hard failure
				return CandidatePoolCache.AppIDs;
			}

			CandidatePoolCache = (DateTime.UtcNow, [.. appIDs]);

			return CandidatePoolCache.AppIDs;
		} finally {
			CandidatePoolLock.Release();
		}
	}

	private static async Task<HashSet<uint>> GetCurrentWishlistAsync(Bot bot) {
		Uri request = new($"https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid={bot.SteamID}");
		ObjectResponse<WishlistServiceResponse>? response = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<WishlistServiceResponse>(request).ConfigureAwait(false);

		return [.. (response?.Content?.Response?.Items ?? []).Select(static item => item.AppID)];
	}

	private static async Task<bool> IsRealGameAsync(Bot bot, uint appID) {
		Uri request = new($"https://store.steampowered.com/api/appdetails?appids={appID}&cc=us&l=english");
		ObjectResponse<Dictionary<string, AppDetailsEntry>>? response = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<Dictionary<string, AppDetailsEntry>>(request).ConfigureAwait(false);

		if ((response?.Content == null) || !response.Content.TryGetValue(appID.ToString(CultureInfo.InvariantCulture), out AppDetailsEntry? entry) || !entry.Success || (entry.Data == null)) {
			return false;
		}

		return string.Equals(entry.Data.Type, "game", StringComparison.OrdinalIgnoreCase);
	}

	private static async Task<bool> AddToWishlistAsync(Bot bot, uint appID) {
		Uri request = new("https://store.steampowered.com/api/addtowishlist");
		Dictionary<string, string> data = new(1, StringComparer.Ordinal) { { "appid", appID.ToString(CultureInfo.InvariantCulture) } };

		ObjectResponse<AddToWishlistResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<AddToWishlistResponse>(request, data: data).ConfigureAwait(false);

		return response?.Content?.Success ?? false;
	}

	private sealed record FeaturedCategoriesResponse(
		[property: JsonPropertyName("new_releases")] FeaturedCategory? NewReleases,
		[property: JsonPropertyName("specials")] FeaturedCategory? Specials
	);

	private sealed record FeaturedCategory([property: JsonPropertyName("items")] IReadOnlyList<FeaturedItem>? Items);

	private sealed record FeaturedItem([property: JsonPropertyName("id")] uint ID);

	private sealed record WishlistServiceResponse([property: JsonPropertyName("response")] WishlistServiceResponseInner? Response);

	private sealed record WishlistServiceResponseInner([property: JsonPropertyName("items")] IReadOnlyList<WishlistItem>? Items);

	private sealed record WishlistItem([property: JsonPropertyName("appid")] uint AppID);

	private sealed record AppDetailsEntry([property: JsonPropertyName("success")] bool Success, [property: JsonPropertyName("data")] AppDetailsData? Data);

	private sealed record AppDetailsData([property: JsonPropertyName("type")] string? Type);

	private sealed record AddToWishlistResponse([property: JsonPropertyName("success")] bool Success);
}
#pragma warning restore CA5394
#pragma warning restore CA1001
#pragma warning restore CA1812

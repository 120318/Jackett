using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Jackett.Common.Indexers.Definitions
{
    public class SunnyPT : IndexerBase
    {
        public override string Id => "sunnypt";
        public override string Name => "SunnyPT";
        public override string Description => "SunnyPT is a CHINESE Private Torrent Tracker for MOVIES / TV";
        public override string SiteLink { get; protected set; } = "https://sunnypt.top/";
        public override string Language => "zh-CN";
        public override string Type => "private";
        public override TorznabCapabilities TorznabCaps => SetCapabilities();

        private const string ApiUrl = "https://api.sunnypt.top/api/v1/torrent/";
        private readonly SemaphoreSlim _sessionLock = new SemaphoreSlim(1, 1);
        private string _accessToken;
        private DateTimeOffset _tokenExpires;
        private new ConfigurationDataBasicLoginWith2FA configData => (ConfigurationDataBasicLoginWith2FA)base.configData;

        public SunnyPT(IIndexerConfigurationService configService, WebClient wc, Logger l,
            IProtectionService ps, ICacheService cs)
            : base(configService: configService, client: wc, logger: l, p: ps, cacheService: cs,
                configData: new ConfigurationDataBasicLoginWith2FA("Enter a current 2FA code only if enabled on your account."))
        {
        }

        private static TorznabCapabilities SetCapabilities()
        {
            var caps = new TorznabCapabilities
            {
                TvSearchParams = new List<TvSearchParam> { TvSearchParam.Q, TvSearchParam.Season, TvSearchParam.Ep },
                MovieSearchParams = new List<MovieSearchParam> { MovieSearchParam.Q },
                MusicSearchParams = new List<MusicSearchParam> { MusicSearchParam.Q }
            };
            caps.Categories.AddCategoryMapping("Movies", TorznabCatType.Movies);
            caps.Categories.AddCategoryMapping("TV Series", TorznabCatType.TV);
            caps.Categories.AddCategoryMapping("TV Shows", TorznabCatType.TV);
            caps.Categories.AddCategoryMapping("Animations", TorznabCatType.TVAnime);
            caps.Categories.AddCategoryMapping("Documentaries", TorznabCatType.TVDocumentary);
            caps.Categories.AddCategoryMapping("Sports", TorznabCatType.TVSport);
            caps.Categories.AddCategoryMapping("Music Videos", TorznabCatType.AudioVideo);
            caps.Categories.AddCategoryMapping("HQ Audio", TorznabCatType.Audio);
            caps.Categories.AddCategoryMapping("Misc", TorznabCatType.Other);
            return caps;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);
            _accessToken = null;
            CookieHeader = string.Empty;
            await EnsureSessionAsync();
            var releases = await PerformQuery(new TorznabQuery());
            await ConfigureIfOK(CookieHeader, releases.Any(),
                () => throw new Exception("Testing returned no results"));
            return IndexerConfigurationStatus.Completed;
        }

        private async Task EnsureSessionAsync()
        {
            await _sessionLock.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(_accessToken) && _tokenExpires > DateTimeOffset.UtcNow.AddMinutes(1))
                    return;

                if (!string.IsNullOrEmpty(CookieHeader))
                {
                    var refresh = await RequestWithCookiesAsync(SiteLink + "api/auth/session/refresh", method: RequestType.POST);
                    var refreshed = JObject.Parse(refresh.ContentString)["data"];
                    if (refreshed?["accessToken"] != null)
                    {
                        var refreshedSession = await RequestWithCookiesAsync(SiteLink + "api/auth/session");
                        SetSession(JObject.Parse(refreshedSession.ContentString)["data"]);
                        SaveConfig();
                        return;
                    }
                }

                var body = new JObject
                {
                    ["username"] = configData.Username.Value,
                    ["password"] = configData.Password.Value,
                    ["code"] = configData.TwoFactorAuth.Value ?? string.Empty,
                    ["rememberMe"] = true
                };
                var login = await RequestWithCookiesAsync(SiteLink + "api/auth/session/login", method: RequestType.POST,
                    headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    rawbody: body.ToString(Formatting.None));
                if (JObject.Parse(login.ContentString)["error"] != null)
                    throw new ExceptionWithConfigData("Login failed. Check your credentials and, if enabled, enter a current 2FA code", configData);

                var response = await RequestWithCookiesAsync(SiteLink + "api/auth/session");
                var session = JObject.Parse(response.ContentString)["data"];
                if (session?["accessToken"] == null)
                    throw new ExceptionWithConfigData("Login failed: no authenticated session returned", configData);
                SetSession(session);
                SaveConfig();
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private void SetSession(JToken session)
        {
            _accessToken = (string)session["accessToken"];
            _tokenExpires = DateTimeOffset.FromUnixTimeSeconds((long)session["exp"]);
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            await EnsureSessionAsync();
            var categories = TorznabCaps.Categories.MapTorznabCapsToTrackers(query);
            if (!categories.Any())
                categories.Add(string.Empty);
            var releases = new List<ReleaseInfo>();
            foreach (var category in categories)
            {
                var body = new JObject
                {
                    ["limit"] = 100,
                    ["offset"] = 0,
                    ["keyword"] = query.GetQueryString(),
                    ["sort_by"] = "added",
                    ["sort_order"] = "desc"
                };
                if (!string.IsNullOrEmpty(category))
                    body["category"] = category;
                var response = await RequestWithCookiesAndRetryAsync(ApiUrl + "list", method: RequestType.POST,
                    headers: new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer " + _accessToken,
                        ["Content-Type"] = "application/json"
                    }, rawbody: body.ToString(Formatting.None));
                var json = JObject.Parse(response.ContentString);
                if ((int?)json["code"] != 0)
                    throw new Exception("SunnyPT search failed: " + (string)json["msg"]);
                foreach (var torrent in json["data"]["list"])
                    releases.Add(ParseRelease(torrent, SiteLink, TorznabCaps.Categories));
            }
            return releases.GroupBy(r => r.Guid).Select(g => g.First());
        }

        protected static ReleaseInfo ParseRelease(JToken torrent, string siteLink, TorznabCapabilitiesCategories categories)
        {
            var id = (long)torrent["id"];
            var promotion = (int?)torrent["promotion_type"] ?? 1;
            var until = (long?)torrent["promotion_until"] ?? 0;
            if (until > 0 && until <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                promotion = 1;
            var details = new Uri(siteLink + "torrent/" + id);
            return new ReleaseInfo
            {
                Guid = details,
                Details = details,
                Link = new Uri(ApiUrl + "download?id=" + id),
                Title = (string)torrent["name"],
                Description = (string)torrent["sub_name"],
                Category = categories.MapTrackerCatToNewznab((string)torrent["category"]),
                PublishDate = DateTimeOffset.FromUnixTimeSeconds((long)torrent["added"]).UtcDateTime,
                Size = (long)torrent["size"],
                Seeders = (long)torrent["seeders"],
                Peers = (long)torrent["seeders"] + (long)torrent["leechers"],
                Grabs = (long)torrent["times_completed"],
                Poster = Uri.TryCreate((string)torrent["cover"], UriKind.Absolute, out var poster) ? poster : null,
                DownloadVolumeFactor = promotion switch { 2 or 4 => 0, 5 or 6 => 0.5, 7 => 0.3, _ => 1 },
                UploadVolumeFactor = promotion is 3 or 4 or 6 ? 2 : 1,
                MinimumSeedTime = 86400
            };
        }

        public override async Task<byte[]> Download(Uri link)
        {
            if (link.GetLeftPart(UriPartial.Path) != ApiUrl + "download")
                throw new Exception("Invalid SunnyPT download URL");
            await EnsureSessionAsync();
            return await Download(link, RequestType.GET,
                headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + _accessToken });
        }
    }
}

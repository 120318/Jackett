using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jackett.Common.Indexers.Definitions;
using Jackett.Common.Models;
using Jackett.Common.Utils.Clients;
using Jackett.Test.TestHelpers;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using WebRequest = Jackett.Common.Utils.Clients.WebRequest;

namespace Jackett.Test.Common.Indexers
{
    [TestFixture]
    public class SunnyPTTests
    {
        private class TestIndexer : SunnyPT
        {
            public TestIndexer() : base(null, new TestWebClient(), LogManager.GetCurrentClassLogger(), null, new TestCacheService())
            {
            }

            public ReleaseInfo Parse(JToken torrent) => ParseRelease(torrent, SiteLink, TorznabCaps.Categories);
        }

        private class SessionWebClient : TestWebClient
        {
            public List<WebRequest> Requests { get; } = new List<WebRequest>();
            public int ApiCode { get; set; }

            public override Task<WebResult> GetResultAsync(WebRequest request)
            {
                Requests.Add(request);
                var json = request.Url.EndsWith("/login")
                    ? new JObject { ["data"] = new JObject { ["username"] = "test" } }
                    : request.Url.EndsWith("/session")
                        ? new JObject
                        {
                            ["data"] = new JObject
                            {
                                ["accessToken"] = "test-token",
                                ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                            }
                        }
                        : new JObject
                        {
                            ["code"] = ApiCode,
                            ["msg"] = ApiCode == 0 ? "ok" : "permission denied",
                            ["data"] = new JObject { ["list"] = new JArray(Torrent(1)) }
                        };
                return Task.FromResult(new WebResult
                {
                    Status = HttpStatusCode.OK,
                    Request = request,
                    ContentBytes = Encoding.UTF8.GetBytes(json.ToString())
                });
            }
        }

        private class SessionIndexer : SunnyPT
        {
            public SessionIndexer(SessionWebClient client)
                : base(null, client, LogManager.GetCurrentClassLogger(), null, new TestCacheService())
            {
            }

            public override void SaveConfig()
            {
            }

            public Task<IEnumerable<ReleaseInfo>> Search(TorznabQuery query) => PerformQuery(query);
        }

        [Test]
        public async Task LoginUsesJsonAndSessionIsReusedForCategorySearch()
        {
            var client = new SessionWebClient();
            var indexer = new SessionIndexer(client);
            await indexer.Search(new TorznabQuery { SearchTerm = "Example", Categories = new[] { TorznabCatType.Movies.ID } });
            await indexer.Search(new TorznabQuery());
            Assert.That(client.Requests.FindAll(r => r.Url.EndsWith("/login")), Has.Count.EqualTo(1));
            var login = client.Requests.Find(r => r.Url.EndsWith("/login"));
            Assert.That(login.Type, Is.EqualTo(RequestType.POST));
            Assert.That(JObject.Parse(login.RawBody)["rememberMe"].Value<bool>(), Is.True);
            var search = client.Requests.Find(r => r.Url.EndsWith("/list"));
            Assert.That(search.Headers["Authorization"], Is.EqualTo("Bearer test-token"));
            var body = JObject.Parse(search.RawBody);
            Assert.That((string)body["keyword"], Is.EqualTo("Example"));
            Assert.That((string)body["category"], Is.EqualTo("Movies"));
        }

        [Test]
        public void ApiErrorDoesNotBecomeAnEmptySuccessfulSearch()
        {
            var indexer = new SessionIndexer(new SessionWebClient { ApiCode = 400001 });
            Assert.That(async () => await indexer.Search(new TorznabQuery()),
                Throws.Exception.With.Message.EqualTo("SunnyPT search failed: permission denied"));
        }

        private static JObject Torrent(int promotion, long expiry = 0) => new JObject
        {
            ["id"] = 42,
            ["name"] = "Example Movie 2026 1080p",
            ["sub_name"] = "Example subtitle",
            ["category"] = "Movies",
            ["added"] = 1700000000,
            ["size"] = 5000000000L,
            ["seeders"] = 8,
            ["leechers"] = 3,
            ["times_completed"] = 20,
            ["promotion_type"] = promotion,
            ["promotion_until"] = expiry,
            ["cover"] = ""
        };

        [Test]
        public void ParseMetadataAndStableDownloadUrl()
        {
            var release = new TestIndexer().Parse(Torrent(1));
            Assert.That(release.Details.AbsoluteUri, Is.EqualTo("https://sunnypt.top/torrent/42"));
            Assert.That(release.Link.AbsoluteUri, Is.EqualTo("https://api.sunnypt.top/api/v1/torrent/download?id=42"));
            Assert.That(release.Size, Is.EqualTo(5000000000L));
            Assert.That(release.Peers, Is.EqualTo(11));
            Assert.That(release.Category, Does.Contain(TorznabCatType.Movies.ID));
            Assert.That(release.PublishDate, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime));
            Assert.That(release.Poster, Is.Null);
        }

        [TestCase(1, 1, 1)]
        [TestCase(2, 0, 1)]
        [TestCase(3, 1, 2)]
        [TestCase(4, 0, 2)]
        [TestCase(5, 0.5, 1)]
        [TestCase(6, 0.5, 2)]
        [TestCase(7, 0.3, 1)]
        public void ParsePromotion(int promotion, double download, double upload)
        {
            var release = new TestIndexer().Parse(Torrent(promotion));
            Assert.That(release.DownloadVolumeFactor, Is.EqualTo(download));
            Assert.That(release.UploadVolumeFactor, Is.EqualTo(upload));
        }

        [Test]
        public void ExpiredPromotionDoesNotRemainFreeleech()
        {
            var release = new TestIndexer().Parse(Torrent(4, 1700000000));
            Assert.That(release.DownloadVolumeFactor, Is.EqualTo(1));
            Assert.That(release.UploadVolumeFactor, Is.EqualTo(1));
        }
    }
}

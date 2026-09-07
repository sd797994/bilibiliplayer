using System.Net;
using BiliBiliPlayer.Services;

static void True(bool value, string message)
{
    if (!value) throw new Exception(message);
}

var handler = new RecordingHandler();
using var client = new HttpClient(handler);
var service = new BiliWatchHistoryService(client);
var cookie = "buvid3=device; SESSDATA=session; bili_jct=csrf-value; DedeUserID=42";

True(await service.ReportStartedAsync("BV_TEST", 123, 456, 12.8, cookie),
    "A code=0 heartbeat should succeed");
True(handler.RequestCount == 1, "Exactly one request should be sent");
True(handler.Method == HttpMethod.Post, "Heartbeat should use POST");
True(handler.Uri?.AbsoluteUri == "https://api.bilibili.com/x/click-interface/web/heartbeat",
    "Heartbeat endpoint mismatch");
True(handler.Cookie == cookie, "Login cookies must be forwarded");
True(handler.Origin == "https://www.bilibili.com", "Origin mismatch");
True(handler.Referrer == "https://www.bilibili.com/video/BV_TEST/", "Referrer mismatch");

var form = ParseForm(handler.Body);
True(form["aid"] == "123" && form["bvid"] == "BV_TEST" && form["cid"] == "456",
    "Video identity is incomplete");
True(form["played_time"] == "12", "Playback position should be rounded down");
True(form["realtime"] == "1" && form["type"] == "3" && form["dt"] == "2" &&
     form["play_type"] == "0", "Start-heartbeat fields mismatch");
True(form["csrf"] == "csrf-value" && form["csrf_token"] == "csrf-value",
    "CSRF should come from bili_jct");
True(long.TryParse(form["start_ts"], out var startTs) && startTs > 0, "start_ts is missing");

var requestsBeforeInvalidInput = handler.RequestCount;
True(!await service.ReportStartedAsync("BV_TEST", 123, 456, 1, string.Empty),
    "Anonymous sessions must not report account history");
True(handler.RequestCount == requestsBeforeInvalidInput, "Invalid input must not send a request");

handler.ResponseBody = "{\"code\":-101,\"message\":\"账号未登录\"}";
True(!await service.ReportStartedAsync("BV_TEST", 123, 456, 1, cookie),
    "Non-zero API code must fail");

handler.ResponseBody = """
    {
      "code": 0,
      "message": "0",
      "data": {
        "cursor": { "max": 9876, "view_at": 1788200000, "business": "archive", "ps": 20 },
        "list": [
          {
            "title": "测试历史视频",
            "cover": "//i0.hdslb.com/test.jpg",
            "author_name": "测试 UP",
            "author_face": "http://i1.hdslb.com/face.jpg",
            "author_mid": 99,
            "progress": 65,
            "duration": 123,
            "view_at": 1788200100,
            "history": {
              "oid": 123,
              "cid": 456,
              "bvid": "BV_HISTORY",
              "business": "archive"
            }
          },
          {
            "title": "直播记录不应进入视频卡片",
            "history": { "oid": 321, "business": "live" }
          }
        ]
      }
    }
    """;
var firstPage = await service.GetPageAsync(cookie);
True(handler.Method == HttpMethod.Get, "History should use GET");
True(handler.Uri?.AbsolutePath == "/x/web-interface/history/cursor",
    "History cursor endpoint mismatch");
True(handler.Uri?.Query == "?ps=20",
    "Initial history cursor query mismatch");
True(handler.Cookie == cookie, "History request must reuse login cookies");
True(firstPage.Items.Count == 1, "Only archive video records should be shown");
True(firstPage.Items[0].Bvid == "BV_HISTORY" &&
     firstPage.Items[0].CoverUrl == "https://i0.hdslb.com/test.jpg" &&
     firstPage.Items[0].ProgressText == "看到 1:05",
    "History item mapping mismatch");
True(firstPage.NextCursor is { Max: 9876, ViewAt: 1788200000, Business: "archive" } &&
     firstPage.HasMore,
    "History next cursor mismatch");

handler.ResponseBody = "{\"code\":0,\"message\":\"0\",\"data\":{\"cursor\":{\"max\":9876,\"view_at\":1788200000,\"business\":\"archive\"},\"list\":[]}}";
var lastPage = await service.GetPageAsync(cookie, firstPage.NextCursor);
True(handler.Uri?.Query.Contains("max=9876") == true &&
     handler.Uri.Query.Contains("view_at=1788200000"),
    "Next history request must forward the server cursor");
True(!lastPage.HasMore, "An empty history page should end pagination");

Console.WriteLine("PASS watch history: report, account list mapping and cursor pagination");

static Dictionary<string, string> ParseForm(string body) =>
    body.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Split('=', 2))
        .ToDictionary(
            item => Uri.UnescapeDataString(item[0].Replace('+', ' ')),
            item => Uri.UnescapeDataString(item.ElementAtOrDefault(1)?.Replace('+', ' ') ?? string.Empty));

sealed class RecordingHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public HttpMethod? Method { get; private set; }
    public Uri? Uri { get; private set; }
    public string Cookie { get; private set; } = string.Empty;
    public string Origin { get; private set; } = string.Empty;
    public string Referrer { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string ResponseBody { get; set; } = "{\"code\":0,\"message\":\"0\"}";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        Method = request.Method;
        Uri = request.RequestUri;
        Cookie = request.Headers.TryGetValues("Cookie", out var cookies)
            ? string.Join("; ", cookies)
            : string.Empty;
        Origin = request.Headers.TryGetValues("Origin", out var origins)
            ? origins.Single()
            : string.Empty;
        Referrer = request.Headers.Referrer?.AbsoluteUri ?? string.Empty;
        Body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseBody)
        };
    }
}

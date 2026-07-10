using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LanFileTransfer.App.Infrastructure;
using LanFileTransfer.App.Models;
using LanFileTransfer.App.Services;

namespace LanFileTransfer.Tests;

public sealed class HttpFileServerTests
{
    [Fact]
    public async Task ServesWebUploadsUnicodeFilesRangesAndReleasesPort()
    {
        using var temp = new TempDirectory();
        var port = GetFreePort();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths); config.Load();
        config.Replace(new AppSettings
        {
            Port = port,
            UploadDirectory = "Data",
            LanOnly = true,
            AllowWebUpload = true,
            AllowWebDelete = false,
            DuplicateBehavior = DuplicateBehavior.Overwrite,
            MaxUploadBytes = 1024 * 1024
        }, persist: false);
        var log = new AppLogService(paths);
        var events = new EventHub();
        var transfers = new TransferRegistry(events);
        using var catalog = new FileCatalog(paths, config, events, log);
        var server = new HttpFileServer(config, catalog, new NetworkAddressService(), events, transfers, new WebAssetProvider(), log);
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var web = await client.GetAsync("/web");
        Assert.Equal(HttpStatusCode.OK, web.StatusCode);
        Assert.Contains("内网文件传输工具", await web.Content.ReadAsStringAsync());

        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("abcdef"));
        multipart.Add(content, "file", "中文.conf");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = multipart };
        upload.Headers.Add("X-Lan-Transfer", "1");
        upload.Headers.Add("X-File-Size", "6");
        var uploadResponse = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        Assert.Contains(log.Recent, entry => entry.Contains("上传 · 中文.conf · 127.0.0.1", StringComparison.Ordinal));

        var listJson = await client.GetStringAsync("/api/files");
        Assert.Contains("中文.conf", listJson);

        var direct = await client.GetAsync("/%E4%B8%AD%E6%96%87.conf");
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.Equal("abcdef", await direct.Content.ReadAsStringAsync());
        Assert.NotNull(direct.Headers.ETag);
        Assert.Equal("no-cache", direct.Headers.CacheControl?.ToString());
        Assert.Contains(log.Recent, entry => entry.Contains("访问 · 中文.conf · 127.0.0.1", StringComparison.Ordinal));

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/%E4%B8%AD%E6%96%87.conf");
        rangeRequest.Headers.Range = new RangeHeaderValue(1, 3);
        var rangeResponse = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal("bcd", await rangeResponse.Content.ReadAsStringAsync());

        using (var subscription = events.Subscribe())
        {
            var trackedResponse = await client.GetAsync("/%E4%B8%AD%E6%96%87.conf?download=1&transferId=integration-download");
            Assert.Equal(HttpStatusCode.OK, trackedResponse.StatusCode);
            await trackedResponse.Content.LoadIntoBufferAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            JsonElement completedTransfer = default;
            while (await subscription.Reader.WaitToReadAsync(timeout.Token))
            {
                var message = JsonDocument.Parse(await subscription.Reader.ReadAsync(timeout.Token)).RootElement;
                if (message.GetProperty("type").GetString() == "transfer" &&
                    message.GetProperty("data").GetProperty("status").GetString() == "completed")
                {
                    completedTransfer = message.GetProperty("data").Clone();
                    break;
                }
            }
            Assert.Equal("download", completedTransfer.GetProperty("direction").GetString());
            Assert.Equal(100, completedTransfer.GetProperty("percent").GetInt32());
            Assert.Contains(log.Recent, entry => entry.Contains("下载 · 中文.conf · 127.0.0.1", StringComparison.Ordinal));
        }

        using (var subscription = events.Subscribe())
        {
            using var rangedDownload = new HttpRequestMessage(HttpMethod.Get, "/%E4%B8%AD%E6%96%87.conf?download=1&transferId=ranged-download");
            rangedDownload.Headers.Range = new RangeHeaderValue(1, 3);
            var rangedResponse = await client.SendAsync(rangedDownload);
            Assert.Equal(HttpStatusCode.PartialContent, rangedResponse.StatusCode);
            Assert.Equal("bcd", await rangedResponse.Content.ReadAsStringAsync());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            JsonElement completedTransfer = default;
            while (await subscription.Reader.WaitToReadAsync(timeout.Token))
            {
                var message = JsonDocument.Parse(await subscription.Reader.ReadAsync(timeout.Token)).RootElement;
                if (message.GetProperty("type").GetString() == "transfer" &&
                    message.GetProperty("data").GetProperty("id").GetString() == "ranged-download" &&
                    message.GetProperty("data").GetProperty("status").GetString() == "completed")
                {
                    completedTransfer = message.GetProperty("data").Clone();
                    break;
                }
            }
            Assert.Equal(3, completedTransfer.GetProperty("total").GetInt64());
            Assert.Equal(100, completedTransfer.GetProperty("percent").GetInt32());
        }

        using (var reservedMultipart = new MultipartFormDataContent())
        {
            reservedMultipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("route")), "file", "web");
            using var reservedUpload = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = reservedMultipart };
            reservedUpload.Headers.Add("X-Lan-Transfer", "1");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(reservedUpload)).StatusCode);
        }

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/files/%E4%B8%AD%E6%96%87.conf");
        delete.Headers.Add("X-Lan-Transfer", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(delete)).StatusCode);

        var traversal = await client.GetAsync("/%2e%2e%5csecret.txt");
        Assert.True(traversal.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);

        config.Replace(config.Current with { AllowWebDelete = true }, persist: false);
        using var allowedDelete = new HttpRequestMessage(HttpMethod.Delete, "/api/files/%E4%B8%AD%E6%96%87.conf");
        allowedDelete.Headers.Add("X-Lan-Transfer", "1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(allowedDelete)).StatusCode);
        Assert.Contains(log.Recent, entry => entry.Contains("删除 · 中文.conf · 127.0.0.1", StringComparison.Ordinal));

        await server.StopAsync();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public async Task RejectsUploadOverConfiguredLimitWithoutPartialFile()
    {
        await using var environment = await ServerEnvironment.StartAsync(maxUploadBytes: 4);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("too-large")), "file", "large.txt");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = multipart };
        request.Headers.Add("X-Lan-Transfer", "1");
        request.Headers.Add("X-File-Size", "9");
        var response = await environment.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(environment.Catalog.GetFiles());
    }

    [Fact]
    public async Task ReadOnlyModeRejectsDirectUploadAndDeleteRequests()
    {
        await using var environment = await ServerEnvironment.StartAsync(maxUploadBytes: 1024, readOnlyMode: true);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("blocked")), "file", "blocked.txt");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = multipart };
        upload.Headers.Add("X-Lan-Transfer", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await environment.Client.SendAsync(upload)).StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/files/blocked.txt");
        delete.Headers.Add("X-Lan-Transfer", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await environment.Client.SendAsync(delete)).StatusCode);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ServerEnvironment : IAsyncDisposable
    {
        private readonly TempDirectory _temp;
        private readonly HttpFileServer _server;
        private ServerEnvironment(TempDirectory temp, HttpFileServer server, FileCatalog catalog, HttpClient client)
        {
            _temp = temp; _server = server; Catalog = catalog; Client = client;
        }
        public FileCatalog Catalog { get; }
        public HttpClient Client { get; }
        public static async Task<ServerEnvironment> StartAsync(long maxUploadBytes, bool readOnlyMode = false)
        {
            var temp = new TempDirectory(); var port = GetFreePort(); var paths = new AppPaths(temp.Path);
            var config = new PortableConfigStore(paths); config.Load();
            config.Replace(new AppSettings { Port = port, MaxUploadBytes = maxUploadBytes, ReadOnlyMode = readOnlyMode }, persist: false);
            var log = new AppLogService(paths); var events = new EventHub(); var transfers = new TransferRegistry(events);
            var catalog = new FileCatalog(paths, config, events, log);
            var server = new HttpFileServer(config, catalog, new NetworkAddressService(), events, transfers, new WebAssetProvider(), log);
            await server.StartAsync();
            return new ServerEnvironment(temp, server, catalog, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") });
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await _server.StopAsync(); Catalog.Dispose(); _temp.Dispose();
        }
    }
}

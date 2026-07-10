using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using LanFileTransfer.App.Infrastructure;
using LanFileTransfer.App.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace LanFileTransfer.App.Services;

public sealed class HttpFileServer(
    PortableConfigStore config,
    FileCatalog catalog,
    NetworkAddressService network,
    EventHub events,
    TransferRegistry transfers,
    WebAssetProvider webAssets,
    AppLogService log)
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _application;
    private Timer? _transferCleanupTimer;

    public bool IsRunning => _application is not null;

    public event Action<bool>? RunningChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_application is not null)
            {
                return;
            }

            var settings = config.Current;
            try
            {
                config.ValidateCurrent();
                if (!Directory.Exists(catalog.DirectoryPath))
                {
                    throw new DirectoryNotFoundException("共享目录不存在。");
                }
            }
            catch (Exception exception)
            {
                throw ServerStartException.Wrap(exception, settings.Port);
            }
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(HttpFileServer).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = settings.MaxUploadBytes + 16L * 1024 * 1024;
                options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
                options.ListenAnyIP(settings.Port);
            });
            builder.Services.AddProblemDetails();

            var app = builder.Build();
            ConfigurePipeline(app);

            try
            {
                await app.StartAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                await app.DisposeAsync();
                throw ServerStartException.Wrap(exception, settings.Port);
            }

            _application = app;
            _transferCleanupTimer = new Timer(
                _ => transfers.CleanupExpired(TimeSpan.FromHours(2)),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
            log.Info($"服务启动 · 端口 {settings.Port}");
            RunningChanged?.Invoke(true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var app = _application;
            if (app is null)
            {
                return;
            }

            _application = null;
            _transferCleanupTimer?.Dispose();
            _transferCleanupTimer = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await app.StopAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                log.Warning("等待传输结束超时，已强制停止服务。");
            }
            finally
            {
                await app.DisposeAsync();
                transfers.FailAll("服务已停止。");
                catalog.CleanupTemporaryFiles();
            }

            log.Info("服务停止");
            RunningChanged?.Invoke(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void ConfigurePipeline(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (config.Current.LanOnly && !network.IsLocalNetworkClient(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "仅允许同一局域网中的设备访问。" });
                return;
            }

            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "about:blank",
                title = "服务器处理请求时发生错误",
                status = 500
            });
        }));

        app.MapGet("/", () => Results.Redirect("/web"));
        app.MapGet("/web", (HttpContext context) =>
        {
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'";
            var html = webAssets.ReadText("index.html");
            return Results.Text(html, "text/html; charset=utf-8");
        });
        app.MapGet("/assets/app.css", () => Results.Text(webAssets.ReadText("app.css"), "text/css; charset=utf-8"));
        app.MapGet("/assets/app.js", () => Results.Text(webAssets.ReadText("app.js"), "text/javascript; charset=utf-8"));

        app.MapGet("/api/settings", () =>
        {
            var settings = config.Current;
            return Results.Ok(new
            {
                settings.AllowWebUpload,
                settings.AllowWebDelete,
                settings.MaxUploadBytes,
                duplicateBehavior = settings.DuplicateBehavior.ToString()
            });
        });

        app.MapGet("/api/files", () =>
        {
            try
            {
                return Results.Ok(catalog.GetFiles().Select(file => new
                {
                    file.Name,
                    file.Size,
                    file.LastModifiedUtc,
                    url = "/" + Uri.EscapeDataString(file.Name)
                }));
            }
            catch (IOException exception)
            {
                log.Error("读取文件列表失败", exception);
                return Results.Problem("共享目录暂时不可访问。", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost("/api/files", HandleUploadAsync);
        app.MapDelete("/api/files/{fileName}", HandleDeleteAsync);
        app.MapGet("/api/events", HandleEventsAsync);
        app.MapMethods("/{fileName}", [HttpMethods.Get, HttpMethods.Head], HandleFileAsync);
    }

    private async Task HandleUploadAsync(HttpContext context)
    {
        var settings = config.Current;
        if (!settings.AllowWebUpload)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Web 上传已关闭。" });
            return;
        }

        if (!IsTrustedStateChangingRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "请求来源验证失败。" });
            return;
        }

        if (context.Request.ContentLength > settings.MaxUploadBytes + 16L * 1024 * 1024)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "上传内容超过最大限制。" });
            return;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await context.Response.WriteAsJsonAsync(new { error = "上传必须使用 multipart/form-data。" });
            return;
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 200)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "上传边界无效。" });
            return;
        }

        var transferId = context.Request.Headers["X-Transfer-Id"].ToString();
        var declaredSize = long.TryParse(context.Request.Headers["X-File-Size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? size
            : 0;
        if (declaredSize > settings.MaxUploadBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "文件超过最大上传大小。" });
            return;
        }

        var reader = new MultipartReader(boundary, context.Request.Body);
        string? temporary = null;
        var tracked = false;
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(context.RequestAborted)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rawName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue
                    ? disposition.FileNameStar
                    : disposition.FileName).Value;
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                var fileName = Path.GetFileName(rawName);
                if (!FileNamePolicy.IsSafeLeafName(fileName, out var nameError))
                {
                    throw new InvalidDataException(nameError);
                }

                temporary = catalog.CreateTemporaryPath();
                tracked = !string.IsNullOrWhiteSpace(transferId) && transfers.Begin(transferId, "upload", fileName, declaredSize);
                long written = 0;
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = await section.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        written += read;
                        if (written > settings.MaxUploadBytes)
                        {
                            throw new UploadTooLargeException();
                        }
                        await destination.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                        if (tracked) transfers.Report(transferId, written);
                    }
                    await destination.FlushAsync(context.RequestAborted);
                }

                var item = await catalog.CommitTemporaryAsync(temporary, fileName, settings.DuplicateBehavior, context.RequestAborted);
                temporary = null;
                if (tracked) transfers.Complete(transferId, written);
                log.Info($"上传 · {item.Name} · {GetClientIp(context)}");
                await context.Response.WriteAsJsonAsync(item, context.RequestAborted);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "请求中没有文件。" });
        }
        catch (UploadTooLargeException)
        {
            if (tracked) transfers.Fail(transferId, "文件超过最大上传大小。");
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "文件超过最大上传大小。" });
        }
        catch (DuplicateFileException exception)
        {
            if (tracked) transfers.Fail(transferId, exception.Message);
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (InvalidDataException exception)
        {
            if (tracked) transfers.Fail(transferId, exception.Message);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            if (tracked) transfers.Fail(transferId, "磁盘空间不足。");
            context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
            await context.Response.WriteAsJsonAsync(new { error = "磁盘空间不足。" });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (tracked) transfers.Fail(transferId, "上传已中断。");
        }
        catch (UnauthorizedAccessException exception)
        {
            if (tracked) transfers.Fail(transferId, "共享目录没有写入权限。");
            log.Error("上传被拒绝", exception);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "共享目录没有写入权限。" });
        }
        catch (IOException exception)
        {
            if (tracked) transfers.Fail(transferId, "写入共享目录失败。");
            log.Error("上传写入失败", exception);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "写入共享目录失败，请检查文件是否被占用或目录是否可用。" });
        }
        catch (Exception exception)
        {
            if (tracked) transfers.Fail(transferId, "上传失败。");
            log.Error("上传发生未知错误", exception);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "上传失败，请稍后重试。" });
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    private async Task HandleDeleteAsync(HttpContext context, string fileName)
    {
        if (!config.Current.AllowWebDelete)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Web 删除已关闭。" });
            return;
        }

        if (!IsTrustedStateChangingRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "请求来源验证失败。" });
            return;
        }

        try
        {
            await catalog.DeleteAsync(fileName, context.RequestAborted);
            log.Info($"删除 · {fileName} · {GetClientIp(context)}");
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (FileNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (InvalidDataException exception)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
    }

    private async Task HandleEventsAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.ContentType = "text/event-stream";
        using var subscription = events.Subscribe();
        await context.Response.WriteAsync("retry: 2000\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var messageTask = subscription.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
                var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
                if (await Task.WhenAny(messageTask, heartbeatTask) == messageTask)
                {
                    if (!await messageTask)
                    {
                        break;
                    }
                    while (subscription.Reader.TryRead(out var json))
                    {
                        await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
                    }
                }
                else
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                }
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private async Task HandleFileAsync(HttpContext context, string fileName)
    {
        string path;
        try
        {
            path = catalog.ResolveExisting(fileName);
        }
        catch (FileNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var info = new FileInfo(path);
        var (contentType, canPreview) = SafeContentTypes.Get(info.Name);
        var forceDownload = context.Request.Query["download"] == "1" || !canPreview;
        var transferId = context.Request.Query["transferId"].ToString();
        var transferLength = GetRequestedLength(context, info.Length);
        var tracked = context.Request.Method == HttpMethods.Get && forceDownload &&
                      !string.IsNullOrWhiteSpace(transferId) &&
                      transfers.Begin(transferId, "download", info.Name, transferLength);

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        FileStream fileStream;
        try
        {
            fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            if (tracked) transfers.Fail(transferId, "文件已删除。");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "文件不存在或已删除。" });
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (tracked) transfers.Fail(transferId, "文件不可访问。");
            log.Error($"读取文件失败：{info.Name}", exception);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "文件暂时无法访问。" });
            return;
        }
        var result = Results.File(
            fileStream,
            contentType,
            fileDownloadName: forceDownload ? info.Name : null,
            lastModified: info.LastWriteTimeUtc,
            entityTag: etag,
            enableRangeProcessing: true);

        if (!tracked)
        {
            if (context.Request.Method == HttpMethods.Get)
            {
                log.Info($"{(forceDownload ? "下载" : "访问")} · {info.Name} · {GetClientIp(context)}");
            }
            await result.ExecuteAsync(context);
            return;
        }

        var originalBody = context.Response.Body;
        var progressBody = new ProgressWriteStream(originalBody, bytes => transfers.Report(transferId, bytes));
        context.Response.Body = progressBody;
        try
        {
            log.Info($"下载 · {info.Name} · {GetClientIp(context)}");
            await result.ExecuteAsync(context);
            transfers.Complete(transferId, progressBody.BytesWritten);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            transfers.Fail(transferId, "下载已中断。");
            throw;
        }
        finally
        {
            if (tracked) transfers.Fail(transferId, "下载未完成。");
            context.Response.Body = originalBody;
        }
    }

    private static bool IsTrustedStateChangingRequest(HttpContext context)
    {
        if (context.Request.Headers["X-Lan-Transfer"] != "1")
        {
            return false;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiskFull(IOException exception)
    {
        var errorCode = exception.HResult & 0xffff;
        return errorCode is 0x27 or 0x70;
    }

    private static long GetRequestedLength(HttpContext context, long fullLength)
    {
        var range = context.Request.GetTypedHeaders().Range;
        var item = range?.Ranges.Count == 1 ? range.Ranges.First() : null;
        if (item is null) return fullLength;

        var from = item.From;
        var to = item.To;
        if (from is null && to is { } suffix) return Math.Min(suffix, fullLength);
        var start = Math.Clamp(from ?? 0, 0, fullLength);
        var end = Math.Clamp(to ?? fullLength - 1, start, fullLength - 1);
        return Math.Max(0, end - start + 1);
    }

    private static string GetClientIp(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }
        return address?.ToString() ?? "未知 IP";
    }

    private sealed class UploadTooLargeException : IOException;
}

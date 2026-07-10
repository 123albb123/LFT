using System.Net.Sockets;

namespace LanFileTransfer.App.Services;

public enum ServerStartFailureKind
{
    PortInUse,
    PermissionDenied,
    AddressUnavailable,
    SharedDirectoryInvalid,
    Unknown
}

public sealed class ServerStartException(ServerStartFailureKind kind, int port, Exception innerException)
    : Exception(CreateMessage(kind, port), innerException)
{
    public ServerStartFailureKind Kind { get; } = kind;
    public int Port { get; } = port;

    public static ServerStartException Wrap(Exception exception, int port)
    {
        var socket = FindSocketException(exception);
        var kind = socket?.SocketErrorCode switch
        {
            SocketError.AddressAlreadyInUse => ServerStartFailureKind.PortInUse,
            SocketError.AccessDenied => ServerStartFailureKind.PermissionDenied,
            SocketError.AddressNotAvailable => ServerStartFailureKind.AddressUnavailable,
            _ when exception is UnauthorizedAccessException => ServerStartFailureKind.PermissionDenied,
            _ when exception is DirectoryNotFoundException or IOException => ServerStartFailureKind.SharedDirectoryInvalid,
            _ => ServerStartFailureKind.Unknown
        };
        return new ServerStartException(kind, port, exception);
    }

    private static SocketException? FindSocketException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException) return socketException;
        }
        return null;
    }

    private static string CreateMessage(ServerStartFailureKind kind, int port) => kind switch
    {
        ServerStartFailureKind.PortInUse => $"端口 {port} 已被其他程序占用。",
        ServerStartFailureKind.PermissionDenied => $"没有权限监听端口 {port}。",
        ServerStartFailureKind.AddressUnavailable => $"网络地址不可用，无法监听端口 {port}。",
        ServerStartFailureKind.SharedDirectoryInvalid => "共享目录不可用或没有访问权限。",
        _ => $"无法启动 HTTP 服务（端口 {port}）。"
    };
}

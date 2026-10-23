using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Celeste.Everest.Modules.Updating.Transport;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Networking
{
    /// <summary>Actual .NET 8 HTTP transport; no game engine or native-hook dependency.</summary>
    public sealed class SocketsCompressedHttpTransport : IEverestCompressedHttpTransport, IDisposable
    {
        private readonly object _gate = new();
        private SocketsHttpHandler? _handler;
        private bool _disposed;

        public HttpMessageHandler CreateCompressedHandler()
        {
            lock (_gate)
            {
#pragma warning disable CA1513
                // ReSharper disable once ConvertIfStatementToReturnStatement
                if (_disposed) throw new ObjectDisposedException(nameof(SocketsCompressedHttpTransport));
#pragma warning restore CA1513
                // The original type initializer owns one handler, shared by clients
                // with disposeHandler:false. The physical transport owns its disposal.
                return _handler ??= new()
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectCallback = ConnectIpv4Async,
                };
            }
        }

        private static async ValueTask<Stream> ConnectIpv4Async(
            SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            if (context.DnsEndPoint.AddressFamily != AddressFamily.Unspecified &&
                context.DnsEndPoint.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("no IPv4 address");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new DnsEndPoint(context.DnsEndPoint.Host,
                        context.DnsEndPoint.Port, AddressFamily.InterNetwork), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _handler?.Dispose();
                _handler = null;
            }
        }
    }
}

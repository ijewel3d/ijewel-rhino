using Fleck;
using Rhino;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ijewel3D
{
    internal interface IjewelStreamClient
    {
        bool IsOpen { get; }
        Task SendAsync(byte[] payload, CancellationToken cancellationToken);
        void Close();
    }

    internal sealed class TCPServer : IDisposable
    {
        private readonly int _port;
        private readonly IjewelStreamChangeDatabase _streamDatabase;
        private WebSocketServer _server;
        private bool _disposed;

        public TCPServer(int port, IjewelStreamChangeDatabase streamDatabase)
        {
            _port = port;
            _streamDatabase = streamDatabase ?? throw new ArgumentNullException(nameof(streamDatabase));
        }

        public void Start()
        {
            if (_server != null) return;

            _server = new WebSocketServer($"ws://0.0.0.0:{_port}/ws/model");
            _server.Start(socket =>
            {
                var client = new FleckStreamClient(socket);

                socket.OnOpen = () => _streamDatabase.AddClient(client);
                socket.OnClose = () => _streamDatabase.RemoveClient(client);
                socket.OnError = ex =>
                {
                    RhinoApp.WriteLine($"Model WebSocket error: {ex.Message}");
                    _streamDatabase.RemoveClient(client);
                };
            });
        }

        public void Stop()
        {
            if (_server == null) return;

            try { _server.Dispose(); }
            catch { }

            _server = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private sealed class FleckStreamClient : IjewelStreamClient
        {
            private readonly IWebSocketConnection _socket;

            public FleckStreamClient(IWebSocketConnection socket)
            {
                _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            }

            public bool IsOpen => _socket.IsAvailable;

            public Task SendAsync(byte[] payload, CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled(cancellationToken);
                }

                return _socket.Send(payload);
            }

            public void Close()
            {
                _socket.Close();
            }
        }
    }
}

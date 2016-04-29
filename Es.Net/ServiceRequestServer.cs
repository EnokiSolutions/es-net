using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Es.Net
{
    internal sealed class ServiceRequestServer : IServer
    {
        private readonly ushort _port;
        private readonly IDictionary<string, IServiceCallHandler> _handlerMapping;
        private readonly IPAddress _ip;
        private readonly Action<string> _log;
        private const int ListenBacklogSize = 256;
        private const int BufferSize = 1024*128;
        private const int MaxRequestSize = 1024*1024;

        public ServiceRequestServer(IPAddress ip, ushort port, IDictionary<string, IServiceCallHandler> handlerMapping, Action<string> log)
        {
            _ip = ip;
            _port = port;
            _handlerMapping = handlerMapping;
            _log = log ?? (_=>{});
        }

        public async Task Run(CancellationToken token)
        {
            var socketAsyncEventArgs = new SocketAsyncEventArgs();
            var awaitable = new SocketAwaitable(socketAsyncEventArgs, token);
            while (!token.IsCancellationRequested)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IPv4)
                {
                    Blocking = false,
                    NoDelay = true,
                    SendTimeout = 1000,
                    ReceiveTimeout = 1000
                };

                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                var bindEndpoint = new IPEndPoint(_ip, _port);
                s.Bind(bindEndpoint);
                s.Listen(ListenBacklogSize);

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await s.AcceptAsync(awaitable);
                        HandleConnection(socketAsyncEventArgs.AcceptSocket, token);
                    }
                }
                catch (Exception ex)
                {
                    _log($"{ex}");
                }
            }
        }

        private static readonly byte[] _contentLengthUpper = { 0x43, 0x4F, 0x4E, 0x54, 0x45, 0x4E, 0x54, 0x2D, 0x4C, 0x45, 0x4E, 0x47, 0x54, 0x48 };
        private static readonly byte[] _contentLengthLower = { 0x63, 0x6F, 0x6E, 0x74, 0x65, 0x6E, 0x74, 0x2D, 0x6C, 0x65, 0x6E, 0x67, 0x74, 0x68 };

        private async void HandleConnection(Socket remoteSocket, CancellationToken token)
        {
            var args = new SocketAsyncEventArgs();
            var buffer = new byte[BufferSize];
            args.SetBuffer(buffer, 0, buffer.Length);
            var awaitable = new SocketAwaitable(args, token);

            var requestBuffer = new byte[MaxRequestSize*2];

            var requestBufferUsed = 0;
            var eohi = 0;
            var endOfHeadersFound = false;

            while(!token.IsCancellationRequested)
            {
                await remoteSocket.ReceiveAsync(awaitable);

                var n = args.BytesTransferred;
                if (n == 0)
                {
                    // client hung up
                    remoteSocket.Close();
                    break; 
                }

                // each request MUST match: POST / HTTP/1.1\r\nHost: ...\r\nContent-Length: 0\r\n\r\nDATA
                // DATA is sized by header Content-Length: bytes (int in base 10 ascii) \r\n
                //
                // so we need at least 50 bytes of data before we even bother looking, and we can skip over 40 chars in the initial search

                Buffer.BlockCopy(args.Buffer, 0, requestBuffer, requestBufferUsed, n);
                requestBufferUsed += n;

                if (!endOfHeadersFound)
                { 
                    // first packet of data, and it's large enough it might have the entire header payload already
                    // search for \r\n\r\n (end of headers)

                    while (eohi < requestBufferUsed - 4)
                    {
                        if (requestBuffer[eohi] == '\r'
                            && requestBuffer[eohi + 1] == '\n'
                            && requestBuffer[eohi + 2] == '\r'
                            && requestBuffer[eohi + 3] == '\n')
                        {
                            endOfHeadersFound = true;
                            break;
                        }
                        ++eohi;
                    }

                    if (!endOfHeadersFound)
                    {
                        continue; // get more data before continuing
                    }

                    {
                        // find the content-length
                        // start at hn and go backwards
                        var xi = eohi - 4;
                        var contentLength = -1;

                        while (xi > _contentLengthLower.Length + 1)
                        {
                            if (requestBuffer[xi] == ':')
                            {
                                var yi = xi - 1 - _contentLengthLower.Length;
                                var foundContentLength = true;

                                for (var i = 0; i < _contentLengthLower.Length; ++i)
                                {
                                    if (requestBuffer[yi] == _contentLengthLower[i]
                                        || requestBuffer[yi] == _contentLengthUpper[i]) continue;
                                    foundContentLength = false;
                                    break;
                                }

                                if (foundContentLength)
                                {
                                    contentLength = 0;
                                    ++xi;
                                    while (requestBuffer[xi] == ' ') ++xi;
                                    while (char.IsDigit((char) requestBuffer[xi]))
                                    {
                                        contentLength = contentLength*10 + requestBuffer[xi] - '0';
                                        ++xi;
                                    }
                                    break;
                                }
                            }
                            --xi;
                        }

                        if (contentLength < 0) // invalid request
                        {
                            remoteSocket.Shutdown(SocketShutdown.Both);
                            remoteSocket.Close();
                            break;
                        }
                    }

                }
            }
        }
    }

    internal interface IServer
    {
        Task Run(CancellationToken token);
    }
}
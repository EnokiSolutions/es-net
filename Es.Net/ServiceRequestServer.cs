using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Es.Net
{
    internal sealed class ServiceRequestServer : IServer
    {
        private const int ListenBacklogSize = 256;
        private const int BufferSize = 1024*128;
        private const int MaxRequestSize = 1024*1024;

        private static readonly byte[] ContentLengthUpper =
        {
            0x43, 0x4F, 0x4E, 0x54, 0x45, 0x4E, 0x54, 0x2D, 0x4C, 0x45,
            0x4E, 0x47, 0x54, 0x48
        };

        private static readonly byte[] ContentLengthLower =
        {
            0x63, 0x6F, 0x6E, 0x74, 0x65, 0x6E, 0x74, 0x2D, 0x6C, 0x65,
            0x6E, 0x67, 0x74, 0x68
        };

        // Bare minimum of http/1.1 (this isn't a general http server, we're abusing HTTP to get across firewalls and assume the client is one of ours.) 
        // we assume keep-alive

        private static readonly byte[] BadRequest = Encoding.UTF8.GetBytes("400 BAD REQUEST\r\n");

        private static readonly bool IsLittle = BitConverter.IsLittleEndian;
        private readonly IDictionary<ulong, IServiceCallHandler> _handlerMapping;
        private readonly IPAddress _ip;
        private readonly Action<string> _log;
        private readonly ushort _port;

        public ServiceRequestServer(IPAddress ip, ushort port, IDictionary<ulong, IServiceCallHandler> handlerMapping,
            Action<string> log)
        {
            _ip = ip;
            _port = port;
            _handlerMapping = handlerMapping;
            _log = log ?? (_ => { });
        }

        public async Task Run(CancellationToken token)
        {
            var socketAsyncEventArgs = new SocketAsyncEventArgs();
            var awaitable = new SocketAwaitable(socketAsyncEventArgs, token);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                    var bindEndpoint = new IPEndPoint(_ip, _port);
                    s.Bind(bindEndpoint);
                    s.Listen(ListenBacklogSize);

                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            await s.AcceptAsync(awaitable);

                            HandleConnection(socketAsyncEventArgs.AcceptSocket, token);
                            socketAsyncEventArgs.AcceptSocket = null;
                        }
                        catch (Exception ex)
                        {
                            _log($"{ex}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"{ex}");
                    throw;
                }
            }
        }

        private async void HandleConnection(Socket remoteSocket, CancellationToken token)
        {
            var recvArgs = new SocketAsyncEventArgs();
            var buffer = new byte[BufferSize];
            recvArgs.SetBuffer(buffer, 0, buffer.Length);
            var recvAwaitable = new SocketAwaitable(recvArgs, token);

            var sendArgs = new SocketAsyncEventArgs();
            var sendAwaitable = new SocketAwaitable(sendArgs, token);

            var requestBuffer = new byte[2*MaxRequestSize];

            var requestBufferUsed = 0;
            var contentLength = -1;
            var eohi = 0;
            var eori = 0;
            var endOfHeadersFound = false;

            while (!token.IsCancellationRequested)
            {
                await remoteSocket.ReceiveAsync(recvAwaitable);

                var n = recvArgs.BytesTransferred;
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

                Buffer.BlockCopy(recvArgs.Buffer, 0, requestBuffer, requestBufferUsed, n);
                requestBufferUsed += n;

                if (!endOfHeadersFound)
                {
                    // first packet of data, and it's large enough it might have the entire header payload already
                    // search for \r\n\r\n (end of headers)
                    var tmp = Encoding.UTF8.GetString(requestBuffer, 0, requestBufferUsed);
                    while (eohi <= requestBufferUsed - 4)
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
                    // find the content-length
                    // start at hn and go backwards
                    var xi = eohi;

                    while (xi > ContentLengthLower.Length + 1)
                    {
                        if (requestBuffer[xi] == ':')
                        {
                            var yi = xi - ContentLengthLower.Length;
                            var foundContentLength = true;

                            for (var i = 0; i < ContentLengthLower.Length; ++i)
                            {
                                var c = requestBuffer[yi+i];
                                if (c == ContentLengthLower[i] || c == ContentLengthUpper[i])
                                    continue;
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

                    if (contentLength < 8) // invalid request, need at least 8 bytes so we can map to a handler.
                    {
                        remoteSocket.Shutdown(SocketShutdown.Both);
                        remoteSocket.Close();
                        break;
                    }
                    // we expect contentLength bytes after eohi (last byte of headers) + 4 (\r\n\r\n after the headers)
                    eori = eohi + 4 + contentLength;
                }

                if (n >= eori)
                {
                    var offset = eohi + 4;

                    ulong shid;

                    unsafe
                    {
                        fixed (byte* b = &requestBuffer[offset])
                        {
                            shid = LoadULong(b);
                        }
                    }

                    IServiceCallHandler sh;
                    if (!_handlerMapping.TryGetValue(shid, out sh))
                    {
                        sendArgs.SetBuffer(BadRequest, 0, BadRequest.Length);
                        await remoteSocket.SendAsync(sendAwaitable);
                        remoteSocket.Close();
                        return;
                    }

                    var bufferList = new List<ArraySegment<byte>>();
                    var headers = new ArraySegment<byte>();
                    bufferList.Add(headers);

                    await sh.Handle(bufferList, token, requestBuffer, offset + 8, contentLength - 8);

                    var responseContentLength = bufferList.Skip(1).Sum(x => x.Count);
                    var headerBytes = Encoding.UTF8.GetBytes($"200 OK\r\nContent-Type: binary\r\nContent-Length: {responseContentLength}\r\n");
                    bufferList[0] = new ArraySegment<byte>(headerBytes);
                    sendArgs.BufferList = bufferList;

                    await remoteSocket.SendAsync(sendAwaitable);

                    requestBufferUsed = n - offset - contentLength;
                    if (requestBufferUsed != 0)
                    {
                        Buffer.BlockCopy(requestBuffer, eori, requestBuffer, 0, requestBufferUsed);
                    }
                    contentLength = -1;
                    eohi = 0;
                    eori = 0;
                    endOfHeadersFound = false;
                }
            }
        }

        [ExcludeFromCodeCoverage]
        private static unsafe ulong LoadULong(byte* source)
        {
            if (IsLittle)
                return source[0]
                       | ((ulong) source[1] << 8)
                       | ((ulong) source[2] << 16)
                       | ((ulong) source[3] << 24)
                       | ((ulong) source[4] << 32)
                       | ((ulong) source[5] << 40)
                       | ((ulong) source[6] << 48)
                       | ((ulong) source[7] << 56);
            return ((ulong) source[0] << 56)
                   | ((ulong) source[1] << 48)
                   | ((ulong) source[2] << 40)
                   | ((ulong) source[3] << 32)
                   | ((ulong) source[4] << 24)
                   | ((ulong) source[5] << 16)
                   | ((ulong) source[6] << 8)
                   | source[7];
        }
    }

    internal interface IServer
    {
        Task Run(CancellationToken token);
    }
}
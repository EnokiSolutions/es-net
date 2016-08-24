using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Es.Fw;
using Es.FwI;

namespace Es.Net
{
    internal sealed class SystemServer : ISystemServer
    {
        // Bare minimum of http/1.1 (this isn't a general http server, we're abusing HTTP to get across firewalls and assume the client is one of ours.) 
        // we assume keep-alive

        private readonly IDictionary<int, ISystem> _systems;
        private readonly IPAddress _ip;
        private readonly Action<string> _log;
        private readonly ushort _port;
        private readonly IIdGenerator _idGenerator;

        public SystemServer(
            IPAddress ip, 
            ushort port, 
            Action<string> log, 
            IIdGenerator idGenerator, 
            IEnumerable<ISystem> systems)
        {
            _ip = ip;
            _port = port;
            _systems = new Dictionary<int, ISystem>();
            _idGenerator = idGenerator;

            foreach(var system in systems)
                _systems[system.SystemNumber] = system;

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
                    s.Listen(NetConstants.ListenBacklogSize);

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
                            try
                            {
                                socketAsyncEventArgs.AcceptSocket?.Disconnect(true);
                                socketAsyncEventArgs.AcceptSocket?.Close();
                            }
                            catch { 
                                // ignored
                            }
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
            var buffer = new byte[NetConstants.ReceiveBufferSize];
            recvArgs.SetBuffer(buffer, 0, buffer.Length);
            var recvAwaitable = new SocketAwaitable(recvArgs, token);

            var sendArgs = new SocketAsyncEventArgs();
            var sendAwaitable = new SocketAwaitable(sendArgs, token);

            var requestByteBuffer = new ByteBuffer(2*NetConstants.MaxRequestSize);
            var responseByteBuffer = new ByteBuffer(2*NetConstants.MaxResponseSize);
            var responseBufferList = new List<ArraySegment<byte>>(2);
            var tasks = new List<Task>(16);
            var lastEventIdSeen = new Id();
            var callerId = new Id();
            var sessionId = new Id();
            var commandInstanceId = new Id();
            var key = 0UL;
            // salt is unread, hash is used to verify the packet

            var pbbs = new List<ByteBuffer>();
            {
                for (var i=0;i<16;++i)
                    pbbs[i] = new ByteBuffer(0);
            }

            var contentLength = -1;
            var endOfRequestIndex = 0;
            var endOfHeadersFound = false;

            while (!token.IsCancellationRequested)
            {
                var endOfHeadersIndex = -1;

                // read an entire request
                for (;;)
                {
                    await remoteSocket.ReceiveAsync(recvAwaitable);

                    var n = recvArgs.BytesTransferred;
                    if (n == 0)
                    {
                        // client hung up
                        remoteSocket.Close();
                    }
                    else
                    {

                        // each request MUST match: POST / HTTP/1.1\r\nHost: ...\r\nContent-Length: nnn\r\n\r\nDATA
                        // DATA is sized by header Content-Length: bytes (int in base 10 ascii) \r\n

                        ByteBuffer.WriteBytes(requestByteBuffer, recvArgs.Buffer, 0, n);
                        ByteBuffer.WriteCommit(requestByteBuffer);
                        var requestByteBufferBytes = requestByteBuffer.Bytes;
                        // do not memorize early as it can reallocate on write!

                        if (!endOfHeadersFound)
                        {
                            // search for \r\n\r\n (end of headers)
                            var maxOffsetForDoubleNewline = requestByteBuffer.WriteCommitPosition - 4;
                            while (endOfHeadersIndex <= maxOffsetForDoubleNewline)
                            {
                                if (requestByteBufferBytes[endOfHeadersIndex] == '\r'
                                    && requestByteBufferBytes[endOfHeadersIndex + 1] == '\n'
                                    && requestByteBufferBytes[endOfHeadersIndex + 2] == '\r'
                                    && requestByteBufferBytes[endOfHeadersIndex + 3] == '\n')
                                {
                                    endOfHeadersFound = true;
                                    break;
                                }
                                ++endOfHeadersIndex;
                            }

                            if (!endOfHeadersFound)
                            {
                                continue; // get more data before continuing
                            }

                            // find the content-length
                            // start at end of headers index and go backwards
                            var xi = endOfHeadersIndex;

                            while (xi > NetConstants.ContentLengthLower.Length + 1)
                            {
                                if (requestByteBufferBytes[xi] == ':')
                                {
                                    var yi = xi - NetConstants.ContentLengthLower.Length;
                                    var foundContentLength = true;

                                    for (var i = 0; i < NetConstants.ContentLengthLower.Length; ++i)
                                    {
                                        var c = requestByteBufferBytes[yi + i];
                                        if (c == NetConstants.ContentLengthLower[i] ||
                                            c == NetConstants.ContentLengthUpper[i])
                                            continue;
                                        foundContentLength = false;
                                        break;
                                    }

                                    if (foundContentLength)
                                    {
                                        contentLength = 0;
                                        ++xi;
                                        while (requestByteBufferBytes[xi] == ' ') ++xi;
                                        while (char.IsDigit((char) requestByteBufferBytes[xi]))
                                        {
                                            contentLength = contentLength*10 + requestByteBufferBytes[xi] - '0';
                                            ++xi;
                                        }
                                        break;
                                    }
                                }
                                --xi;
                            }

                            if (contentLength < 48) // invalid request.
                            {
                                remoteSocket.Shutdown(SocketShutdown.Both);
                                remoteSocket.Close();
                            }

                            if (contentLength > NetConstants.MaxRequestSize)
                            {
                                await BadRequest(remoteSocket, responseBufferList, sendArgs, sendAwaitable);
                            }
                            // we expect contentLength bytes after eohi (last byte of headers) + 4 (\r\n\r\n after the headers)
                            endOfRequestIndex = endOfHeadersIndex + 4 + contentLength;
                        }

                        if (n >= endOfRequestIndex)
                        {
                            break;
                        }
                    }
                }

                if (endOfHeadersIndex <= 0)
                    return;

                requestByteBuffer.ReadPosition = endOfHeadersIndex + 4; // skip over the headers

                int sessionTokenEndPos;
                ByteBuffer.StartTryReadPacket(requestByteBuffer, out sessionTokenEndPos, 0);
                sessionId.Ulongs[0] = ByteBuffer.ReadUlong(requestByteBuffer);
                sessionId.Ulongs[1] = ByteBuffer.ReadUlong(requestByteBuffer);
                ByteBuffer.EndReadPacket(requestByteBuffer, sessionTokenEndPos);

                key = sessionId.Ulongs[0] ^ sessionId.Ulongs[1];
                    // TODO: lookup key based on sessionId! TODO: ssh based login -> sessionId + key; may be cached from previous requests in this session via keepalive.

                int receivePacketEndPos;
                ByteBuffer.StartTryReadPacket(requestByteBuffer, out receivePacketEndPos, key);

                // read internal header data global to all commands to follow
                lastEventIdSeen.Ulongs[0] = ByteBuffer.ReadUlong(requestByteBuffer);
                lastEventIdSeen.Ulongs[1] = ByteBuffer.ReadUlong(requestByteBuffer);
                callerId.Ulongs[0] = ByteBuffer.ReadUlong(requestByteBuffer);
                callerId.Ulongs[1] = ByteBuffer.ReadUlong(requestByteBuffer);

                // write back the command ids
                var responseStartingPos = ByteBuffer.StartWritePacket(responseByteBuffer);

                var commandsToFollow = ByteBuffer.ReadInt(requestByteBuffer);

                ByteBuffer.WriteInt(responseByteBuffer, commandsToFollow); // command instance Ids to follow
                try
                {
                    var unused = pbbs[commandsToFollow - 1]; // ensure we have room.
                }
                catch
                {
                    for (var i = 0; i < commandsToFollow - pbbs.Count; ++i)
                        pbbs.Add(new ByteBuffer(0));
                }

                for (var cmdNum = 0; cmdNum < commandsToFollow; ++cmdNum)
                {
                    var systemNumber = ByteBuffer.ReadInt(requestByteBuffer);
                    var requestNumber = ByteBuffer.ReadInt(requestByteBuffer);

                    ISystem system;

                    if (!_systems.TryGetValue(systemNumber, out system))
                    {
                        await BadRequest(remoteSocket, responseBufferList, sendArgs, sendAwaitable);
                        return;
                    }

                    var pbb = pbbs[cmdNum];

                    if (!ByteBuffer.TryStartPeelPacket(requestByteBuffer, pbb))
                    {
                        await BadRequest(remoteSocket, responseBufferList, sendArgs, sendAwaitable);
                        return;
                    }

                    _idGenerator.Create(commandInstanceId);

                    ByteBuffer.WriteInt(responseByteBuffer, systemNumber);
                    ByteBuffer.WriteInt(responseByteBuffer, requestNumber);
                    ByteBuffer.WriteUlong(responseByteBuffer, commandInstanceId.Ulongs[0]);
                    ByteBuffer.WriteUlong(responseByteBuffer, commandInstanceId.Ulongs[1]);

                    tasks.Add(system.ProcessCommand(callerId, sessionId, commandInstanceId, pbb, token).Background());
                }

                // salt = ByteBuffer.ReadUlong(requestByteBuffer);

                ByteBuffer.EndReadPacket(requestByteBuffer, receivePacketEndPos); // done reading commands
                ByteBuffer.ReadCommit(requestByteBuffer);

                await Task.WhenAll(tasks);
                tasks.Clear();

                for (var i = 0; i < commandsToFollow; ++i)
                    ByteBuffer.EndPeelPacket(pbbs[i]);

                foreach (var system in _systems)
                {
                    tasks.Add(
                        system.Value.GetStateWriter(lastEventIdSeen, callerId)
                            .Background()
                            .ContinueWith(t =>
                            {
                                if (t.IsCanceled)
                                    return;

                                var stateWriter = t.Result;
                                if (stateWriter == null)
                                    return;

                                lock (responseByteBuffer)
                                {
                                    ByteBuffer.WriteInt(responseByteBuffer, system.Key);
                                    var sp = ByteBuffer.StartWritePacket(responseByteBuffer);
                                    stateWriter(responseByteBuffer);
                                    ByteBuffer.EndWriteInnerPacket(responseByteBuffer, sp);
                                }
                            },
                                token
                            )
                        );
                }
                await Task.WhenAll(tasks);
                tasks.Clear();

                ByteBuffer.EndWritePacket(responseByteBuffer, responseStartingPos, ~key); // done adding the command ids and system state responses
                ByteBuffer.WriteCommit(responseByteBuffer);

                var responseContentLength = responseByteBuffer.Count;
                var headerBytes =
                    Encoding.UTF8.GetBytes(
                        $"200 OK\r\nContent-Type: binary\r\nContent-Length: {responseContentLength}\r\n"
                        );

                responseBufferList.Clear();
                responseBufferList.Add(new ArraySegment<byte>(headerBytes));
                responseBufferList.Add(
                    new ArraySegment<byte>(
                        responseByteBuffer.Bytes,
                        responseByteBuffer.ReadPosition, 
                        responseByteBuffer.WriteCommitPosition
                    )
                );

                sendArgs.BufferList = responseBufferList;

                await remoteSocket.SendAsync(sendAwaitable);

                ByteBuffer.Reset(responseByteBuffer);

                contentLength = -1;
                endOfHeadersIndex = 0;
                endOfRequestIndex = 0;
                endOfHeadersFound = false;
            }
        }

        private static async Task BadRequest(Socket remoteSocket, List<ArraySegment<byte>> responseBufferList, SocketAsyncEventArgs sendArgs,
            SocketAwaitable sendAwaitable)
        {
            var headerBytes = Encoding.UTF8.GetBytes($"400 BAD REQUEST\r\n");
            responseBufferList.Clear();
            responseBufferList.Add(new ArraySegment<byte>(headerBytes));
            sendArgs.BufferList = responseBufferList;
            await remoteSocket.SendAsync(sendAwaitable);
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Es.Fw;
using NUnit.Framework;

namespace Es.Net.Test
{
    [TestFixture]
    public sealed class ServerTf
    {
        private sealed class Echo : IServiceCallHandler
        {

            public Task Handle(List<ArraySegment<byte>> bufferList, CancellationToken token, byte[] requestBuffer, int offset, int count)
            {
                bufferList.Add(new ArraySegment<byte>(requestBuffer,offset-8,count+8));
                return TaskEx.Done;
            }
        }

        [Test]
        public void Test()
        {
            IServer sc = new ServiceRequestServer(IPAddress.Any, 666, new Dictionary<ulong, IServiceCallHandler> { { 1, new Echo() } },Console.WriteLine);
            var cts = new CancellationTokenSource();
            var sct = sc.Run(cts.Token);

            var data = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 3, 4, 5, 6 };
            var t = SendEcho(cts.Token, data);
            t.Wait();
            var result = t.Result;
            Assert.IsTrue(result.Eq(data));

            cts.Cancel();
            sct.Wait();
        }

        private static async Task<byte[]> SendEcho(CancellationToken token, byte[] data)
        {
            // TODO make client
            var ipEndPoint = new IPEndPoint(IPAddress.Loopback, 666);
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var args = new SocketAsyncEventArgs();
            var awaitable = new SocketAwaitable(args, token);
            args.RemoteEndPoint = ipEndPoint;
            
            await s.ConnectAsync(awaitable);

            var headerData = Encoding.UTF8.GetBytes($"POST / HTTP/1.1\r\nHost: localhost:666\r\nContent-Type: binary\r\nContent-Length: {data.Length}\r\n\r\n");

            args.BufferList = new List<ArraySegment<byte>>
            {
                new ArraySegment<byte>(headerData),
                new ArraySegment<byte>(data)
            };

            var connectSocket = args.ConnectSocket;
            await connectSocket.SendAsync(awaitable);

            var recvBuffer = new byte[1024];
            args.BufferList = null;
            args.SetBuffer(recvBuffer,0,recvBuffer.Length);
            await connectSocket.ReceiveAsync(awaitable);

            var r = args.Buffer.Skip(args.BytesTransferred-data.Length).Take(data.Length).ToArray();

            args.DisconnectReuseSocket = true;
            await connectSocket.DisconnectAsync(awaitable);
            connectSocket.Close();

            return r;
        }
    }
}

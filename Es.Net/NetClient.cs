using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Es.Fw;
using Es.FwI;

namespace Es.Net
{
    // need to put some thought into why we have client/server as separate parts ...
    internal sealed class NetClient : IRun
    {
        private readonly IDictionary<int, ISystemClient> _systemsClients;
        private readonly Action<string> _log;
        private readonly IPEndPoint _destinationEndPoint;
        private readonly int _autoDisconnectCount;
        private readonly ByteBuffer _sendByteBuffer = new ByteBuffer(NetConstants.MaxRequestSize);
        private readonly ByteBuffer _recvByteBuffer = new ByteBuffer(NetConstants.MaxResponseSize);
        private readonly Socket _socket;
        private readonly SocketAsyncEventArgs _connectArgs = new SocketAsyncEventArgs();
        private readonly SocketAsyncEventArgs _receiveArgs = new SocketAsyncEventArgs();
        private readonly SocketAsyncEventArgs _sendArgs = new SocketAsyncEventArgs();
        private readonly byte[] _receiveBuffer = new byte[NetConstants.ReceiveBufferSize];
        private readonly List<ArraySegment<byte>> _sendBufferList = new List<ArraySegment<byte>>();
        private int _emptyPumps;

        private Id _userId = new Id(); // TODO
        private Id _sessionId = new Id(); // TODO
        private ulong _sessionKey = 1; // TODO

        sealed class DeferredSendData
        {
            public readonly int SystemNumber;
            public readonly int RequestNumber;
            public readonly Action<ByteBuffer> Writer;

            public DeferredSendData(int systemNumber, int requestNumber, Action<ByteBuffer> writer)
            {
                SystemNumber = systemNumber;
                RequestNumber = requestNumber;
                Writer = writer;
            }
        }

        private readonly List<DeferredSendData> _deferredSends = new List<DeferredSendData>();

        static class ConnectionState
        {
            public const int Unconnected = 0;
            public const int Connecting = 1;
            public const int Connected = 2;
        }

        static class SendState
        {
            public const int Idle = 0;
            public const int InProgress = 1;
            public const int Failed = 2;
            public const int Completed = 3;
        }
        static class ReceiveState
        {
            public const int Idle = 0;
            public const int InProgress = 1;
            public const int Failed = 2;
            public const int Completed = 3;
        }

        private int _connectionState = ConnectionState.Unconnected;
        private int _sendState = SendState.Idle;
        private int _receiveState = SendState.Idle;

        public NetClient(
            IPAddress ip,
            ushort port, 
            int autoDisconnectCount,
            Action<string> log,
            IEnumerable<ISystemClient> systemClients)
        {
            _autoDisconnectCount = autoDisconnectCount;
            _log = log ?? (_ => { });
            _systemsClients = new Dictionary<int, ISystemClient>();

            foreach (var systemClient in systemClients)
                _systemsClients[systemClient.Number] = systemClient;

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { Blocking = false };
            _destinationEndPoint = new IPEndPoint(ip, port);

            _connectArgs.Completed += OnConnect;
            _connectArgs.RemoteEndPoint = _destinationEndPoint;

            _receiveArgs.Completed += OnReceive;
            _receiveArgs.SetBuffer(_receiveBuffer,0,_receiveBuffer.Length);
            _sendArgs.Completed += OnSend;
            _sendArgs.BufferList = _sendBufferList;
        }

        private void OnConnect(object sender, SocketAsyncEventArgs e)
        {
            if (_connectionState == ConnectionState.Unconnected)
            {
                if (_connectArgs.ConnectSocket == null)
                    return;

                _connectArgs.ConnectSocket.Disconnect(true);
                _connectArgs.ConnectSocket.Close();
                return;
            }

            _connectionState = ConnectionState.Connected;
        }
        private void OnSend(object sender, SocketAsyncEventArgs e)
        {
        }
        private void OnReceive(object sender, SocketAsyncEventArgs e)
        {
        }

        private void Connect()
        {
            if (_connectionState != ConnectionState.Unconnected)
                return;

            _connectionState = ConnectionState.Connecting;

            _socket.ConnectAsync(_connectArgs);
        }

        public void Disconnect()
        {
            if (_connectionState == ConnectionState.Connecting || _connectionState == ConnectionState.Connected && _connectArgs.ConnectSocket != null && _connectArgs.ConnectSocket.Connected)
            {
                _connectArgs.ConnectSocket.Disconnect(true);
                _connectArgs.ConnectSocket.Close();
            }
            _connectionState = ConnectionState.Unconnected;
        }

        public void Pump()
        {
            if (_connectionState != ConnectionState.Connected)
            {
                if (_deferredSends.Count > 0 && _connectionState != ConnectionState.Connecting)
                {
                    Connect();
                }
                return;
            }

            var didAnything = false;

            // check send
            if ((_sendState == SendState.Idle && _deferredSends.Count > 0) || _sendState == SendState.Failed)
            {
                didAnything = true;
                Send();
            }

            // check receive
            if (_receiveState == ReceiveState.Idle)
            {
                Receive();
            }

            if (_receiveState == ReceiveState.Completed)
            {
                ProcessData();
                Receive();
            }

            if (!didAnything)
            {
                ++_emptyPumps;

                if (_emptyPumps >= _autoDisconnectCount)
                {
                    Disconnect();
                }
            }
        }

        private void ProcessData()
        {
            throw new NotImplementedException();
        }

        private void Receive()
        {
            throw new NotImplementedException();
        }

        private void Send()
        {
            var sp = ByteBuffer.StartWritePacket(_sendByteBuffer);
            foreach (var x in _deferredSends)
            {
                ByteBuffer.WriteInt(_sendByteBuffer, x.SystemNumber);
                ByteBuffer.WriteInt(_sendByteBuffer, x.RequestNumber);
                x.Writer(_sendByteBuffer);

            }
            ByteBuffer.EndWritePacket(_sendByteBuffer,sp,_sessionKey);

            _deferredSends.Clear();
        }

        public void ScheduleSend(int systemNumber, int requestNumber, Action<ByteBuffer> writer)
        {
            _deferredSends.Add(new DeferredSendData(systemNumber,requestNumber,writer));
        }

        public Task Run(CancellationToken token)
        {
            throw new NotImplementedException();
        }
    }
}
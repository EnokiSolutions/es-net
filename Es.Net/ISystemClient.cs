using System;
using Es.Fw;

namespace Es.Net
{
    public interface ISystemClient
    {
        int Number { get; }
        void Pump();
        void Disconnect();
        void ScheduleSend(int systemNumber, int requestNumber, Action<ByteBuffer> writer);
        void ApplyUpdate(int requestNumber, ByteBuffer byteBuffer);

    }
}
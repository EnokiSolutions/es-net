using System;
using Es.Fw;

namespace Es.Net
{
    public interface ISystemClient
    {
        void Pump();
        void Disconnect();
        void ScheduleSend(int systemNumber, int requestNumber, Action<ByteBuffer> writer);
    }
}
#define SERVER
#define CLIENT

using System;
using System.Threading;
using System.Threading.Tasks;
using Es.Fw;
using Es.FwI;

namespace Es.Net
{
    internal interface ISystem
    {
        int SystemNumber { get; }

#if SERVER
        Task ProcessCommand(Id callerId, Id sessionId, Id commandInstanceId, ByteBuffer peeledByteBuffer, CancellationToken token);

        Task<Action<ByteBuffer>> GetStateWriter(Id lastEventIdSeen, Id callerId);
#endif

#if CLIENT
        void ApplyUpdate(int requestNumber, ByteBuffer byteBuffer);

        ISystemClient SystemClient { set; get; }
#endif
    }
}
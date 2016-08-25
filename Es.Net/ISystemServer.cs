using System;
using System.Threading;
using System.Threading.Tasks;
using Es.Fw;
using Es.FwI;

namespace Es.Net
{
    public interface ISystemServer
    {
        int Number { get; }
        Task ProcessCommand(Id callerId, Id sessionId, Id commandInstanceId, ByteBuffer peeledByteBuffer, CancellationToken token);
        Task<Action<ByteBuffer>> GetStateWriter(Id lastEventIdSeen, Id callerId);
    }
}
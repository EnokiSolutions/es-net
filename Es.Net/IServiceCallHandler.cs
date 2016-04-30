using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Es.Net
{
    public interface IServiceCallHandler
    {
        Task Handle(List<ArraySegment<byte>> bufferList, CancellationToken token, byte[] requestBuffer, int offset, int count);
    }
}
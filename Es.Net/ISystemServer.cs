using System.Threading;
using System.Threading.Tasks;

namespace Es.Net
{
    public interface ISystemServer
    {
        Task Run(CancellationToken token);
    }
}
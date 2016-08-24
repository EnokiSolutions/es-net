using Es.FwI;

namespace Es.Net
{
    internal interface ISession
    {
        Id Id { get; }
        Id CallerId { get; }
        string Data { get; }
    }
}
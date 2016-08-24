using Es.FwI;

namespace Es.Net
{
    internal interface ISessionFactory
    {
        ISession New(Id callerId, string data);
        ISession Find(Id sessionId);
        ISession Update(Id sessionId, string data);
    }
}
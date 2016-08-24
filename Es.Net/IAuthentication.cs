using Es.FwI;

namespace Es.Net
{
    internal interface IAuthentication
    {
        bool Authenticate(Id userId, string authInfo);
    }
}
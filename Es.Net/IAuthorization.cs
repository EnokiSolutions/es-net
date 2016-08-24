using Es.FwI;

namespace Es.Net
{
    internal interface IAuthorization
    {
        bool Can(Id userId, Id whatId);
    }
}
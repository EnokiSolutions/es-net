namespace Es.Net
{
    internal static class NetConstants
    {
        internal const int ListenBacklogSize = 256;
        internal const int ReceiveBufferSize = 1024 * 128;
        internal const int MaxRequestSize = 1024 * 1024;
        internal const int MaxResponseSize = 1024 * 1024;

        internal static readonly byte[] ContentLengthUpper =
        {
            0x43, 0x4F, 0x4E, 0x54, 0x45, 0x4E, 0x54, 0x2D, 0x4C, 0x45,
            0x4E, 0x47, 0x54, 0x48
        };

        internal static readonly byte[] ContentLengthLower =
        {
            0x63, 0x6F, 0x6E, 0x74, 0x65, 0x6E, 0x74, 0x2D, 0x6C, 0x65,
            0x6E, 0x67, 0x74, 0x68
        };
    }
}
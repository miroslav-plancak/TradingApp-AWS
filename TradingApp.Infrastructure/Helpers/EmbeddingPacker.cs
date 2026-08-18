using System.Runtime.InteropServices;

namespace TradingApp.Infrastructure.Helpers
{
    public static class EmbeddingPacker
    {
        public static byte[] RePackEmbeddingFromFloatToByte(float[] embedding)
        {
            return MemoryMarshal.Cast<float, byte>(embedding).ToArray();
        }
    }
}

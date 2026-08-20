using System.Runtime.InteropServices;

namespace TradingApp.Infrastructure.Helpers
{
    public static class TextChunker
    {
        public static List<string> ChunkText(string text, int chunkSize = 1500, int overlap = 200)
        {
            var chunks = new List<string>();
            var start = 0;

            while (start < text.Length)
            {
                var length = Math.Min(chunkSize, text.Length - start);
                chunks.Add(text.Substring(start, length));

                if (start + length >= text.Length)
                {
                    break;
                }

                start += chunkSize - overlap;
            }

            return chunks;
        }
    }
}

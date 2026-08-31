using System.Text;
using TradingApp.Infrastructure.Models;

namespace TradingApp.Infrastructure.Helpers
{
    public static class RetrievalResultLogFormatter
    {
        public static string FormatRetrievalResultIntoFileLog(RetrievalResult retrievalResult)
        {
            var helperDivider = new string('-', 80);
            var sb = new StringBuilder();
            var maxChunks = retrievalResult.ChunkFallbacks.Count;
            var maxFullFiles = retrievalResult.FullFileContents.Count;

            if (retrievalResult.ChunkFallbacks.Count != 0)
            {
                sb.AppendLine(helperDivider);
                var chunkNumberCounter = 0;
                foreach (var chunk in retrievalResult.ChunkFallbacks)
                {
                    sb.AppendLine($"CHUNK #{chunkNumberCounter}");
                    sb.AppendLine($"SOURCEFILE: {chunk.SourceFile}");
                    sb.AppendLine();
                    sb.AppendLine($"KNNSCORE: {chunk.KnnScore}");
                    sb.AppendLine($"LEXICALSCORE: {chunk.LexicalScore}");
                    sb.AppendLine($"RELEVANCESCORE: {chunk.RelevanceScore}");
                    sb.AppendLine($"RECIPROCALRANKFUSIONSCORE: {chunk.ReciprocalRankFusionScore}");
                    sb.AppendLine();
                    sb.AppendLine($"CONTENT:\n{chunk.Content}");
                    chunkNumberCounter++;

                    if (maxChunks >= chunkNumberCounter)
                    {
                        sb.AppendLine(helperDivider);
                    }
                }

            }

            if (retrievalResult.FullFileContents.Count != 0)
            {
                sb.AppendLine(helperDivider);
                var fileNumberCounter = 0;
                foreach (var file in retrievalResult.FullFileContents)
                {
                    sb.AppendLine($"FILE #{fileNumberCounter}");
                    sb.AppendLine($"FILENAME: {file.Key}");
                    sb.AppendLine();
                    sb.AppendLine($"CONTENT:\n{file.Value}");
                    fileNumberCounter++;

                    if (maxFullFiles > fileNumberCounter)
                    {
                        sb.AppendLine(helperDivider);
                    }
                }

            }

            return sb.ToString();
        }

        public static string FormatRerankResultIntoFileLog(List<RerankResult> rerankResults)
        {
            var helperDivider = new string('-', 80);
            var sb = new StringBuilder();
            var maxRerankResults = rerankResults.Count;

            if (rerankResults.Count != 0)
            {
                sb.AppendLine(helperDivider);
                var rerankResultCounter = 0;

                foreach (var rerankResult in rerankResults)
                {
                    sb.AppendLine($"INDEX: {rerankResult.Index}");
                    sb.AppendLine($"RELEVANCE SCORE: {rerankResult.RelevanceScore}");
                    rerankResultCounter++;

                    if (maxRerankResults > rerankResultCounter)
                    {
                        sb.AppendLine(helperDivider);
                    }
                }
            }

            return sb.ToString();
        }
    }
}

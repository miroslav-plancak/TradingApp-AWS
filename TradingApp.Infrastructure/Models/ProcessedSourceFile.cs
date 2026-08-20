namespace TradingApp.Infrastructure.Models
{
    public class ProcessedSourceFile
    {
        public string FileName { get; set; } = string.Empty;
        public string FileContent { get; set; } = string.Empty;
        public bool ExceedsFullIndexCap { get; set; }
    }
}

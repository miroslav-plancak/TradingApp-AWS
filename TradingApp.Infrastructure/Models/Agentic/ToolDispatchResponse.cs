namespace TradingApp.Infrastructure.Models.Agentic
{
    public class ToolDispatchResponse
    {
       public string Text { get; set; } = string.Empty;
       public bool IsError { get; set; }
       public int CurrentToolCallNumber { get; set; }
    }
}

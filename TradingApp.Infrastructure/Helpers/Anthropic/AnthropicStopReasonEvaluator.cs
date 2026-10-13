using Anthropic.Models.Messages;

namespace TradingApp.Infrastructure.Helpers.Anthropic
{
    public class AnthropicStopReasonEvaluator
    {
        public static bool IsNotCoveredStopReason(StopReason stopReason)
        {
            switch (stopReason)
            {
                case StopReason.EndTurn:
                    return false;
                case StopReason.PauseTurn:
                    return true;
                case StopReason.StopSequence:
                    return true;
                case StopReason.ToolUse:
                    return false;
                case StopReason.Refusal:
                    return true;
                case StopReason.ModelContextWindowExceeded:
                    return true;
                case StopReason.MaxTokens:
                    return false;
                default:
                    return false;
            }
        }

        public static bool IsCoveredStopReason(StopReason stopReason)
        {
            switch (stopReason)
            {
                case StopReason.EndTurn:
                    return false;
                case StopReason.PauseTurn:
                    return false;
                case StopReason.StopSequence:
                    return false;
                case StopReason.ToolUse:
                    return true;
                case StopReason.Refusal:
                    return false;
                case StopReason.ModelContextWindowExceeded:
                    return false;
                case StopReason.MaxTokens:
                    return true;
                default:
                    return false;
            }
        }
    }
}

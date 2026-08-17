using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;
using System.Collections.Generic;

namespace TradingApp.API.Hubs
{
    public class AiChatHub : Hub
    {
        private readonly ILogger<AiChatHub> _logger;
        private readonly AnthropicClient _anthropicClient;

        public AiChatHub(ILogger<AiChatHub> logger, AnthropicClient anthropicClient)
        {
            _logger = logger;
            _anthropicClient = anthropicClient;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("AiChatHub client connected | ConnectionId: {ConnectionId}", Context.ConnectionId);

            return base.OnConnectedAsync();
        }

        public async IAsyncEnumerable<string> Ask(string question)
        {
            var parameters = new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = 4096,
                Messages = [new () { Role = Role.User, Content = question}]
            };

            await foreach(var streamEvent in _anthropicClient.Messages.CreateStreaming(parameters))
            {
                if(streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    yield return text.Text;
                }
            }
        }
    }
}

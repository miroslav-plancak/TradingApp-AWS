using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Conversation;
using TradingApp.Business.DTOs.ConversationMessage;
using TradingApp.Business.Interfaces.Services.Regular;

namespace TradingApp.API.Controllers
{
    public class ConversationController : TradingAppBaseController<ConversationController>
    {
        private readonly IConversationService _conversationService;
        public ConversationController
        (
            ILogger<ConversationController> logger,
            IConversationService conversationService
        ) : base(logger)
        {
            _conversationService = conversationService;
        }

        [HttpGet("{conversationId}")]
        [ProducesResponseType(typeof(CreatedConversationResponseDTO), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult> GetConversationByIdAsync([FromRoute] Guid conversationId)
        {
            _logger.LogInformation("GetConversationByIdAsyncRequest");

            var result = await _conversationService.GetConversationByIdAsync(conversationId);

            return Ok(result);
        }

        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<CreatedConversationResponseDTO>), 200)]
        public async Task<ActionResult> GetConversationsAsync()
        {
            _logger.LogInformation("GetConversationsAsyncRequest");

            var result = await _conversationService.GetConversationsAsync();

            return Ok(result);
        }

        [HttpGet("{conversationId}/messages")]
        [ProducesResponseType(typeof(IEnumerable<ConversationHistoryMessageDTO>), 200)]
        public async Task<ActionResult> GetConversationMessagesAsync([FromRoute] Guid conversationId)
        {
            _logger.LogInformation("GetConversationMessagesAsyncRequest");

            var result = await _conversationService.GetConversationMessagesAsync(conversationId, null);

            return Ok(result);
        }

        [HttpDelete("{conversationId}")]
        [ProducesResponseType(200)]
        public async Task<ActionResult> DeleteConversationByIdAsync([FromRoute] Guid conversationId)
        {
            _logger.LogInformation("DeleteConversationByIdAsyncRequest");

            var result = await _conversationService.DeleteConversationByIdAsync(conversationId);

            return Ok(result);
        }
    }
}

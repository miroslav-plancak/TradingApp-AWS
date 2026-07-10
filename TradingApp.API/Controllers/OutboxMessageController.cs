using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingApp.Business.DTOs.Outbox;
using TradingApp.Business.Interfaces.Services;

namespace TradingApp.API.Controllers
{
    public class OutboxMessageController : TradingAppBaseController<OutboxMessageController>
    {
        private readonly IOutboxMessageService _outboxMessageService;

        public OutboxMessageController(
            ILogger<OutboxMessageController> logger,
            IOutboxMessageService outboxMessageService)
            : base(logger)
        {
            _outboxMessageService = outboxMessageService;
        }

        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<OutboxMessageResponseDTO>), 200)]
        public async Task<ActionResult> GetAllAsync()
        {
            _logger.LogInformation("GetAllOutboxMessagesRequest");

            var result = await _outboxMessageService.GetAllAsync();
            return Ok(result);
        }

        [HttpGet("unprocessed")]
        [ProducesResponseType(typeof(IEnumerable<OutboxMessageResponseDTO>), 200)]
        public async Task<ActionResult> GetUnprocessedAsync()
        {
            _logger.LogInformation("GetUnprocessedOutboxMessagesRequest");

            var result = await _outboxMessageService.GetUnprocessedAsync();
            return Ok(result);
        }

        [HttpGet("processed")]
        [ProducesResponseType(typeof(IEnumerable<OutboxMessageResponseDTO>), 200)]
        public async Task<ActionResult> GetProcessedAsync()
        {
            _logger.LogInformation("GetProcessedOutboxMessagesRequest");

            var result = await _outboxMessageService.GetProcessedAsync();
            return Ok(result);
        }

        [HttpGet("stats")]
        [ProducesResponseType(typeof(OutboxMessageStatsDTO), 200)]
        public async Task<ActionResult> GetStatsAsync()
        {
            _logger.LogInformation("GetOutboxMessageStatsRequest");

            var result = await _outboxMessageService.GetStatsAsync();
            return Ok(result);
        }

        [HttpGet("{id}")]
        [ProducesResponseType(typeof(OutboxMessageResponseDTO), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult> GetByIdAsync([FromRoute] Guid id)
        {
            _logger.LogInformation("GetOutboxMessageByIdRequest | Id: {Id}", id);

            var result = await _outboxMessageService.GetByIdAsync(id);
            return Ok(result);
        }

        [HttpPost("{id}/mark-processed")]
        [ProducesResponseType(typeof(OutboxMessageResponseDTO), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult> MarkAsProcessedAsync([FromRoute] Guid id)
        {
            _logger.LogInformation("MarkOutboxMessageAsProcessedRequest | Id: {Id}", id);

            var result = await _outboxMessageService.MarkAsProcessedAsync(id);
            return Ok(result);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(typeof(OutboxMessageResponseDTO), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult> DeleteAsync([FromRoute] Guid id)
        {
            _logger.LogInformation("DeleteOutboxMessageRequest | Id: {Id}", id);

            var result = await _outboxMessageService.DeleteAsync(id);
            return Ok(result);
        }

        [HttpDelete]
        [ProducesResponseType(200)]
        public async Task<ActionResult> DeleteAllAsync()
        {
            _logger.LogInformation("DeleteAllOutboxMessagesRequest");

            var deletedCount = await _outboxMessageService.DeleteAllAsync();
            return Ok(new { deletedCount });
        }
    }
}

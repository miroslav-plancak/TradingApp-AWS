using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using TradingApp.Infrastructure.Interfaces.Agentic;

namespace TradingApp.API.Controllers
{
    public class DebugAgenticController : TradingAppBaseController<DebugAgenticController>
    {
        private readonly IAgenticLoopService _agenticLoopService;

        public DebugAgenticController
        (
            ILogger<DebugAgenticController> logger,
            IAgenticLoopService agenticLoopService
        ) : base(logger)
        {
            _agenticLoopService = agenticLoopService;
        }

        [HttpGet]
        public async Task<ActionResult<string>> RunAsync([FromQuery] string question)
        {
            var answer = await _agenticLoopService.RunAgenticLoopAsync(question);
            return Ok(answer);
        }
    }
}

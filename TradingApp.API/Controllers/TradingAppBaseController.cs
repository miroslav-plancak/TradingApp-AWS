using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace TradingApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public abstract class TradingAppBaseController<T> : ControllerBase
    {
        protected readonly ILogger<T> _logger;

        protected TradingAppBaseController(ILogger<T> logger)
        {
            _logger = logger;
        }
    }
}


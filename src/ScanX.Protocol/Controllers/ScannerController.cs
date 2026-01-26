using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ScanX.Core;
using ScanX.Core.Models;

namespace ScanX.Protocol.Controllers
{
    public class ScannerController : ApiBaseController
    {
        private readonly ILogger<ScannerController> _logger;
        private static DeviceClient _client;
        private static readonly object _clientLock = new object();

        public ScannerController(ILogger<ScannerController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Get all available TWAIN scanners.
        /// </summary>
        [HttpGet]
        public IActionResult Get()
        {
            try
            {
                EnsureClientInitialized();
                var result = _client.GetAllScanners();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting scanners: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private void EnsureClientInitialized()
        {
            lock (_clientLock)
            {
                if (_client == null)
                {
                    _client = new DeviceClient(_logger);
                    _client.Initialize();
                }
            }
        }
    }
}

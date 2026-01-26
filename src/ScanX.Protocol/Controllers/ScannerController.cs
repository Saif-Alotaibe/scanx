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
        private static TwainDeviceClient _twainClient;
        private static readonly object _twainLock = new object();

        public ScannerController(ILogger<ScannerController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Get all WIA scanners.
        /// </summary>
        [HttpGet]
        public IActionResult Get()
        {
            List<ScannerDevice> result = new List<ScannerDevice>();

            using (DeviceClient client = new DeviceClient())
            {
                result = client.GetAllScanners();
            }
            return Ok(result);
        }

        /// <summary>
        /// Get all TWAIN scanners. Use this for document scanners like Fujitsu fi-8170.
        /// </summary>
        [HttpGet("twain")]
        public IActionResult GetTwainScanners()
        {
            try
            {
                EnsureTwainInitialized();
                var result = _twainClient.GetAllScanners();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting TWAIN scanners: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get all scanners from both WIA and TWAIN.
        /// </summary>
        [HttpGet("all")]
        public IActionResult GetAllScanners()
        {
            var result = new List<ScannerDevice>();

            // Get WIA scanners
            try
            {
                using (DeviceClient client = new DeviceClient())
                {
                    var wiaScanners = client.GetAllScanners();
                    foreach (var scanner in wiaScanners)
                    {
                        scanner.Description = $"[WIA] {scanner.Description}";
                        result.Add(scanner);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Error getting WIA scanners: {ex.Message}");
            }

            // Get TWAIN scanners
            try
            {
                EnsureTwainInitialized();
                var twainScanners = _twainClient.GetAllScanners();
                foreach (var scanner in twainScanners)
                {
                    scanner.Description = $"[TWAIN] {scanner.Description}";
                    result.Add(scanner);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Error getting TWAIN scanners: {ex.Message}");
            }

            return Ok(result);
        }

        private void EnsureTwainInitialized()
        {
            lock (_twainLock)
            {
                if (_twainClient == null)
                {
                    _twainClient = new TwainDeviceClient(_logger);
                    _twainClient.Initialize();
                }
            }
        }
    }
}
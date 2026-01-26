using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ScanX.Core;
using ScanX.Core.Args;
using ScanX.Core.Exceptions;
using ScanX.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ScanX.Protocol.Protocol
{

    [AllowAnonymous]
    public class ScanXProtocol : Hub
    {
        private readonly ILogger _logger;
        private static TwainDeviceClient _twainClient;
        private static readonly object _twainLock = new object();

        public ScanXProtocol(ILogger<ScanXProtocol> logger)
        {
            _logger = logger;
        }

        public override Task OnConnectedAsync()
        {
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            return base.OnDisconnectedAsync(exception);
        }

        public async Task ScanTest()
        {
            var processFileName = Process.GetCurrentProcess().MainModule.FileName;
            var file = new FileInfo(processFileName);
            var dir = file.DirectoryName;

            var imagesPath = Path.Combine(dir, "wwwroot", "images");

            await Clients.Caller.SendAsync(ClientMethod.ON_LOG, imagesPath);
            
            var img1 = File.ReadAllBytes($"{imagesPath}\\1.png");
            var img2 = File.ReadAllBytes($"{imagesPath}\\2.png");
            var img3 = File.ReadAllBytes($"{imagesPath}\\3.png");
            var img4 = File.ReadAllBytes($"{imagesPath}\\4.png");
            var img5 = File.ReadAllBytes($"{imagesPath}\\5.jpg");

            var result1 = new DeviceImageScannedEventArgs(img1, ".png", 1);
            var result2 = new DeviceImageScannedEventArgs(img2, ".png", 2);
            var result3 = new DeviceImageScannedEventArgs(img3, ".png", 3);
            var result4 = new DeviceImageScannedEventArgs(img4, ".png", 4);
            var result5 = new DeviceImageScannedEventArgs(img2, ".jpg", 5);



            await Clients.Caller.SendAsync(ClientMethod.ON_IMAGE_SCANNED, result1);
            await Clients.Caller.SendAsync(ClientMethod.ON_IMAGE_SCANNED, result2);
            await Clients.Caller.SendAsync(ClientMethod.ON_IMAGE_SCANNED, result3);
            await Clients.Caller.SendAsync(ClientMethod.ON_IMAGE_SCANNED, result4);
            await Clients.Caller.SendAsync(ClientMethod.ON_IMAGE_SCANNED, result5);



            await Clients.Caller.SendAsync(ClientMethod.ON_SCAN_FINISHED);
        }

        public async Task ScanSingle(string deviceId,ScanSetting settings)
        {
            using (DeviceClient client = new DeviceClient(_logger))
            {
                RegisterImageScannedEvents(client);

                await TryInvoke(() => client.Scan(deviceId, settings));

                await Clients.Caller.SendAsync(ClientMethod.ON_SCAN_FINISHED);
            }
        }
        
        public async Task ScanMultiple(string deviceId,ScanSetting settings)
        {
            using (DeviceClient client = new DeviceClient(_logger))
            {

                RegisterImageScannedEvents(client);

                await TryInvoke(() => client.Scan(deviceId, settings, true));

                await Clients.Caller.SendAsync(ClientMethod.ON_SCAN_FINISHED);
            }
        }
        
        private async Task TryInvoke(Action action)
        {
            try
            {
                action.Invoke();

            }
            catch (ScanXException ex)
            {
                _logger?.LogError(ex.ToString());

                await Clients.Caller.SendAsync(ClientMethod.ON_ERROR, ex);
            }
        }

        private void RegisterImageScannedEvents(DeviceClient client)
        {
            client.OnImageScanned += async (sender, args) =>
            {
                var data = args as DeviceImageScannedEventArgs;

                await Clients.Caller.SendAsync(ClientMethod.ON_IMAGE_SCANNED, data);
            };
        }

        #region TWAIN Methods

        /// <summary>
        /// Get all available TWAIN scanners.
        /// </summary>
        public async Task<List<ScannerDevice>> GetTwainScanners()
        {
            try
            {
                EnsureTwainInitialized();
                return _twainClient.GetAllScanners();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting TWAIN scanners: {ex}");
                await Clients.Caller.SendAsync(ClientMethod.ON_ERROR,
                    new ScanXException($"Failed to get TWAIN scanners: {ex.Message}", ScanXExceptionCodes.UnkownError));
                return new List<ScannerDevice>();
            }
        }

        /// <summary>
        /// Scan a single page using TWAIN (reliable for document scanners like fi-8170).
        /// </summary>
        public async Task TwainScanSingle(string deviceName, ScanSetting settings)
        {
            try
            {
                EnsureTwainInitialized();
                RegisterTwainImageScannedEvents();

                await Task.Run(() => _twainClient.Scan(deviceName, settings, false));

                await Clients.Caller.SendAsync(ClientMethod.ON_SCAN_FINISHED);
            }
            catch (ScanXException ex)
            {
                _logger?.LogError(ex.ToString());
                await Clients.Caller.SendAsync(ClientMethod.ON_ERROR, ex);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex.ToString());
                await Clients.Caller.SendAsync(ClientMethod.ON_ERROR,
                    new ScanXException($"TWAIN scan error: {ex.Message}", ScanXExceptionCodes.UnkownError));
            }
        }

        /// <summary>
        /// Scan all pages in ADF using TWAIN (reliable for document scanners like fi-8170).
        /// </summary>
        public async Task TwainScanMultiple(string deviceName, ScanSetting settings)
        {
            try
            {
                EnsureTwainInitialized();
                RegisterTwainImageScannedEvents();

                await Task.Run(() => _twainClient.Scan(deviceName, settings, true));

                await Clients.Caller.SendAsync(ClientMethod.ON_SCAN_FINISHED);
            }
            catch (ScanXException ex)
            {
                _logger?.LogError(ex.ToString());
                await Clients.Caller.SendAsync(ClientMethod.ON_ERROR, ex);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex.ToString());
                await Clients.Caller.SendAsync(ClientMethod.ON_ERROR,
                    new ScanXException($"TWAIN scan error: {ex.Message}", ScanXExceptionCodes.UnkownError));
            }
        }

        private void EnsureTwainInitialized()
        {
            lock (_twainLock)
            {
                if (_twainClient == null)
                {
                    _twainClient = new TwainDeviceClient(_logger);
                    _twainClient.Initialize();
                    _logger?.LogInformation("TWAIN client initialized");
                }
            }
        }

        private void RegisterTwainImageScannedEvents()
        {
            // Remove existing handlers to prevent duplicates
            _twainClient.OnImageScanned -= TwainClient_OnImageScanned;
            _twainClient.OnImageScanned += TwainClient_OnImageScanned;
        }

        private async void TwainClient_OnImageScanned(object sender, EventArgs args)
        {
            var data = args as DeviceImageScannedEventArgs;
            await Clients.Caller.SendAsync(ClientMethod.ON_IMAGE_SCANNED, data);
        }

        #endregion

    }
}

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
        private static DeviceClient _client;
        private static readonly object _clientLock = new object();

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

        /// <summary>
        /// Scan a single page using TWAIN.
        /// </summary>
        public async Task ScanSingle(string deviceId, ScanSetting settings)
        {
            try
            {
                EnsureClientInitialized();
                RegisterImageScannedEvents();

                await Task.Run(() => _client.Scan(deviceId, settings, false));

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
                    new ScanXException($"Scan error: {ex.Message}", ScanXExceptionCodes.UnkownError));
            }
        }

        /// <summary>
        /// Scan all pages from ADF using TWAIN.
        /// </summary>
        public async Task ScanMultiple(string deviceId, ScanSetting settings)
        {
            try
            {
                EnsureClientInitialized();
                RegisterImageScannedEvents();

                await Task.Run(() => _client.Scan(deviceId, settings, true));

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
                    new ScanXException($"Scan error: {ex.Message}", ScanXExceptionCodes.UnkownError));
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
                    _logger?.LogInformation("TWAIN client initialized");
                }
            }
        }

        private void RegisterImageScannedEvents()
        {
            // Remove existing handlers to prevent duplicates
            _client.OnImageScanned -= Client_OnImageScanned;
            _client.OnImageScanned += Client_OnImageScanned;
        }

        private async void Client_OnImageScanned(object sender, EventArgs args)
        {
            var data = args as DeviceImageScannedEventArgs;
            await Clients.Caller.SendAsync(ClientMethod.ON_IMAGE_SCANNED, data);
        }

    }
}

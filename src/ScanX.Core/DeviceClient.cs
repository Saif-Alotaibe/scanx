using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using NTwain;
using NTwain.Data;
using ScanX.Core.Args;
using ScanX.Core.Exceptions;
using ScanX.Core.Models;

namespace ScanX.Core
{
    /// <summary>
    /// TWAIN-based scanner client for reliable ADF scanning.
    /// Supports document scanners like Fujitsu fi-8170.
    /// </summary>
    public class DeviceClient : IDisposable
    {
        private readonly ILogger _logger;
        private TwainSession _session;
        private TwainSessionManager _sessionManager;
        private bool _useSessionManager;
        private DataSource _currentSource;
        private readonly List<byte[]> _scannedImages;
        private readonly ManualResetEventSlim _scanCompleteEvent;
        private ScanSetting _currentSettings;
        private int _pageCount;
        private Exception _scanException;
        private bool _disposed;

        public event EventHandler OnImageScanned;

        public DeviceClient()
        {
            _scannedImages = new List<byte[]>();
            _scanCompleteEvent = new ManualResetEventSlim(false);
        }

        public DeviceClient(ILogger logger) : this()
        {
            _logger = logger;
        }

        /// <summary>
        /// Initialize TWAIN session with a message loop hook for UI applications.
        /// </summary>
        /// <param name="messageLoopHook">
        /// The message loop hook for your UI framework:
        /// - WinForms: new WindowsFormsMessageLoopHook(yourControl)
        /// - WPF: new WpfMessageLoopHook(yourWindow)
        /// </param>
        public void Initialize(MessageLoopHook messageLoopHook)
        {
            if (messageLoopHook == null)
                throw new ArgumentNullException(nameof(messageLoopHook));

            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
            _session = new TwainSession(appId);
            _session.Open(messageLoopHook);

            // Subscribe to TWAIN events
            _session.TransferReady += Session_TransferReady;
            _session.DataTransferred += Session_DataTransferred;
            _session.TransferError += Session_TransferError;
            _session.SourceDisabled += Session_SourceDisabled;

            _logger?.LogInformation("TWAIN session initialized with message loop hook");
        }

        /// <summary>
        /// Initialize TWAIN session for console/service apps.
        /// Creates a dedicated STA thread with a message loop for TWAIN compatibility.
        /// </summary>
        public void Initialize()
        {
            // Use the session manager which provides a proper message loop
            _sessionManager = new TwainSessionManager(_logger);
            _sessionManager.Start();

            _session = _sessionManager.Session;
            _useSessionManager = true;

            // Subscribe to TWAIN events (must be done on TWAIN thread)
            _sessionManager.Invoke(() =>
            {
                _session.TransferReady += Session_TransferReady;
                _session.DataTransferred += Session_DataTransferred;
                _session.TransferError += Session_TransferError;
                _session.SourceDisabled += Session_SourceDisabled;
            });

            _logger?.LogInformation("TWAIN session initialized with dedicated message loop thread");
        }

        /// <summary>
        /// Get all available TWAIN scanners.
        /// </summary>
        public List<ScannerDevice> GetAllScanners()
        {
            if (_session == null)
            {
                throw new ScanXException("TWAIN session not initialized. Call Initialize() first.",
                    ScanXExceptionCodes.UnkownError);
            }

            // Execute on TWAIN thread if using session manager
            if (_useSessionManager)
            {
                return _sessionManager.Invoke(() => GetScannersInternal());
            }

            return GetScannersInternal();
        }

        private List<ScannerDevice> GetScannersInternal()
        {
            var result = new List<ScannerDevice>();

            foreach (var source in _session.GetSources())
            {
                result.Add(new ScannerDevice
                {
                    DeviceId = source.Name,
                    Name = source.Name,
                    Description = $"{source.Manufacturer} - {source.ProductFamily}",
                    Port = source.Version.Info
                });
            }

            _logger?.LogInformation($"Found {result.Count} TWAIN scanner(s)");
            return result;
        }

        /// <summary>
        /// Get all installed printers.
        /// </summary>
        public List<string> GetAllPrinters()
        {
            var result = new List<string>();

            var printers = PrinterSettings.InstalledPrinters;

            foreach (string item in printers)
            {
                result.Add(item);
            }

            return result;
        }

        /// <summary>
        /// Get device properties for a connected TWAIN scanner.
        /// </summary>
        public List<DeviceProperty> GetItemDeviceConnectProperties(string deviceId)
        {
            if (_session == null)
            {
                throw new ScanXException("TWAIN session not initialized. Call Initialize() first.",
                    ScanXExceptionCodes.UnkownError);
            }

            // Execute on TWAIN thread if using session manager
            if (_useSessionManager)
            {
                return _sessionManager.Invoke(() => GetDevicePropertiesInternal(deviceId));
            }

            return GetDevicePropertiesInternal(deviceId);
        }

        private List<DeviceProperty> GetDevicePropertiesInternal(string deviceId)
        {
            var result = new List<DeviceProperty>();

            var source = _session.GetSources()
                .FirstOrDefault(s => s.Name == deviceId);

            if (source == null)
            {
                throw new ScanXException($"Scanner '{deviceId}' not found.",
                    ScanXExceptionCodes.NoDevice);
            }

            try
            {
                var openResult = source.Open();
                if (openResult != ReturnCode.Success)
                {
                    throw new ScanXException($"Failed to open scanner: {openResult}",
                        ScanXExceptionCodes.DeviceBusy);
                }

                var caps = source.Capabilities;

                // Add common capabilities as properties
                if (caps.ICapPixelType.IsSupported)
                {
                    result.Add(new DeviceProperty
                    {
                        Id = 0,
                        Name = "PixelType",
                        Value = caps.ICapPixelType.GetCurrent()
                    });
                }

                if (caps.ICapXResolution.IsSupported)
                {
                    result.Add(new DeviceProperty
                    {
                        Id = 1,
                        Name = "XResolution",
                        Value = caps.ICapXResolution.GetCurrent()
                    });
                }

                if (caps.ICapYResolution.IsSupported)
                {
                    result.Add(new DeviceProperty
                    {
                        Id = 2,
                        Name = "YResolution",
                        Value = caps.ICapYResolution.GetCurrent()
                    });
                }

                if (caps.CapFeederEnabled.IsSupported)
                {
                    result.Add(new DeviceProperty
                    {
                        Id = 3,
                        Name = "FeederEnabled",
                        Value = caps.CapFeederEnabled.GetCurrent()
                    });
                }

                if (caps.ICapSupportedSizes.IsSupported)
                {
                    result.Add(new DeviceProperty
                    {
                        Id = 4,
                        Name = "SupportedSizes",
                        Value = caps.ICapSupportedSizes.GetCurrent()
                    });
                }
            }
            finally
            {
                if (source.IsOpen)
                {
                    source.Close();
                }
            }

            return result.OrderBy(a => a.Name).ToList();
        }

        /// <summary>
        /// Scan documents using TWAIN. Properly handles ADF multi-page scanning.
        /// </summary>
        /// <param name="deviceName">The TWAIN source name</param>
        /// <param name="setting">Scan settings</param>
        /// <param name="scanAllPages">If true, scans all pages in ADF</param>
        public void Scan(string deviceName, ScanSetting setting = null, bool scanAllPages = false)
        {
            if (_session == null)
            {
                throw new ScanXException("TWAIN session not initialized. Call Initialize() first.",
                    ScanXExceptionCodes.UnkownError);
            }

            if (setting == null)
                setting = new ScanSetting();

            _currentSettings = setting;
            _pageCount = 0;
            _scanException = null;
            _scanCompleteEvent.Reset();
            _scannedImages.Clear();

            // Execute scan on TWAIN thread if using session manager
            if (_useSessionManager)
            {
                _sessionManager.Invoke(() => ScanInternal(deviceName, setting, scanAllPages));
            }
            else
            {
                ScanInternal(deviceName, setting, scanAllPages);
            }
        }

        private void ScanInternal(string deviceName, ScanSetting setting, bool scanAllPages)
        {
            // Find and open the data source
            _currentSource = _session.GetSources()
                .FirstOrDefault(s => s.Name == deviceName);

            if (_currentSource == null)
            {
                throw new ScanXException($"Scanner '{deviceName}' not found.",
                    ScanXExceptionCodes.NoDevice);
            }

            try
            {
                var openResult = _currentSource.Open();
                if (openResult != ReturnCode.Success)
                {
                    throw new ScanXException($"Failed to open scanner: {openResult}",
                        ScanXExceptionCodes.DeviceBusy);
                }

                _logger?.LogInformation($"Opened TWAIN source: {deviceName}");

                // Configure scanner settings
                ConfigureSource(_currentSource, setting, scanAllPages);

                // Start scanning - this enables the source
                var enableResult = _currentSource.Enable(SourceEnableMode.NoUI, false, IntPtr.Zero);
                if (enableResult != ReturnCode.Success)
                {
                    throw new ScanXException($"Failed to start scanning: {enableResult}",
                        ScanXExceptionCodes.UnkownError);
                }

                _logger?.LogInformation("Scanning started...");

                // Wait for scanning to complete (with timeout)
                if (!_scanCompleteEvent.Wait(TimeSpan.FromMinutes(10)))
                {
                    throw new ScanXException("Scan operation timed out.",
                        ScanXExceptionCodes.UnkownError);
                }

                // Check if there was an error during scanning
                if (_scanException != null)
                {
                    throw _scanException;
                }

                _logger?.LogInformation($"Scanning completed. Total pages: {_pageCount}");
            }
            finally
            {
                if (_currentSource != null && _currentSource.IsOpen)
                {
                    _currentSource.Close();
                }
            }
        }

        private void ConfigureSource(DataSource source, ScanSetting setting, bool scanAllPages)
        {
            var caps = source.Capabilities;

            // Set pixel type (color mode)
            if (caps.ICapPixelType.IsSupported)
            {
                PixelType pixelType;
                switch (setting.Color)
                {
                    case ScanSetting.ColorModel.Grayscale:
                        pixelType = PixelType.Gray;
                        break;
                    case ScanSetting.ColorModel.BlackAndWhite:
                        pixelType = PixelType.BlackWhite;
                        break;
                    case ScanSetting.ColorModel.Color:
                    default:
                        pixelType = PixelType.RGB;
                        break;
                }

                caps.ICapPixelType.SetValue(pixelType);
                _logger?.LogInformation($"Set pixel type: {pixelType}");
            }

            // Set resolution (DPI)
            if (caps.ICapXResolution.IsSupported && caps.ICapYResolution.IsSupported)
            {
                var dpi = ScanSetting.GetResolution(setting.Dpi);
                caps.ICapXResolution.SetValue(dpi);
                caps.ICapYResolution.SetValue(dpi);
                _logger?.LogInformation($"Set resolution: {dpi} DPI");
            }

            // Configure for ADF (Automatic Document Feeder)
            if (caps.CapFeederEnabled.IsSupported)
            {
                caps.CapFeederEnabled.SetValue(BoolType.True);
                _logger?.LogInformation("Enabled document feeder");
            }

            // Set to scan all pages in feeder
            if (scanAllPages && caps.CapXferCount.IsSupported)
            {
                // -1 means scan all pages until feeder is empty
                caps.CapXferCount.SetValue(-1);
                _logger?.LogInformation("Set to scan all pages from feeder");
            }
            else if (caps.CapXferCount.IsSupported)
            {
                caps.CapXferCount.SetValue(1);
                _logger?.LogInformation("Set to scan single page");
            }

            // Enable auto feed if available
            if (caps.CapAutoFeed.IsSupported)
            {
                caps.CapAutoFeed.SetValue(BoolType.True);
                _logger?.LogInformation("Enabled auto feed");
            }

            // Set paper size to A4 if supported
            if (caps.ICapSupportedSizes.IsSupported)
            {
                try
                {
                    caps.ICapSupportedSizes.SetValue(SupportedSize.A4);
                    _logger?.LogInformation("Set paper size: A4");
                }
                catch
                {
                    _logger?.LogWarning("Could not set paper size to A4");
                }
            }

            // Set image format to JPEG or BMP
            if (caps.ICapImageFileFormat.IsSupported)
            {
                try
                {
                    caps.ICapImageFileFormat.SetValue(FileFormat.Jfif);
                    _logger?.LogInformation("Set image format: JPEG");
                }
                catch
                {
                    _logger?.LogWarning("Could not set image format to JPEG");
                }
            }
        }

        private void Session_TransferReady(object sender, TransferReadyEventArgs e)
        {
            _logger?.LogInformation($"Transfer ready. Pending count: {e.PendingTransferCount}");
        }

        private void Session_DataTransferred(object sender, DataTransferredEventArgs e)
        {
            _pageCount++;
            _logger?.LogInformation($"Page {_pageCount} transferred");

            try
            {
                byte[] imageBytes = null;

                // Handle native transfer (DIB/bitmap)
                if (e.NativeData != IntPtr.Zero)
                {
                    using (var stream = e.GetNativeImageStream())
                    {
                        if (stream != null)
                        {
                            imageBytes = ConvertToJpeg(stream);
                        }
                    }
                }
                // Handle memory transfer
                else if (e.MemoryData != null && e.MemoryData.Length > 0)
                {
                    using (var stream = new MemoryStream(e.MemoryData))
                    {
                        imageBytes = ConvertToJpeg(stream);
                    }
                }
                // Handle file transfer
                else if (!string.IsNullOrEmpty(e.FileDataPath) && File.Exists(e.FileDataPath))
                {
                    using (var stream = File.OpenRead(e.FileDataPath))
                    {
                        imageBytes = ConvertToJpeg(stream);
                    }
                }

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    _scannedImages.Add(imageBytes);

                    var args = new DeviceImageScannedEventArgs(imageBytes, ".jpg", _pageCount)
                    {
                        Settings = _currentSettings
                    };

                    // Get dimensions from the image
                    try
                    {
                        using (var ms = new MemoryStream(imageBytes))
                        using (var img = Image.FromStream(ms))
                        {
                            args.Width = img.Width;
                            args.Height = img.Height;
                        }
                    }
                    catch
                    {
                        // Ignore dimension errors
                    }

                    OnImageScanned?.Invoke(this, args);
                }
                else
                {
                    _logger?.LogWarning($"Page {_pageCount}: No image data received");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error processing page {_pageCount}: {ex}");
            }
        }

        private byte[] ConvertToJpeg(Stream inputStream)
        {
            using (var image = Image.FromStream(inputStream))
            using (var outputStream = new MemoryStream())
            {
                // Get JPEG encoder
                var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

                if (jpegEncoder != null)
                {
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
                    image.Save(outputStream, jpegEncoder, encoderParams);
                }
                else
                {
                    image.Save(outputStream, ImageFormat.Jpeg);
                }

                return outputStream.ToArray();
            }
        }

        private void Session_TransferError(object sender, TransferErrorEventArgs e)
        {
            _logger?.LogError($"Transfer error: {e.Exception?.Message ?? "Unknown error"}");

            // Map TWAIN errors to ScanX exceptions
            var errorCode = e.Exception?.Message?.ToLower() ?? "";

            if (errorCode.Contains("paper") && errorCode.Contains("jam"))
            {
                _scanException = new ScanXException("Paper is jammed in the scanner's document feeder.",
                    ScanXExceptionCodes.PaperJammed);
            }
            else if (errorCode.Contains("no paper") || errorCode.Contains("empty"))
            {
                // No paper is expected at end of ADF scanning - not an error
                if (_pageCount == 0)
                {
                    _scanException = new ScanXException("There are no documents in the document feeder.",
                        ScanXExceptionCodes.NoPaper);
                }
                // If we've scanned pages, this just means we're done
            }
            else if (errorCode.Contains("cover") && errorCode.Contains("open"))
            {
                _scanException = new ScanXException("One or more of the device's covers is open.",
                    ScanXExceptionCodes.CoverOpen);
            }
            else if (e.Exception != null)
            {
                _scanException = new ScanXException($"Scan error: {e.Exception.Message}",
                    e.Exception, ScanXExceptionCodes.UnkownError);
            }
        }

        private void Session_SourceDisabled(object sender, EventArgs e)
        {
            _logger?.LogInformation("Source disabled - scanning complete");
            _scanCompleteEvent.Set();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_useSessionManager)
                {
                    // Session manager handles cleanup
                    _sessionManager?.Dispose();
                }
                else
                {
                    if (_currentSource != null && _currentSource.IsOpen)
                    {
                        _currentSource.Close();
                    }

                    if (_session != null && _session.State > 2)
                    {
                        _session.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error disposing TWAIN client: {ex}");
            }

            _scanCompleteEvent?.Dispose();
        }
    }
}

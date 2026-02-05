using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using NTwain;
using NTwain.Data;

namespace ScanX.Core
{
    /// <summary>
    /// Manages TWAIN session on a dedicated STA thread with a message loop.
    /// TWAIN requires a Windows message pump to function properly.
    /// </summary>
    public class TwainSessionManager : IDisposable
    {
        private readonly ILogger _logger;
        private Thread _twainThread;
        private Form _messageLoopForm;
        private TwainSession _session;
        private readonly ManualResetEventSlim _initCompleteEvent;
        private readonly BlockingCollection<Action> _workQueue;
        private volatile bool _disposed;
        private volatile bool _initialized;
        private Exception _initException;

        // Win32 API for session detection
        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll")]
        private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

        public TwainSession Session => _session;
        public bool IsInitialized => _initialized;

        public TwainSessionManager(ILogger logger = null)
        {
            _logger = logger;
            _initCompleteEvent = new ManualResetEventSlim(false);
            _workQueue = new BlockingCollection<Action>();
        }

        /// <summary>
        /// Check if the current process is running in an interactive session (not Session 0).
        /// Windows Services run in Session 0 and cannot create UI elements.
        /// </summary>
        public static bool IsInteractiveSession()
        {
            try
            {
                uint sessionId;
                if (ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id, out sessionId))
                {
                    // Session 0 is the non-interactive services session
                    return sessionId != 0;
                }
            }
            catch
            {
                // Fall back to Environment check
            }

            return Environment.UserInteractive;
        }

        /// <summary>
        /// Start the TWAIN session on a dedicated STA thread.
        /// This method blocks until initialization is complete.
        /// </summary>
        public void Start()
        {
            if (_initialized)
                return;

            _logger?.LogInformation("Starting TWAIN session manager...");
            _logger?.LogInformation($"Is 64-bit process: {Environment.Is64BitProcess}");
            _logger?.LogInformation($"Is 64-bit OS: {Environment.Is64BitOperatingSystem}");
            _logger?.LogInformation($"Is interactive session: {IsInteractiveSession()}");
            _logger?.LogInformation($"Environment.UserInteractive: {Environment.UserInteractive}");

            // Check for Session 0 (Windows Service) - TWAIN cannot work in Session 0
            if (!IsInteractiveSession())
            {
                _logger?.LogWarning("Running in Session 0 (non-interactive). TWAIN requires an interactive session with desktop access.");
                _logger?.LogWarning("Consider running as a desktop application instead of a Windows Service, or use a helper process.");
            }

            _twainThread = new Thread(TwainThreadProc)
            {
                Name = "TWAIN Message Loop",
                IsBackground = true
            };
            _twainThread.SetApartmentState(ApartmentState.STA);
            _twainThread.Start();

            // Wait for initialization to complete (with timeout)
            if (!_initCompleteEvent.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("TWAIN initialization timed out");
            }

            if (_initException != null)
            {
                throw new InvalidOperationException("TWAIN initialization failed", _initException);
            }

            _logger?.LogInformation("TWAIN session manager started successfully");
        }

        private void TwainThreadProc()
        {
            try
            {
                _logger?.LogInformation("TWAIN thread started, creating message window...");

                // Create a hidden form to provide the message loop
                _messageLoopForm = new Form
                {
                    Text = "TWAIN Message Window",
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-10000, -10000),
                    Size = new System.Drawing.Size(1, 1)
                };

                // Create the handle immediately
                var handle = _messageLoopForm.Handle;
                _logger?.LogInformation($"Message window created. Handle: {handle}");

                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create message window - handle is zero");
                }

                // Initialize TWAIN session BEFORE Application.Run
                try
                {
                    _logger?.LogInformation("Creating TWAIN identity...");
                    var appId = TWIdentity.CreateFromAssembly(
                        DataGroups.Image,
                        Assembly.GetExecutingAssembly());

                    _logger?.LogInformation($"TWAIN identity created: {appId.ProductName}");

                    _session = new TwainSession(appId);
                    _logger?.LogInformation("TwainSession created, opening with message loop hook...");

                    // Open with the form's message loop hook
                    var hook = new WindowsFormsMessageLoopHook(handle);
                    _session.Open(hook);

                    _logger?.LogInformation($"TWAIN session opened. State: {_session.State}");

                    // Check the state
                    if (_session.State < 3)
                    {
                        _logger?.LogWarning($"TWAIN session state is {_session.State}, expected >= 3 (DSM loaded)");
                    }

                    // Try to enumerate sources immediately to verify
                    var sourceCount = 0;
                    foreach (var source in _session.GetSources())
                    {
                        sourceCount++;
                        _logger?.LogInformation($"Found TWAIN source: {source.Name}");
                    }
                    _logger?.LogInformation($"Total TWAIN sources found during init: {sourceCount}");

                    _initialized = true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"TWAIN initialization error: {ex}");
                    _initException = ex;
                }
                finally
                {
                    _initCompleteEvent.Set();
                }

                // Only run message loop if initialization succeeded
                if (_initialized)
                {
                    // Start a timer to process work queue
                    var timer = new System.Windows.Forms.Timer { Interval = 50 };
                    timer.Tick += (ts, te) => ProcessWorkQueue();
                    timer.Start();

                    _logger?.LogInformation("Starting message loop...");
                    // Run the message loop - this blocks until the form is closed
                    Application.Run(_messageLoopForm);
                    _logger?.LogInformation("Message loop ended");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"TWAIN thread error: {ex}");
                _initException = ex;
                _initCompleteEvent.Set();
            }
        }

        private void ProcessWorkQueue()
        {
            while (_workQueue.TryTake(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Error executing TWAIN operation: {ex}");
                }
            }
        }

        /// <summary>
        /// Execute an action on the TWAIN thread synchronously.
        /// </summary>
        public void Invoke(Action action)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TwainSessionManager));

            if (!_initialized)
                throw new InvalidOperationException("TWAIN session not initialized. Call Start() first.");

            if (Thread.CurrentThread == _twainThread)
            {
                // Already on TWAIN thread, execute directly
                action();
                return;
            }

            var completionEvent = new ManualResetEventSlim(false);
            Exception exception = null;

            _workQueue.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    completionEvent.Set();
                }
            });

            // Also post a message to ensure the form processes the queue
            if (_messageLoopForm?.IsHandleCreated == true)
            {
                _messageLoopForm.BeginInvoke(new Action(() => { }));
            }

            completionEvent.Wait();

            if (exception != null)
            {
                throw new InvalidOperationException("TWAIN operation failed", exception);
            }
        }

        /// <summary>
        /// Execute a function on the TWAIN thread and return the result.
        /// </summary>
        public T Invoke<T>(Func<T> func)
        {
            T result = default;
            Invoke(() => { result = func(); });
            return result;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                // Close session on the TWAIN thread
                if (_initialized && _messageLoopForm?.IsHandleCreated == true)
                {
                    _messageLoopForm.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (_session?.State > 2)
                            {
                                _session.Close();
                            }
                        }
                        catch { }

                        _messageLoopForm.Close();
                    }));
                }

                // Wait for thread to finish
                _twainThread?.Join(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error disposing TWAIN session manager: {ex}");
            }

            _workQueue?.Dispose();
            _initCompleteEvent?.Dispose();
        }
    }
}

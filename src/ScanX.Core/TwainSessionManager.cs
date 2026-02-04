using System;
using System.Collections.Concurrent;
using System.Reflection;
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

        public TwainSession Session => _session;
        public bool IsInitialized => _initialized;

        public TwainSessionManager(ILogger logger = null)
        {
            _logger = logger;
            _initCompleteEvent = new ManualResetEventSlim(false);
            _workQueue = new BlockingCollection<Action>();
        }

        /// <summary>
        /// Start the TWAIN session on a dedicated STA thread.
        /// This method blocks until initialization is complete.
        /// </summary>
        public void Start()
        {
            if (_initialized)
                return;

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
                // Create a hidden form to provide the message loop
                _messageLoopForm = new Form
                {
                    ShowInTaskbar = false,
                    WindowState = FormWindowState.Minimized,
                    FormBorderStyle = FormBorderStyle.None,
                    Opacity = 0
                };

                _messageLoopForm.Load += (s, e) =>
                {
                    try
                    {
                        // Hide the form
                        _messageLoopForm.Visible = false;
                        _messageLoopForm.Size = new System.Drawing.Size(0, 0);

                        // Initialize TWAIN session
                        var appId = TWIdentity.CreateFromAssembly(
                            DataGroups.Image,
                            Assembly.GetExecutingAssembly());

                        _session = new TwainSession(appId);

                        // Open with the form's message loop hook
                        _session.Open(new WindowsFormsMessageLoopHook(_messageLoopForm.Handle));

                        _logger?.LogInformation($"TWAIN session opened. State: {_session.State}");
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
                };

                // Start processing work items
                _messageLoopForm.Shown += (s, e) =>
                {
                    // Start a timer to process work queue
                    var timer = new System.Windows.Forms.Timer { Interval = 10 };
                    timer.Tick += (ts, te) => ProcessWorkQueue();
                    timer.Start();
                };

                // Run the message loop - this blocks until the form is closed
                Application.Run(_messageLoopForm);
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

using ESCPOS_NET.Emitters;
using ESCPOS_NET.Emitters.BaseCommandValues;
using ESCPOS_NET.Utilities;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace ESCPOS_NET
{
    public abstract partial class BasePrinter : IPrinter, IDisposable
    {
        private bool disposed = false;

        //private volatile bool _isMonitoring;

        private CancellationTokenSource _readCancellationTokenSource;
        private bool _readTaskRunning = false;
        private CancellationTokenSource _writeCancellationTokenSource;
        private bool _writeTaskRunning = false;

        private readonly int _maxBytesPerWrite = 15000; // max byte chunks to write at once.

        protected byte[] StatusBytes { get; set; } = null;
        public PrinterStatusEventArgs Status { get; private set; } = new PrinterStatusEventArgs();

        public event EventHandler StatusChanged;
        public event EventHandler Disconnected;
        public event EventHandler Connected;

        protected ConcurrentQueue<byte> ReadBuffer { get; set; } = new ConcurrentQueue<byte>();

        protected ConcurrentQueue<byte[]> WriteBuffer { get; set; } = new ConcurrentQueue<byte[]>();

        protected int BytesWrittenSinceLastFlush { get; set; } = 0;

        protected volatile bool IsConnected = true;

        public string PrinterName { get; protected set; }

        protected abstract int ReadBytesUnderlying(byte[] buffer, int offset, int bufferSize);
        protected abstract void WriteBytesUnderlying(byte[] buffer, int offset, int count);
        protected abstract void FlushUnderlying();

        protected BasePrinter()
        {
            PrinterName = Guid.NewGuid().ToString();
        }
        protected BasePrinter(string printerName)
        {
            if (string.IsNullOrEmpty(printerName))
            {
                printerName = Guid.NewGuid().ToString();
            }
            PrinterName = printerName;
        }

        public virtual void Connect(bool reconnecting = false)
        {
            if (!reconnecting)
            {
                _readCancellationTokenSource = new CancellationTokenSource();
                _writeCancellationTokenSource = new CancellationTokenSource();
                Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Initializing Task Threads...", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
                //Task.Factory.StartNew(MonitorPrinterStatusLongRunningTask, _connectivityCancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
                Task.Factory.StartNew(WriteLongRunningAsync, _writeCancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
                Task.Factory.StartNew(ReadLongRunningAsync, _readCancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
                // TODO: read and status monitoring probably won't work for fileprinter, should let printer types disable this feature.
                Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Task Threads started", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
            }
        }

        protected void InvokeConnect()
        {
            Connected?.Invoke(this, new ConnectionEventArgs() { IsConnected = true });
        }
        protected void InvokeDisconnect()
        {
            Disconnected?.Invoke(this, new ConnectionEventArgs() { IsConnected = false });
        }

        protected virtual async void WriteLongRunningAsync()
        {
            _writeTaskRunning = true;
            List<byte> internalWriteBuffer = new List<byte>();

            List<byte> pollCommand = new List<byte>() { Cmd.GS, ESCPOS_NET.Emitters.BaseCommandValues.Status.RequestStatus, StatusCommand.PaperStatus };
            const long delay = 100;
            const long pollInterval = 500 - delay + 1;

            Stopwatch stopwatch = Stopwatch.StartNew();

            while (true)
            {
                await Task.Delay((int)delay);

                try
                {
                    var didDequeue = WriteBuffer.TryDequeue(out var nextBytes);
                    if (didDequeue && nextBytes?.Length > 0)
                    {
                        internalWriteBuffer.AddRange(nextBytes);
                        WriteToBinaryWriter(ref internalWriteBuffer);
                        stopwatch.Restart();
                    }
                    else if (stopwatch.ElapsedMilliseconds >= pollInterval)
                    {
                        // poll
                        WriteToBinaryWriter(pollCommand);
                        stopwatch.Restart();
                    }
                }
                catch (IOException)
                {
                    // Thrown if the printer times out the socket connection
                    // default is 90 seconds
                    Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Swallowing IOException... sometimes happens with network printers. Should get reconnected automatically.");
                }
                catch
                {
                    // Swallow the exception
                    Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Swallowing generic read exception... sometimes happens with serial port printers.");
                }

                if (!WriteBuffer.Any() && _writeCancellationTokenSource != null && _writeCancellationTokenSource.IsCancellationRequested)
                {
                    Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Write Long-Running Task Cancellation was requested.", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
                    break;
                }
            }

            Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Write Long-Running Task has exited.", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
            _writeTaskRunning = false;
        }

        protected virtual async void ReadLongRunningAsync()
        {
            _readTaskRunning = true;
            Stopwatch stopwatch = Stopwatch.StartNew();

            const long maxNoDataInterval = 2000;
            bool connection = true;

            while (true)
            {
                await Task.Delay(100);

                try
                {
                    // Sometimes the serial port lib will throw an exception and read past the end of the queue if a
                    // status changes while data is being written.  We just ignore these bytes.
                    byte[] buffer = new byte[4096];
                    int readBytes = this.ReadBytesUnderlying(buffer, 0, 4096);
                    if (readBytes > 0)
                    {
                        for (int ix = 0; ix < readBytes; ix++)
                        {
                            ReadBuffer.Enqueue((byte)buffer[ix]);
                            DataAvailable();
                        }

                        stopwatch.Restart();

                        if (!connection)
                        {
                            connection = true;
                            TryUpdatePrinterStatus(true, null);
                        }

                    }
                    else if (connection && stopwatch.ElapsedMilliseconds >= maxNoDataInterval)
                    {
                        connection = false;
                        TryUpdatePrinterStatus(false, null);
                    }
                }

                catch (Exception ex)
                {
                    // Swallow the exception
                    //Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Swallowing generic read exception... sometimes happens with serial port printers.", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
                }

                if (_readCancellationTokenSource != null && _readCancellationTokenSource.IsCancellationRequested)
                {
                    Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Read Long-Running Task Cancellation was requested.", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
                    break;
                }
            }

            Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Read Long-Running Task has exited.", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
            _readTaskRunning = false;
        }

        public virtual void Write(params byte[][] arrays)
        {
            Write(ByteSplicer.Combine(arrays));
        }

        public virtual void Write(byte[] bytes)
        {
            WriteBuffer.Enqueue(bytes);
        }

        protected void WriteToBinaryWriter(ref List<byte> bytes)
        {
            try
            {
                while (bytes.Count > 0)
                {

                    int count = Math.Min(_maxBytesPerWrite, bytes.Count);
                    this.WriteBytesUnderlying(bytes.ToArray(), 0, count);
                    bytes.RemoveRange(0, count);

                    BytesWrittenSinceLastFlush += count;
                    if (BytesWrittenSinceLastFlush >= 200)
                    {
                        // Immediately trigger a flush before proceeding so the output buffer will not be delayed.
                        Flush(null, null);
                    }
                }

                Flush(null, null);
            }
            catch (IOException e)
            {
                // Network or serial connection failed, dont consume the buffer this time around
                Logging.Logger?.LogDebug(e, "Device appears disconnected.  No more bytes will be written until it is reconnected.");
            }
        }

        protected void WriteToBinaryWriter(IReadOnlyList<byte> bytes)
        {
            try
            {
                while (bytes.Count > 0)
                {

                    int count = Math.Min(_maxBytesPerWrite, bytes.Count);
                    this.WriteBytesUnderlying(bytes.ToArray(), 0, count);

                    BytesWrittenSinceLastFlush += count;
                    if (BytesWrittenSinceLastFlush >= 200)
                    {
                        // Immediately trigger a flush before proceeding so the output buffer will not be delayed.
                        Flush(null, null);
                    }
                }

                Flush(null, null);
            }
            catch (IOException e)
            {
                // Network or serial connection failed, dont consume the buffer this time around
                Logging.Logger?.LogDebug(e, "Device appears disconnected.  No more bytes will be written until it is reconnected.");
            }
        }

        protected virtual void Flush(object sender, ElapsedEventArgs e)
        {
            try
            {
                BytesWrittenSinceLastFlush = 0;
                this.FlushUnderlying();
            }
            catch (Exception ex)
            {
                Logging.Logger?.LogError(ex, "[{Function}]:[{PrinterName}] Flush threw exception.", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
            }
        }

        public virtual void DataAvailable()
        {
            if (ReadBuffer.Count() % 4 == 0)
            {
                var bytes = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    if (!ReadBuffer.TryDequeue(out bytes[i]))
                    {
                        // Ran out of bytes unexpectedly.
                        return;
                    }
                }

                TryUpdatePrinterStatus(true, bytes);

                // TODO: call other update handlers.
            }
        }

        private bool DidStatusChange(bool connected, byte[] newStatusBytes)
        {
            if (this.Status == null)
            {
                return true;
            }

            if (this.Status.Connection != connected)
            {
                return true;
            }

            if (this.StatusBytes == null)
            {
                return true;
            }

            if (newStatusBytes == null)
            {
                return false;
            }


            for (int i = 0; i < this.StatusBytes.Length && i < newStatusBytes.Length; i++)
            {
                if (this.StatusBytes[i] != newStatusBytes[i])
                {
                    return true;
                }
            }

            return false;
        }


        static readonly byte[] EmptyStatusBytes = new byte[4];
        private void TryUpdatePrinterStatus(bool connection, byte[] statusBytes)
        {
            statusBytes = statusBytes ?? StatusBytes ?? EmptyStatusBytes;
            var bytesToString = BitConverter.ToString(statusBytes);

            Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] TryUpdatePrinterStatus: Received flag values {bytesToString}", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName, bytesToString);

            // Check header bits 0, 1 and 7 are 0, and 4 is 1
            if (statusBytes[0].IsBitNotSet(0) && statusBytes[0].IsBitNotSet(1) && statusBytes[0].IsBitSet(4) && statusBytes[0].IsBitNotSet(7))
            {
                // TODO reverse check, return early
            }
            else if (DidStatusChange(connection, statusBytes))
            {
            }
            else
            { 
                return;
            }

            this.StatusBytes = statusBytes;

            Status = new PrinterStatusEventArgs()
            {
                // byte[0] == 20 cash drawer closed
                // byte[0] == 16 cash drawer open
                // Note some cash drawers do not close properly.
                Connection = connection,
                IsCashDrawerOpen = statusBytes[0].IsBitNotSet(2),
                IsPrinterOnline = statusBytes[0].IsBitNotSet(3),
                IsCoverOpen = statusBytes[0].IsBitSet(5),
                IsPaperCurrentlyFeeding = statusBytes[0].IsBitSet(6),
                IsWaitingForOnlineRecovery = statusBytes[1].IsBitSet(0),
                IsPaperFeedButtonPushed = statusBytes[1].IsBitSet(1),
                DidRecoverableNonAutocutterErrorOccur = statusBytes[1].IsBitSet(2),
                DidAutocutterErrorOccur = statusBytes[1].IsBitSet(3),
                DidUnrecoverableErrorOccur = statusBytes[1].IsBitSet(5),
                DidRecoverableErrorOccur = statusBytes[1].IsBitSet(6),
                IsPaperLow = statusBytes[2].IsBitSet(0) && statusBytes[2].IsBitSet(1),
                IsPaperOut = statusBytes[2].IsBitSet(2) && statusBytes[2].IsBitSet(3),
            };

            if (StatusChanged != null)
            {
                Logging.Logger?.LogDebug("[{Function}]:[{PrinterName}] Invoking Status Changed Event Handler...", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
                StatusChanged?.Invoke(this, Status);
            }
        }

        ~BasePrinter()
        {
            Flush(this, null);
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void OverridableDispose() // This method should only be called by the Dispose method.  // It allows synchronous disposing of derived class dependencies with base class disposes.
        {
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    _readCancellationTokenSource?.Cancel();
                    _writeCancellationTokenSource?.Cancel();
                }
                catch (Exception e)
                {
                    Logging.Logger?.LogDebug(e, "[{Function}]:[{PrinterName}] Dispose Issue during cancellation token cancellation call.", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
                }

                while (_readTaskRunning || _writeTaskRunning) Thread.Sleep(100);

                try
                {
                    OverridableDispose();
                }
                catch (Exception e)
                {
                    Logging.Logger?.LogDebug(e, "[{Function}]:[{PrinterName}] Dispose Issue during overridable dispose.", $"{this}.{MethodBase.GetCurrentMethod().Name}", PrinterName);
                }
            }

            disposed = true;
        }

        public PrinterStatusEventArgs GetStatus()
        {
            throw new NotImplementedException();
        }
    }
}
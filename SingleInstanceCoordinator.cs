using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DeepSeekHarnessDesktop;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\DeepSeekHarnessDesktop.SingleInstance.v1";
    private const string PipeName = "DeepSeekHarnessDesktop.Activation.v1";
    private const string ActivateCommand = "ACTIVATE";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _listeningCancellation = new();
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public bool IsPrimaryInstance { get; }

    public event EventHandler? ActivationRequested;

    public void StartListening()
    {
        if (!IsPrimaryInstance || _listenerTask is not null)
        {
            return;
        }

        _listenerTask = ListenAsync(_listeningCancellation.Token);
    }

    public async Task SignalPrimaryInstanceAsync()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.Out,
                options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await client.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 256,
                leaveOpen: true);
            await writer.WriteLineAsync(ActivateCommand);
            await writer.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // The primary instance may still be starting. The secondary instance should still exit.
        }
        catch (IOException)
        {
            // The mutex is authoritative; an unavailable activation pipe must not start a second app.
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(
                    server,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 256,
                    leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, ActivateCommand, StringComparison.Ordinal))
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(150, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listeningCancellation.Cancel();
        _listeningCancellation.Dispose();

        if (IsPrimaryInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already exiting or ownership has already been released.
            }
        }

        _mutex.Dispose();
    }
}

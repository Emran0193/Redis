using System.IO.Pipelines;
using System.Net.Sockets;
using NovaDB.Protocol;

namespace NovaDB.Networking;

/// <summary>
/// Creates a duplex <see cref="PipeReader"/> / <see cref="PipeWriter"/> pair over a stream with backpressure.
/// </summary>
internal static class SocketPipeline
{
    /// <summary>
    /// Attaches pipeline readers and writers to a connected socket (plain TCP).
    /// </summary>
    public static SocketPipelineEndpoints Create(Socket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        return Create(new NetworkStream(socket, ownsSocket: false), cancellationToken);
    }

    /// <summary>
    /// Attaches pipeline readers and writers to an arbitrary duplex stream (TCP or TLS).
    /// </summary>
    /// <param name="stream">Connected duplex stream.</param>
    /// <param name="cancellationToken">Cancellation token used to stop pump tasks.</param>
    /// <param name="pauseWriterThreshold">
    /// Bytes buffered before the input pump pauses. Must be at least
    /// <c>MaxBulkBytes + overhead</c> so a valid in-limit message can complete.
    /// </param>
    public static SocketPipelineEndpoints Create(
        Stream stream,
        CancellationToken cancellationToken,
        long pauseWriterThreshold = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Default: enough headroom for the process-wide max bulk plus headers/CRLF.
        if (pauseWriterThreshold <= 0)
        {
            pauseWriterThreshold = Math.Max(
                65_536L,
                (long)RespParser.Limits.MaxBulkBytes + 65_536L);
        }

        var resume = Math.Max(32_768L, pauseWriterThreshold / 2);
        var pipeOptions = new PipeOptions(
            pauseWriterThreshold: pauseWriterThreshold,
            resumeWriterThreshold: resume,
            minimumSegmentSize: 4096,
            useSynchronizationContext: false);

        var inputPipe = new Pipe(pipeOptions);
        var outputPipe = new Pipe(pipeOptions);

        var inputPump = PumpStreamToPipeAsync(stream, inputPipe.Writer, cancellationToken);
        var outputPump = PumpPipeToStreamAsync(outputPipe.Reader, stream, cancellationToken);

        return new SocketPipelineEndpoints(inputPipe.Reader, outputPipe.Writer, inputPump, outputPump);
    }

    private static async Task PumpStreamToPipeAsync(Stream stream, PipeWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var memory = writer.GetMemory(4096);
                var bytesRead = await stream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                writer.Advance(bytesRead);
                var flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flushResult.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static async Task PumpPipeToStreamAsync(PipeReader reader, Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;

                foreach (var segment in buffer)
                {
                    await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }

                if (!buffer.IsEmpty)
                {
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Pipeline endpoints and background pump tasks for a socket connection.
/// </summary>
internal sealed class SocketPipelineEndpoints
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SocketPipelineEndpoints"/> class.
    /// </summary>
    public SocketPipelineEndpoints(
        PipeReader reader,
        PipeWriter writer,
        Task inputPump,
        Task outputPump)
    {
        Reader = reader;
        Writer = writer;
        InputPump = inputPump;
        OutputPump = outputPump;
    }

    /// <summary>Gets the inbound RESP reader.</summary>
    public PipeReader Reader { get; }

    /// <summary>Gets the outbound RESP writer.</summary>
    public PipeWriter Writer { get; }

    /// <summary>Gets the stream-to-pipe pump task.</summary>
    public Task InputPump { get; }

    /// <summary>Gets the pipe-to-stream pump task.</summary>
    public Task OutputPump { get; }
}

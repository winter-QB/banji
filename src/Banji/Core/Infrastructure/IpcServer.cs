using System.Text;
using System.Text.Json;

namespace Banji.Core.Infrastructure;

/// <summary>单实例命名管道：第二个实例把命令转发给首实例。</summary>
public sealed class IpcServer : IDisposable
{
    public const string PipeName = "Banji.Ipc";

    private readonly Action<string[]> _handler;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;

    public IpcServer(Action<string[]> handler)
    {
        _handler = handler;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Banji.IpcServer"
        };
        _thread.Start();
        Log.Info("IPC 服务已启动");
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(
                    PipeName, System.IO.Pipes.PipeDirection.In, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.None);
                server.WaitForConnection();
                using var reader = new StreamReader(server, new UTF8Encoding(false));
                var line = reader.ReadLine();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var args = ParseLine(line);
                    if (args.Length > 0)
                    {
                        try { _handler(args); }
                        catch (Exception ex) { Log.Error("IPC 命令执行失败", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_running) break;
                Log.Warn($"IPC 循环异常: {ex.Message}");
                Thread.Sleep(200);
            }
        }
    }

    public static bool TrySendCommand(string[] args, int connectTimeoutMs = 1200)
    {
        try
        {
            var payload = JsonSerializer.Serialize(args);
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.Out);
            client.Connect(connectTimeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(payload);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string[] ParseLine(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(line) ?? Array.Empty<string>();
        }
        catch
        {
            return line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        try { _thread?.Join(300); } catch { /* ignore */ }
    }
}

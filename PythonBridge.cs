using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Text;

namespace YoloDatasetStudio;

public sealed class PythonBridge
{
    public async Task<string> RunAsync(Dictionary<string, object> request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"yolo-request-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(request));
        var bundledWorker = Path.Combine(AppContext.BaseDirectory, "python", "yolo_worker.exe");
        var bundledPython = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        var executable = File.Exists(bundledWorker) ? bundledWorker : File.Exists(bundledPython) ? bundledPython : "python";
        var arguments = executable.EndsWith("yolo_worker.exe", StringComparison.OrdinalIgnoreCase)
            ? $"--request \"{temp}\"" : $"\"{Path.Combine(AppContext.BaseDirectory, "python", "yolo_worker.py")}\" --request \"{temp}\"";
        var psi = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить Python.");
        using var cancellation = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var output = new StringBuilder();
        var errorTask = process.StandardError.ReadToEndAsync();
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
        {
            output.AppendLine(line);
            progress?.Report(line);
        }
        var error = await errorTask;
        await process.WaitForExitAsync();
        File.Delete(temp);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(output.ToString()) ? error : output.ToString());
        return output.ToString();
    }
}

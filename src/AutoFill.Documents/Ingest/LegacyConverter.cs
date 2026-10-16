using System.Diagnostics;
using AutoFill.Core;

namespace AutoFill.Documents.Ingest;

/// <param name="SofficePath">LibreOffice's soffice; null = look in the usual places</param>
public sealed record LegacyConverterOptions(string? SofficePath, TimeSpan Timeout);

/// <summary>
/// Old Office files (.doc, .xls) → .docx/.xlsx with LibreOffice headless. Each conversion gets
/// its own temporary folder and LibreOffice profile, so conversions can run side by side.
/// </summary>
public sealed class LegacyConverter(LegacyConverterOptions options)
{
    static readonly string[] KnownPaths =
    [
        "/usr/bin/soffice",
        "/usr/lib/libreoffice/program/soffice",
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
    ];

    /// <summary>null when LibreOffice is not installed (a configured path that does not exist counts as not installed).</summary>
    public string? Soffice => options.SofficePath is { } path
        ? File.Exists(path) ? path : null
        : KnownPaths.FirstOrDefault(File.Exists);

    /// <param name="fromExt">"doc" or "xls"</param>
    /// <returns>the .docx / .xlsx bytes</returns>
    public async Task<byte[]> ConvertAsync(byte[] content, string fromExt, CancellationToken ct = default)
    {
        var toExt = fromExt switch
        {
            "doc" => "docx",
            "xls" => "xlsx",
            _ => throw new ArgumentException($"Cannot convert .{fromExt}", nameof(fromExt)),
        };
        var soffice = Soffice ?? throw new UserFacingException("Máy chủ chưa cài LibreOffice nên chưa đọc được file .doc/.xls; hãy lưu lại thành .docx/.xlsx", 503);

        var dir = Directory.CreateTempSubdirectory("autofill-convert-");
        try
        {
            var input = Path.Combine(dir.FullName, $"input.{fromExt}");
            await File.WriteAllBytesAsync(input, content, ct);
            var start = new ProcessStartInfo(soffice)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                $"-env:UserInstallation={new Uri(Path.Combine(dir.FullName, "profile")).AbsoluteUri}",
                "--headless", "--norestore", "--nolockcheck",
                "--convert-to", toExt, "--outdir", dir.FullName, input,
            }) start.ArgumentList.Add(arg);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("soffice did not start");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                ct.ThrowIfCancellationRequested();
                throw new UserFacingException($"Chuyển đổi file .{fromExt} quá {options.Timeout.TotalSeconds:0} giây, file có thể bị hỏng");
            }
            await Task.WhenAll(stdout, stderr);

            var output = Path.Combine(dir.FullName, $"input.{toExt}");
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new UserFacingException($"Không chuyển đổi được file .{fromExt}, file có thể bị hỏng hoặc có mật khẩu");
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

using System.Diagnostics;
using System.Text;

namespace Kakikomi.Services;

/// <summary>遠隔などからアプリ本体を再起動する（1 秒後に同じ exe を起動）。</summary>
internal static class AppRestartService
{
    private const int RestartDelaySec = 1;

    public static bool TryScheduleRestart(out string? error)
    {
        error = null;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            error = "起動パスを取得できません";
            return false;
        }

        exePath = Path.GetFullPath(exePath);
        if (!File.Exists(exePath))
        {
            error = "実行ファイルが見つかりません";
            return false;
        }

        var workDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(workDir))
            workDir = Environment.CurrentDirectory;

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"kakikomi-restart-{Guid.NewGuid():N}.cmd");
            var script = new StringBuilder();
            script.AppendLine("@echo off");
            script.AppendLine($"timeout /t {RestartDelaySec} /nobreak >nul");
            script.AppendLine($"start \"\" /D \"{workDir}\" \"{exePath}\"");
            script.AppendLine("del \"%~f0\"");
            File.WriteAllText(scriptPath, script.ToString(), Encoding.UTF8);

            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                WorkingDirectory = workDir,
                CreateNoWindow = true,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

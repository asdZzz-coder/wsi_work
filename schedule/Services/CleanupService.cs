using System.IO;

namespace schedule.Services
{
    /// <summary>
    /// 啟動時清掉更新後不再需要的舊檔案，只刪除下列明確指定的檔案，絕不碰使用者的排程資料（schedule.json）：
    ///   1. %TEMP%\WorkSchedule-Update（下載並解壓的更新包）
    ///   2. %TEMP% 內舊版遺留的安裝檔 WorkSchedule-Setup-*.exe
    ///   3. 資料夾內寫入中斷留下的 schedule.json.tmp
    /// 任何一項刪除失敗（例如檔案還被安裝程式占用）都直接略過，下次啟動再試。
    /// </summary>
    public static class CleanupService
    {
        public static void RunInBackground() => Task.Run(Run);

        public static void Run()
        {
            UpdateService.TryDeleteDirectory(UpdateService.DownloadFolder);
            DeleteMatching(Path.GetTempPath(), "WorkSchedule-Setup-*.exe");
            DeleteMatching(DataStore.DataDirectory, "schedule.json.tmp");
        }

        /// <summary>刪除 folder 內符合 pattern 的檔案（不遞迴、不刪資料夾）。</summary>
        public static void DeleteMatching(string folder, string pattern)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 被占用，下次再清 */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

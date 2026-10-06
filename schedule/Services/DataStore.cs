using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using schedule.Models;

namespace schedule.Services
{
    /// <summary>存檔內容：所有工令。之後要加設定或其他清單時，加在這裡舊檔也讀得懂。</summary>
    public class ScheduleData
    {
        public List<Job> Jobs { get; set; } = new();

        /// <summary>表單下拉選單記住的輸入（null = 舊版資料，還沒建立過）。</summary>
        public InputHistoryData? History { get; set; }
    }

    /// <summary>
    /// 排程資料存成 JSON（%AppData%\WorkSchedule\schedule.json），不加密，可直接備份或用記事本查看。
    /// 寫入時先寫暫存檔再換名，存到一半當機也不會把舊資料弄壞。換電腦時請用「匯出 Excel → 匯入」搬資料。
    /// </summary>
    public static class DataStore
    {
        // 環境變數 WORKSCHEDULE_DATA_DIR 可指定其他資料夾（測試用）；平常不設定，存在 %AppData%\WorkSchedule
        public static readonly string DataDirectory =
            Environment.GetEnvironmentVariable("WORKSCHEDULE_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkSchedule");

        public const string FileName = "schedule.json";

        private static string FilePath => Path.Combine(DataDirectory, FileName);

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文直接存成中文，用記事本打開也看得懂
        };

        public static ScheduleData Load() => Load(FilePath);

        public static void Save(ScheduleData data) => Save(FilePath, data);

        internal static ScheduleData Load(string path)
        {
            if (!File.Exists(path)) return new();
            try
            {
                var data = JsonSerializer.Deserialize<ScheduleData>(File.ReadAllText(path), Options) ?? new();
                data.Jobs.RemoveAll(j => j == null);
                return data;
            }
            catch (JsonException)
            {
                // 檔案損毀（例如被手動改壞）：備份後以空資料開始，不覆蓋原檔
                File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                return new();
            }
        }

        internal static void Save(string path, ScheduleData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
            File.Move(tmp, path, overwrite: true);
        }
    }
}

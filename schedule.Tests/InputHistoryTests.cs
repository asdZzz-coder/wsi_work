using System.IO;
using schedule.Models;
using schedule.Services;

namespace schedule.Tests
{
    /// <summary>表單下拉選單記住的輸入。</summary>
    public class InputHistoryTests
    {
        [Fact]
        public void Ensure_SeedsFromJobs_NewestFirst_OnlyOnce()
        {
            var data = new ScheduleData
            {
                Jobs =
                {
                    new Job { Model = "MCC-400", Customer = "台積電", InnerWiring = "阿明", OuterWiring = "小陳", Consumables = "公司" },
                    new Job { Model = "PLC-200", Customer = "台積電", InnerWiring = "小陳", Consumables = "" },
                },
            };

            var history = InputHistory.Ensure(data);

            Assert.Equal(new[] { "PLC-200", "MCC-400" }, history.Models);
            Assert.Equal(new[] { "台積電" }, history.Customers);
            Assert.Equal(new[] { "小陳", "阿明" }, history.People); // 盤內、盤外共用
            Assert.Equal(new[] { "公司" }, history.Consumables);

            // 已經有記錄就不再從工令重建（移除過的不會回來）
            InputHistory.Forget(history, HistoryField.Model, "MCC-400");
            Assert.Same(history, InputHistory.Ensure(data));
            Assert.Equal(new[] { "PLC-200" }, history.Models);
        }

        [Fact]
        public void Remember_MovesToFront_IgnoresCaseAndBlank_AndCaps()
        {
            var items = new List<string> { "A", "b", "C" };

            InputHistory.Remember(items, " B ");
            InputHistory.Remember(items, "  ");
            InputHistory.Remember(items, null);

            Assert.Equal(new[] { "B", "A", "C" }, items);

            for (int i = 0; i < InputHistory.MaxItems + 10; i++) InputHistory.Remember(items, $"x{i}");
            Assert.Equal(InputHistory.MaxItems, items.Count);
            Assert.Equal($"x{InputHistory.MaxItems + 9}", items[0]);
        }

        [Fact]
        public void Forget_RemovesIgnoringCase()
        {
            var history = new InputHistoryData { Customers = { "台積電", "Foxconn" } };

            Assert.True(InputHistory.Forget(history, HistoryField.Customer, "foxconn"));
            Assert.False(InputHistory.Forget(history, HistoryField.Customer, "沒有這個"));
            Assert.Equal(new[] { "台積電" }, history.Customers);
        }

        [Fact]
        public void Filter_Contains_PrefixFirst_KeepsRecentOrder()
        {
            var items = new[] { "XMCC", "MCC-400", "PLC-200", "mcc-100" };

            Assert.Equal(new[] { "MCC-400", "mcc-100", "XMCC" }, InputHistory.Filter(items, "mcc"));
            Assert.Equal(items, InputHistory.Filter(items, " "));
            Assert.Empty(InputHistory.Filter(items, "ZZZ"));
        }

        [Fact]
        public void History_IsSavedWithData()
        {
            var path = Path.Combine(Path.GetTempPath(), "WorkSchedule-Tests-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var data = new ScheduleData { History = new InputHistoryData { People = { "阿明", "小陳" } } };
                DataStore.Save(path, data);

                var loaded = DataStore.Load(path);
                Assert.Equal(new[] { "阿明", "小陳" }, loaded.History!.People);

                // 舊版的檔案沒有這一段
                File.WriteAllText(path, """{ "Jobs": [] }""");
                Assert.Null(DataStore.Load(path).History);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

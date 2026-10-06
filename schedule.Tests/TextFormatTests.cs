using schedule.Services;

namespace schedule.Tests
{
    public class TextFormatTests
    {
        private static readonly DateTime Today = new(2026, 10, 6);

        [Theory]
        [InlineData("2026/10/06", 2026, 10, 6)]
        [InlineData("2026/1/2", 2026, 1, 2)]
        [InlineData("2026-1-2", 2026, 1, 2)]
        [InlineData("2026.01.02", 2026, 1, 2)]
        [InlineData("20260102", 2026, 1, 2)]
        [InlineData(" ２０２６／１０／０６ ", 2026, 10, 6)] // 全形
        [InlineData("10/6", 2026, 10, 6)]                 // 只打月日 → 今年
        [InlineData("1-15", 2026, 1, 15)]
        public void TryParseDate_AcceptsCommonFormats(string text, int y, int m, int d)
        {
            Assert.True(TextFormat.TryParseDate(text, Today, out var date));
            Assert.Equal(new DateTime(y, m, d), date);
        }

        [Theory]
        [InlineData("")]
        [InlineData("下週")]
        [InlineData("2026/13/01")]
        [InlineData("2/30")]
        public void TryParseDate_RejectsInvalid(string text) => Assert.False(TextFormat.TryParseDate(text, Today, out _));

        [Fact]
        public void TryParseDate_MonthDayFeb29_OnlyInLeapYears()
        {
            Assert.False(TextFormat.TryParseDate("2/29", new DateTime(2026, 1, 1), out _));
            Assert.True(TextFormat.TryParseDate("2/29", new DateTime(2028, 1, 1), out var d));
            Assert.Equal(new DateTime(2028, 2, 29), d);
        }

        [Fact]
        public void TryParseOptionalDate_BlankIsNull()
        {
            Assert.True(TextFormat.TryParseOptionalDate("  ", out var d));
            Assert.Null(d);
            Assert.False(TextFormat.TryParseOptionalDate("abc", out _));
        }

        [Theory]
        [InlineData("12", 12)]
        [InlineData(" １,２００ ", 1200)]
        public void TryParseInt(string text, int expected)
        {
            Assert.True(TextFormat.TryParseInt(text, out var v));
            Assert.Equal(expected, v);
        }

        [Fact]
        public void ShortDate_HidesCurrentYear()
        {
            Assert.Equal("10/06", TextFormat.ShortDate(new DateTime(2026, 10, 6), Today));
            Assert.Equal("27/01/05", TextFormat.ShortDate(new DateTime(2027, 1, 5), Today));
            Assert.Equal("—", TextFormat.ShortDate(null, Today));
        }
    }
}

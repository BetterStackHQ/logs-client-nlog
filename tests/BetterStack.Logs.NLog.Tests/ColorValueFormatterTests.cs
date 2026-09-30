using System.Globalization;
using System.Text;
using NLog.MessageTemplates;
using Xunit;

namespace BetterStack.Logs.NLog.Tests
{
    public class ColorValueFormatterTests
    {
        private static string Format(object value)
        {
            var builder = new StringBuilder();
            new ColorValueFormatter().FormatValue(value, null, CaptureType.Normal, CultureInfo.InvariantCulture, builder);

            return builder.ToString();
        }

        [Fact]
        public void ColorsNumbersYellow()
        {
            Assert.Equal("\x1b[33;1m95845\x1b[0m", Format(95845));
        }

        [Fact]
        public void ColorsBooleansGreenOrRed()
        {
            Assert.Equal("\x1b[32;1mtrue\x1b[0m", Format(true));
            Assert.Equal("\x1b[31;1mfalse\x1b[0m", Format(false));
        }

        [Fact]
        public void ColorsStringsCyan()
        {
            // NLog 6 stopped quoting strings
            Assert.Contains(Format("Josh"), new[] { "\x1b[36;1m\"Josh\"\x1b[0m", "\x1b[36;1mJosh\x1b[0m" });
        }

        [Fact]
        public void ColorsNullGray()
        {
            Assert.Equal("\x1b[37;1mNULL\x1b[0m", Format(null));
        }

        [Fact]
        public void ColorsOtherValuesBlue()
        {
            Assert.Equal("\x1b[34;1m1.5\x1b[0m", Format(1.5));
        }
    }
}

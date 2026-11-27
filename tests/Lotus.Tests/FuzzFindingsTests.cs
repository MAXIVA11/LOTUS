using System;
using System.Collections.Generic;
using Lotus.Core;
using Xunit;

namespace Lotus.Tests
{
    /// <summary>What the fuzzer in tests/Lotus.Fuzz found, kept as tests so it stays fixed.</summary>
    public class FuzzFindingsTests
    {
        [Theory]
        [InlineData("1e999")]
        [InlineData("-12.5e333333333333333333333")]
        [InlineData("[1, 2, 1e400]")]
        [InlineData("{\"version\":1e999}")]
        public void A_number_too_large_to_hold_is_refused_on_every_system(string json)
        {
            // Newer versions of .NET read such a number as infinity, older ones refuse it.
            // Infinity cannot be written back as JSON, so LOTUS refuses it everywhere.
            Assert.False(Json.TryParse(json, out object _));
            FormatException error = Assert.Throws<FormatException>(() => Json.Parse(json));
            Assert.Contains("number", error.Message);
        }

        [Fact]
        public void The_largest_and_smallest_real_numbers_are_still_read()
        {
            Assert.Equal(1.7976931348623157e308, (double)Json.Parse("1.7976931348623157e308"));
            Assert.Equal(-1e308, (double)Json.Parse("-1e308"));
            Assert.Equal(0.0, (double)Json.Parse("1e-999"));
        }

        [Fact]
        public void A_number_that_is_not_a_number_is_written_as_null_and_never_as_broken_json()
        {
            string json = Json.Write(new List<object> { double.PositiveInfinity, double.NegativeInfinity, double.NaN, float.NaN, 1.5 }, false);
            Assert.Equal("[null,null,null,null,1.5]", json);
            Assert.True(Json.TryParse(json, out object _));
        }

        [Fact]
        public void A_very_long_broken_number_does_not_come_back_whole_in_the_error()
        {
            FormatException error = Assert.Throws<FormatException>(() => Json.Parse("1" + new string('-', 5000)));
            Assert.True(error.Message.Length < 200, error.Message.Length.ToString());
        }

        [Fact]
        public void A_policy_with_such_a_number_is_refused_and_not_half_read()
        {
            Assert.Throws<FormatException>(() => Policy.FromJson("{\"version\":1e999,\"armed\":true}"));
        }

        [Theory]
        [InlineData(95)]
        [InlineData(96)]
        [InlineData(97)]
        public void Cleaned_text_is_never_longer_than_its_limit_when_a_space_falls_on_the_edge(int before)
        {
            // The space before the last word was added without being counted, so the result was one too long.
            string text = new string('a', before) + " b c";
            string clean = TextSafety.Clean(text);
            Assert.True(clean.Length <= TextSafety.MaxNameLength, clean.Length.ToString());
            Assert.False(clean.EndsWith(" "));
            Assert.Equal(clean, TextSafety.Clean(clean));
        }

        [Fact]
        public void The_same_holds_when_the_last_character_is_a_pair()
        {
            string text = new string('a', 94) + " 😀😀";
            string clean = TextSafety.Clean(text);
            Assert.True(clean.Length <= TextSafety.MaxNameLength, clean.Length.ToString());
            Assert.False(char.IsHighSurrogate(clean[clean.Length - 1]));
        }

        [Fact]
        public void Text_that_fits_is_left_whole()
        {
            string text = new string('a', 90) + " bcdef";
            Assert.Equal(text, TextSafety.Clean(text));
            Assert.Equal(96, TextSafety.Clean(text).Length);
        }
    }
}

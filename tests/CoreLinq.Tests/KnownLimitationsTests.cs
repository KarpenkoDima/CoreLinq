using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CoreLinq.Tests
{
    /// <summary>
    /// Упрощения нашей реализации, о которых прямо сказано в методичке (Части 10, 11 и 14).
    /// Если один из этих тестов покраснел — значит, реализация изменилась и методичку надо поправить.
    /// </summary>
    public class KnownLimitationsTests
    {
        [Fact]
        public void MyGroupBy_rejects_null_keys_but_standard_GroupBy_accepts_them()
        {
            string?[] names = { "Anna", null, "Boris", null };

            Assert.Throws<ArgumentNullException>(() => names.MyGroupBy(n => n).MyToList());

            List<int> sizes = names.GroupBy(n => n).Select(g => g.Count()).ToList();
            Assert.Equal(new[] { 1, 2, 1 }, sizes);
        }

        [Fact]
        public void MyGroupBy_rejects_null_nullable_keys_too()
        {
            int?[] numbers = { 1, null, 2 };

            Assert.Throws<ArgumentNullException>(() => numbers.MyGroupBy(n => n).MyToList());
            Assert.Equal(3, numbers.GroupBy(n => n).Count());
        }

        [Fact]
        public void OrderBy_ThenBy_recompute_keys_on_every_comparison_while_standard_linq_computes_each_key_once()
        {
            const int Count = 100;
            List<int> data = Enumerable.Range(0, Count).Select(i => (i * 37) % 11).ToList();

            int standardFirst = 0;
            int standardSecond = 0;
            _ = data
                .OrderBy(x =>
                {
                    standardFirst++;
                    return x % 5;
                })
                .ThenBy(x =>
                {
                    standardSecond++;
                    return x;
                })
                .ToList();

            int ourFirst = 0;
            int ourSecond = 0;
            _ = data
                .MyOrderBy(x =>
                {
                    ourFirst++;
                    return x % 5;
                })
                .MyThenBy(x =>
                {
                    ourSecond++;
                    return x;
                })
                .MyToList();

            Assert.Equal(Count, standardFirst);
            Assert.Equal(Count, standardSecond);
            Assert.True(ourFirst > Count, $"OrderBy: ожидали больше {Count} вызовов селектора, получили {ourFirst}");
            Assert.True(ourSecond > Count, $"ThenBy: ожидали больше {Count} вызовов селектора, получили {ourSecond}");
        }
    }
}

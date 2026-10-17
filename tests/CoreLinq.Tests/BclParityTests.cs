using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CoreLinq.Tests
{
    /// <summary>
    /// На случайных данных наши OrderBy/ThenBy/GroupBy должны давать тот же результат, что и стандартный LINQ.
    /// Размеры 16 и 17 — граница, на которой нестабильная сортировка (Introsort) перестаёт быть вставками.
    /// </summary>
    public class BclParityTests
    {
        private sealed record Row(int Id, int A, int B);

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(16)]
        [InlineData(17)]
        [InlineData(100)]
        [InlineData(1000)]
        public void OrderBy_ThenBy_gives_the_same_order_as_standard_linq(int size)
        {
            List<Row> rows = CreateRows(size, seed: size * 31 + 7);

            List<int> expected = rows.OrderBy(r => r.A).ThenBy(r => r.B).Select(r => r.Id).ToList();
            List<int> actual = rows.MyOrderBy(r => r.A).MyThenBy(r => r.B).MySelect(r => r.Id).MyToList();

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(17)]
        [InlineData(100)]
        [InlineData(1000)]
        public void OrderBy_alone_gives_the_same_order_as_standard_linq(int size)
        {
            List<Row> rows = CreateRows(size, seed: size * 17 + 3);

            List<int> expected = rows.OrderBy(r => r.A).Select(r => r.Id).ToList();
            List<int> actual = rows.MyOrderBy(r => r.A).MySelect(r => r.Id).MyToList();

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(17)]
        [InlineData(100)]
        [InlineData(1000)]
        public void GroupBy_gives_the_same_groups_in_the_same_order_as_standard_linq(int size)
        {
            List<Row> rows = CreateRows(size, seed: size * 13 + 5);

            List<string> expected = rows
                .GroupBy(r => r.A)
                .Select(g => $"{g.Key}: {string.Join(",", g.Select(r => r.Id))}")
                .ToList();

            List<string> actual = rows
                .MyGroupBy(r => r.A)
                .MySelect(g => $"{g.Key}: {string.Join(",", g.MySelect(r => r.Id))}")
                .MyToList();

            Assert.Equal(expected, actual);
        }

        private static List<Row> CreateRows(int size, int seed)
        {
            Random random = new(seed);
            List<Row> rows = new();
            for (int i = 0; i < size; i++)
            {
                // Мало разных значений ключей — много равных, значит, стабильность проверяется по-настоящему
                rows.Add(new Row(i, random.Next(0, 5), random.Next(0, 3)));
            }

            return rows;
        }
    }
}

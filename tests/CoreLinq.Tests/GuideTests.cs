using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CoreLinq.Tests
{
    /// <summary>
    /// Тесты 1–6 из Части 12 методички. В методичке они печатают результат в консоль,
    /// здесь то же самое проверяется утверждениями.
    /// </summary>
    public class GuideTests
    {
        [Fact]
        public void Test1_GroupBy_then_MyCount_inside_group()
        {
            IEnumerable<IGrouping<string, Employee>> groups = TestData.Employees
                .MyGroupBy(e => e.Dept)
                .MyOrderBy(g => g.Key);

            List<string> actual = new();
            foreach (IGrouping<string, Employee> group in groups)
            {
                int highEarners = group.MyCount(e => e.Salary > 80000);
                actual.Add($"{group.Key}: {highEarners} высокооплачиваемых");
            }

            Assert.Equal(new[] { "HR: 0 высокооплачиваемых", "IT: 4 высокооплачиваемых" }, actual);
        }

        [Fact]
        public void Test2_ThenBy_on_three_levels_keeps_every_level_and_is_stable()
        {
            List<string> names = TestData.Employees
                .MyOrderBy(e => e.Dept)
                .MyThenBy(e => e.Salary)
                .MyThenBy(e => e.Age)
                .MySelect(e => e.Name)
                .MyToList();

            // Boris и Fedor равны по всем трём ключам: порядок решает исходная позиция
            Assert.Equal(new[] { "Boris", "Fedor", "Dima", "Clara", "Anna", "Galya", "Elena" }, names);
        }

        [Fact]
        public void Test3_nested_calls_group_sizes_then_names_inside_group()
        {
            IEnumerable<IGrouping<string, Employee>> groups = TestData.Employees
                .MyGroupBy(e => e.Dept)
                .MyOrderBy(g => g.MyCount(_ => true))
                .MyThenBy(g => g.Key);

            List<string> actual = new();
            foreach (IGrouping<string, Employee> group in groups)
            {
                actual.Add($"[{group.Key}] ({group.MyCount(_ => true)} чел.)");

                foreach (Employee employee in group.MyOrderBy(x => x.Name))
                {
                    actual.Add($"  {employee.Name}");
                }
            }

            Assert.Equal(
                new[]
                {
                    "[HR] (3 чел.)", "  Boris", "  Dima", "  Fedor",
                    "[IT] (4 чел.)", "  Anna", "  Clara", "  Elena", "  Galya",
                },
                actual);
        }

        [Fact]
        public void Test4_GroupBy_inside_GroupBy()
        {
            List<string> actual = new();

            foreach (IGrouping<string, Employee> deptGroup in TestData.Employees.MyGroupBy(e => e.Dept).MyOrderBy(g => g.Key))
            {
                foreach (IGrouping<int, Employee> salaryGroup in deptGroup.MyGroupBy(e => e.Salary).MyOrderBy(g => g.Key))
                {
                    string names = string.Join(", ", salaryGroup.MyOrderBy(e => e.Name).MySelect(e => e.Name));
                    actual.Add($"{deptGroup.Key}/{salaryGroup.Key}: {names}");
                }
            }

            Assert.Equal(
                new[] { "HR/60000: Boris, Fedor", "HR/75000: Dima", "IT/90000: Anna, Clara, Galya", "IT/110000: Elena" },
                actual);
        }

        [Fact]
        public void Test5_empty_collection_returns_zeros_and_does_not_fail()
        {
            List<int> empty = new();

            Assert.Equal(0, empty.MyCount(x => x > 0));
            Assert.Equal(0, empty.MyOrderBy(x => x).MyCount(_ => true));
            Assert.Equal(0, empty.MyGroupBy(x => x).MyCount(_ => true));
        }

        [Fact]
        public void Test5_single_element()
        {
            int[] single = { 42 };

            Assert.Equal(42, single.MyOrderBy(x => x).MyFirstOrDefault(_ => true));
            Assert.Equal(42, single.MyGroupBy(x => x).MyFirstOrDefault(_ => true).Key);
        }

        [Fact]
        public void Test5_all_elements_are_equal()
        {
            int[] same = { 1, 1, 1, 1 };

            Assert.Equal(1, same.MyGroupBy(x => x).MyCount(_ => true));
            Assert.Equal(4, same.MyCount(x => x == 1));
        }

        [Fact]
        public void Test5_null_elements_are_counted_and_grouped_through_a_placeholder()
        {
            string?[] withNulls = { "a", null, "b", null, "c" };

            Assert.Equal(2, withNulls.MyCount(x => x == null));

            // Наш MyGroupBy не умеет null-ключи (Dictionary их не принимает),
            // поэтому подменяем null заглушкой прямо в селекторе ключа
            List<string> actual = new();
            foreach (IGrouping<string, string?> group in withNulls.MyGroupBy(x => x ?? "<null>"))
            {
                actual.Add($"{group.Key}: {group.MyCount(_ => true)}");
            }

            Assert.Equal(new[] { "a: 1", "<null>: 2", "b: 1", "c: 1" }, actual);
        }

        [Fact]
        public void Test6_sort_is_stable_on_a_big_input()
        {
            // 100 элементов, ключ принимает всего 3 значения. Внутри одного ключа
            // элементы обязаны остаться в исходном порядке — то есть по возрастанию Id
            List<(int Id, int Group)> many = new();
            for (int i = 0; i < 100; i++)
            {
                many.Add((i, i % 3));
            }

            List<(int Id, int Group)> sorted = many.MyOrderBy(x => x.Group).MyToList();

            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Group == sorted[i - 1].Group)
                {
                    Assert.True(sorted[i].Id > sorted[i - 1].Id, $"Порядок равных элементов нарушен на позиции {i}");
                }
            }
        }
    }
}

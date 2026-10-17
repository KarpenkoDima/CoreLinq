using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CoreLinq.Tests
{
    /// <summary>Источник, который считает, сколько раз его перебирали и сколько элементов из него вытащили.</summary>
    internal sealed class SpySource<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _inner;

        public SpySource(IEnumerable<T> inner)
        {
            _inner = inner;
        }

        public int GetEnumeratorCalls { get; private set; }

        public int ElementsPulled { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            GetEnumeratorCalls++;
            return Pull();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private IEnumerator<T> Pull()
        {
            foreach (T item in _inner)
            {
                ElementsPulled++;
                yield return item;
            }
        }
    }

    /// <summary>Коллекция, у которой известен Count, но перебирать её нельзя.</summary>
    internal sealed class CountOnlyCollection : ICollection<int>
    {
        public CountOnlyCollection(int count)
        {
            Count = count;
        }

        public int Count { get; }

        public bool IsReadOnly => true;

        public IEnumerator<int> GetEnumerator()
        {
            throw new InvalidOperationException("Перебирать нельзя: размер известен заранее");
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Add(int item)
        {
            throw new NotSupportedException();
        }

        public void Clear()
        {
            throw new NotSupportedException();
        }

        public bool Contains(int item)
        {
            throw new NotSupportedException();
        }

        public void CopyTo(int[] array, int arrayIndex)
        {
            throw new NotSupportedException();
        }

        public bool Remove(int item)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Части 2, 4 и 6 методички: что ленивое, что жадное и где работает ранний выход.</summary>
    public class LazinessTests
    {
        [Fact]
        public void MyWhere_and_MySelect_do_nothing_until_enumerated()
        {
            SpySource<int> source = new(new[] { 1, 2, 3, 4 });

            IEnumerable<string> query = source.MyWhere(x => x % 2 == 0).MySelect(x => $"#{x}");

            Assert.Equal(0, source.GetEnumeratorCalls);
            Assert.Equal(0, source.ElementsPulled);

            List<string> result = query.MyToList();

            Assert.Equal(new[] { "#2", "#4" }, result);
            Assert.Equal(1, source.GetEnumeratorCalls);
            Assert.Equal(4, source.ElementsPulled);
        }

        [Fact]
        public void Pipeline_passes_elements_one_at_a_time()
        {
            List<string> log = new();

            IEnumerable<int> query = new[] { 1, 2, 3 }
                .MyWhere(x =>
                {
                    log.Add($"where {x}");
                    return x != 2;
                })
                .MySelect(x =>
                {
                    log.Add($"select {x}");
                    return x * 10;
                });

            foreach (int value in query)
            {
                log.Add($"use {value}");
            }

            Assert.Equal(
                new[] { "where 1", "select 1", "use 10", "where 2", "where 3", "select 3", "use 30" },
                log);
        }

        [Fact]
        public void MyFirstOrDefault_stops_at_the_first_match()
        {
            SpySource<int> source = new(Enumerable.Range(1, 100));

            int found = source.MyFirstOrDefault(x => x == 3);

            Assert.Equal(3, found);
            Assert.Equal(3, source.ElementsPulled);
        }

        [Fact]
        public void MyAny_stops_at_the_first_match()
        {
            SpySource<int> source = new(Enumerable.Range(1, 100));

            bool any = source.MyAny(x => x == 2);

            Assert.True(any);
            Assert.Equal(2, source.ElementsPulled);
        }

        [Fact]
        public void MyOrderBy_is_lazy_outside_but_reads_everything_on_the_first_MoveNext()
        {
            SpySource<int> source = new(new[] { 3, 1, 2 });

            IEnumerable<int> query = source.MyOrderBy(x => x);
            using IEnumerator<int> enumerator = query.GetEnumerator();

            Assert.Equal(0, source.GetEnumeratorCalls);

            Assert.True(enumerator.MoveNext());

            Assert.Equal(1, enumerator.Current);
            Assert.Equal(1, source.GetEnumeratorCalls);
            Assert.Equal(3, source.ElementsPulled); // весь источник прочитан ради первого элемента
        }

        [Fact]
        public void MyGroupBy_is_lazy_outside_but_reads_everything_on_the_first_MoveNext()
        {
            SpySource<int> source = new(new[] { 1, 2, 3, 4 });

            IEnumerable<IGrouping<int, int>> query = source.MyGroupBy(x => x % 2);
            using IEnumerator<IGrouping<int, int>> enumerator = query.GetEnumerator();

            Assert.Equal(0, source.GetEnumeratorCalls);

            Assert.True(enumerator.MoveNext());

            Assert.Equal(1, enumerator.Current.Key); // первым встретился ключ 1 (элемент 1)
            Assert.Equal(1, source.GetEnumeratorCalls);
            Assert.Equal(4, source.ElementsPulled);
        }

        [Fact]
        public void MyCount_without_predicate_uses_ICollection_and_does_not_enumerate()
        {
            CountOnlyCollection collection = new(5);

            Assert.Equal(5, collection.MyCount());
        }

        [Fact]
        public void MyCount_with_predicate_has_to_enumerate()
        {
            CountOnlyCollection collection = new(5);

            Assert.Throws<InvalidOperationException>(() => collection.MyCount(x => true));
        }
    }
}

using System;
using System.Collections.Generic;
using Xunit;

namespace CoreLinq.Tests
{
    /// <summary>
    /// Часть 3 методички: публичный метод проверяет аргументы сразу при вызове,
    /// а не при первом MoveNext(). Лямбды ниже только вызывают метод и ничего не перебирают.
    /// </summary>
    public class FailFastTests
    {
        private static readonly int[] Source = { 1, 2, 3 };

        private static IEnumerable<int> NullSource => null!;

        [Fact]
        public void MyWhere_validates_arguments_at_call_time()
        {
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MyWhere(x => true)));
            Assert.Equal("predicate", ParamNameOfNullArgument(() => Source.MyWhere(null!)));
        }

        [Fact]
        public void MySelect_validates_arguments_at_call_time()
        {
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MySelect(x => x)));
            Assert.Equal("map", ParamNameOfNullArgument(() => Source.MySelect<int, int>(null!)));
        }

        [Fact]
        public void MyOrderBy_validates_arguments_at_call_time()
        {
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MyOrderBy(x => x)));
            Assert.Equal("keySelector", ParamNameOfNullArgument(() => Source.MyOrderBy<int, int>(null!)));
        }

        [Fact]
        public void MyThenBy_validates_arguments_at_call_time()
        {
            IMyOrderedEnumerable<int> ordered = Source.MyOrderBy(x => x);

            Assert.Equal("items", ParamNameOfNullArgument(() => ((IMyOrderedEnumerable<int>)null!).MyThenBy(x => x)));
            Assert.Equal("keySelector", ParamNameOfNullArgument(() => ordered.MyThenBy<int, int>(null!)));
        }

        [Fact]
        public void MyGroupBy_validates_arguments_at_call_time()
        {
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MyGroupBy(x => x)));
            Assert.Equal("keySelector", ParamNameOfNullArgument(() => Source.MyGroupBy<int, int>(null!)));
        }

        [Fact]
        public void Immediate_methods_validate_arguments_too()
        {
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MyToList()));
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MyCount()));
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MyCount(x => true)));
            Assert.Equal("predicate", ParamNameOfNullArgument(() => Source.MyCount((Func<int, bool>)null!)));
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MyAny(x => true)));
            Assert.Equal("predicate", ParamNameOfNullArgument(() => Source.MyAny(null!)));
            Assert.Equal("items", ParamNameOfNullArgument(() => NullSource.MyFirstOrDefault(x => true)));
            Assert.Equal("predicate", ParamNameOfNullArgument(() => Source.MyFirstOrDefault(null!)));
        }

        [Fact]
        public void Iterator_with_validation_inside_fails_only_on_the_first_MoveNext()
        {
            // Это тот самый ❌-вариант из методички: проверка внутри метода с yield return
            IEnumerable<int> query = BadWhere(NullSource, x => true);

            // Вызов прошёл тихо: тело метода ещё не запускалось
            Assert.Throws<ArgumentNullException>(() =>
            {
                using IEnumerator<int> enumerator = query.GetEnumerator();
                enumerator.MoveNext();
            });
        }

        private static string? ParamNameOfNullArgument(Func<object> call)
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(call);
            return exception.ParamName;
        }

        private static IEnumerable<T> BadWhere<T>(IEnumerable<T> items, Func<T, bool> predicate)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            foreach (T item in items)
            {
                if (predicate(item))
                {
                    yield return item;
                }
            }
        }
    }
}

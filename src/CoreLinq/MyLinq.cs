using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq; // нужен только ради интерфейса IGrouping<TKey, T>; методы расширения у нас свои (My*)

// ─── Вспомогательные типы ───────────────────────────────────────────────────

public interface IMyOrderedEnumerable<T> : IEnumerable<T>
{
    IMyOrderedEnumerable<T> CreateOrderedEnumerable<TKey>(Func<T, TKey> keySelector);
}

public class MyOrderedEnumerable<T> : IMyOrderedEnumerable<T>
{
    private readonly IEnumerable<T> _source;
    private readonly Comparison<T> _comparison;

    public MyOrderedEnumerable(IEnumerable<T> source, Comparison<T> comparison)
    {
        _source = source;
        _comparison = comparison;
    }

    public IMyOrderedEnumerable<T> CreateOrderedEnumerable<TKey>(Func<T, TKey> keySelector)
    {
        Comparison<T> newComparison = (a, b) =>
        {
            int result = _comparison(a, b);
            if (result != 0) return result;
            return Comparer<TKey>.Default.Compare(keySelector(a), keySelector(b));
        };
        return new MyOrderedEnumerable<T>(_source, newComparison);
    }

    // Стабильная сортировка: сортируем индексы, а равные элементы различаем по исходной позиции
    public IEnumerator<T> GetEnumerator()
    {
        var buffer = new List<T>(_source);

        var map = new int[buffer.Count];
        for (int i = 0; i < map.Length; i++)
            map[i] = i;

        Array.Sort(map, (a, b) =>
        {
            int result = _comparison(buffer[a], buffer[b]);
            return result != 0 ? result : a.CompareTo(b);
        });

        foreach (int index in map)
            yield return buffer[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class MyGrouping<TKey, T> : IGrouping<TKey, T>
{
    private readonly List<T> _items = new List<T>();
    public TKey Key { get; }
    public MyGrouping(TKey key) { Key = key; }
    public void Add(T item) => _items.Add(item);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// ─── Основной класс ─────────────────────────────────────────────────────────

public static class MyLinq
{
    // WHERE — ленивая фильтрация
    public static IEnumerable<T> MyWhere<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        return WhereIterator(items, predicate);
    }
    private static IEnumerable<T> WhereIterator<T>(IEnumerable<T> items, Func<T, bool> predicate)
    {
        foreach (var item in items)
            if (predicate(item)) yield return item;
    }

    // SELECT — ленивая проекция
    public static IEnumerable<R> MySelect<T, R>(this IEnumerable<T> items, Func<T, R> map)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (map == null)   throw new ArgumentNullException(nameof(map));
        return SelectIterator(items, map);
    }
    private static IEnumerable<R> SelectIterator<T, R>(IEnumerable<T> items, Func<T, R> map)
    {
        foreach (var item in items) yield return map(item);
    }

    // TOLIST — материализация
    public static List<T> MyToList<T>(this IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        return new List<T>(items);
    }

    // FIRSTORDEFAULT — немедленный, ранний выход
    public static T MyFirstOrDefault<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        foreach (var item in items)
            if (predicate(item)) return item;
        return default(T);
    }

    // ANY — немедленный, ранний выход, short-circuiting
    public static bool MyAny<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        foreach (var item in items)
            if (predicate(item)) return true;
        return false;
    }

    // COUNT — без предиката, с оптимизацией ICollection
    public static int MyCount<T>(this IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (items is ICollection<T> col) return col.Count;
        int count = 0;
        foreach (var _ in items) count++;
        return count;
    }

    // COUNT — с предикатом, без оптимизации
    public static int MyCount<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items == null)     throw new ArgumentNullException(nameof(items));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        int count = 0;
        foreach (var item in items)
            if (predicate(item)) count++;
        return count;
    }

    // ORDERBY — возвращает IMyOrderedEnumerable для поддержки ThenBy
    public static IMyOrderedEnumerable<T> MyOrderBy<T, TKey>(
        this IEnumerable<T> items,
        Func<T, TKey> keySelector)
    {
        if (items == null)       throw new ArgumentNullException(nameof(items));
        if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
        return new MyOrderedEnumerable<T>(items, (a, b) =>
            Comparer<TKey>.Default.Compare(keySelector(a), keySelector(b)));
    }

    // THENBY — добавляет вторичный компаратор поверх существующего
    public static IMyOrderedEnumerable<T> MyThenBy<T, TKey>(
        this IMyOrderedEnumerable<T> items,
        Func<T, TKey> keySelector)
    {
        if (items == null)       throw new ArgumentNullException(nameof(items));
        if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
        return items.CreateOrderedEnumerable(keySelector);
    }

    // GROUPBY — жадный внутри, ленивый снаружи
    public static IEnumerable<IGrouping<TKey, T>> MyGroupBy<T, TKey>(
        this IEnumerable<T> items,
        Func<T, TKey> keySelector)
    {
        if (items == null)       throw new ArgumentNullException(nameof(items));
        if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
        return MyGroupByIterator(items, keySelector);
    }
    private static IEnumerable<IGrouping<TKey, T>> MyGroupByIterator<T, TKey>(
        IEnumerable<T> items,
        Func<T, TKey> keySelector)
    {
        var groups = new List<MyGrouping<TKey, T>>();               // порядок появления ключей
        var lookup = new Dictionary<TKey, MyGrouping<TKey, T>>();   // поиск группы по ключу
        foreach (T item in items)
        {
            TKey key = keySelector(item);   // null-ключ → ArgumentNullException (см. Часть 14)
            if (!lookup.TryGetValue(key, out var group))
            {
                group = new MyGrouping<TKey, T>(key);
                lookup[key] = group;
                groups.Add(group);
            }
            group.Add(item);
        }
        foreach (var group in groups)
            yield return group;
    }
}

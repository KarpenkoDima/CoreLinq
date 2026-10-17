using System;
using System.Linq;

namespace CoreLinq.EfCore.Experiments.Infrastructure
{
    /// <summary>
    /// «Свои методы», о которых EF Core ничего не знает (Часть 7 методички, исключения трансляции).
    /// Провайдер видит в дереве выражений вызов такого метода и не может превратить его в SQL.
    /// </summary>
    public static class AgeRules
    {
        public static bool IsAdult(int age)
        {
            return age >= 18;
        }

        public static int Rank(int value)
        {
            return value % 3;
        }

        public static int Bucket(int age)
        {
            return age / 10;
        }

        public static string Label(string name)
        {
            return "user:" + name;
        }
    }

    /// <summary>Свой метод расширения — в Части 7 он показан как «ловушка»: u.Name.IsNullOrEmpty().</summary>
    public static class StringExtras
    {
        public static bool IsNullOrEmpty(this string? value)
        {
            return string.IsNullOrEmpty(value);
        }
    }

    /// <summary>Объект, свойство которого захватывается лямбдой (policy.MinAge из Части 7).</summary>
    public sealed class AgePolicy
    {
        public int MinAge { get; set; }
    }

    public static class SqlText
    {
        /// <summary>Хвост запроса, начиная с WHERE (пустая строка, если фильтра в SQL нет).</summary>
        public static string FromWhere(string sql)
        {
            int index = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            return index < 0 ? string.Empty : sql.Substring(index);
        }

        /// <summary>Читаемое представление команды для вывода в лог теста.</summary>
        public static string Describe(RecordedCommand command)
        {
            string parameters = command.Parameters.Count == 0
                ? "(без параметров)"
                : string.Join(", ", command.Parameters.Select(p => $"{p.Key} = {p.Value ?? "NULL"}"));

            return $"{command.Sql}{Environment.NewLine}-- параметры: {parameters}";
        }
    }
}

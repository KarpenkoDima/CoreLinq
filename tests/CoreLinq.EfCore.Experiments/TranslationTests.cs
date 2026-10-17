using System;
using System.Collections.Generic;
using System.Linq;
using CoreLinq.EfCore.Experiments.Infrastructure;
using CoreLinq.EfCore.Experiments.Model;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace CoreLinq.EfCore.Experiments
{
    /// <summary>
    /// Часть 7 методички: как EF Core превращает лямбды в SQL и что происходит, когда не может.
    /// Каждый тест выводит настоящий SQL в лог (в CI: dotnet test --logger "console;verbosity=detailed").
    /// </summary>
    public class TranslationTests
    {
        private readonly ITestOutputHelper _output;

        public TranslationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Literals_go_into_SQL_as_constants()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            List<string> names = db.Users
                .Where(u => u.Age > 18 && u.IsActive)
                .OrderBy(u => u.Id)
                .Select(u => u.Name)
                .ToList();

            RecordedCommand command = Assert.Single(database.Sent);
            _output.WriteLine(SqlText.Describe(command));

            Assert.Equal(new[] { "Boris", "Elena" }, names);
            Assert.Contains("WHERE", command.Sql);
            Assert.Contains("> 18", command.Sql);
            Assert.Empty(command.Parameters);
        }

        [Fact]
        public void Equality_with_a_string_literal_becomes_Name_equals_literal()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            List<User> users = db.Users.Where(u => u.Name == "Boris").ToList();

            RecordedCommand command = Assert.Single(database.Sent);
            _output.WriteLine(SqlText.Describe(command));

            Assert.Equal("Boris", Assert.Single(users).Name);
            Assert.Matches(@"""?Name""?\s*=\s*'Boris'", command.Sql);
        }

        [Fact]
        public void Captured_variable_goes_into_SQL_as_a_parameter_and_the_SQL_text_does_not_change()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            int minAge = 18;
            List<User> first = db.Users.Where(u => u.Age > minAge).ToList();

            minAge = 30;
            List<User> second = db.Users.Where(u => u.Age > minAge).ToList();

            Assert.Equal(2, database.Sent.Count);
            RecordedCommand firstCommand = database.Sent[0];
            RecordedCommand secondCommand = database.Sent[1];
            _output.WriteLine(SqlText.Describe(firstCommand));
            _output.WriteLine(SqlText.Describe(secondCommand));

            // Значение уехало параметром, а текст запроса остался прежним — на этом держится кэш запросов
            Assert.Equal(firstCommand.Sql, secondCommand.Sql);
            Assert.DoesNotContain("18", firstCommand.Sql);
            Assert.DoesNotContain("30", firstCommand.Sql);

            KeyValuePair<string, object?> firstParameter = Assert.Single(firstCommand.Parameters);
            KeyValuePair<string, object?> secondParameter = Assert.Single(secondCommand.Parameters);
            Assert.Contains("minAge", firstParameter.Key, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(18, Convert.ToInt32(firstParameter.Value));
            Assert.Equal(30, Convert.ToInt32(secondParameter.Value));

            Assert.Equal(3, first.Count); // Boris, Clara, Elena
            Assert.Equal(2, second.Count); // Clara, Elena
        }

        [Fact]
        public void Property_of_a_captured_object_goes_into_SQL_as_a_parameter_too()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            AgePolicy policy = new() { MinAge = 18 };
            List<User> users = db.Users.Where(u => u.Age > policy.MinAge).ToList();

            RecordedCommand command = Assert.Single(database.Sent);
            _output.WriteLine(SqlText.Describe(command));

            KeyValuePair<string, object?> parameter = Assert.Single(command.Parameters);
            Assert.Contains("MinAge", parameter.Key, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(18, Convert.ToInt32(parameter.Value));
            Assert.Equal(3, users.Count);
        }

        [Fact]
        public void Custom_method_in_Where_fails_at_execution_not_when_the_query_is_built()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            // Запрос построен — и пока тишина: дерево выражений никто не переводил
            IQueryable<User> query = db.Users.Where(u => AgeRules.IsAdult(u.Age));
            Assert.Empty(database.Sent);

            // Исключение вылетает при выполнении, и до базы дело не доходит
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => query.ToList());
            _output.WriteLine(exception.Message);

            Assert.Contains("could not be translated", exception.Message);
            Assert.Contains("AsEnumerable", exception.Message);
            Assert.Empty(database.Sent);
        }

        [Fact]
        public void Custom_extension_method_is_not_translated_but_string_IsNullOrEmpty_is()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            // ❌ Свой метод расширения
            Assert.Throws<InvalidOperationException>(() => db.Users.Where(u => u.Name.IsNullOrEmpty()).ToList());
            Assert.Empty(database.Sent);

            // ✅ Встроенный string.IsNullOrEmpty
            List<User> empty = db.Users.Where(u => string.IsNullOrEmpty(u.Name)).ToList();

            RecordedCommand command = Assert.Single(database.Sent);
            _output.WriteLine(SqlText.Describe(command));
            Assert.Empty(empty);
        }

        [Fact]
        public void AsEnumerable_moves_the_rest_of_the_query_to_memory()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            List<string> names = db.Users
                .Where(u => u.IsActive) // уйдёт в SQL
                .AsEnumerable() // дальше — LINQ to Objects
                .Where(u => AgeRules.IsAdult(u.Age))
                .Select(u => u.Name)
                .ToList();

            RecordedCommand command = Assert.Single(database.Sent);
            _output.WriteLine(SqlText.Describe(command));

            names.Sort(StringComparer.Ordinal);
            Assert.Equal(new[] { "Boris", "Dima", "Elena" }, names);

            string whereClause = SqlText.FromWhere(command.Sql);
            Assert.Contains("IsActive", whereClause);
            Assert.DoesNotContain("Age", whereClause); // возраст проверен уже в памяти, а не в SQL
        }

        [Fact]
        public void Custom_method_is_forgiven_in_the_final_Select_and_runs_on_the_client()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            List<string> labels = db.Users
                .Where(u => u.IsActive)
                .OrderBy(u => u.Id)
                .Select(u => AgeRules.Label(u.Name))
                .ToList();

            RecordedCommand command = Assert.Single(database.Sent);
            _output.WriteLine(SqlText.Describe(command));

            Assert.Equal(new[] { "user:Anna", "user:Boris", "user:Dima", "user:Elena" }, labels);
            Assert.DoesNotContain("user:", command.Sql); // метод в SQL не попал
            Assert.DoesNotContain("Age", command.Sql); // лишних колонок не тянули: нужен был только Name
        }

        [Fact]
        public void Custom_method_is_not_forgiven_in_OrderBy_GroupBy_Join_or_before_a_Where()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            Dictionary<string, Action> cases = new()
            {
                ["OrderBy"] = () => db.Users.OrderBy(u => AgeRules.Rank(u.Age)).ToList(),
                ["GroupBy"] = () => db.Users
                    .GroupBy(u => AgeRules.Bucket(u.Age))
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToList(),
                ["Join"] = () => db.Users
                    .Join(db.Orders, u => AgeRules.Rank(u.Id), o => o.UserId, (u, o) => o.AmountCents)
                    .ToList(),
                ["Select, а за ним Where"] = () => db.Users
                    .Select(u => AgeRules.Label(u.Name))
                    .Where(label => label.StartsWith("user:A"))
                    .ToList(),
            };

            foreach (KeyValuePair<string, Action> testCase in cases)
            {
                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(testCase.Value);
                _output.WriteLine($"{testCase.Key}: {exception.Message}");
                Assert.Contains("could not be translated", exception.Message);
            }

            Assert.Empty(database.Sent);
        }

        [Fact]
        public void ToQueryString_shows_the_SQL_without_executing_the_query()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            string sql = db.Users.Where(u => u.Age > 18).OrderBy(u => u.Name).ToQueryString();
            _output.WriteLine(sql);

            Assert.Contains("SELECT", sql);
            Assert.Contains("WHERE", sql);
            Assert.Contains("ORDER BY", sql);
            Assert.Empty(database.Sent); // ни одной команды в базу не ушло
        }

        [Fact]
        public void LogTo_writes_executed_commands_to_the_log()
        {
            List<string> log = new();
            using TestDatabase database = new(log: log.Add);
            using ShopContext db = database.CreateContext();

            List<User> users = db.Users.Where(u => u.IsActive).ToList();

            Assert.Equal(4, users.Count);
            Assert.Contains(log, line => line.Contains("SELECT"));
            _output.WriteLine(string.Join(Environment.NewLine, log));
        }

        [Fact]
        public void EnumerableQuery_runs_in_memory_and_accepts_what_the_real_provider_rejects()
        {
            using TestDatabase database = new();
            using ShopContext db = database.CreateContext();

            // 1. AsQueryable() над списком — это EnumerableQuery<T>: LINQ to Objects под видом IQueryable<T>
            List<User> inMemory = db.Users.ToList();
            IQueryable<User> fake = inMemory.AsQueryable();
            Assert.IsType<EnumerableQuery<User>>(fake);

            // 2. Свой метод ему не страшен: он просто вызывается как обычный C#
            List<User> adults = fake.Where(u => AgeRules.IsAdult(u.Age)).ToList();
            Assert.Equal(4, adults.Count); // Boris, Clara, Dima, Elena

            // 3. Тот же запрос на настоящем провайдере падает — значит, тест на списке перевод в SQL не доказывает
            Assert.Throws<InvalidOperationException>(() => db.Users.Where(u => AgeRules.IsAdult(u.Age)).ToList());

            // 4. Если в метод пришёл DbSet<T> как IEnumerable<T>, AsQueryable() вернёт тот же самый объект
            IEnumerable<User> source = db.Users;
            Assert.Same(source, source.AsQueryable());
        }
    }
}

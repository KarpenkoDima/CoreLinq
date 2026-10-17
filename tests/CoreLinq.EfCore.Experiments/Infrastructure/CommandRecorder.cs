using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CoreLinq.EfCore.Experiments.Infrastructure
{
    /// <summary>Одна команда, которую EF Core отправил в базу: текст SQL и значения параметров на момент отправки.</summary>
    public sealed record RecordedCommand(string Sql, IReadOnlyDictionary<string, object?> Parameters);

    /// <summary>
    /// Перехватчик, который запоминает каждую команду, ушедшую в базу.
    /// Через него тесты считают запросы и читают настоящий SQL.
    /// </summary>
    public sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly object _gate = new();
        private readonly List<RecordedCommand> _commands = new();

        public IReadOnlyList<RecordedCommand> Commands
        {
            get
            {
                lock (_gate)
                {
                    return _commands.ToList();
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _commands.Clear();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Record(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Record(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            // Значения копируем сразу: EF может переиспользовать объект команды
            Dictionary<string, object?> parameters = new();
            foreach (DbParameter parameter in command.Parameters)
            {
                parameters[parameter.ParameterName] = parameter.Value is DBNull ? null : parameter.Value;
            }

            lock (_gate)
            {
                _commands.Add(new RecordedCommand(command.CommandText, parameters));
            }
        }
    }
}

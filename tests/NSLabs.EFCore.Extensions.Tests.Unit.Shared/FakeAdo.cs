using System.Collections;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Data.Common;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Shared;

public sealed class FakeAdo
{
    public sealed class Connection : DbConnection
    {
        public List<Command> ExecutedCommands { get; } = [];

        public Func<Command, FakeReader?>? ReaderFactory { get; set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = "";

        public override string Database => "FakeDb";

        public override string DataSource => "FakeServer";

        public override string ServerVersion => "15.0";

        public override ConnectionState State => _state;

        private ConnectionState _state = ConnectionState.Closed;

        public override void ChangeDatabase(string? databaseName) { }

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbCommand CreateDbCommand()
        {
            var command = new Command { OwnerConnection = this };
            command.SetOwningDb(this);
            return command;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => new Transaction(this, isolationLevel);
    }

    public sealed class Transaction(Connection connection, IsolationLevel isolationLevel) : DbTransaction
    {
        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        internal DbConnection OwnerDbConnection { get; } = connection;

        public override IsolationLevel IsolationLevel { get; } = isolationLevel;

        protected override DbConnection DbConnection { get; } = connection;

        public override void Commit() => Committed = true;

        public override void Rollback() => RolledBack = true;
    }

    public sealed class Parameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = "";

        public override int Size { get; set; }

        [AllowNull]
        public override string SourceColumn { get; set; } = "";

        public override bool SourceColumnNullMapping { get; set; }

        public override object? Value { get; set; }

        public override void ResetDbType() => DbType = DbType.Object;
    }

    public sealed class ParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot => ((ICollection)_items).SyncRoot;

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array? values)
        {
            if (values is null)
            {
                return;
            }

            foreach (var value in values)
            {
                Add(value);
            }
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object? value) => value is DbParameter dbParam && _items.Contains(dbParam);

        public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);

        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object? value) => value is DbParameter dbParam ? _items.IndexOf(dbParam) : -1;

        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);

        public override void Insert(int index, object? value) => _items.Insert(index, (DbParameter)value!);

        public override void Remove(object? value) { if (value is DbParameter dbParam) _items.Remove(dbParam); }

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    public sealed class Command : DbCommand
    {
        public required Connection OwnerConnection { get; init; }

        [AllowNull]
        public override string CommandText { get; set; } = "";

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } = new ParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery()
        {
            Record();
            return -1;
        }

        public override object? ExecuteScalar()
        {
            Record();
            return null;
        }

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new Parameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();
            var reader = OwnerConnection.ReaderFactory?.Invoke(this)
                ?? throw new InvalidOperationException($"No scripted result for command: {CommandText}");

            return reader;
        }

        internal void SetOwningDb(DbConnection connection) => DbConnection = connection;

        private void Record()
        {
            if (DbTransaction is Transaction t && !ReferenceEquals(t.OwnerDbConnection, DbConnection))
            {
                throw new InvalidOperationException("Command transaction belongs to a different connection.");
            }

            OwnerConnection.ExecutedCommands.Add(this);
        }
    }

    public sealed class FakeReader(IReadOnlyList<string> columnNames, IReadOnlyList<IReadOnlyList<int>> rows)
        : DbDataReader
    {
        private int _rowIndex = -1;

        public override int FieldCount { get; } = columnNames.Count;

        public override bool HasRows => rows.Count > 0;

        public override bool IsClosed => false;

        public override int RecordsAffected => -1;

        public override int Depth => 0;

        public override object this[int ordinal] => GetValue(ordinal);

        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool Read()
        {
            _rowIndex++;
            return _rowIndex < rows.Count;
        }

        public override bool NextResult() => false;

        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

        public override object GetValue(int ordinal) => rows[_rowIndex][ordinal];

        public override string GetName(int ordinal) => columnNames[ordinal];

        public override int GetOrdinal(string name)
        {
            for (var i = 0; i < columnNames.Count; i++)
            {
                if (columnNames[i] == name)
                {
                    return i;
                }
            }

            throw new IndexOutOfRangeException(name);
        }

        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

        public override char GetChar(int ordinal) => (char)GetValue(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
            => throw new NotSupportedException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
            => throw new NotSupportedException();

        public override string GetDataTypeName(int ordinal) => nameof(Int32);

        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

        public override Type GetFieldType(int ordinal) => typeof(int);

        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

        public override string GetString(int ordinal) => (string)GetValue(ordinal);

        public override int GetValues(object[] values)
        {
            for (var i = 0; i < FieldCount; i++)
            {
                values[i] = GetValue(i);
            }

            return FieldCount;
        }

        public override bool IsDBNull(int ordinal) => false;
    }
}

// This file is part of HangFire.
// Copyright � 2013-2014 Sergey Odinokov.
// 
// HangFire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// HangFire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with HangFire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Transactions;
using Dapper;
using HangFire.Common;
using HangFire.States;
using HangFire.Storage;

namespace HangFire.SqlServer
{
    internal class SqlServerWriteOnlyTransaction : IWriteOnlyTransaction
    {
        private readonly Queue<Action<SqlConnection>> _commandQueue
            = new Queue<Action<SqlConnection>>();

        private readonly ConcurrentDictionary<string, byte> _registeredQueues
            = new ConcurrentDictionary<string, byte>();

        private readonly IPersistentJobQueue _persistentQueue;
        private readonly SqlConnection _connection;
        private readonly bool _cacheQueueRegistration;

        public SqlServerWriteOnlyTransaction(
            IPersistentJobQueue persistentQueue, 
            SqlConnection connection,
            bool cacheQueueRegistration)
        {
            if (persistentQueue == null) throw new ArgumentNullException("persistentQueue");
            if (connection == null) throw new ArgumentNullException("connection");

            _persistentQueue = persistentQueue;
            _connection = connection;
            _cacheQueueRegistration = cacheQueueRegistration;
        }

        public void Dispose()
        {
        }
        
        public void Commit()
        {
            using (var transaction = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.Serializable }))
            {
                _connection.EnlistTransaction(Transaction.Current);

                foreach (var command in _commandQueue)
                {
                    command(_connection);
                }

                transaction.Complete();
            }
        }

        public void ExpireJob(string jobId, TimeSpan expireIn)
        {
            QueueCommand(x => x.Execute(
                @"update HangFire.Job set ExpireAt = @expireAt where Id = @id",
                new { expireAt = DateTime.UtcNow.Add(expireIn), id = jobId }));
        }

        public void PersistJob(string jobId)
        {
            QueueCommand(x => x.Execute(
                @"update HangFire.Job set ExpireAt = NULL where Id = @id",
                new { id = jobId }));
        }

        public void SetJobState(string jobId, IState state)
        {
            const string addAndSetStateSql = @"
insert into HangFire.State (JobId, Name, Reason, CreatedAt, Data)
values (@jobId, @name, @reason, @createdAt, @data);
update HangFire.Job set StateId = SCOPE_IDENTITY(), StateName = @name where Id = @id;";

            QueueCommand(x => x.Execute(
                addAndSetStateSql,
                new
                {
                    jobId = jobId,
                    name = state.Name,
                    reason = state.Reason,
                    createdAt = DateTime.UtcNow,
                    data = JobHelper.ToJson(state.SerializeData()),
                    id = jobId
                }));
        }

        public void AddJobState(string jobId, IState state)
        {
            const string addStateSql = @"
insert into HangFire.State (JobId, Name, Reason, CreatedAt, Data)
values (@jobId, @name, @reason, @createdAt, @data)";

            QueueCommand(x => x.Execute(
                addStateSql,
                new
                {
                    jobId = jobId, 
                    name = state.Name,
                    reason = state.Reason,
                    createdAt = DateTime.UtcNow, 
                    data = JobHelper.ToJson(state.SerializeData())
                }));
        }

        public void AddToQueue(string queue, string jobId)
        {
            if (!_cacheQueueRegistration || !_registeredQueues.ContainsKey(queue))
            {
                QueueCommand(connection =>
                {
                    const string appendQueueSql = @"
merge HangFire.[Queue] as Target
using (VALUES (@type, @name)) as Source ([Type], Name)
on Target.[Type] = Source.[Type] and Target.Name = Source.Name
when not matched then insert ([Type], Name) values (Source.[Type], Source.Name);";

                    try
                    {
                        connection.Execute(appendQueueSql, new { type = _persistentQueue.QueueType, name = queue });
                    }
                    catch (SqlException ex)
                    {
                        if (ex.Message.Contains("UX_HangFire_Queue_Name"))
                        {
                            throw new InvalidOperationException(
                                String.Format("The queue '{0}' has been already registered with the different type. Please, use the different queue name.", queue),
                                ex);
                        }
                    }
                });

                _registeredQueues.AddOrUpdate(queue, 0, (s, b) => b);
            }

            QueueCommand(_ => _persistentQueue.Enqueue(queue, jobId));
        }

        public void IncrementCounter(string key)
        {
            QueueCommand(x => x.Execute(
                @"insert into HangFire.Counter ([Key], [Value]) values (@key, @value)",
                new { key, value = +1 }));
        }

        public void IncrementCounter(string key, TimeSpan expireIn)
        {
            QueueCommand(x => x.Execute(
                @"insert into HangFire.Counter ([Key], [Value], [ExpireAt]) values (@key, @value, @expireAt)",
                new { key, value = +1, expireAt = DateTime.UtcNow.Add(expireIn) }));
        }

        public void DecrementCounter(string key)
        {
            QueueCommand(x => x.Execute(
                @"insert into HangFire.Counter ([Key], [Value]) values (@key, @value)",
                new { key, value = -1 }));
        }

        public void DecrementCounter(string key, TimeSpan expireIn)
        {
            QueueCommand(x => x.Execute(
                @"insert into HangFire.Counter ([Key], [Value], [ExpireAt]) values (@key, @value, @expireAt)",
                new { key, value = -1, expireAt = DateTime.UtcNow.Add(expireIn) }));
        }

        public void AddToSet(string key, string value)
        {
            AddToSet(key, value, 0.0);
        }

        public void AddToSet(string key, string value, double score)
        {
            const string addSql = @"
merge HangFire.[Set] as Target
using (VALUES (@key, @value, @score)) as Source ([Key], Value, Score)
on Target.[Key] = Source.[Key] and Target.Value = Source.Value
when matched then update set Score = Source.Score
when not matched then insert ([Key], Value, Score) values (Source.[Key], Source.Value, Source.Score);";

            QueueCommand(x => x.Execute(
                addSql,
                new { key, value, score }));
        }

        public void RemoveFromSet(string key, string value)
        {
            QueueCommand(x => x.Execute(
                @"delete from HangFire.[Set] where [Key] = @key and Value = @value",
                new { key, value }));
        }

        public void InsertToList(string key, string value)
        {
            QueueCommand(x => x.Execute(
                @"insert into HangFire.List ([Key], Value) values (@key, @value)",
                new { key, value }));
        }

        public void RemoveFromList(string key, string value)
        {
            QueueCommand(x => x.Execute(
                @"delete from HangFire.List where [Key] = @key and Value = @value",
                new { key, value }));
        }

        public void TrimList(string key, int keepStartingFrom, int keepEndingAt)
        {
            const string trimSql = @"
with cte as (
select row_number() over (order by Id desc) as row_num, [Key] from HangFire.List)
delete from cte where row_num not between @start and @end and [Key] = @key";

            QueueCommand(x => x.Execute(
                trimSql,
                new { key = key, start = keepStartingFrom + 1, end = keepEndingAt + 1 }));
        }

        internal void QueueCommand(Action<SqlConnection> action)
        {
            _commandQueue.Enqueue(action);
        }
    }
}
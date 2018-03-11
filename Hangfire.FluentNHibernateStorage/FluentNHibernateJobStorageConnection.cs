using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hangfire.Common;
using Hangfire.FluentNHibernateStorage.Entities;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.Storage;

namespace Hangfire.FluentNHibernateStorage
{
    public class FluentNHibernateJobStorageConnection : JobStorageConnection
    {
        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        public FluentNHibernateJobStorageConnection(FluentNHibernateJobStorage storage)
        {
            Storage = storage ?? throw new ArgumentNullException("storage");
        }

        public FluentNHibernateJobStorage Storage { get; }

        public override IWriteOnlyTransaction CreateWriteTransaction()
        {
            return new FluentNHibernateWriteOnlyTransaction(Storage);
        }

        public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
        {
            return new FluentNHibernateDistributedLock(Storage, resource, timeout).Acquire();
        }

        public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt,
            TimeSpan expireIn)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (parameters == null) throw new ArgumentNullException("parameters");

            var invocationData = InvocationData.Serialize(job);

            Logger.TraceFormat("CreateExpiredJob={0}", JobHelper.ToJson(invocationData));

            return Storage.UseSession(session =>
            {
                using (var transaction = session.BeginTransaction())
                {
                    var jobEntity = new _Job
                    {
                        InvocationData = JobHelper.ToJson(invocationData),
                        Arguments = invocationData.Arguments,
                        CreatedAt = createdAt,
                        ExpireAt = createdAt.Add(expireIn)
                    };
                    session.Insert(jobEntity);
                    session.Flush();
                    foreach (var keyValuePair in parameters)
                    {
                        session.Insert(new _JobParameter
                        {
                            Job = jobEntity,
                            Name = keyValuePair.Key,
                            Value = keyValuePair.Value
                        });
                    }
                    session.Flush();

                    transaction.Commit();
                    return jobEntity.Id.ToString();
                }
            });
        }

        public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null || queues.Length == 0) throw new ArgumentNullException("queues");

            var providers = queues
                .Select(queue => Storage.QueueProviders.GetProvider(queue))
                .Distinct()
                .ToArray();

            if (providers.Length != 1)
            {
                throw new InvalidOperationException(string.Format(
                    "Multiple provider instances registered for queues: {0}. You should choose only one type of persistent queues per server instance.",
                    string.Join(", ", queues)));
            }

            var persistentQueue = providers[0].GetJobQueue();
            return persistentQueue.Dequeue(queues, cancellationToken);
        }

        public override void SetJobParameter(string id, string name, string value)
        {
            if (id == null) throw new ArgumentNullException("id");
            if (name == null) throw new ArgumentNullException("name");
            var converter = StringToInt32Converter.Convert(id);
            if (!converter.Valid)
            {
                return;
            }
            Storage.UseSession(session =>
            {
                session.UpsertEntity<_JobParameter>(i => i.Id == converter.Value,
                    i => i.Value = value, i =>
                    {
                        i.Job = new _Job {Id = converter.Value};
                        i.Name = name;
                    });
                ;
            });
        }

        public override string GetJobParameter(string id, string name)
        {
            if (id == null) throw new ArgumentNullException("id");
            var converter = StringToInt32Converter.Convert(id);
            if (!converter.Valid)
            {
                return null;
            }
            if (name == null) throw new ArgumentNullException("name");

            return Storage.UseSession(session =>
                session.Query<_JobParameter>()
                    .Where(i => i.Job.Id == converter.Value && i.Name == name)
                    .Select(i => i.Value)
                    .SingleOrDefault());
        }

        public override JobData GetJobData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException("jobId");
            var converter = StringToInt32Converter.Convert(jobId);

            if (!converter.Valid)
            {
                return null;
            }
            Logger.InfoFormat("Get job data for job '{0}'", jobId);

            return Storage.UseSession(session =>
            {
                var jobData =
                    session
                        .Query<_Job>()
                        .SingleOrDefault(i => i.Id == converter.Value);

                if (jobData == null) return null;

                var invocationData = JobHelper.FromJson<InvocationData>(jobData.InvocationData);
                invocationData.Arguments = jobData.Arguments;

                Job job = null;
                JobLoadException loadException = null;

                try
                {
                    job = invocationData.Deserialize();
                }
                catch (JobLoadException ex)
                {
                    loadException = ex;
                }

                return new JobData
                {
                    Job = job,
                    State = jobData.StateName,
                    CreatedAt = jobData.CreatedAt,
                    LoadException = loadException
                };
            });
        }

        public override StateData GetStateData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException("jobId");
            var converter = StringToInt32Converter.Convert(jobId);
            if (!converter.Valid)
            {
                return null;
            }
            return Storage.UseSession(session =>
            {
                var job = session.Query<_Job>()
                    .Where(i => i.Id == converter.Value)
                    .Select(i => new {i.StateName, i.StateData, i.StateReason})
                    .SingleOrDefault();
                if (job == null)
                {
                    return null;
                }


                return new StateData
                {
                    Name = job.StateName,
                    Reason = job.StateReason,
                    Data = new Dictionary<string, string>(
                        JobHelper.FromJson<Dictionary<string, string>>(job.StateData),
                        StringComparer.OrdinalIgnoreCase)
                };
            });
        }

        public override void AnnounceServer(string serverId, ServerContext context)
        {
            if (serverId == null) throw new ArgumentNullException("serverId");
            if (context == null) throw new ArgumentNullException("context");

            Storage.UseSession(session =>
            {
                session.UpsertEntity<_Server>(i => i.Id == serverId,
                    i =>
                    {
                        i.Data = JobHelper.ToJson(new ServerData
                        {
                            WorkerCount = context.WorkerCount,
                            Queues = context.Queues,
                            StartedAt = session.Storage.UtcNow
                        });
                        i.LastHeartbeat = session.Storage.UtcNow;
                    }, i => { i.Id = serverId; });
            });
        }

        public override void RemoveServer(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException("serverId");

            Storage.UseSession(
                session =>
                {
                    session.CreateQuery(SqlUtil.DeleteServerByIdStatement)
                        .SetParameter(SqlUtil.IdParameterName, serverId)
                        .ExecuteUpdate();
                });
        }

        public override void Heartbeat(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException("serverId");

            Storage.UseSession(session =>
            {
                session.CreateQuery(SqlUtil.UpdateServerLastHeartbeatStatement)
                    .SetParameter(SqlUtil.ValueParameterName, session.Storage.UtcNow)
                    .SetParameter(SqlUtil.IdParameterName, serverId)
                    .ExecuteUpdate();
            });
        }

        public override int RemoveTimedOutServers(TimeSpan timeOut)
        {
            if (timeOut.Duration() != timeOut)
            {
                throw new ArgumentException("The `timeOut` value must be positive.", "timeOut");
            }

            return
                Storage.UseSession(session =>
                    session.CreateQuery(SqlUtil.DeleteServerByLastHeartbeatStatement)
                        .SetParameter(SqlUtil.ValueParameterName, session.Storage.UtcNow.Subtract(timeOut))
                        .ExecuteUpdate());
        }

        public override long GetSetCount(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            return
                Storage.UseSession(session =>
                    session.Query<_Set>().Count(i => i.Key == key));
        }

        public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException("key");
            return Storage.UseSession(session =>
            {
                return session.Query<_Set>()
                    .OrderBy(i => i.Id)
                    .Where(i => i.Key == key)
                    .Skip(startingFrom)
                    .Take(endingAt - startingFrom + 1)
                    .Select(i => i.Value)
                    .ToList();
            });
        }

        public override HashSet<string> GetAllItemsFromSet(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            return
                Storage.UseSession(session =>
                {
                    var result = session.Query<_Set>()
                        .Where(i => i.Key == key)
                        .OrderBy(i => i.Id)
                        .Select(i => i.Value)
                        .ToList();
                    return new HashSet<string>(result);
                });
        }

        public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (toScore < fromScore)
                throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.");

            return
                Storage.UseSession(session =>
                    session.Query<_Set>()
                        .OrderBy(i => i.Score)
                        .Where(i => i.Key == key && i.Score >= fromScore && i.Score <= toScore)
                        .Select(i => i.Value)
                        .FirstOrDefault());
        }

        public override long GetCounter(string key)
        {
            if (key == null) throw new ArgumentNullException("key");
            return
                Storage.UseSession(session =>
                {
                    return session.Query<_Counter>().Where(i => i.Key == key).Sum(i => i.Value) +
                           session.Query<_AggregatedCounter>().Where(i => i.Key == key).Sum(i => i.Value);
                });
        }

        public override long GetHashCount(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            return
                Storage.UseSession(session =>
                    session.Query<_Hash>().Count(i => i.Key == key));
        }

        public override TimeSpan GetHashTtl(string key)
        {
            return GetTTL<_Hash>(key);
        }

        public override long GetListCount(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            return
                Storage.UseSession(session =>
                    session.Query<_List>().Count(i => i.Key == key));
        }

        public override TimeSpan GetListTtl(string key)
        {
            return GetTTL<_List>(key);
        }

        public override string GetValueFromHash(string key, string name)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (name == null) throw new ArgumentNullException("name");

            return
                Storage.UseSession(session =>
                    session.Query<_Hash>()
                        .Where(i => i.Key == key && i.Field == name)
                        .Select(i => i.Value)
                        .SingleOrDefault());
        }

        public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException("key");
            return Storage.UseSession(session =>
            {
                return
                    session.Query<_List>()
                        .OrderByDescending(i => i.Id)
                        .Where(i => i.Key == key)
                        .Select(i => i.Value)
                        .Skip(startingFrom)
                        .Take(endingAt - startingFrom + 1)
                        .ToList();
            });
        }

        public override List<string> GetAllItemsFromList(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            return Storage.UseSession(session =>
            {
                return
                    session.Query<_List>()
                        .OrderByDescending(i => i.Id)
                        .Where(i => i.Key == key)
                        .Select(i => i.Value)
                        .ToList();

                ;
            });
        }

        private TimeSpan GetTTL<T>(string key) where T : IExpirableWithKey
        {
            if (key == null) throw new ArgumentNullException("key");

            return Storage.UseSession(session =>
            {
                var item = session.Query<T>().Where(i => i.Key == key).Min(i => i.ExpireAt);
                if (item == null)
                {
                    return TimeSpan.FromSeconds(-1);
                }
                return item.Value - session.Storage.UtcNow;
            });
        }

        public override TimeSpan GetSetTtl(string key)
        {
            return GetTTL<_Set>(key);
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (keyValuePairs == null) throw new ArgumentNullException("keyValuePairs");

            Storage.UseTransaction(session =>
            {
                foreach (var keyValuePair in keyValuePairs)
                {
                    session.UpsertEntity<_Hash>(i => i.Key == key && i.Field == keyValuePair.Key,
                        i => { i.Value = keyValuePair.Value; },
                        i =>
                        {
                            i.Key = key;
                            i.Field = keyValuePair.Key;
                        });
                }
            });
        }

        public override Dictionary<string, string> GetAllEntriesFromHash(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            return Storage.UseSession(session =>
            {
                var result = session.Query<_Hash>()
                    .Where(i => i.Key == key)
                    .ToDictionary(i => i.Field, i => i.Value);
                return result.Count != 0 ? result : null;
            });
        }
    }
}
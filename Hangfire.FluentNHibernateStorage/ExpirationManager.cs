﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hangfire.FluentNHibernateStorage.Entities;
using Hangfire.Logging;
using Hangfire.Server;

namespace Hangfire.FluentNHibernateStorage
{
#pragma warning disable 618
    public class ExpirationManager : IBackgroundProcess, IServerComponent
    {
        private const string DistributedLockKey = "expirationmanager";
        private const int NumberOfRecordsInSinglePass = 1000;
        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DelayBetweenPasses = TimeSpan.FromSeconds(1);

        private readonly TimeSpan _checkInterval;

        private readonly FluentNHibernateJobStorage _storage;

        public ExpirationManager(FluentNHibernateJobStorage storage)
            : this(storage, TimeSpan.FromHours(1))
        {
        }

        public ExpirationManager(FluentNHibernateJobStorage storage, TimeSpan checkInterval)
        {
            _storage = storage ?? throw new ArgumentNullException("storage");
            _checkInterval = checkInterval;
        }

        public void Execute(BackgroundProcessContext context)
        {
            var cancellationToken = context.CancellationToken;
            Execute(cancellationToken);
        }

        public void Execute(CancellationToken cancellationToken)
        {
            List<Action> actions = new List<Action>();


            EnqueueBatchDelete<_JobState>(actions, cancellationToken, session =>
            {
                var idList = session.Query<_JobState>()
                    .Where(i => i.Job.ExpireAt < session.Storage.UtcNow)
                    .Take(NumberOfRecordsInSinglePass)
                    .Select(i => i.Id)
                    .ToList();
                return session.DeleteByInt32Id<_JobState>(idList);
            });
            
            EnqueueBatchDelete<_JobQueue>(actions, cancellationToken, session =>
            {
                var idList = session.Query<_JobQueue>()
                    .Where(i => i.Job.ExpireAt < session.Storage.UtcNow)
                    .Take(NumberOfRecordsInSinglePass)
                    .Select(i => i.Id)
                    .ToList();
                return session.DeleteByInt32Id<_JobState>(idList);
            });
          
            EnqueueBatchDelete<_JobParameter>(actions, cancellationToken, session =>
            {
                var idList = session.Query<_JobParameter>()
                    .Where(i => i.Job.ExpireAt < session.Storage.UtcNow)
                    .Take(NumberOfRecordsInSinglePass)
                    .Select(i => i.Id)
                    .ToList();
                return session.DeleteByInt32Id<_JobParameter>(idList);
            });
            
            EnqueueBatchDelete<_DistributedLock>(actions, cancellationToken, session =>
            {
                var idList = session.Query<_DistributedLock>()
                    .Where(i => i.ExpireAtAsLong < session.Storage.UtcNow.ToUnixDate())
                    .Take(NumberOfRecordsInSinglePass)
                    .Select(i => i.Id)
                    .ToList();
                return session.DeleteByInt32Id<_DistributedLock>(idList);
            });
            EnqueueBatchDelete<_AggregatedCounter>(actions, cancellationToken, DeleteExpirableWithId<_AggregatedCounter>);
            EnqueueBatchDelete<_Job>(actions, cancellationToken, DeleteExpirableWithId<_Job>);
            EnqueueBatchDelete<_List>(actions, cancellationToken, DeleteExpirableWithId<_List>);
            EnqueueBatchDelete<_Set>(actions, cancellationToken, DeleteExpirableWithId<_Set>);
            EnqueueBatchDelete<_Hash>(actions, cancellationToken, DeleteExpirableWithId<_Hash>);

            foreach(var action in actions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }         
                action();
            }

            cancellationToken.WaitHandle.WaitOne(_checkInterval);
        }

        internal static long DeleteExpirableWithId<T>(SessionWrapper session) where T : IExpirableWithId

        {
            return session.DeleteExpirableWithId<T>(NumberOfRecordsInSinglePass);
        }


        private void EnqueueBatchDelete<T>(List<Action> actions, CancellationToken cancellationToken,
            Func<SessionWrapper, long> deleteFunc)

        {
            actions.Add(() =>
            {
                try
                {
                    var entityName = typeof(T).Name;
                    Logger.InfoFormat("Removing outdated records from table '{0}'...", entityName);

                    long removedCount = 0;

                    while (true)
                    {
                        try
                        {
                            using (new FluentNHibernateDistributedLock(_storage, DistributedLockKey, DefaultLockTimeout,
                                cancellationToken).Acquire())
                            {
                                using (var session = _storage.GetSession())
                                {
                                    removedCount = deleteFunc(session);
                                }
                            }

                            Logger.InfoFormat("removed records count={0}", removedCount);
                        }
                        catch (FluentNHibernateDistributedLockException)
                        {
                            // do nothing
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex.ToString());
                        }


                        if (removedCount <= 0)
                        {
                            break;
                        }

                        Logger.Info(string.Format("Removed {0} outdated record(s) from '{1}' table.", removedCount,
                            entityName));

                        cancellationToken.WaitHandle.WaitOne(DelayBetweenPasses);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    //do nothing
                }
            });
        }

        public override string ToString()
        {
            return GetType().ToString();
        }
#pragma warning restore 618
    }
}
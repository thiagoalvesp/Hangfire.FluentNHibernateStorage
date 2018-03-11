﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Transactions;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.FluentNHibernateStorage.Entities;
using Hangfire.FluentNHibernateStorage.JobQueue;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace Hangfire.FluentNHibernateStorage.Monitoring
{
    public class FluentNHibernateMonitoringApi : IMonitoringApi
    {
        private readonly int? _jobListLimit;
        private readonly FluentNHibernateJobStorage _storage;

        public FluentNHibernateMonitoringApi([NotNull] FluentNHibernateJobStorage storage, int? jobListLimit)
        {
            _storage = storage ?? throw new ArgumentNullException("storage");
            _jobListLimit = jobListLimit;
        }

        public IList<QueueWithTopEnqueuedJobsDto> Queues()
        {
            var tuples = _storage.QueueProviders
                .Select(x => x.GetJobQueueMonitoringApi())
                .SelectMany(x => x.GetQueues(), (monitoring, queue) => new {Monitoring = monitoring, Queue = queue})
                .OrderBy(x => x.Queue)
                .ToArray();

            var result = new List<QueueWithTopEnqueuedJobsDto>(tuples.Length);

            foreach (var tuple in tuples)
            {
                var enqueuedJobIds = tuple.Monitoring.GetEnqueuedJobIds(tuple.Queue, 0, 5);
                var counters = tuple.Monitoring.GetEnqueuedAndFetchedCount(tuple.Queue);

                var firstJobs = UseStatefulTransaction(session => EnqueuedJobs(session, enqueuedJobIds));

                result.Add(new QueueWithTopEnqueuedJobsDto
                {
                    Name = tuple.Queue,
                    Length = counters.EnqueuedCount ?? 0,
                    Fetched = counters.FetchedCount,
                    FirstJobs = firstJobs
                });
            }

            return result;
        }

        public IList<ServerDto> Servers()
        {
            return UseStatefulTransaction<IList<ServerDto>>(session =>
            {
                var result = new List<ServerDto>();

                foreach (var server in session.Query<_Server>())
                {
                    var data = JobHelper.FromJson<ServerData>(server.Data);
                    result.Add(new ServerDto
                    {
                        Name = server.Id,
                        Heartbeat = server.LastHeartbeat,
                        Queues = data.Queues,
                        StartedAt = data.StartedAt.HasValue ? data.StartedAt.Value : DateTime.MinValue,
                        WorkersCount = data.WorkerCount
                    });
                }

                return result;
            });
        }

        public JobDetailsDto JobDetails(string jobId)
        {
            var converter = StringToInt32Converter.Convert(jobId);
            if (!converter.Valid)
            {
                return null;
            }
            return UseStatefulTransaction(session =>
            {
                var job = session.Query<_Job>().SingleOrDefault(i => i.Id == converter.Value);
                if (job == null) return null;

                var parameters = job.Parameters.ToDictionary(x => x.Name, x => x.Value);
                var history =
                    job.History.OrderByDescending(i => i.Id)
                        .Select(jobState => new StateHistoryDto
                        {
                            StateName = jobState.Name,
                            CreatedAt = jobState.CreatedAt,
                            Reason = jobState.Reason,
                            Data = new Dictionary<string, string>(
                                JobHelper.FromJson<Dictionary<string, string>>(jobState.Data),
                                StringComparer.OrdinalIgnoreCase)
                        })
                        .ToList();

                return new JobDetailsDto
                {
                    CreatedAt = job.CreatedAt,
                    ExpireAt = job.ExpireAt,
                    Job = DeserializeJob(job.InvocationData, job.Arguments),
                    History = history,
                    Properties = parameters
                };
            });
        }

        public StatisticsDto GetStatistics()
        {
            var statistics =
                UseStatefulTransaction(session =>
                    {
                        var statesDictionary = session.Query<_Job>()
                            .Where(i => i.StateName != null && i.StateName != string.Empty)
                            .GroupBy(i => i.StateName)
                            .Select(i => new {i.Key, Count = i.Count()})
                            .ToDictionary(i => i.Key, j => j.Count);

                        int GetJobStatusCount(string key)
                        {
                            if (statesDictionary.ContainsKey(key))
                            {
                                return statesDictionary[key];
                            }
                            return 0;
                        }

                        long CountStats(string key)
                        {
                            var l1 = session.Query<_AggregatedCounter>().Where(i => i.Key == key).Select(i => i.Value)
                                .ToList();
                            var l2 = session.Query<_Counter>().Where(i => i.Key == key).Select(i => i.Value).ToList();
                            return l1.Sum() + l2.Sum();
                        }

                        return new StatisticsDto
                        {
                            
                            Enqueued = GetJobStatusCount("Enqueued"),
                            Failed = GetJobStatusCount("Failed"),
                            Processing = GetJobStatusCount("Processing"),
                            Scheduled = GetJobStatusCount("Scheduled"),
                            Servers = session.Query<_Server>().Count(),
                            Succeeded = CountStats("stats:succeeded"),
                            Deleted = CountStats("stats:deleted"),
                            Recurring = session.Query<_Set>().Count(i => i.Key == "recurring-jobs")
                        };
                    }
                );

            statistics.Queues = _storage.QueueProviders
                .SelectMany(x => x.GetJobQueueMonitoringApi().GetQueues())
                .Count();

            return statistics;
        }

        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int from, int perPage)
        {
            var queueApi = GetQueueApi(queue);
            var enqueuedJobIds = queueApi.GetEnqueuedJobIds(queue, from, perPage);

            return UseStatefulTransaction(session => EnqueuedJobs(session, enqueuedJobIds));
        }

        public JobList<FetchedJobDto> FetchedJobs(string queue, int from, int perPage)
        {
            var queueApi = GetQueueApi(queue);
            var fetchedJobIds = queueApi.GetFetchedJobIds(queue, from, perPage);

            return UseStatefulTransaction(session => FetchedJobs(session, fetchedJobIds));
        }

        public JobList<ProcessingJobDto> ProcessingJobs(int from, int count)
        {
            return UseStatefulTransaction(session => GetJobs(
                session,
                from, count,
                ProcessingState.StateName,
                (sqlJob, job, stateData) => new ProcessingJobDto
                {
                    Job = job,
                    ServerId = stateData.ContainsKey("ServerId") ? stateData["ServerId"] : stateData["ServerName"],
                    StartedAt = JobHelper.DeserializeDateTime(stateData["StartedAt"])
                }));
        }

        public JobList<ScheduledJobDto> ScheduledJobs(int from, int count)
        {
            return UseStatefulTransaction(session => GetJobs(
                session,
                from, count,
                ScheduledState.StateName,
                (sqlJob, job, stateData) => new ScheduledJobDto
                {
                    Job = job,
                    EnqueueAt = JobHelper.DeserializeDateTime(stateData["EnqueueAt"]),
                    ScheduledAt = JobHelper.DeserializeDateTime(stateData["ScheduledAt"])
                }));
        }

        public JobList<SucceededJobDto> SucceededJobs(int from, int count)
        {
            return UseStatefulTransaction(session => GetJobs(
                session,
                from,
                count,
                SucceededState.StateName,
                (sqlJob, job, stateData) => new SucceededJobDto
                {
                    Job = job,
                    Result = stateData.ContainsKey("Result") ? stateData["Result"] : null,
                    TotalDuration = stateData.ContainsKey("PerformanceDuration") && stateData.ContainsKey("Latency")
                        ? (long?) long.Parse(stateData["PerformanceDuration"]) +
                          (long?) long.Parse(stateData["Latency"])
                        : null,
                    SucceededAt = JobHelper.DeserializeNullableDateTime(stateData["SucceededAt"])
                }));
        }

        public JobList<FailedJobDto> FailedJobs(int from, int count)
        {
            return UseStatefulTransaction(session => GetJobs(
                session,
                from,
                count,
                FailedState.StateName,
                (sqlJob, job, stateData) => new FailedJobDto
                {
                    Job = job,
                    Reason = sqlJob.StateReason,
                    ExceptionDetails = stateData["ExceptionDetails"],
                    ExceptionMessage = stateData["ExceptionMessage"],
                    ExceptionType = stateData["ExceptionType"],
                    FailedAt = JobHelper.DeserializeNullableDateTime(stateData["FailedAt"])
                }));
        }

        public JobList<DeletedJobDto> DeletedJobs(int from, int count)
        {
            return UseStatefulTransaction(session => GetJobs(
                session,
                from,
                count,
                DeletedState.StateName,
                (sqlJob, job, stateData) => new DeletedJobDto
                {
                    Job = job,
                    DeletedAt = JobHelper.DeserializeNullableDateTime(stateData["DeletedAt"])
                }));
        }

        public long ScheduledCount()
        {
            return UseStatefulTransaction(session =>
                GetNumberOfJobsByStateName(session, ScheduledState.StateName));
        }

        public long EnqueuedCount(string queue)
        {
            var queueApi = GetQueueApi(queue);
            var counters = queueApi.GetEnqueuedAndFetchedCount(queue);

            return counters.EnqueuedCount ?? 0;
        }

        public long FetchedCount(string queue)
        {
            var queueApi = GetQueueApi(queue);
            var counters = queueApi.GetEnqueuedAndFetchedCount(queue);

            return counters.FetchedCount ?? 0;
        }

        public long FailedCount()
        {
            return UseStatefulTransaction(session =>
                GetNumberOfJobsByStateName(session, FailedState.StateName));
        }

        public long ProcessingCount()
        {
            return UseStatefulTransaction(session =>
                GetNumberOfJobsByStateName(session, ProcessingState.StateName));
        }

        public long SucceededListCount()
        {
            return UseStatefulTransaction(session =>
                GetNumberOfJobsByStateName(session, SucceededState.StateName));
        }

        public long DeletedListCount()
        {
            return UseStatefulTransaction(session =>
                GetNumberOfJobsByStateName(session, DeletedState.StateName));
        }

        public IDictionary<DateTime, long> SucceededByDatesCount()
        {
            return UseStatefulTransaction(session =>
                GetTimelineStats(session, "succeeded"));
        }

        public IDictionary<DateTime, long> FailedByDatesCount()
        {
            return UseStatefulTransaction(session =>
                GetTimelineStats(session, "failed"));
        }

        public IDictionary<DateTime, long> HourlySucceededJobs()
        {
            return UseStatefulTransaction(session =>
                GetHourlyTimelineStats(session, "succeeded"));
        }

        public IDictionary<DateTime, long> HourlyFailedJobs()
        {
            return UseStatefulTransaction(session =>
                GetHourlyTimelineStats(session, "failed"));
        }

        private T UseStatefulTransaction<T>(Func<SessionWrapper, T> action)
        {
            return _storage.UseTransaction(action, IsolationLevel.ReadUncommitted);
        }

        private long GetNumberOfJobsByStateName(SessionWrapper session, string stateName)
        {
            var count = session.Query<_Job>().Count(i => i.StateName == stateName);
            if (_jobListLimit.HasValue)
            {
                return Math.Max(count, _jobListLimit.Value);
            }
            return count;
        }

        private IPersistentJobQueueMonitoringApi GetQueueApi(string queueName)
        {
            var provider = _storage.QueueProviders.GetProvider(queueName);
            var monitoringApi = provider.GetJobQueueMonitoringApi();

            return monitoringApi;
        }

        private JobList<TDto> GetJobs<TDto>(
            SessionWrapper session,
            int from,
            int count,
            string stateName,
            Func<_Job, Job, Dictionary<string, string>, TDto> selector)
        {
            var jobs = session.Query<_Job>()
                .OrderBy(i => i.Id)
                .Where(i => i.StateName == stateName)
                .Skip(from)
                .Take(count)
                .ToList();

            return DeserializeJobs(jobs, selector);
        }

        private static JobList<TDto> DeserializeJobs<TDto>(
            ICollection<_Job> jobs,
            Func<_Job, Job, Dictionary<string, string>, TDto> selector)
        {
            var result = new List<KeyValuePair<string, TDto>>(jobs.Count);

            foreach (var job in jobs)
            {
                var deserializedData = JobHelper.FromJson<Dictionary<string, string>>(job.StateData);
                var stateData = deserializedData != null
                    ? new Dictionary<string, string>(deserializedData, StringComparer.OrdinalIgnoreCase)
                    : null;

                var dto = selector(job, DeserializeJob(job.InvocationData, job.Arguments), stateData);

                result.Add(new KeyValuePair<string, TDto>(
                    job.Id.ToString(), dto));
            }

            return new JobList<TDto>(result);
        }

        private static Job DeserializeJob(string invocationData, string arguments)
        {
            var data = JobHelper.FromJson<InvocationData>(invocationData);
            data.Arguments = arguments;

            try
            {
                return data.Deserialize();
            }
            catch (JobLoadException)
            {
                return null;
            }
        }

        private Dictionary<DateTime, long> GetTimelineStats(
            SessionWrapper session,
            string type)
        {
            var endDate = session.Storage.UtcNow.Date;
            var dates = new List<DateTime>();
            for (var i = 0; i < 7; i++)
            {
                dates.Add(endDate);
                endDate = endDate.AddDays(-1);
            }

            var keyMaps = dates.ToDictionary(x => string.Format("stats:{0}:{1}", type, x.ToString("yyyy-MM-dd")),
                x => x);

            return GetTimelineStats(session, keyMaps);
        }

        private Dictionary<DateTime, long> GetTimelineStats(SessionWrapper session,
            IDictionary<string, DateTime> keyMaps)
        {
            var valuesMap = session.Query<_AggregatedCounter>()
                .Where(i => keyMaps.Keys.Contains(i.Key))
                .ToDictionary(x => x.Key, x => x.Value);

            foreach (var key in keyMaps.Keys)
            {
                if (!valuesMap.ContainsKey(key)) valuesMap.Add(key, 0);
            }

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < keyMaps.Count; i++)
            {
                var value = valuesMap[keyMaps.ElementAt(i).Key];
                result.Add(keyMaps.ElementAt(i).Value, value);
            }

            return result;
        }

        private JobList<EnqueuedJobDto> EnqueuedJobs(
            SessionWrapper session,
            IEnumerable<int> jobIds)
        {
            var jobs = session.Query<_Job>().Where(i => jobIds.Contains(i.Id)).ToList();

            return DeserializeJobs(
                jobs,
                (sqlJob, job, stateData) => new EnqueuedJobDto
                {
                    Job = job,
                    State = sqlJob.StateName,
                    EnqueuedAt = sqlJob.StateName == EnqueuedState.StateName
                        ? JobHelper.DeserializeNullableDateTime(stateData["EnqueuedAt"])
                        : null
                });
        }

        private JobList<FetchedJobDto> FetchedJobs(
            SessionWrapper session,
            IEnumerable<int> jobIds)
        {
            var result = new List<KeyValuePair<string, FetchedJobDto>>();

            foreach (var job in session.Query<_Job>().Where(i => jobIds.Contains(i.Id)))
            {
                result.Add(new KeyValuePair<string, FetchedJobDto>(
                    job.Id.ToString(),
                    new FetchedJobDto
                    {
                        Job = DeserializeJob(job.InvocationData, job.Arguments),
                        State = job.StateName
                    }));
            }

            return new JobList<FetchedJobDto>(result);
        }

        private Dictionary<DateTime, long> GetHourlyTimelineStats(
            SessionWrapper session,
            string type)
        {
            var endDate = session.Storage.UtcNow;
            var dates = new List<DateTime>();
            for (var i = 0; i < 24; i++)
            {
                dates.Add(endDate);
                endDate = endDate.AddHours(-1);
            }

            var keyMaps = dates.ToDictionary(x => string.Format("stats:{0}:{1}", type, x.ToString("yyyy-MM-dd-HH")),
                x => x);

            return GetTimelineStats(session, keyMaps);
        }
    }
}
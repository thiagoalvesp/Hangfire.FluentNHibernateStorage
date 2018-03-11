using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Hangfire.FluentNHibernateStorage.Entities;
using Hangfire.FluentNHibernateStorage.Maps;
using NHibernate;
using NHibernate.Exceptions;

namespace Hangfire.FluentNHibernateStorage
{
    internal static class SqlUtil
    {
        public const string ValueParameterName = "newValue";
        public const string ValueParameter2Name = "newValue2";
        public const string IdParameterName = "entityId";

        public const string NowAsLongParameterName = "nowAsLong";
        public const string NowParameterName = "now";
        public const string ResourceParameterName = "resource";
        public const string ExpireAtAsLongParameterName = "expireAtAsLong";


        private static readonly Dictionary<Type, string> DeleteByIdCommands = new Dictionary<Type, string>();

        /// <summary>
        /// Prevent multiple threads from generating the same HQL statements.
        /// </summary>
        private static readonly object GenerateStatementMutex = new object();


        /// <summary>
        /// HQL statement by which a Server entity will be deleted based on its id
        /// </summary>
        internal static readonly string DeleteServerByIdStatement =
            string.Format("delete from {0} where {1}=:{2}", nameof(_Server).WrapObjectName(),
                nameof(_Server.Id).WrapObjectName(),
                IdParameterName);

        /// <summary>
        /// HQL statement by which a Server entity will have its LastHeartBeat property updated
        /// </summary>
        internal static readonly string UpdateServerLastHeartbeatStatement =
            GetSingleFieldUpdateSql(nameof(_Server), nameof(_Server.LastHeartbeat), nameof(_Server.Id));

        /// <summary>
        /// HQL statement by which a Server entity will be deleted based on its last heartbeart
        /// </summary>
        internal static readonly string DeleteServerByLastHeartbeatStatement = string.Format(
            "delete from {0} where {1} < :{2}",
            nameof(_Server).WrapObjectName(),
            nameof(_Server.LastHeartbeat).WrapObjectName(), ValueParameterName);


        /// <summary>
        /// HQL statement by which the ExpireAt property of a Job entity will be updated
        /// </summary>
        internal static readonly string UpdateJobExpireAtStatement =
            GetSingleFieldUpdateSql(nameof(_Job), nameof(_Job.ExpireAt), nameof(_Job.Id));

        /// <summary>
        /// HQL statement by which the FetchedAt property of a JobQueue entity will be updated
        /// </summary>
        internal static readonly string UpdateJobQueueFetchedAtStatement =
            GetSingleFieldUpdateSql(nameof(_JobQueue), nameof(_JobQueue.FetchedAt), nameof(_JobQueue.Id));

        /// <summary>
        /// HQL statement by which to delete a JobQueue entity based on its id
        /// </summary>
        internal static readonly string DeleteJobQueueStatement = string.Format("delete from {0} where {1}=:{2}",
            nameof(_JobQueue).WrapObjectName(),
            nameof(_JobQueue.Id).WrapObjectName(), IdParameterName);

        /// <summary>
        /// HQL statement by which to update an aggregated counter based upon its key
        /// </summary>
        internal static readonly string UpdateAggregateCounterStatement = string.Format(
            "update {0} s set s.{1}=s.{1} + :{4}, s.{3}= case when s.{3} >  :{6} then s.{3} else :{6} end where s.{2} = :{5}",
            nameof(_AggregatedCounter).WrapObjectName(), nameof(_AggregatedCounter.Value).WrapObjectName(),
            nameof(_AggregatedCounter.Key).WrapObjectName(),
            nameof(_AggregatedCounter.ExpireAt).WrapObjectName(), ValueParameterName, IdParameterName,
            ValueParameter2Name);

        /// <summary>
        /// HQL statements by which to delete Hash, Set or List entities by key
        /// </summary>
        internal static readonly Dictionary<Type, string> DeleteByKeyStatementDictionary = new Dictionary<Type, string>
        {
            {typeof(_Set), GetDeleteByKeyStatement<_Set>()},
            {typeof(_Hash), GetDeleteByKeyStatement<_Hash>()},
            {typeof(_List), GetDeleteByKeyStatement<_List>()}
        };

        /// <summary>
        /// HQL statements by which to delete Set or List entities by key and value properties
        /// </summary>
        internal static readonly Dictionary<Type, string> DeleteByKeyAndValueStatementDictionary =
            new Dictionary<Type, string>
            {
                {typeof(_Set), GetDeleteByKeyValueStatement<_Set>()},
                {typeof(_List), GetDeleteByKeyValueStatement<_List>()}
            };


        /// <summary>
        /// HQL statements by which to set ExpireAt property of Hash, Set or List entities 
        /// </summary>
        internal static readonly Dictionary<Type, string> SetExpireAtByKeyStatementDictionary = new Dictionary<Type, string>
        {
            {typeof(_Set), GetSetExpireAtByKeyStatement<_Set>()},
            {typeof(_Hash), GetSetExpireAtByKeyStatement<_Hash>()},
            {typeof(_List), GetSetExpireAtByKeyStatement<_List>()}
        };

        /// <summary>
        /// HQL statement by which to delete a distributed lock entity
        /// </summary>
        internal static readonly string DeleteDistributedLockSql = string.Format("delete from {0} where {1}=:{2}",
            nameof(_DistributedLock).WrapObjectName(),
            nameof(_DistributedLock.Resource).WrapObjectName(), IdParameterName);

        private static string _createDistributedLockStatement;


        /// <summary>
        /// Generate HQL to update a single property of an entity based on matched some column to a value.
        /// </summary>
        /// <param name="entityTypeName">The type name of the entity</param>
        /// <param name="updatedProperty">The property being updated</param>
        /// <param name="idProperty">The property to match against that uniquely identifies the entity</param>
        /// <returns></returns>
        private static string GetSingleFieldUpdateSql(string entityTypeName, string updatedProperty, string idProperty)
        {
            return string.Format("update {0} set {1}=:{3} where {2}=:{4}", entityTypeName.WrapObjectName(),
                updatedProperty.WrapObjectName(), idProperty.WrapObjectName(),
                ValueParameterName,
                IdParameterName);
        }


        /// <summary>
        /// do an upsert into a table
        /// </summary>
        /// <typeparam name="T">The entity type to upsert</typeparam>
        /// <param name="session">a SessionWrapper instance to act upon</param>
        /// <param name="matchFunc">A function that returns a single instance of T</param>
        /// <param name="changeAction">A delegate that changes specified properties of instance of T </param>
        /// <param name="keysetAction">A delegate that sets the primary key properties of instance of T if we have to do an upsert</param>
        public static void UpsertEntity<T>(this SessionWrapper session, Expression<Func<T, bool>> matchFunc,
            Action<T> changeAction,
            Action<T> keysetAction) where T : new()
        {
            var entity = session.Query<T>().SingleOrDefault(matchFunc);
            if (entity == null)
            {
                entity = new T();
                keysetAction(entity);
                changeAction(entity);
                session.Insert(entity);
            }
            else
            {
                changeAction(entity);
                session.Update(entity);
            }
            session.Flush();
        }


        /// <summary>
        /// delete entities that implement IInt64Id, by using the value stored in their Id property.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="session">Sessionwrapper instance to act upon</param>
        /// <param name="id">Collection of ids to delete</param>
        /// <returns>the number of rows deleted</returns>
        public static long DeleteByInt32Id<T>(this SessionWrapper session, ICollection<int> id) where T : IInt32Id
        {
            if (!id.Any())
            {
                return 0;
            }
            string queryString;
            lock (GenerateStatementMutex)
            {
                var typeName = typeof(T);
                if (DeleteByIdCommands.ContainsKey(typeName))
                {
                    queryString = DeleteByIdCommands[typeName];
                }
                else
                {
                    queryString = string.Format("delete from {0} where {1} in (:{2})", typeName.Name.WrapObjectName(),
                        nameof(IInt32Id.Id).WrapObjectName(), IdParameterName);
                    DeleteByIdCommands[typeName] = queryString;
                }
            }
            return session.CreateQuery(queryString).SetParameterList(IdParameterName, id).ExecuteUpdate();
        }

        public static void DoActionByExpression<T>(this SessionWrapper session, Expression<Func<T, bool>> expr,
            Action<T> action)
            where T : IExpirableWithId
        {
            var entities = session.Query<T>().Where(expr);
            foreach (var entity in entities)
            {
                action(entity);
            }
        }

        /// <summary>
        /// Delete entities that implement iexpirablewithid and have expired based on server's system date
        /// </summary>
        /// <typeparam name="T">The type of entity</typeparam>
        /// <param name="session">Sessionwrapper instance to act upon</param>
        /// <param name="count">Max number of entities to delete</param>
        /// <returns></returns>
        internal static long DeleteExpirableWithId<T>(this SessionWrapper session, int count) where T : IExpirableWithId

        {
            var ids = session.Query<T>()
                .Where(i => i.ExpireAt < session.Storage.UtcNow)
                .Take(count)
                .Select(i => i.Id)
                .ToList();
            return session.DeleteByInt32Id<T>(ids);
        }

        /// <summary>
        /// Generate a HQL statement that sets ExpireAt property of entity that implements IExpirableWithKey
        /// </summary>
        /// <typeparam name="T">The type of entity</typeparam>
        /// <returns>The HQL statement</returns>
        static string GetSetExpireAtByKeyStatement<T>() where T : IExpirableWithKey
        {
            return string.Format("update {0} set {1}=:{2} where {3}=:{4}", typeof(T).Name.WrapObjectName(),
                nameof(IExpirable.ExpireAt).WrapObjectName(), ValueParameterName,
                nameof(IExpirableWithKey.Key).WrapObjectName(),
                IdParameterName);
        }

        /// <summary>
        /// Generate a HQL statement that deletes entities that implement IExpirableWithKey and match a given key
        /// </summary>
        /// <typeparam name="T">The type of entity</typeparam>
        /// <returns>The HQL statement</returns>
        static string GetDeleteByKeyStatement<T>() where T : IExpirableWithKey
        {
            return string.Format("delete from {0} where {1}=:{2}", typeof(T).Name.WrapObjectName(),
                nameof(IExpirableWithKey.Key).WrapObjectName(),
                ValueParameterName);
        }

        /// <summary>
        /// Generate a HQL statement that deletes entities that implement IExpirableWithKey and match a given key and
        /// value
        /// </summary>
        /// <typeparam name="T">The type of entity</typeparam>
        /// <returns>The HQL statement</returns>
        static string GetDeleteByKeyValueStatement<T>() where T : IKeyWithStringValue
        {
            return string.Format("delete from {0} where {1}=:{2} and {3}=:{4}", typeof(T).Name.WrapObjectName(),
                nameof(IExpirableWithKey.Key).WrapObjectName(),
                ValueParameterName, nameof(IKeyWithStringValue.Value).WrapObjectName(), ValueParameter2Name);
        }

        public static T WrapForTransaction<T>(Func<T> safeFunc)
        {
            try
            {
                return safeFunc();
            }
            catch (AssertionFailure)
            {
                //do nothing
            }
            catch (TransactionException)
            {
                //do nothing
            }
            catch (GenericADOException)
            {
                //do nothing
            }
            return default(T);
        }

        public static void WrapForTransaction(Action safeAction)
        {
            WrapForTransaction(() =>
            {
                safeAction();
                return true;
            });
        }

        /// <summary>
        /// Generate a HQL statement that inserts a distributed lock entry into the table if no prior
        /// one exists for the same resource.
        /// </summary>
        /// <returns>The HQL statement</returns>
        public static string GetCreateDistributedLockStatement()
        {
            lock (GenerateStatementMutex)
            {
                if (_createDistributedLockStatement != null)
                {
                    return _createDistributedLockStatement;
                }

                var distributedLockTableName = nameof(_DistributedLock).WrapObjectName();
                var createdAtColumnName = nameof(_DistributedLock.CreatedAt).WrapObjectName();
                var resourceColumnName = nameof(_DistributedLock.Resource).WrapObjectName();
                var dualTableName = nameof(_Dual).WrapObjectName();
                var expireAtAsLongColumnName = nameof(_DistributedLock.ExpireAtAsLong).WrapObjectName();


                _createDistributedLockStatement =
                    $@"insert into {distributedLockTableName} ({resourceColumnName}, {createdAtColumnName}, {
                            expireAtAsLongColumnName
                        })
                        select :{ResourceParameterName}, :{NowParameterName},  
                        :{ExpireAtAsLongParameterName} from 
                        {dualTableName} where not exists (select {Constants.ColumnNames.Id.WrapObjectName()} from 
                        {distributedLockTableName} as d where d.{resourceColumnName} = 
                        :{ResourceParameterName} and 
                        d.{expireAtAsLongColumnName} > :{NowAsLongParameterName})";
                return _createDistributedLockStatement;
            }
        }
    }
}
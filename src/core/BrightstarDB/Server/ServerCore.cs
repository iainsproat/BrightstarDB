﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrightstarDB.Caching;
using BrightstarDB.Client;
using BrightstarDB.Query;
using BrightstarDB.Storage;
using System.Threading;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using ITransactionInfo = BrightstarDB.Storage.ITransactionInfo;
using TransactionType = BrightstarDB.Dto.TransactionType;
using Triple = BrightstarDB.Model.Triple;
#if PORTABLE
using BrightstarDB.Portable.Compatibility;
#endif

namespace BrightstarDB.Server
{
    internal delegate void JobCompletedDelegate(object sender, JobCompletedEventArgs e);

    internal class ServerCore
    {
        private readonly string _baseLocation;

        // maps logical store names to a store worker
        private readonly Dictionary<string, StoreWorker> _stores;

        private readonly IStoreManager _storeManager;

        private readonly ICache _queryCache;

        public ServerCore(string baseLocation, ICache queryCache, PersistenceType persistenceType)
        {
            Logging.LogInfo("ServerCore Initialised {0}", baseLocation);
            _baseLocation = baseLocation;
            _stores = new Dictionary<string, StoreWorker>();
            var configuration = new StoreConfiguration {PersistenceType = persistenceType};
            _storeManager = StoreManagerFactory.GetStoreManager(configuration);
            _queryCache = queryCache;
        }

        /// <summary>
        /// Event invoked when a transaction has completed processing successfully but
        /// begore the job status is updated to completed OK
        /// </summary>
        public event JobCompletedDelegate JobCompleted;

        public void Shutdown(bool completeJobs)
        {
            foreach (var worker in _stores.Values)
            {
                worker.Shutdown(completeJobs);
            }
        }

        public void ShutdownStore(string storeName, bool completeJobs)
        {
            var storeWorker = GetStoreWorker(storeName);
            storeWorker.Shutdown(true);
        }

        public IEnumerable<string> ListStores()
        {
            Logging.LogInfo("List Stores");
            return _storeManager.ListStores(_baseLocation);
        }

        /// <summary>
        /// Creates a new store and returns the store id
        /// </summary>
        /// <returns></returns>
        public string CreateStore()
        {
            Logging.LogInfo("Create Store");
            var sid = Guid.NewGuid().ToString();
            _storeManager.CreateStore(Path.Combine(_baseLocation, sid), true);
            Logging.LogInfo("Store id is {0}", sid);
            return sid;
        }

        public string CreateStore(string storeName, PersistenceType persistenceType)
        {
            Logging.LogInfo("Create Store");
            var store = _storeManager.CreateStore(Path.Combine(_baseLocation, storeName), persistenceType, true);
            store.Close();
            Logging.LogInfo("Store id is {0}", storeName);
            return storeName;
        }

        public bool DoesStoreExist(string storeName)
        {
            Logging.LogInfo("check store exists store {0}", storeName);
            return _storeManager.DoesStoreExist(Path.Combine(_baseLocation, storeName));
        }

#if PORTABLE
        public void DeleteStore(string storeName)
        {
            Logging.LogInfo("Delete store {0}", storeName);
            var storeWorker = GetStoreWorker(storeName);
            // remove store worker from collection
            RemoveStoreWorker(storeName);
            storeWorker.Shutdown(false, () => _storeManager.DeleteStore(_baseLocation + "\\" + storeName));            
        }
#else
        public void DeleteStore(string storeName, bool waitForCompletion = true)
        {
            Logging.LogInfo("Delete store {0}", storeName);
            var storeWorker = GetStoreWorker(storeName);
            // remove store worker from collection
            RemoveStoreWorker(storeName);
            storeWorker.Shutdown(false, () => _storeManager.DeleteStore(_baseLocation + "\\" + storeName));

            if (waitForCompletion) 
            {
                while (DoesStoreExist(storeName))
                {
                    Thread.Sleep(10);
                }
            }
        }
#endif

        private void RemoveStoreWorker(string storeName)
        {
            lock (_stores)
            {
                if (_stores.ContainsKey(_baseLocation + "\\" + storeName))
                {
                    _stores.Remove(_baseLocation + "\\" + storeName);
                }
            }
        }

        private StoreWorker GetStoreWorker(string storeName)
        {
            lock (_stores)
            {
                if (_stores.ContainsKey(_baseLocation + "\\" + storeName))
                {
                    return _stores[_baseLocation + "\\" + storeName];
                }
                if (!DoesStoreExist(storeName))
                {
                    throw new NoSuchStoreException(storeName);
                }
                // create store manager
                var storeWorker = new StoreWorker(_baseLocation , storeName);
                storeWorker.JobCompleted += HandleStoreWorkerJobCompleted;
                _stores.Add(_baseLocation + "\\" + storeName, storeWorker);
                storeWorker.Start();
                return storeWorker;
            }
        }

        private void HandleStoreWorkerJobCompleted(object sender, JobCompletedEventArgs e)
        {
            if (JobCompleted != null)
            {
                JobCompleted(this, e);
            }
        }

        // operations
        public ISerializationFormat Query(string storeName, string queryExpression, IEnumerable<string> defaultGraphUris,
                                          DateTime? ifNotModifiedSince,
                                          SparqlResultsFormat sparqlResultFormat, RdfFormat graphFormat,
                                          Stream responseStream)
        {
            Logging.LogDebug("Query {0} {1}", storeName, queryExpression);
            var commitPoint =
                _storeManager.GetMasterFile(Path.Combine(_baseLocation, storeName)).GetCommitPoints().First();
            if (ifNotModifiedSince.HasValue && ifNotModifiedSince > commitPoint.CommitTime)
            {
                throw new BrightstarStoreNotModifiedException();
            }
            var g = defaultGraphUris == null ? null : defaultGraphUris.ToArray();
            var query = ParseSparql(queryExpression);
            var targetFormat = QueryReturnsGraph(query) ? (ISerializationFormat) graphFormat : sparqlResultFormat;

            var cacheKey = MakeQueryCacheKey(storeName, commitPoint.CommitTime.Ticks, query, g, targetFormat);
            var cachedResult = GetCachedResult(cacheKey);
            if (cachedResult == null)
            {
                // Not in the cache so execute the query on the StoreWorker
                var storeWorker = GetStoreWorker(storeName);
                var cacheStream = new MemoryStream();
                storeWorker.Query(query, sparqlResultFormat, graphFormat, cacheStream, g);
                cachedResult = CacheResult(cacheKey, cacheStream.ToArray());
            }
            cachedResult.WriteTo(responseStream);
            return targetFormat;
        }

        public ISerializationFormat Query(string storeName, ulong commitPointId, string queryExpression,
                                          IEnumerable<string> defaultGraphUris,
                                          SparqlResultsFormat sparqlResultFormat, RdfFormat graphFormat,
                                          Stream responseStream)
        {
            Logging.LogDebug("Query {0}@{1} {2}", storeName, commitPointId, queryExpression);
            var g = defaultGraphUris == null ? null : defaultGraphUris.ToArray();
            var storeLocation = Path.Combine(_baseLocation, storeName);
            var masterFile = _storeManager.GetMasterFile(storeLocation);
            if (masterFile.PersistenceType == PersistenceType.Rewrite)
            {
                throw new BrightstarClientException(
                    "Query of past commit points is not supported by the binary page persistence type");
            }
            var commitPoint =
                masterFile.GetCommitPoints().FirstOrDefault(c => c.LocationOffset == commitPointId);
            if (commitPoint == null)
            {
                throw new InvalidCommitPointException(String.Format("Could not find commit point {0} for store {1}.",
                                                                    commitPointId, storeName));
            }

            var query = ParseSparql(queryExpression);
            var targetFormat = QueryReturnsGraph(query) ? (ISerializationFormat)graphFormat : sparqlResultFormat;
            var cacheKey = MakeQueryCacheKey(storeName, commitPoint.CommitTime.Ticks, query, g, targetFormat);
            var cachedResult = GetCachedResult(cacheKey);
            if (cachedResult == null)
            {
                var storeWorker = GetStoreWorker(storeName);
                var cacheStream = new MemoryStream();
                storeWorker.Query(commitPointId, query, sparqlResultFormat, graphFormat, cacheStream, g);
                cachedResult = CacheResult(cacheKey, cacheStream.ToArray());
            }

            cachedResult.WriteTo(responseStream);
            return targetFormat;
        }

        private static bool QueryReturnsGraph(SparqlQuery query)
        {
            return (query.QueryType == SparqlQueryType.Construct ||
                    query.QueryType == SparqlQueryType.Describe ||
                    query.QueryType == SparqlQueryType.DescribeAll);
        }

        #region Query Caching

        
        private string MakeQueryCacheKey(string storeName, long commitTime, SparqlQuery query, string[] defaultGraphUris, ISerializationFormat targetFormat)
        {
            var graphHashCode = defaultGraphUris == null ? 0 : String.Join(",", defaultGraphUris.OrderBy(s=>s)).GetHashCode();
            return storeName + "_" + commitTime + "_" + query.GetHashCode() + "_" + graphHashCode + "_" + targetFormat;
        }



        private QueryCacheEntry GetCachedResult(string key)
        {
            return _queryCache.Lookup<QueryCacheEntry>(key);
        }

        private QueryCacheEntry CacheResult(string key, byte[] data)
        {
            var entry = new QueryCacheEntry(data);
            _queryCache.Insert(key, entry, CachePriority.Normal);
            return entry;
        }

        #endregion


        public Guid ProcessTransaction(string storeName, string preconditions, string deletePatterns, string insertData, string defaultGraphUri)
        {
            var storeWorker = GetStoreWorker(storeName);
            return storeWorker.ProcessTransaction(preconditions, deletePatterns, insertData, defaultGraphUri, "nt");
        }

        //public void QueueTransaction(Guid jobId, string storeId, string preconditions, string deletes, string inserts)
        //{
        //    var storeWorker = GetStoreWorker(storeId);
        //    storeWorker.QueueJob(new UpdateTransaction(jobId, storeWorker, preconditions, deletes, inserts));
        //}

        //public void Insert(string storeName, string data, string format)
        //{
        //    var storeWorker = GetStoreWorker(storeName);
        //    storeWorker.Insert(data, format);
        //}

        public Guid Import(string storeName, string contentFileName, string graphUri/* = Constants.DefaultGraphUri*/)
        {
            var storeWorker = GetStoreWorker(storeName);
            return storeWorker.Import(contentFileName, graphUri);
        }

        internal IEnumerable<JobExecutionStatus> GetJobs(string storeName)
        {
            var storeWorker = GetStoreWorker(storeName);
            return storeWorker.GetJobs();
        }

        public JobExecutionStatus GetJobStatus(string storeId, string jobId)
        {
            var storeWorker = GetStoreWorker(storeId);
            return storeWorker.GetJobStatus(jobId);
        }

        public Guid Export(string storeName, string fileName, string graphUri)
        {
            var storeWorker = GetStoreWorker(storeName);
            return storeWorker.Export(fileName, graphUri);
        }

        public IEnumerable<Triple> GetResourceStatements(string storeId, string resourceUri)
        {
            var storeWorker = GetStoreWorker(storeId);
            return storeWorker.GetResourceStatements(resourceUri);                        
        }

        public IEnumerable<CommitPoint> GetCommitPoints(string storeId)
        {
            return _storeManager.GetMasterFile(Path.Combine(_baseLocation, storeId)).GetCommitPoints();
        }

        public void RevertToCommitPoint(string storeId, ulong commitPointLocation)
        {
            // TODO: Validate that this is a proper commit point location
            //var commitPoint = _storeManager.GetCommitPoint(_baseLocation + "\\" + storeId, commitPointLocation);
            var lastCommit = _storeManager.GetMasterFile(Path.Combine(_baseLocation, storeId)).GetLatestCommitPoint();
            var commitPoint = new CommitPoint(commitPointLocation, lastCommit.NextCommitNumber, DateTime.UtcNow, Guid.Empty);
            var storeWorker = GetStoreWorker(storeId);
            storeWorker.WriteStore.RevertToCommitPoint(commitPoint);
            storeWorker.InvalidateReadStore();
        }

        public IEnumerable<ITransactionInfo> GetTransactions(string storeId)
        {
            var transactionLog = _storeManager.GetTransactionLog(_baseLocation + "\\" + storeId);
            var transactionList = transactionLog.GetTransactionList();
            while (transactionList.MoveNext())
            {
                yield return transactionList.Current;
            }
        }

        /// <summary>
        /// Return an enumeration of the most recent transactions in the transaction log in 
        /// time order (oldest first)
        /// </summary>
        /// <param name="storeId">The store to read from</param>
        /// <param name="maxCount">The maximum number of transactions to return</param>
        /// <param name="ts">The maximum age of transaction to return</param>
        /// <returns>An enumeration of the <paramref name="maxCount"/> most recent transactions that match the age filter</returns>
        public List<ITransactionInfo> GetRecentTransactions(string storeId, int maxCount, TimeSpan ts)
        {
            var transactionLog = _storeManager.GetTransactionLog(Path.Combine(_baseLocation, storeId));
            return transactionLog.GetTransactionList(maxCount, ts);
        } 

        public Guid ReExecuteTransaction(string storeId, ulong dataStartPosition, TransactionType transactionType)
        {
            var storeWorker = GetStoreWorker(storeId);
            var transactionLog = _storeManager.GetTransactionLog(_baseLocation + "\\" + storeId);

            var jobId = Guid.NewGuid();
            switch (transactionType)
            {
                case TransactionType.ImportJob:
                    var importJob = new ImportJob(jobId, storeWorker);
                    importJob.ReadTransactionDataFromStream(transactionLog.GetTransactionData(dataStartPosition));
                    storeWorker.QueueJob(importJob);
                    break;
                case TransactionType.UpdateTransaction:
                    var updateJob = new UpdateTransaction(jobId, storeWorker);
                    updateJob.ReadTransactionDataFromStream(transactionLog.GetTransactionData(dataStartPosition));
                    storeWorker.QueueJob(updateJob);
                    break;
                case TransactionType.SparqlUpdateTransaction:
                    var sparqlUpdateJob = new SparqlUpdateJob(jobId, storeWorker, null);
                    sparqlUpdateJob.ReadTransactionDataFromStream(transactionLog.GetTransactionData(dataStartPosition));
                    storeWorker.QueueJob(sparqlUpdateJob);
                    break;
            }
            return jobId;
        }

        public CommitPoint GetCommitPoint(string storeName, ulong commitOffset)
        {
            return GetCommitPoints(storeName).FirstOrDefault(x => x.LocationOffset.Equals(commitOffset));
        }

        public CommitPoint GetCommitPoint(string storeName, DateTime timestamp)
        {
            var utcTimestamp = timestamp.ToUniversalTime();
            return GetCommitPoints(storeName).FirstOrDefault(x => x.CommitTime <= utcTimestamp);
        }

        public Guid Consolidate(string store)
        {
            var storeWorker = GetStoreWorker(store);
            var jobId = Guid.NewGuid();
            storeWorker.QueueJob(new ConsolidateJob(jobId, storeWorker));
            return jobId;
        }

        public Guid ExecuteUpdate(string store, string updateExpression)
        {
            var storeWorker = GetStoreWorker(store);
            var jobId = Guid.NewGuid();
            storeWorker.QueueJob(new SparqlUpdateJob(jobId, storeWorker, updateExpression));
            return jobId;
        }

        public void QueueUpdate(Guid jobId, string store, string updateExpression)
        {
            var storeWorker = GetStoreWorker(store);
            storeWorker.QueueJob(new SparqlUpdateJob(jobId, storeWorker, updateExpression));
        }

        public Job LoadTransaction(string storeId, ITransactionInfo txn)
        {
            var transactionLog = _storeManager.GetTransactionLog(_baseLocation + "\\" + storeId);

            var jobId = Guid.NewGuid();
            switch (txn.TransactionType)
            {
                case TransactionType.ImportJob:
                    var importJob = new ImportJob(jobId, null);
                    importJob.ReadTransactionDataFromStream(transactionLog.GetTransactionData(txn.DataStartPosition));
                    return importJob;
                case TransactionType.UpdateTransaction:
                    var updateJob = new UpdateTransaction(jobId, null);
                    updateJob.ReadTransactionDataFromStream(transactionLog.GetTransactionData(txn.DataStartPosition));
                    return updateJob;
                case TransactionType.SparqlUpdateTransaction:
                    var sparqlUpdateJob = new SparqlUpdateJob(jobId, null, null);
                    sparqlUpdateJob.ReadTransactionDataFromStream(transactionLog.GetTransactionData(txn.DataStartPosition));
                    return sparqlUpdateJob;
            }
            return null;
        }

        public IEnumerable<Storage.Statistics.StoreStatistics> GetStatistics(string store)
        {
            var storeWorker = GetStoreWorker(store);
            return storeWorker.StoreStatistics.GetStatistics();
        }

        public Guid UpdateStatistics(string storeName)
        {
            var storeWorker = GetStoreWorker(storeName);
            return storeWorker.UpdateStatistics();
        }

        public Guid CreateSnapshot(string sourceStoreName, string targetStoreName, PersistenceType persistenceType, ulong sourceCommitPointId)
        {

            var storeWorker = GetStoreWorker(sourceStoreName);
            return storeWorker.QueueSnapshotJob(targetStoreName, persistenceType, sourceCommitPointId);
        }

        private static SparqlQuery ParseSparql(string exp)
        {
            var parser = new SparqlQueryParser(SparqlQuerySyntax.Extended);
            var expressionFactories = parser.ExpressionFactories.ToList();
            expressionFactories.Add(new BrightstarFunctionFactory());
            parser.ExpressionFactories = expressionFactories;
            var query = parser.ParseFromString(exp);
            return query;
        }
    }
}

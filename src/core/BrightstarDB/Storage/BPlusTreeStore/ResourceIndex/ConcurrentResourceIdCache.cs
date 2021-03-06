﻿using System;
#if !PORTABLE
using System.Collections.Concurrent;
#else
using BrightstarDB.Portable.Compatibility;
#endif

namespace BrightstarDB.Storage.BPlusTreeStore.ResourceIndex
{
    internal class ConcurrentResourceIdCache : IResourceIdCache
    {
        private ConcurrentDictionary<string, ulong > _cache = new ConcurrentDictionary<string, ulong>();

        #region Implementation of IResourceIdCache

        /// <summary>
        /// Returns the number of items currently held in the cache
        /// </summary>
        public int CacheEntryCount
        {
            get { return _cache.Count; }
        }

        /// <summary>
        /// Adds a new entry to the cache
        /// </summary>
        /// <param name="resourceHashString">The resource hash string</param>
        /// <param name="resourceId">The ID assigned to the resource</param>
        public void Add(string resourceHashString, ulong resourceId)
        {
            _cache.TryAdd(resourceHashString, resourceId);
        }

        /// <summary>
        /// Looks up the ID for the specified resource
        /// </summary>
        /// <param name="resourceHashString">The resource hash string</param>
        /// <param name="resourceId">Receives the resource ID if a match is found</param>
        /// <returns>True if a match is found, false otherwise</returns>
        public bool TryGetValue(string resourceHashString, out ulong resourceId)
        {
            return _cache.TryGetValue(resourceHashString, out resourceId);
        }

        /// <summary>
        /// Clears the cache
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
        }

        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool _disposed;
        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_disposed)
                {
                    _cache = null;
                    _disposed = true;
                }
            }
        }

        ~ConcurrentResourceIdCache()
        {
            Dispose(false);
        }
    }
}
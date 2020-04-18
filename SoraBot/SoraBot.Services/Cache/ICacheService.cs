﻿using System;
using System.Threading.Tasks;
using ArgonautCore.Maybe;

namespace SoraBot.Services.Cache
{
    /// <summary>
    /// Cache service. Has 2 different caches. One custom cache which users strings (slower)
    /// And one specifically for Discord snowflake IDs (faster)
    /// </summary>
    public interface ICacheService
    {
        Maybe<object> Get(string id);
        Maybe<object> Get(ulong id);
        Maybe<T> Get<T>(string id) where T : class;
        Maybe<T> Get<T>(ulong id) where T : class;

        Maybe<T> GetOrSetAndGet<T>(string id, Func<T> set, TimeSpan? ttl = null);
        Maybe<T> GetOrSetAndGet<T>(ulong id, Func<T> set, TimeSpan? ttl = null);
        
        Task<Maybe<T>> GetOrSetAndGetAsync<T>(string id, Func<Task<T>> set, TimeSpan? ttl = null);
        Task<Maybe<T>> GetOrSetAndGetAsync<T>(ulong id, Func<Task<T>> set, TimeSpan? ttl = null);

        void Set(string id, object obj, TimeSpan? ttl = null);
        void Set(ulong id, object obj, TimeSpan? ttl = null);
    }
}
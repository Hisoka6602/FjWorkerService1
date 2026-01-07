using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace FjWorkerService1.Helpers {

    /// <summary>
    /// CollectionsExtension
    /// </summary>
    public static class CollectionsExtension {

        #region 字典扩展

        /// <summary>
        /// 移除满足条件的项目。
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="pairs"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        public static int RemoveWhen<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> pairs, Func<KeyValuePair<TKey, TValue>, bool> func) {
            var list = new List<TKey>();
            foreach (var item in pairs) {
                if (func?.Invoke(item) == true) {
                    list.Add(item.Key);
                }
            }

            var count = 0;
            foreach (var item in list) {
                if (pairs.TryRemove(item, out _)) {
                    count++;
                }
            }
            return count;
        }

#if NET45_OR_GREATER || NETSTANDARD2_0_OR_GREATER

        /// <summary>
        /// 尝试添加
        /// </summary>
        /// <typeparam name="Tkey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="dictionary"></param>
        /// <param name="tkey"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool TryAdd<Tkey, TValue>(this Dictionary<Tkey, TValue> dictionary, Tkey tkey, TValue value)
        {
            if (dictionary.ContainsKey(tkey))
            {
                return false;
            }
            dictionary.Add(tkey, value);
            return true;
        }

#endif

        /// <summary>
        /// 尝试添加
        /// </summary>
        /// <typeparam name="Tkey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="dictionary"></param>
        /// <param name="tkey"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static void AddOrUpdate<Tkey, TValue>(this Dictionary<Tkey, TValue> dictionary, Tkey tkey, TValue value) {
            if (!dictionary.TryAdd(tkey, value)) {
                dictionary[tkey] = value;
            }
        }

        /// <summary>
        /// 尝试添加
        /// </summary>
        /// <typeparam name="Tkey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="dictionary"></param>
        /// <param name="tkey"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static void AddOrUpdate<Tkey, TValue>(this ConcurrentDictionary<Tkey, TValue> dictionary, Tkey tkey, TValue value) {
            if (!dictionary.TryAdd(tkey, value)) {
                dictionary[tkey] = value;
            }
        }

        /// <summary>
        /// 获取值。如果键不存在，则返回默认值。
        /// </summary>
        /// <typeparam name="Tkey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="dictionary"></param>
        /// <param name="tkey"></param>
        /// <returns></returns>
        public static TValue GetValue<Tkey, TValue>(this Dictionary<Tkey, TValue> dictionary, Tkey tkey) {
            return dictionary.TryGetValue(tkey, out var value) ? value : default;
        }

        #endregion 字典扩展

        #region ConcurrentQueue

#if !NET6_0_OR_GREATER

        /// <summary>
        /// 清除所有成员
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="queue"></param>
        public static void Clear<T>(this ConcurrentQueue<T> queue)
        {
            while (queue.TryDequeue(out _))
            {
            }
        }

#endif

        /// <summary>
        /// 清除所有成员
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="queue"></param>
        /// <param name="action"></param>
        public static void Clear<T>(this ConcurrentQueue<T> queue, Action<T> action) {
            while (queue.TryDequeue(out var t)) {
                action?.Invoke(t);
            }
        }

        #endregion ConcurrentQueue

        #region IEnumerableT

        /// <summary>
        /// 循环遍历每个元素，执行Action动作
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="values"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public static IEnumerable<T> ForEach<T>(this IEnumerable<T> values, Action<T> action) {
            foreach (var item in values) {
                action.Invoke(item);
            }

            return values;
        }

        /// <summary>
        /// 循环遍历每个元素，执行异步动作
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="values"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        public static async Task<IEnumerable<T>> ForEachAsync<T>(this IEnumerable<T> values, Func<T, Task> func) {
            foreach (var item in values) {
                await func.Invoke(item);
            }

            return values;
        }

        #endregion IEnumerableT
    }
}

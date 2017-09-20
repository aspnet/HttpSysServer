// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.HttpSys.Internal
{
    internal partial class RequestHeaders : IDictionary<string, StringValues>
    {
        private IDictionary<string, StringValues> _extra;
        private NativeRequestContext _requestMemoryBlob;

        internal RequestHeaders(NativeRequestContext requestMemoryBlob)
        {
            _requestMemoryBlob = requestMemoryBlob;
        }

        private IDictionary<string, StringValues> Extra
        {
            get
            {
                if (_extra == null)
                {
                    var newDict = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
                    GetUnknownHeaders(newDict);
                    Interlocked.CompareExchange(ref _extra, newDict, null);
                }
                return _extra;
            }
        }

        StringValues IDictionary<string, StringValues>.this[string key]
        {
            get
            {
                StringValues value;
                return PropertiesTryGetValue(key, out value) ? value : Extra[key];
            }
            set
            {
                if (!PropertiesTrySetValue(key, value))
                {
                    Extra[key] = value;
                }
            }
        }

        private string GetKnownHeader(HttpSysRequestHeader header)
        {
            return _requestMemoryBlob.GetKnownHeader(header);
        }

        private void GetUnknownHeaders(IDictionary<string, StringValues> extra)
        {
            _requestMemoryBlob.GetUnknownHeaders(extra);
        }

        void IDictionary<string, StringValues>.Add(string key, StringValues value)
        {
            if (!PropertiesTrySetValue(key, value))
            {
                Extra.Add(key, value);
            }
        }

        public bool ContainsKey(string key)
        {
            return PropertiesContainsKey(key) || Extra.ContainsKey(key);
        }

        public ICollection<string> Keys
        {
            get { return PropertiesKeys().Concat(Extra.Keys).ToArray(); }
        }

        ICollection<StringValues> IDictionary<string, StringValues>.Values
        {
            get { return PropertiesValues().Concat(Extra.Values).ToArray(); }
        }

        public int Count
        {
            get { return PropertiesKeys().Count() + Extra.Count; }
        }

        public bool Remove(string key)
        {
            // Although this is a mutating operation, Extra is used instead of StrongExtra,
            // because if a real dictionary has not been allocated the default behavior of the
            // nil dictionary is perfectly fine.
            return PropertiesTryRemove(key) || Extra.Remove(key);
        }

        public bool TryGetValue(string key, out StringValues value)
        {
            return PropertiesTryGetValue(key, out value) || Extra.TryGetValue(key, out value);
        }

        void ICollection<KeyValuePair<string, StringValues>>.Add(KeyValuePair<string, StringValues> item)
        {
            ((IDictionary<string, object>)this).Add(item.Key, item.Value);
        }

        void ICollection<KeyValuePair<string, StringValues>>.Clear()
        {
            foreach (var key in PropertiesKeys())
            {
                PropertiesTryRemove(key);
            }
            Extra.Clear();
        }

        bool ICollection<KeyValuePair<string, StringValues>>.Contains(KeyValuePair<string, StringValues> item)
        {
            object value;
            return ((IDictionary<string, object>)this).TryGetValue(item.Key, out value) && Object.Equals(value, item.Value);
        }

        void ICollection<KeyValuePair<string, StringValues>>.CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex)
        {
            PropertiesEnumerable().Concat(Extra).ToArray().CopyTo(array, arrayIndex);
        }

        bool ICollection<KeyValuePair<string, StringValues>>.IsReadOnly
        {
            get { return false; }
        }

        bool ICollection<KeyValuePair<string, StringValues>>.Remove(KeyValuePair<string, StringValues> item)
        {
            return ((IDictionary<string, StringValues>)this).Contains(item) &&
                ((IDictionary<string, StringValues>)this).Remove(item.Key);
        }

        IEnumerator<KeyValuePair<string, StringValues>> IEnumerable<KeyValuePair<string, StringValues>>.GetEnumerator()
        {
            return PropertiesEnumerable().Concat(Extra).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IDictionary<string, StringValues>)this).GetEnumerator();
        }
    }
}

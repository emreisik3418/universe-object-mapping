using System;
using System.Collections.Generic;

namespace UnvObjectMapping.Model
{
    /// <summary>Tek bir universe'ten okunan her şey. TableError doluysa tablolar okunamamıştır (nesneler geçerlidir).</summary>
    public sealed class UniverseContent
    {
        public UniverseContent(List<UniverseObject> objects, List<UniverseTable> tables, Exception tableError)
        {
            Objects = objects;
            Tables = tables;
            TableError = tableError;
        }

        public List<UniverseObject> Objects { get; }
        public List<UniverseTable> Tables { get; }
        public Exception TableError { get; }
    }
}

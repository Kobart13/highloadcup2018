using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AspNetCoreWebApi.Processing;
using AspNetCoreWebApi.Processing.Requests;

namespace AspNetCoreWebApi.Storage.Contexts
{
    public class FirstNameContext : IBatchLoader<string>, ICompresable
    {
        private ReaderWriterLock _rw = new ReaderWriterLock();
        private string[] _names = new string[DataConfig.MaxId];
        private List<int> _ids = new List<int>();
        private List<int> _null = new List<int>();

        public FirstNameContext()
        {
        }

        public void InitNull(IdStorage ids)
        {
            _null.Clear();
            foreach(var id in ids.AsEnumerable())
            {
                if (_names[id] == null)
                {
                    _null.Add(id);
                }
            }
            _null.FilterSort();
            _null.TrimExcess();
        }

        public void LoadBatch(int id, string name)
        {
            _names[id] = string.Intern(name);
            _ids.Add(id);
        }

        public void AddOrUpdate(int id, string name)
        {
            _rw.AcquireWriterLock(2000);
            if (_names[id] == null)
            {
                _ids.SortedInsert(id);
            }
            _names[id] = string.Intern(name);

            _rw.ReleaseWriterLock();
        }

        public bool TryGet(int id, out string fname)
        {
            fname = _names[id];
            return fname != null;
        }

        public IEnumerable<int> Filter(
            FilterRequest.FnameRequest fname,
            IdStorage idStorage)
        {
            if (fname.IsNull != null)
            {
                if (fname.IsNull.Value)
                {
                    if (fname.Eq == null && fname.Any.Count == 0)
                    {
                        return _null;
                    }
                    else
                    {
                        return Enumerable.Empty<int>();
                    }
                }
            }

            if (fname.Eq != null && fname.Any.Count > 0)
            {
                if (fname.Any.Contains(fname.Eq))
                {
                    return _ids.Where(x => _names[x] == fname.Eq);
                }
                else
                {
                    return Enumerable.Empty<int>();
                }
            }

            if (fname.Any.Count > 0)
            {
                return _ids.Where(x => fname.Any.Contains(_names[x]));
            }
            else if (fname.Eq != null)
            {
                return _ids.Where(x => _names[x] == fname.Eq);
            }

            return _ids;
        }

        public void Compress()
        {
            _ids.Sort(ReverseComparer<int>.Default);
            _ids.TrimExcess();
            _null.TrimExcess();
        }
    }
}
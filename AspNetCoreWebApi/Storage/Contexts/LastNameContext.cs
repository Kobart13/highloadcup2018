using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AspNetCoreWebApi.Processing;
using AspNetCoreWebApi.Processing.Requests;

namespace AspNetCoreWebApi.Storage.Contexts
{
    public class LastNameContext : IBatchLoader<string>, ICompresable
    {
        private ReaderWriterLock _rw = new ReaderWriterLock();
        private string[] _names = new string[DataConfig.MaxId];
        private DelaySortedList _ids = new DelaySortedList();
        private DelaySortedList _null = new DelaySortedList();

        public LastNameContext()
        {
        }

        public void InitNull(IdStorage ids)
        {
            _null.Clear();
            foreach(var id in ids.AsEnumerable())
            {
                if (_names[id] == null)
                {
                    _null.Load(id);
                }
            }
            _null.LoadEnded();
        }

        public void LoadBatch(int id, string lastname)
        {
            _names[id] = string.Intern(lastname);
            _ids.Load(id);
        }

        public void AddOrUpdate(int id, string name)
        {
            _rw.AcquireWriterLock(2000);

            if (_names[id] == null)
            {
                _ids.DelayAdd(id);
            }
            _names[id] = string.Intern(name);

            _rw.ReleaseWriterLock();
        }

        public bool TryGet(int id, out string sname)
        {
            sname = _names[id];
            return sname != null;
        }

        public IEnumerable<int> Filter(FilterRequest.SnameRequest sname, IdStorage idStorage)
        {
            if (sname.IsNull != null)
            {
                if (sname.IsNull.Value)
                {
                    if (sname.Eq == null && sname.Starts == null)
                    {
                        return _null.AsEnumerable();
                    }
                    else
                    {
                        return Enumerable.Empty<int>();
                    }
                }
            }

            if (sname.Eq != null && sname.Starts != null)
            {
                if (sname.Eq.StartsWith(sname.Starts))
                {
                    return _ids.AsEnumerable().Where(x => _names[x] == sname.Eq);
                }
                else
                {
                    return Enumerable.Empty<int>();
                }
            }

            if (sname.Starts != null)
            {
                return _ids.AsEnumerable().Where(x => _names[x].StartsWith(sname.Starts));
            }
            else if (sname.Eq != null)
            {
                return _ids.AsEnumerable().Where(x => _names[x] == sname.Eq);
            }

            return _ids.AsEnumerable();
        }

        public void Compress()
        {
            _rw.AcquireWriterLock(2000);
            _ids.Flush();
            _rw.ReleaseWriterLock();
        }

        public void LoadEnded()
        {
            _ids.LoadEnded();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AspNetCoreWebApi.Domain;
using AspNetCoreWebApi.Processing;
using AspNetCoreWebApi.Processing.Pooling;
using AspNetCoreWebApi.Processing.Requests;

namespace AspNetCoreWebApi.Storage.Contexts
{
    public class JoinedContext : IBatchLoader<UnixTime>, ICompresable
    {
        private ReaderWriterLock _rw = new ReaderWriterLock();
        private UnixTime[] _id2time = new UnixTime[DataConfig.MaxId];
        private Dictionary<int, FilterSet> _years = new Dictionary<int, FilterSet>();

        public JoinedContext()
        {
        }

        public void AddOrUpdate(int id, UnixTime time)
        {
            var oldYear = _id2time[id].Year;
            _id2time[id] = time;

            if (_years.ContainsKey(oldYear))
            {
                _years[oldYear].Remove(id);
            }

            var newYear = time.Year;
            if (!_years.ContainsKey(newYear))
            {
                _years[newYear] = new FilterSet();
            }

            _years[newYear].Add(id);
        }

        public void Compress()
        {
            _years.TrimExcess();
        }

        public FilterSet Filter(GroupRequest.JoinedRequest joined)
        {
            if (_years.ContainsKey(joined.Year))
            {
                return _years[joined.Year];
            }
            else
            {
                return FilterSet.Empty;
            }
        }

        public void LoadBatch(int id, UnixTime joined)
        {
            var newYear = joined.Year;
            if (!_years.ContainsKey(newYear))
            {
                _years[newYear] = new FilterSet();
            }

            _years[newYear].Add(id);
            _id2time[id] = joined;
        }
    }
}
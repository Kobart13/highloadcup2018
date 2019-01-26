using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AspNetCoreWebApi.Domain;
using AspNetCoreWebApi.Processing;
using AspNetCoreWebApi.Processing.Pooling;
using AspNetCoreWebApi.Processing.Requests;
using AspNetCoreWebApi.Storage.StringPools;

namespace AspNetCoreWebApi.Storage.Contexts
{
    public class CountryContext : IBatchLoader<short>, ICompresable
    {
        private ReaderWriterLock _rw = new ReaderWriterLock();
        private short[] _raw = new short[DataConfig.MaxId];
        private DelaySortedList[] _id2AccId = new DelaySortedList[200];
        private DelaySortedList _null = new DelaySortedList();
        private DelaySortedList _ids = new DelaySortedList();

        public CountryContext()
        {
        }

        public void Add(int id, short countryId)
        {
            _rw.AcquireWriterLock(2000);
            if (_raw[id] == 0)
            {
                _ids.DelayAdd(id);
            }
            _raw[id] = countryId;
            if (_id2AccId[countryId] == null)
            {
                _id2AccId[countryId] = new DelaySortedList();
            }
            _id2AccId[countryId].DelayAdd(id);
            _rw.ReleaseWriterLock();
        }

        public void AddOrUpdate(int id, short countryId)
        {
            _rw.AcquireWriterLock(2000);

            if (_raw[id] > 0)
            {
                _id2AccId[_raw[id]].DelayRemove(id);
            }

            Add(id, countryId);

            _rw.ReleaseWriterLock();
        }

        public bool TryGet(int id, out short countryId)
        {
            if (_raw[id] > 0)
            {
                countryId = _raw[id];
                return true;
            }
            else
            {
                countryId = 0;
                return false;
            }
        }

        public short Get(int id)
        {
            return _raw[id];
        }

        public IEnumerable<int> Filter(
            FilterRequest.CountryRequest country,
            IdStorage ids,
            CountryStorage countries)
        {
            if (country.IsNull.HasValue)
            {
                if (country.IsNull.Value)
                {
                    return country.Eq == null
                        ? _null.AsEnumerable()
                        : Enumerable.Empty<int>();
                }
            }

            if (country.Eq == null)
            {
                return _ids.AsEnumerable();
            }
            short countryId = countries.Get(country.Eq);

            if (_id2AccId[countryId] != null)
            {
                return _id2AccId[countryId].AsEnumerable();
            }
            else
            {
                return Enumerable.Empty<int>();
            }
        }

        public IEnumerable<int> Filter(
            GroupRequest.CountryRequest country,
            CountryStorage countries)
        {
            short countryId = countries.Get(country.Country);

            if (_id2AccId[countryId] != null)
            {
                return _id2AccId[countryId].AsEnumerable();
            }
            else
            {
                return Enumerable.Empty<int>();
            }
        }

        public bool Contains(short? countryId, int id)
        {
            return _raw[id] == countryId;
        }

        public void InitNull(IdStorage ids)
        {
            _null.Clear();
            foreach(var id in ids.AsEnumerable())
            {
                if (_raw[id] == 0)
                {
                    _null.Load(id);
                }
            }
            _null.LoadEnded();
        }

        public void LoadBatch(int id, short countryId)
        {
            _raw[id] = countryId;
            _ids.Load(id);
            if (_id2AccId[countryId] == null)
            {
                _id2AccId[countryId] = new DelaySortedList();
            }
            _id2AccId[countryId].Load(id);
        }

        public IEnumerable<SingleKeyGroup<short>> GetGroups()
        {
            yield return new SingleKeyGroup<short>(0, _null.AsEnumerable(), _null.Count);
            for(short i = 0; i < _id2AccId.Length; i++)
            {
                if (_id2AccId[i] != null && _id2AccId[i].Count > 0)
                {
                    yield return new SingleKeyGroup<short>(i, _id2AccId[i].AsEnumerable(), _id2AccId[i].Count);
                }
            }
        }

        public void Compress()
        {
            _rw.AcquireWriterLock(2000);
            _ids.Flush();
            for(int i = 0; i < _id2AccId.Length; i++)
            {
                if (_id2AccId[i] != null)
                {
                    _id2AccId[i].Flush();
                }
            }
            _rw.ReleaseWriterLock();
        }

        public void LoadEnded()
        {
            _ids.LoadEnded();
            for(int i = 0; i < _id2AccId.Length; i++)
            {
                if (_id2AccId[i] != null)
                {
                    _id2AccId[i].LoadEnded();
                }
            }
        }
    }
}
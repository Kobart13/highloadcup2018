using System;
using System.IO;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using AspNetCoreWebApi.Processing.Pooling;
using AspNetCoreWebApi.Processing.Printers;
using AspNetCoreWebApi.Processing.Requests;
using AspNetCoreWebApi.Storage;
using AspNetCoreWebApi.Storage.Contexts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AspNetCoreWebApi.Processing
{
    public class SuggestProcessor
    {
        private readonly MainContext _context;
        private readonly MainStorage _storage;
        private readonly MainPool _pool;
        private readonly SuggestPrinter _printer;
        private readonly MessageProcessor _processor;

        public SuggestProcessor(
            MainStorage mainStorage,
            MainContext mainContext,
            MainPool mainPool,
            SuggestPrinter printer,
            MessageProcessor processor
        )
        {
            _context = mainContext;
            _storage = mainStorage;
            _pool = mainPool;
            _printer = printer;
            _processor = processor;
        }

        private void Free(SuggestRequest request)
        {
            _pool.SuggestRequest.Return(request);
        }

        public bool Process(int id, HttpResponse httpResponse, IQueryCollection query)
        {
            if (DataConfig.DataUpdates || DataConfig.LikesUpdates)
            {
                return false;
            }

            SuggestRequest request = _pool.SuggestRequest.Get();
            request.Id = id;

            foreach (var filter in query)
            {
                bool res = true;
                switch(filter.Key)
                {
                    case "query_id":
                        break;

                    case "limit":
                        uint limit;
                        if (!uint.TryParse(filter.Value,  out limit))
                        {
                            res = false;
                        }
                        else
                        {
                            if (limit == 0)
                            {
                                res = false;
                            }
                            request.Limit = (int)limit;
                        }
                        break;

                    case "country":
                        res = CountryEq(request, filter.Value);
                        break;

                    case "city":
                        res = CityEq(request, filter.Value);
                        break;

                    default:
                        res = false;
                        break;
                }

                if (!res)
                {
                    Free(request);
                    return false;
                }
            }

            var result = _processor.Suggest(request);

            httpResponse.StatusCode = 200;
            httpResponse.ContentType = "application/json";

            var buffer = _pool.WriteBuffer.Get();
            int contentLength = 0;
            using(var bufferStream = new MemoryStream(buffer))
            {
                _printer.Write(result, bufferStream);
                httpResponse.ContentLength = contentLength = (int)bufferStream.Position;
            }

            httpResponse.Body.Write(buffer, 0, contentLength);
            _pool.WriteBuffer.Return(buffer);

            _pool.SuggestResponse.Return(result);
            Free(request);
            return true;
        }

        private bool CityEq(SuggestRequest request, StringValues value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            request.City.IsActive = true;
            request.City.City = value;
            return true;
        }

        private bool CountryEq(SuggestRequest request, StringValues value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            request.Country.IsActive = true;
            request.Country.Country = value;
            return true;
        }
    }
}
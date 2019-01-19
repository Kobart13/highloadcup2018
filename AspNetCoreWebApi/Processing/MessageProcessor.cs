using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AspNetCoreWebApi.Domain;
using AspNetCoreWebApi.Domain.Dto;
using AspNetCoreWebApi.Processing.Parsers;
using AspNetCoreWebApi.Processing.Pooling;
using AspNetCoreWebApi.Processing.Requests;
using AspNetCoreWebApi.Processing.Responses;
using AspNetCoreWebApi.Storage;
using AspNetCoreWebApi.Storage.Contexts;
using AspNetCoreWebApi.Storage.StringPools;
using Microsoft.Extensions.DependencyInjection;

namespace AspNetCoreWebApi.Processing
{
    public class MessageProcessor
    {
        private readonly IDisposable _newAccountProcessorSubscription;
        private readonly IDisposable _editAccountProcessorSubscription;
        private readonly IDisposable _newLikesProcessorSubscription;
        private readonly IDisposable _dataLoaderSubscription;
        private readonly IDisposable _secondPhaseEndSubscription;
        private readonly MainStorage _storage;
        private readonly DomainParser _parser;
        private readonly MainContext _context;
        private readonly MainPool _pool;
        private readonly GroupPreprocessor _groupPreprocessor;
        private readonly IComparer<int> _reverseIntComparer = new ReverseComparer<int>(Comparer<int>.Default);
        private volatile int _editQuery = 0;

        public MessageProcessor(
            MainContext mainContext,
            MainStorage mainStorage,
            DomainParser parser,
            MainPool mainPool,
            GroupPreprocessor groupPreprocessor,
            NewAccountProcessor newAccountProcessor,
            EditAccountProcessor editAccountProcessor,
            NewLikesProcessor newLikesProcessor,
            DataLoader dataLoader)
        {
            _context = mainContext;
            _storage = mainStorage;
            _parser = parser;
            _pool = mainPool;
            _groupPreprocessor = groupPreprocessor;

            var newAccountObservable = newAccountProcessor
                .DataReceived
                .ObserveOn(ThreadPoolScheduler.Instance);

            var editAccountObservable = editAccountProcessor
                .DataReceived
                .ObserveOn(ThreadPoolScheduler.Instance);

            var newLikesObservable = newLikesProcessor
                .DataReceived
                .ObserveOn(ThreadPoolScheduler.Instance);

            _dataLoaderSubscription = dataLoader
                .AccountLoaded
                .Subscribe(LoadAccount);

            _newAccountProcessorSubscription = newAccountObservable
                .Subscribe(AddNewAccount);

            _editAccountProcessorSubscription = editAccountObservable
                .Subscribe(EditAccount);

            _newLikesProcessorSubscription = newLikesObservable
                .Subscribe(NewLikes);

            _secondPhaseEndSubscription = newAccountObservable
                .Select(_ => Interlocked.Increment(ref _editQuery))
                .Merge(editAccountObservable.Select(_ => Interlocked.Increment(ref _editQuery)))
                .Merge(newLikesObservable.Select(_ => Interlocked.Increment(ref _editQuery)))
                .Throttle(TimeSpan.FromSeconds(5))
                .Subscribe(_ =>
                    {
                        _context.InitNull(_storage.Ids);
                        _groupPreprocessor.Rebuild();
                    });
        }

        public SuggestResponse Suggest(SuggestRequest request)
        {
            Dictionary<int, float> similarity = _pool.DictionaryOfFloatByInt.Get();
            Dictionary<int, IEnumerable<int>> suggested = _pool.DictionaryOfIntsByInt.Get();
            _context.Likes.Suggest(request.Id, similarity, suggested);

            IEnumerable<int> result = suggested.Keys;

            bool sex = _context.Sex.Contains(true, request.Id);
            result = result.Where(x => _context.Sex.Contains(sex, x));

            if (request.Country.IsActive)
            {
                short countryId = _storage.Countries.Get(request.Country.Country);
                result = result.Where(x => _context.Countries.Contains(countryId, x));
            }

            if (request.City.IsActive)
            {
                short cityId = _storage.Cities.Get(request.City.City);
                result = result.Where(x => _context.Cities.Contains(cityId, x));
            }

            var response = _pool.SuggestResponse.Get();
            var list = _pool.ListOfIntegers.Get();
            var comparer = _pool.SuggestComparer.Get();
            comparer.Init(similarity);
            list.AddRange(result);
            list.Sort(comparer);
            response.Ids.AddRange(list.SelectMany(x => suggested[x]));
            response.Limit = request.Limit;

            _pool.DictionaryOfFloatByInt.Return(similarity);
            _pool.DictionaryOfIntsByInt.Return(suggested);
            _pool.ListOfIntegers.Return(list);
            _pool.SuggestComparer.Return(comparer);

            return response;
        }

        public RecommendResponse Recommend(RecommendRequest request)
        {
            Dictionary<int, int> recomended = _pool.DictionaryOfIntByInt.Get();
            _context.Interests.Recommend(request.Id, recomended);

            IEnumerable<KeyValuePair<int, int>> result = recomended;

            if (request.Country.IsActive)
            {
                short countryId = _storage.Countries.Get(request.Country.Country);
                result = result.Where(x => _context.Countries.Contains(countryId, x.Key));
            }

            if (request.City.IsActive)
            {
                short cityId = _storage.Cities.Get(request.City.City);
                result = result.Where(x => _context.Cities.Contains(cityId, x.Key));
            }


            bool sex = _context.Sex.Contains(true, request.Id);
            result = result.Where(x => _context.Sex.Contains(!sex, x.Key));

            var comparer = _pool.RecommendComparer.Get();
            comparer.Init(
                _context,
                recomended,
                _context.Birth.Get(request.Id).ToUnixTimeSeconds());

            var response = _pool.RecommendResponse.Get();
            response.Limit = request.Limit;
            response.Ids.AddRange(result.Select(x => x.Key));
            response.Ids.Sort(comparer);

            _pool.DictionaryOfIntByInt.Return(recomended);
            _pool.RecommendComparer.Return(comparer);

            return response;
        }

        public GroupResponse Group(GroupRequest request)
        {
            var result = _pool.FilterSet.Get();

            if (request.Sex.IsActive)
            {
                Intersect(result, _context.Sex.Filter(request.Sex));
            }

            if (request.Status.IsActive)
            {
                Intersect(result, _context.Statuses.Filter(request.Status));
            }

            if (request.Country.IsActive)
            {
                Intersect(result, _context.Countries.Filter(request.Country, _storage.Countries));
            }

            if (request.City.IsActive)
            {
                Intersect(result, _context.Cities.Filter(request.City, _storage.Cities));
            }

            if (request.Birth.IsActive)
            {
                Intersect(result, _context.Birth.Filter(request.Birth, _storage.Ids));
            }

            if (request.Interest.IsActive)
            {
                Intersect(result, _context.Interests.Filter(request.Interest, _storage.Interests));
            }

            if (request.Like.IsActive)
            {
                Intersect(result, _context.Likes.Filter(request.Like));
            }

            if (request.Joined.IsActive)
            {
                Intersect(result, _context.Joined.Filter(request.Joined, _storage.Ids));
            }

            if (!result.Inited)
            {
                Intersect(result, _storage.Ids.AsEnumerable());
            }

            GroupResponse response = _pool.GroupResponse.Get();
            response.Limit = request.Limit;

            _groupPreprocessor.FillResponse(response, result, request.Keys);

            GroupEntryComparer comparer = _pool.GroupEntryComparer.Get();
            comparer.Init(_storage, request.KeyOrder, request.Order);
            response.Entries.Sort(comparer);

            _pool.FilterSet.Return(result);
            _pool.GroupEntryComparer.Return(comparer);

            return response;
        }

        public FilterResponse Filter(FilterRequest request)
        {
            FilterSet result = _pool.FilterSet.Get();

            if (request.Sex.IsActive)
            {
                Intersect(result, _context.Sex.Filter(request.Sex));
            }

            if (request.Email.IsActive)
            {
                Intersect(result, _context.Emails.Filter(request.Email, _storage.Domains, _storage.Ids));
            }

            if (request.Status.IsActive)
            {
                Intersect(result, _context.Statuses.Filter(request.Status));
            }

            if (request.Fname.IsActive)
            {
                Intersect(result, _context.FirstNames.Filter(request.Fname, _storage.Ids));
            }

            if (request.Sname.IsActive)
            {
                Intersect(result, _context.LastNames.Filter(request.Sname, _storage.Ids));
            }

            if (request.Phone.IsActive)
            {
                Intersect(result, _context.Phones.Filter(request.Phone, _storage.Ids));
            }

            if (request.Country.IsActive)
            {
                Intersect(result, _context.Countries.Filter(request.Country, _storage.Ids, _storage.Countries));
            }

            if (request.City.IsActive)
            {
                Intersect(result, _context.Cities.Filter(request.City, _storage.Ids, _storage.Cities));
            }

            if (request.Birth.IsActive)
            {
                Intersect(result, _context.Birth.Filter(request.Birth, _storage.Ids));
            }

            if (request.Interests.IsActive)
            {
                Intersect(result, _context.Interests.Filter(request.Interests, _storage.Interests));
            }

            if (request.Likes.IsActive)
            {
                Intersect(result, _context.Likes.Filter(request.Likes));
            }

            if (request.Premium.IsActive)
            {
                Intersect(result, _context.Premiums.Filter(request.Premium, _storage.Ids));
            }

            if (!result.Inited)
            {
                Intersect(result, _storage.Ids.AsEnumerable());
            }

            var response = _pool.FilterResponse.Get();
            for(int id = 0; id < DataConfig.MaxId; id++)
            {
                if (result.Contains(id))
                {
                    response.Ids.Add(id);
                }
            }
            response.Ids.Sort(_reverseIntComparer);
            response.Limit = request.Limit;

            _pool.FilterSet.Return(result);

            return response;
        }

        private void Intersect(FilterSet result, IEnumerable<int> filtered)
        {
            result.IntersectWith(filtered);
        }

        private void EditAccount(AccountDto dto)
        {
            int id = dto.Id.Value;

            if (dto.Email != null)
            {
                Email email = _parser.ParseEmail(dto.Email);
                _context.Emails.Update(id, email);
            }

            if (dto.FirstName != null)
            {
                _context.FirstNames.AddOrUpdate(id, dto.FirstName);
            }

            if (dto.Surname != null)
            {
                _context.LastNames.AddOrUpdate(id, dto.Surname);
            }

            if (dto.Phone != null)
            {
                Phone phone = _parser.ParsePhone(dto.Phone);
                _context.Phones.Update(id, phone);
            }

            if (dto.Birth.HasValue)
            {
                _context.Birth.AddOrUpdate(id, DateTimeOffset.FromUnixTimeSeconds(dto.Birth.Value));
            }

            if (dto.Country != null)
            {
                _context.Countries.AddOrUpdate(id, _storage.Countries.Get(dto.Country));
            }

            if (dto.City != null)
            {
                _context.Cities.AddOrUpdate(id, _storage.Cities.Get(dto.City));
            }

            if (dto.Joined != null)
            {
                _context.Joined.AddOrUpdate(id, DateTimeOffset.FromUnixTimeSeconds(dto.Joined.Value));
            }

            if (dto.Status != null)
            {
                _context.Statuses.Update(id, StatusHelper.Parse(dto.Status));
            }

            if (dto.Interests != null && dto.Interests.Count > 0)
            {
                _context.Interests.RemoveAccount(id);
                foreach (var interestStr in dto.Interests)
                {
                    _context.Interests.Add(id, _storage.Interests.Get(interestStr));
                }
            }

            if (dto.Sex != null)
            {
                _context.Sex.Update(id, dto.Sex == "m");
            }

            if (dto.Premium != null)
            {
                _context.Premiums.AddOrUpdate(
                    id,
                    new Premium(
                        DateTimeOffset.FromUnixTimeSeconds(dto.Premium.Start),
                        DateTimeOffset.FromUnixTimeSeconds(dto.Premium.Finish)
                    )
                );
            }

            _pool.AccountDto.Return(dto);
        }

        private void NewLikes(List<SingleLikeDto> likeDtos)
        {
            foreach (var likeDto in likeDtos)
            {
                _context.Likes.Add(
                    new Like(
                        likeDto.LikeeId,
                        likeDto.LikerId,
                        DateTimeOffset.FromUnixTimeSeconds(likeDto.Timestamp)
                    )
                );
                _pool.SingleLikeDto.Return(likeDto);
            }
            _pool.ListOfLikeDto.Return(likeDtos);
        }

        private void AddNewAccount(AccountDto dto)
        {
            int id = dto.Id.Value;

            Email email = _parser.ParseEmail(dto.Email);
            _context.Emails.Add(id, email);

            if (dto.FirstName != null)
            {
                _context.FirstNames.AddOrUpdate(id, dto.FirstName);
            }

            if (dto.Surname != null)
            {
                _context.LastNames.AddOrUpdate(id, dto.Surname);
            }

            if (dto.Phone != null)
            {
                Phone phone = _parser.ParsePhone(dto.Phone);
                _context.Phones.Add(id, phone);
            }

            if (dto.Birth.HasValue)
            {
                _context.Birth.AddOrUpdate(id, DateTimeOffset.FromUnixTimeSeconds(dto.Birth.Value));
            }

            if (dto.Country != null)
            {
                _context.Countries.Add(id, _storage.Countries.Get(dto.Country));
            }

            if (dto.City != null)
            {
                _context.Cities.Add(id, _storage.Cities.Get(dto.City));
            }

            if (dto.Joined != null)
            {
                _context.Joined.AddOrUpdate(id, DateTimeOffset.FromUnixTimeSeconds(dto.Joined.Value));
            }

            if (dto.Status != null)
            {
                _context.Statuses.Add(id, StatusHelper.Parse(dto.Status));
            }

            if (dto.Interests != null)
            {
                foreach (var interestStr in dto.Interests)
                {
                    _context.Interests.Add(id, _storage.Interests.Get(interestStr));
                }
            }

            if (dto.Sex != null)
            {
                _context.Sex.Add(id, dto.Sex == "m");
            }

            if (dto.Likes != null)
            {
                foreach (var like in dto.Likes)
                {
                    _context.Likes.Add(new Like(like.Id, id, DateTimeOffset.FromUnixTimeSeconds(like.Timestamp)));
                }
            }

            if (dto.Premium != null)
            {
                _context.Premiums.AddOrUpdate(
                    id,
                    new Premium(
                        DateTimeOffset.FromUnixTimeSeconds(dto.Premium.Start),
                        DateTimeOffset.FromUnixTimeSeconds(dto.Premium.Finish)
                    )
                );
            }

            _pool.AccountDto.Return(dto);
        }

        private void LoadAccount(IEnumerable<AccountDto> dtos)
        {
            var birthList = new List<BatchEntry<DateTimeOffset>>();
            var cityList = new List<BatchEntry<short>>();
            var countryList = new List<BatchEntry<short>>();
            var emailList = new List<BatchEntry<Email>>();
            var fnameList = new List<BatchEntry<string>>();
            var interestList = new List<BatchEntry<IEnumerable<short>>>();
            var joinedList = new List<BatchEntry<DateTimeOffset>>();
            var snameList = new List<BatchEntry<string>>();
            var likeList = new List<BatchEntry<IEnumerable<Like>>>();
            var phoneList = new List<BatchEntry<Phone>>();
            var premiumList = new List<BatchEntry<Premium>>();
            var sexList = new List<BatchEntry<bool>>();
            var statusList = new List<BatchEntry<Status>>();

            foreach (var dto in dtos)
            {
                int id = dto.Id.Value;
                _storage.Ids.Add(id);

                Email email = _parser.ParseEmail(dto.Email);
                _context.Emails.Add(id, email);
                _storage.EmailHashes.Add(dto.Email, id);

                if (dto.FirstName != null)
                {
                    fnameList.Add(new BatchEntry<string>(id, dto.FirstName));
                }

                if (dto.Surname != null)
                {
                    snameList.Add(new BatchEntry<string>(id, dto.Surname));
                }

                if (dto.Phone != null)
                {
                    Phone phone = _parser.ParsePhone(dto.Phone);
                    _storage.PhoneHashes.Add(dto.Phone, id);
                    phoneList.Add(new BatchEntry<Phone>(id, phone));
                }

                if (dto.Birth.HasValue)
                {
                    birthList.Add(new BatchEntry<DateTimeOffset>(id, DateTimeOffset.FromUnixTimeSeconds(dto.Birth.Value)));
                }

                if (dto.Country != null)
                {
                    countryList.Add(new BatchEntry<short>(id, _storage.Countries.Get(dto.Country)));
                }

                if (dto.City != null)
                {
                    cityList.Add(new BatchEntry<short>(id, _storage.Cities.Get(dto.City)));
                }

                if (dto.Joined != null)
                {
                    joinedList.Add(new BatchEntry<DateTimeOffset>(id, DateTimeOffset.FromUnixTimeSeconds(dto.Joined.Value)));
                }

                if (dto.Status != null)
                {
                    statusList.Add(new BatchEntry<Status>(id, StatusHelper.Parse(dto.Status)));
                }

                if (dto.Interests != null)
                {
                    interestList.Add(new BatchEntry<IEnumerable<short>>(id, dto.Interests.Select(x => _storage.Interests.Get(x))));
                }

                if (dto.Sex != null)
                {
                    sexList.Add(new BatchEntry<bool>(id, dto.Sex == "m"));
                }

                if (dto.Likes != null)
                {
                    likeList.Add(new BatchEntry<IEnumerable<Like>>(id, dto.Likes.Select(x => new Like(x.Id, id, DateTimeOffset.FromUnixTimeSeconds(x.Timestamp)))));
                }

                if (dto.Premium != null)
                {
                    premiumList.Add(new BatchEntry<Premium>(id, new Premium(
                            DateTimeOffset.FromUnixTimeSeconds(dto.Premium.Start),
                            DateTimeOffset.FromUnixTimeSeconds(dto.Premium.Finish)
                        )));
                }
            }

            _context.Birth.LoadBatch(birthList);
            _context.Cities.LoadBatch(cityList);
            _context.Countries.LoadBatch(countryList);
            _context.Emails.LoadBatch(emailList);
            _context.FirstNames.LoadBatch(fnameList);
            _context.Interests.LoadBatch(interestList);
            _context.Joined.LoadBatch(joinedList);
            _context.LastNames.LoadBatch(snameList);
            _context.Likes.LoadBatch(likeList);
            _context.Phones.LoadBatch(phoneList);
            _context.Premiums.LoadBatch(premiumList);
            _context.Sex.LoadBatch(sexList);
            _context.Statuses.LoadBatch(statusList);
        }
    }
}